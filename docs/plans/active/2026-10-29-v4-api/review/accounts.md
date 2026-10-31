# C2: review typed account and library responses

The migration rehearsal and account API changes are implemented on
`codex/c2-account-typed-ids`. Review the running responses before mobile work.
This is the API portion of C2; it does not qualify mobile migration or authorize
production cutover.

## What to review

Use the [local review commands](../../../../../local-dev/accounts-review/README.md)
to read `/v1/me` and `/v1/library/snapshot`. The capture also saves batch results,
changes, conflict and cursor-reset responses. The
[response excerpts](responses/accounts-excerpt.json) show selected actual fields;
full responses and protected cursors stay in the ignored local output directory.

Check these decisions:

- Profile identity is `user_id` plus `native_session_id` for native credentials.
  The browser profile omits `native_session_id`.
- Memberships have `favorite_id` and `target: { kind, id }`. Global venue/song
  targets need no artist scope or catalog lookup during writes.
- Batch results distinguish submitted and canonical favorite IDs. A duplicate
  target retains its first membership; an exact retry returns its original receipt.
- Library snapshots and changes keep numeric revisions and protected cursors.
  An invalid or pre-cutover cursor returns 410 and `/v1/library/snapshot`.
- Existing routes remain `/v1/me`, `/v1/library/*`, and the current auth routes.
  Raw UUID payloads and old field aliases are rejected.

A representative profile is:

```json
{
  "contract_version": 1,
  "user_id": "user_01k8000000e008000000000001",
  "username": "c2_listener",
  "username_version": 3,
  "username_review_needed": false,
  "username_reviewed_at": "2026-10-01T00:00:00+00:00",
  "username_change_available_at": null,
  "native_session_id": "session_<U26>"
}
```

The session placeholder varies by sign-in; the linked capture contains its actual
value. The local account fixture is explicit test data, not a production account
export. Real Development sign-in attaches to that migrated provider link.

## Storage and responsibility

The [account contract](../contracts.md#canonical-accountauth-ids-and-the-in-place-library)
remains the source of truth. Six application-owned ID types hold complete canonical
strings and use EF text conversion. New IDs use UUIDv7 suffixes. Existing UUID bits
are encoded without reminting users or favorites. Framework OpenIddict keys,
external provider identities, and private surrogate row IDs remain unchanged.

The account service owns exact membership, quota, receipts and library revisions.
It validates the target's syntax/kind without requiring the catalog. Catalog
availability or normalization redirects cannot silently erase saved membership.

The EF migration runs DDL and conversion in one transaction with old writers
stopped. A migration-local encoder converts IDs and targets, then is removed.
Existing PKs, indexes and FK definitions are retained; the same-user SSO composite
FK is restored. Username receipt JSON contains username/version/date fields and
needs no ID rewrite. Receipt hash bytes and opaque token payloads are retained.

Old native/web/SSO sessions and linked first-party token families are revoked.
Framework subject references become typed users. New cookie/cursor purposes and
command hash domains prevent old credentials or pending operations entering the
new protocol. No dual-format reader or parallel library route was added.

## Evidence from October 30

- **169 account tests pass**, including real PostgreSQL migration, typed storage,
  provider/membership/hash preservation, composite FK enforcement, JSON validation,
  replay/conflicts and existing browser/native security policies.
- **183 existing catalog/popularity/reference tests pass**; the solution builds.
- Real native HTTP exercises sign-in, profile, username replay, library add/read/
  changes/replay/conflict, account separation, logout and concurrent refresh replay.
- Real browser code exchange creates a typed web session for the same account.
  Profile and library reads work; missing CSRF/wrong origin fail; replay and logout
  work. The script emulates the existing relay without changing the web client.
- A pre-cutover backup was restored into a separate database and migrated again:
  all seven memberships and the same user/username survived; active old native
  sessions changed from one to zero.
- The old binary issued real credentials against a separate old-schema clone.
  After migration, its access token returned **401**, refresh returned **400
  `invalid_grant`**, and its protected cursor returned **410**. Fresh sign-in
  returned the same typed user and all seven original favorites.
- Two independent code-review passes found no actionable correctness or
  readability issue. The simplification pass removed redundant cookie parsing
  checks; it retained the existing account serialization and security boundaries.

Evidence comes from local fixtures and real service execution, not a production
account export or production traffic. Focused tests and rehearsal commands are
repeatable from the local guide. Production database size/lock duration and the
production backup/restore procedure still need release qualification.

## Chrome verification and focused-diff review — October 31

The fresh account test run passes all 169 cases: 154 existing cases and 15 added
for C2. The new NUnit files total 217 lines. The migration designer and snapshot
are generated EF output. The review found no runtime defect or test deletion
that would preserve the same coverage. No runtime or frontend code changed in
this pass.

One HTTP assertion was too weak: it accepted an empty changes feed with the
correct revision. The capture now checks exact targets, canonical favorite IDs,
event types and contiguous revisions. Correct responses pass; an empty feed,
wrong favorite ID or skipped revision fails. The account-isolation comment no
longer claims to test same-email linking.

Chrome exercised the existing web relay with real browser cookies: persona
sign-in, profile/library reads, a new favorite and its exact changes, exact retry,
changed-retry conflict (409), missing-CSRF rejection (403), removal, reload,
cursor recovery (410), and logout followed by 401 reads. The temporary favorite
was removed; the original nine memberships remain.

Chrome also exercised the actual iOS development client's authorization request
and registered callback, PKCE exchange, bearer profile/library reads, username
command/replay, favorite add/changes/replay/remove, refresh rotation, account
isolation and logout. Refresh retained the same user and session. Logging out a
second account left the original session valid. Original-session logout returned
204, followed by 401 access and 400 `invalid_grant` refresh. Android development
client authorization/callback, PKCE exchange, typed profile and logout/revocation
also passed. Native callbacks were captured without launching an app. Credentials
were kept out of saved evidence and removed from the page's test variables.

These are browser and native-protocol server checks using Development personas.
They do not establish real Google/Apple integration or native app, deep-link,
SecureStore, Realm, offline or restart behavior.

## Deployment and next checkpoint

Do not deploy this breaking server contract while the current mobile alpha client
is still the only client. After Alec accepts these responses, implement the planned
restartable mobile identity upgrade and tester re-sign-in, keeping catalog reads
on v3 for the isolated account proof.

The eventual account cutover requires maintenance for old account/auth traffic,
a verified whole-account backup, migration, compatible server/client activation,
and tester sign-in. Keep it separate from v4 catalog activation. The migration
blocks downgrade intentionally; recovery after success uses a coordinated backup
restore or forward fix. Accepted post-cutover writes need an explicit recovery
choice before restoring an older backup.

No production deployment, production session revocation, mobile edit or catalog
activation occurred in this checkpoint.
