# Let listeners delete their account

Status: server deletion and Apple revocation for browser and native codes are
implemented locally. Mobile and web integration and deployment are pending.
This plan replaces the account-deletion design
in Milestone 2 of the [July implementation plan](2026-07-18-relisten-identity-user-data-and-sonos-implementation.md).
It must ship before accounts reach an external beta or a public store build.

## Purpose / Big Picture

A signed-in listener taps **Delete account** in the app and confirms. The app
signs out at once, and within moments every row that belongs to them is gone. App
Store review requires that deletion can be started inside the app. Apps outside
highly regulated industries may not require an email or support flow
([Apple guidance](https://developer.apple.com/support/offering-account-deletion-in-your-app/)).

The deletion is deliberately coarse. The request records a job and starts a
Temporal workflow. The workflow deletes the user row, which cascades to
everything the user owns and ends their sessions. There is no outbox, sweep,
impact endpoint, object-storage tombstone, restore replay or
recent-authentication step.

## Context and Orientation

- Every user-owned table currently cascades from `identity.users`:
  external identities, sessions, native sessions, username holds and receipts,
  favorites and their receipts, library state and changes, plays, playlists and
  their receipts, and the user's playlist follows and reports. Playlist segments,
  entries, revisions and reports cascade from playlists. Follows reference the
  playlist by ID only, so a survivor's follow of a deleted playlist remains and
  reads as unavailable.
  OpenIddict authorizations and tokens reference the user only by `subject` text,
  so they need an explicit delete. User IDs are `text` holding a `user_` TypeID.
- Session checks and sign-in already refuse a user who is missing or not
  `active` (`NativeSessionAuthorizationHandler`, `IdentitySessionLifecycle`,
  `TokenController`, `AuthorizationController`). Deleting the user row therefore
  ends every session.
- `AuthorizationController` reads the active user before it opens the issuance
  transaction, so an authorization in progress can commit after a purge has
  started. See step 2.
- Temporal already runs in production. `RelistenIngestionWorker` hosts the
  ingestion task queues as separate hosted workers. It references the
  `RelistenApi` web project, receives only `DATABASE_URL`, and doesn't run either
  service's `Program` setup.
- In `relisten-flux` at `b3c8eec`, the User Service's egress allows DNS, Postgres,
  Traefik and outbound 443, but not Temporal's 7233. Temporal's ingress
  allowlist (`default-network-policies.yaml`) doesn't include
  `relisten-user-service`.
- Relisten doesn't store Apple or Google tokens. Apple still expects apps that
  offer Sign in with Apple to revoke the user's Apple tokens on deletion.

## Plan of Work

### 1. Request deletion

Add `POST /v1/account-deletions` to the User Service under the
`account.manage` policy, the same policy as `PATCH /v1/me`. The request doesn't
change the account. It records the job and starts the workflow:

1. In one transaction, lock the user row and insert
   `identity.account_deletion_jobs` if the user has none: UUIDv7 `id`, `user_id`
   (`text`, unique, no foreign key, so the job outlives the user row),
   `requested_at`, nullable `completed_at`. If a job already exists, reuse it.
   Commit.
2. Start the `AccountDeletion` workflow on the `user-data` task queue with
   workflow ID `account-deletion-<job id>`, passing the job ID and user ID as
   input. If a workflow with that ID is already running or completed, treat the
   start as successful. A failed, cancelled, terminated or timed-out run may be
   replaced by a new start (`AllowDuplicateFailedOnly`). A completed run means the
   user row is gone.
3. If the User Service cannot confirm the start, return `503`. A definite
   rejection leaves the account and session active. A lost reply can mean the
   workflow is already running. A retry with a valid session reuses the job.
4. Otherwise return `202 {state: "deleting"}`.

The session stays valid until the purge runs. A retry after a lost response
gets `202` while the session is still valid and `401` once the purge completes.
The username stays held until the purge removes the user row. A `disabled` user
can't reach the endpoint. An operator can delete that account by hand.

**Apple.** iOS can offer reauthorization when `/v1/me` returns
`apple_native_revocation_available: true`. The field is omitted when the
account has no linked Apple identity or native exchange is unconfigured.
Send the fresh code as `apple_native_authorization_code`. If the listener
cancels reauthorization, send the deletion request without a code.

After step 1, the User Service exchanges a native code using
`Accounts:Apple:NativeClientId`, without a redirect URI. A browser code sent
as `apple_authorization_code` uses the Apple Services ID and `/signin-apple`
redirect. Send one code per request. The native code takes precedence if both
are supplied. Both paths use the existing signing key. Apple Developer lists the Relisten
iOS App ID as the primary app for `net.relisten.auth` and the configured key
`379MK9D59A`.

The service validates the identity token against the selected Apple client and
the account's linked Apple subject before revoking the token. The whole attempt
has a five-second limit. Deletion continues if the code is absent or Apple
fails. Normal mobile sign-in already redeems its browser code, so the iOS
deletion prompt must obtain a fresh native code.

The User Service gets a Temporal client with the same address and namespace
settings as the ingestion services. It only starts workflows and runs none.

### 2. Purge

The `AccountDeletion` workflow calls one purge activity and completes when it
returns. The activity runs one transaction:

1. Lock the job row. If `completed_at` is set, return.
2. Lock the user row `FOR UPDATE`. If it is gone, set `completed_at` and return.
3. Delete OpenIddict tokens and authorizations whose `subject` is the user ID.
4. Delete the `identity.users` row. The cascade removes everything else.
5. Set `completed_at` and commit.

The job row always commits before the workflow starts, so the activity never has
to wait for it. If the job row is missing anyway, for example after a backup
restore, the activity purges by user ID like `purge-account` instead of failing
every retry.

**Issuance race.** OAuth issuance takes the same user row `FOR SHARE` inside its
transaction and rechecks that the user exists and is `active`. If issuance holds
the lock first, the purge waits, then deletes the authorization issuance just
committed. If the purge holds it first, issuance finds no user and fails.

**Retries.** A failure rolls the transaction back. The activity retries with no
attempt limit and backoff capped at one hour. It reports the first failed
attempt to Sentry and then every 24th, about once a day at the capped interval.
A start-to-close timeout counts as a failure. Worker shutdown doesn't.
While a purge is failing, the account still works. A stuck deletion shows up as
an open `AccountDeletion` workflow in the Temporal UI.

**Where the code runs.** Register the workflow and activity on a new `user-data`
task queue in `RelistenIngestionWorker`, as its own hosted worker like the
ingestion queues. Don't reference the `RelistenUserService` web project: both
web projects would publish `appsettings*.json` to the same place. The purge is
one short transaction against known tables, and the worker already uses Dapper,
so write it there as parameterized SQL over the accounts connection. It needs no
EF context. The two-user integration test below proves the SQL matches the
schema.

Every later feature that stores user data adds its rows to the cascade, or adds
an explicit step to this activity. The schema test below fails until it does.

### 3. Deployment

Deploy these `relisten-flux` changes before the User Service build that serves
account deletion:

- Allow egress from `relisten-user-service` to `app: relisten-temporal` on TCP
  7233.
- Add `relisten-user-service` to Temporal's ingress allowlist.
- Bind `ACCOUNTS_DATABASE_URL` on `relistenapi-ingestion-worker`. Without it the
  worker logs a warning and doesn't poll `user-data`, so deletions return `202`
  but never run.
- Set `Temporal__Address` and `Temporal__Namespace` on `relisten-user-service`.
  In Production the User Service refuses to start without both.

Deploy the worker with `ACCOUNTS_DATABASE_URL` first. Verify it is polling the
`user-data` queue and the User Service can reach Temporal before rolling out the
endpoint. After a backup restore, use the worker's `purge-account --user <id>`
command (see Known limitations).

### 4. Mobile and web

- **Mobile.** Add **Delete account** to account settings. The confirmation says
  that deletion is permanent and removes the listener's playlists for everyone,
  using the playlist count already in the local library.
  - On `202`, clear the account's scoped local data and return to anonymous mode.
  - On a network error, retry. The session is valid until the purge completes.
  - On `401`, the app can't tell a finished deletion from an expired session.
    Return to anonymous mode and keep the account's rows and pending edits, but
    clear `lastSignedInUserId`, so they are hidden. This differs from ordinary
    sign-out, which deliberately leaves the last account's playlists visible
    and editable. Never destroy anything on `401`. Ordinary sign-out is
    unchanged.
- **Web.** Add a relisten.net page that explains the in-app button and lists
  team@relisten.net for people without the app. Google Play asks for a web
  resource. Check its current policy before submission.

## Validation and Acceptance

- A request purges the account in the SDK-managed Temporal test server, and
  afterwards the old session gets `401`.
- A definite start rejection returns `503`. The account still works, and a
  retry on the same session starts the workflow and returns `202`.
- If Temporal accepts the workflow and its start reply is lost, the purge still
  commits and marks the deletion job complete.
- A repeated request before the purge returns `202` and starts no second
  workflow.
- A purge activity that fails once succeeds on retry without duplicate effects.
- Pause an OAuth authorization after it reads the user, run the purge, then let
  issuance continue. No OpenIddict row for the user survives. A fresh sign-in
  attempt after deletion doesn't exercise this race.
- The purge test finds user-owned tables from foreign keys, not column names.
  It follows every foreign key path from `identity.users`, plus the subject-only
  OpenIddict tables. It populates two users, one deleted and one surviving,
  with rows in every such table that exists. The test starts with today's
  tables and grows as each later plan adds one:
  - favorites and playlists;
  - a report on the survivor's playlist;
  - follows in both directions;
  - an MCP grant;
  - OpenIddict records;
  - plays in compressed chunks.

  After the purge, none of the deleted user's rows remain. Every survivor row
  remains, including the survivor's follow of the deleted user's playlist as an
  unavailable follow record. `account_deletion_jobs` keeps the ID on purpose.
- Purging a heavy listener with several years of compressed plays succeeds.
  Record how long the transaction takes.
- After the purge, the same Apple or Google identity signs in as a new account.
- On mobile, a `401` from the endpoint leaves the account's playlists and
  pending edits on the device and hidden, with `lastSignedInUserId` cleared. An
  ordinary sign-out still leaves them visible.

## Known limitations

- **Restoring a backup brings deleted accounts back.** Rows restored from before
  a deletion include that account. The restored job table doesn't list
  deletions requested after the backup. Production's `relisten` namespace keeps
  closed workflows for seven days (`604800s`), and each `AccountDeletion`
  workflow's input holds the user ID. After a restore, the operator lists
  `AccountDeletion` workflows that closed after the backup was taken. For each
  user ID, the operator runs a worker command, `purge-account --user <id>`. It
  runs the same purge SQL directly by user ID, without needing a job row,
  because the restored database has none. For deletions older than seven days,
  recovery is best effort.
- **No re-authentication.** A stolen session could delete the account.

## Decision Log

- 2026-11-18, Alec: use a coarse Temporal workflow on a task queue in the existing
  worker. Drop the object-storage tombstone.
- 2026-11-18, Alec: the User Service starts the workflow directly, with no outbox
  or backstop sweep. Deletions are rare.
- 2026-11-18, review: the request leaves Relisten sessions active. Committing the
  job before starting the workflow ensures the purge can find the job. The purge
  deletes the user row, which ends sessions.
- 2026-11-18, review: mobile destroys local data only on `202`. A `401` keeps the
  account's data but clears `lastSignedInUserId`, so it is hidden. Ordinary
  sign-out still leaves it visible.
- 2026-11-18, design: no impact endpoint and no recent-authentication step for
  the first version.
