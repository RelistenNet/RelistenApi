# Review C2 accounts and library

This runs the actual User Service against the separate local `relisten_c2_review`
database. It uses the existing Development Google persona in place of Google;
authorization codes, PKCE, tokens, native/web sessions, account policies and
library persistence use their normal implementations. No mobile changes are needed.

The migrated account is `c2_listener` (`google-alice`). Its seven original
memberships come from the explicit pre-typed migration fixture. Some targets are
intentionally unresolved: library membership must survive unavailable catalog
metadata. The capture adds real `venue_fp4h900f` and `song_62mhvjyd` targets from
the catalog preview. Account payloads contain identities, not catalog metadata.

## Use the running preview

The service is at `https://auth.relisten.localhost:5443` and
`https://accounts.relisten.localhost:5443`. Both use the existing trusted local
certificate. The catalog preview remains at `http://127.0.0.1:43823`.

From the repository root:

```sh
# Restart the account preview if needed (stop its existing process first).
local-dev/accounts-review/run.sh

# In another terminal: sign in, exercise the public APIs, and save responses.
local-dev/accounts-review/capture.py

# Inspect a fresh authenticated response using the saved local review credential.
local-dev/accounts-review/capture.py /v1/me
local-dev/accounts-review/capture.py /v1/library/snapshot
```

`capture.py` uses curl and Python's standard library. Curl must trust the existing
local CA; it never disables TLS validation. The browser checks emulate the existing
web relay and use the actual code-exchange/session/CSRF endpoints. They do not start
or modify the web client. See [local setup](../README.md#run-the-net-services) if the
certificate/client-secret setup is missing.

Every capture resets only its two venue/song review memberships through the API,
then adds them, so the changes response contains real additions on repeated runs.
It also checks command/mutation replay, typed conflicts, wrong-prefix input,
account separation, unauthenticated reads, browser origin/CSRF and logout.

Full responses are in ignored `.output/*.json`. `.output/access-token` is an
owner-only local credential for the GET commands; do not share or commit it.
The script leaves Alice's native session active for review and logs out the
secondary account and browser session. Direct browser navigation to an account
URL requires authentication; the GET command supplies the local credential.

## Inspect the browser flow in Chrome

Start the existing web relay in another terminal:

```sh
cd ../relisten-web
pnpm dev
```

Open <https://web.relisten.localhost:5173/auth/session/start?return_to=%2Fbrowser-session-development>
and choose **Alice**. In the same Chrome session, open `/v1/me` and
`/v1/library/snapshot` on `https://web.relisten.localhost:5173` to inspect real
cookie-authenticated responses. The plain development page is a sign-in landing
page; it does not implement the new account client. No frontend edits are needed
for API inspection. The [checkpoint](../../docs/plans/active/2026-10-29-v4-api/review/accounts.md#chrome-verification-and-focused-diff-review--october-31)
records the browser and native-protocol checks performed through Chrome.

## Recreate the migration rehearsal

Use a **new** local database. Do not point these commands at `relisten_db` or a
shared account database. These commands use the shared local PostgreSQL container.

```sh
export PGPASSWORD=local_dev_password
createdb -h 127.0.0.1 -p 15432 -U relisten relisten_c2_rehearsal

dotnet ef database update 20260827052217_AddDurableBrowserSessions \
  --project RelistenUserService \
  --connection 'Host=127.0.0.1;Port=15432;Database=relisten_c2_rehearsal;Username=relisten;Password=local_dev_password'

psql -X -v ON_ERROR_STOP=1 -h 127.0.0.1 -p 15432 -U relisten \
  -d relisten_c2_rehearsal -f local-dev/accounts-review/pre-typed-fixture.sql

pg_dump -h 127.0.0.1 -p 15432 -U relisten \
  --schema=identity --schema=user_data -Fc \
  -f /tmp/relisten-c2-pre-cutover.dump relisten_c2_rehearsal

dotnet ef database update --project RelistenUserService \
  --connection 'Host=127.0.0.1;Port=15432;Database=relisten_c2_rehearsal;Username=relisten;Password=local_dev_password'
```

The automated preservation checks use the same fixture:

```sh
dotnet test RelistenUserServiceTests --filter FullyQualifiedName~TestTypedAccountMigration
```

To rehearse recovery, restore into another new database. Create the two schemas
before a schema-filtered restore:

```sh
createdb -h 127.0.0.1 -p 15432 -U relisten relisten_c2_recovery
psql -X -v ON_ERROR_STOP=1 -h 127.0.0.1 -p 15432 -U relisten \
  -d relisten_c2_recovery -c 'CREATE SCHEMA identity; CREATE SCHEMA user_data;'
pg_restore --exit-on-error --schema=identity --schema=user_data \
  -h 127.0.0.1 -p 15432 -U relisten -d relisten_c2_recovery \
  /tmp/relisten-c2-pre-cutover.dump
```

The restored database has the old UUID schema and credentials' old revocation
state. It can be migrated again. Never use EF `database update` to downgrade this
cutover: credential revocation is not undone by a schema rollback. A live return
to old binaries needs the coordinated whole-account backup, stopped writes, and
an explicit decision about any writes accepted after cutover.

See the [C2 checkpoint](../../docs/plans/active/2026-10-29-v4-api/review/accounts.md)
for results and the remaining review/deployment work.
