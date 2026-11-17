# Verify and operate the private playlist API

Use the User Service tests to verify private-playlist HTTP behavior and database
transactions. Before release, the mobile consumer and deployment still need
qualification. After every User Service database restore, rotate
`history_generation` before ordinary traffic resumes; the recovery section below
gives the playlist-specific checks.
The [frozen contracts](../plans/active/2026-11-16-v4-playlist-contracts.md) define
payloads and client recovery; the
[delivery plan](../plans/active/2026-11-06-v4-playlists.md) records scope and decisions.

## Verify a backend change

From the repository root, with Docker running:

```sh
./start-local-databases.sh
dotnet tool restore
dotnet test RelistenUserServiceTests/RelistenUserServiceTests.csproj
dotnet build RelistenApi.sln
dotnet ef migrations has-pending-model-changes --project RelistenUserService --startup-project RelistenUserService
dotnet csharpier check .
```

Require a passing User Service suite with **zero skipped tests**. Its
[PostgreSQL fixture](../../RelistenUserServiceTests/PostgresIntegrationDatabase.cs)
creates and removes disposable databases on the shared local server. If it cannot
create a database, it skips integration cases: a green run with those skips does
not verify persistence. Start the local services or set
`RELISTEN_TEST_POSTGRES_ADMIN` to a local test server where the test role can create
databases, then rerun. `./test-local.sh` runs the catalog and normalization suites;
it does not run the User Service suite.

The model check must report no pending changes. If it finds a difference, review
the model and migration together before treating the schema as complete. Build
and formatting checks establish that the solution compiles and follows repository
formatting; they do not replace the behavioral tests.

For a focused iteration, run the playlist HTTP and contract fixtures:

```sh
dotnet test RelistenUserServiceTests/RelistenUserServiceTests.csproj --filter 'FullyQualifiedName~TestPlaylistApi|FullyQualifiedName~TestPlaylistContracts'
```

Read the assertions in
[TestPlaylistApi](../../RelistenUserServiceTests/TestPlaylistApi.cs) when changing
these guarantees:

- Ordered edits, exact retries and library discovery observe one committed result.
  Semantic rejection does not prevent later valid operations in a batch;
  a failure after direct SQL rank staging leaves the prior snapshot, receipts and
  library revision intact.
- Full-playlist quota correction preserves the destination, concurrent creates
  respect the owner's shared quota, and rank rebalancing respects real unique
  indexes without splitting one revision across delta pages.
- Archive retains content and accepts only all-replay batches. A simulated restore
  reuses revision numbers while changing content: the old library cursor expires,
  a fresh snapshot shows the new generation, and a surviving create receipt
  replays under that generation.

The fixture starts `AccountsApplication` on loopback Kestrel with production
registration, middleware and method-aware authorization. It substitutes credential
verification; the real policy admits read-only credentials for reads and refuses
their writes. The size-limit regression sends valid playlist JSON plus trailing
whitespace beyond 2 MiB and requires `413` rather than a server failure.
Shared authorization, route and browser protections also have coverage
in [TestReviewedAccountAccessAuthorization](../../RelistenUserServiceTests/TestReviewedAccountAccessAuthorization.cs),
[TestWebRequestBoundaries](../../RelistenUserServiceTests/TestWebRequestBoundaries.cs)
and [TestWebSessionAntiforgery](../../RelistenUserServiceTests/TestWebSessionAntiforgery.cs).
These checks do not exercise production ingress, a backup restore or mobile
application state.

## After restoring the User Service database

A backup or point-in-time restore can reuse playlist and library revision numbers
for different content. Rotate `user_data.history_generation` to a fresh UUIDv7
after **every restore**, before ordinary traffic or writers resume. Normal
restarts and deploys keep the existing generation.

This section supplies the playlist-specific post-restore steps. Database
restoration, traffic isolation and restored-credential recovery must be arranged
for the incident separately; this repository does not supply their production
commands. Keep ordinary traffic and all writers stopped through restoration,
forward migrations and generation rotation. After rotation, admit only the
controlled test-account requests used for validation.

For a planned rehearsal, retain test-account evidence before taking the backup:
a successful create request and its response, its playlist snapshot, and a library
snapshot containing `next_cursor` and `history_generation`. The create receipt
must be included in the backup. Keep this evidence private. During an incident
without retained probes, rotate anyway, check the database generation and fresh
snapshots, and record cursor/replay validation as not performed.

### 1. Confirm the restored database and migration

Use an authenticated `psql` session to the restored **primary**, with connection
credentials supplied by the recovery runtime:

```sql
SELECT current_database(), inet_server_addr(), inet_server_port(), pg_is_in_recovery();
SELECT "MigrationId"
FROM identity."__EFMigrationsHistory"
WHERE "MigrationId" = '20261117090223_AddPrivatePlaylists';
SELECT id, value FROM user_data.history_generation;
```

The endpoint/database must match the intended restore, `pg_is_in_recovery()` must
be false, the migration query must return one row, and the singleton query must
return exactly one row with `id = 1`. Record its value as the pre-rotation
generation. If the migration is absent, apply the required forward migrations
while traffic remains blocked. The current beta startup mechanism is
[AccountsApplication.InitializeAsync](../../RelistenUserService/AccountsApplication.cs),
which invokes [AccountsDatabaseMigrator](../../RelistenUserService/Persistence/AccountsDatabaseMigrator.cs)
when `Accounts:ApplyMigrationsOnStartup` is enabled. The checked-in
[User Service deployment](https://github.com/RelistenNet/relisten-flux/blob/main/clusters/relisten3-k3s/apps/relisten-user-service.yaml)
enables that setting for the single-replica beta. Rotation does not apply migrations.

For retained probes, record the restored test playlist and receipt before rotation.
Replace these `psql` variable values with the IDs from the retained create request:

```sql
\set playlist_id 'playlist_REPLACE_WITH_RETAINED_ID'
\set command_id 'command_REPLACE_WITH_RETAINED_ID'
SELECT id, owner_id, revision, entry_count, archived
FROM user_data.playlists WHERE id = :'playlist_id';
SELECT id, playlist_id, kind, status, revision, archived, changed
FROM user_data.playlist_receipts WHERE id = :'command_id';
```

Both queries must return the expected test-account row. A missing retained receipt
means this backup cannot validate its replay. These are spot checks; the incident's
database recovery checks must establish the backup's broader integrity.

### 2. Rotate through the published application

Use the published User Service build intended for recovery. In its published
directory, supply `ACCOUNTS_DATABASE_URL` through the operator runtime's Secret,
pointing to the restored primary confirmed above, then run:

```sh
dotnet RelistenUserService.dll rotate-history-generation --traffic-stopped
```

Expect exit code 0 and `History generation rotated to <new UUIDv7>`. Repeat the
singleton and retained-row queries from step 1. The singleton must match the
printed generation and differ from the pre-rotation value; the playlist and
receipt fields must be unchanged. Keep traffic blocked if any check fails.

The command in [Program.cs](../../RelistenUserService/Program.cs) updates only the
singleton and exits before starting a listener or initializing OAuth providers.
It fails if the singleton is missing. `--traffic-stopped` acknowledges the
operator prerequisite; the command cannot stop traffic.

The command uses
[AccountsDbContextFactory](../../RelistenUserService/Persistence/AccountsDbContextFactory.cs)
and [DatabaseConnectionString.Resolve](../../RelistenUserService/Persistence/DatabaseConnectionString.cs):
`ACCOUNTS_DATABASE_URL` takes precedence over `ConnectionStrings:Accounts`, then
`DATABASE_URL`. The factory reads JSON settings relative to the working directory
and environment variables. Supply the explicit runtime Secret so a file default
cannot select another database. Keep credentials out of commands and recovery evidence.

### 3. Validate the recovered protocol before reopening traffic

Start an isolated instance with the intended recovery build and recovered
authentication configuration. Normal service startup initializes OAuth and may
run migrations; only the rotation command bypasses that startup. Keep public
access and ordinary writers blocked. With valid recovered account credentials,
first request `GET /v1/library/snapshot`: it must return `200` with the new
generation confirmed in step 2. Where an owned playlist is available, its fresh
snapshot must carry the same generation and the expected restored content.

Use whichever retained test-account probes are available:

- `GET /v1/library/changes?after=<retained next_cursor>` must return
  `410 sync_cursor_expired`. URL-encode the cursor. This must hold even if the
  restored library's numeric revision equals the captured revision.
- `GET /v1/playlists/<retained playlist_id>/snapshot` must return `200`, the new
  `history_generation`, and the expected restored content. Compare with evidence
  from the backup's recovery point, rather than edits newer than the backup.
- Only if both the playlist and its create receipt were found in step 1, replay
  the exact retained create body with `POST /v1/playlists`. It must return
  `200` with the original create outcome and the new `history_generation`.
  The `command_id`, `playlist_id` and payload must remain unchanged. Repeat the
  retained-row queries to check that replay preserved the playlist and receipt.

The equal-revision case is reproduced locally by
`Restore_generation_detects_equal_revisions_and_wraps_surviving_receipts` in
[TestPlaylistApi](../../RelistenUserServiceTests/TestPlaylistApi.cs). Do not
manufacture revision reuse by editing production rows.

Record each unavailable cursor or replay check as unperformed. If step 1 did not
find the create receipt, do not resend that command: the replay probe cannot be
validated against this backup. Do not treat fresh snapshots as proof of either
missing check. Generation rotation and the fresh library-snapshot check remain required.
Reopen ordinary traffic only when the available checks pass and the incident's
recovery acceptance criteria accept any recorded gaps, including completion of
required restored-credential invalidation. Rotation does not invalidate credentials
or recover data newer than the backup. Receipts
newer than the backup may be lost. Clients retain pending intent, discard old
bases/cursors on a generation mismatch, and follow the
[frozen restore-recovery sequence](../plans/active/2026-11-16-v4-playlist-contracts.md#client-model).
Edits retired from every client and absent from the backup cannot be recovered by
this protocol. Account deletion and deletion-tombstone recovery remain deferred.

## Rollout and rollback constraints

The [AddPrivatePlaylists migration](../../RelistenUserService/Persistence/Migrations/20261117090223_AddPrivatePlaylists.cs)
is additive and preserves existing favorites. Existing library cursors expire
because the new protected payload includes history generation; clients must fetch
a snapshot.

Before initial enablement, ship a mobile library reader that understands
`playlists`, `playlist_summary`, generation-aware snapshots/cursors and unknown
future change types. Qualify lost-create retries, interleaved receipt/delta pulls,
quota correction, archived outboxes and restore recovery through the real mobile
path. Name API latency, owner-lock contention and mobile first-render budgets,
then measure representative workloads and devices against them. Local backend
tests and the measurements below do not establish those release conditions.

The checked-in
[Flux account-host ingress](https://github.com/RelistenNet/relisten-flux/blob/main/clusters/relisten3-k3s/apps/relisten-user-service.yaml)
sends all paths to the User Service.
Same-origin production browser calls through `relisten.net` also need exact
`/v1/playlists` and segment-safe `/v1/playlists/` routes to the User Service,
matching the existing library rules in
[relisten-web.yaml](https://github.com/RelistenNet/relisten-flux/blob/main/clusters/relisten3-k3s/apps/relisten-web.yaml),
before enabling a web playlist consumer. No Flux resources were changed or deployed
for this delivery.

Once `playlist_summary` rows exist, keep a compatible playlist/library reader
available for rollback and recovery. A favorite-only application cannot safely
read that feed. If a writer defect appears, stop the affected writer, preserve
authored rows, revisions and receipts, and deploy a compatible fix or rollback
build. The migration refuses schema downgrade because dropping these tables
would destroy user data and retry knowledge. Restoring an older backup has separate
data-loss consequences and requires generation rotation.

## Recorded local evidence: 2026-11-17

After adversarial-review fix `1b0459980`, the solution built, all 223 User Service
tests passed with zero skips, and EF reported no pending model changes. The
request-size test failed with `500` before the fix and returned `413` afterward
through the actual application on Kestrel. The published application's rotation
command was verified during the initial delivery against a disposable database:
it changed generation to UUIDv7, preserved the test playlist and receipt, and
started no listener. No production backup restore or mobile end-to-end exercise
was performed. The build reported existing cryptography dependency advisories
and compiler warnings in unrelated catalog/normalization code.

A one-off diagnostic of `d0c7d3131` used .NET SDK 10.0.100, Debug build, an Apple M4 Pro
(12 CPUs), macOS 27.2.1 and local Docker PostgreSQL 17.10. Its harness and raw output
are not checked in. Each playlist had one segment and repeated the same track
under distinct entry IDs. One owner ran the operations sequentially. Snapshot
samples used a fresh DbContext and included database reads and UTF-8 JSON
serialization. They excluded HTTP, catalog hydration, mobile parsing and rendering.
After one warm-up, five measured reads at each size produced:

- 50 entries: 2.3–3.5 ms per snapshot; 6,170 bytes of JSON.
- 5,000 entries: 22.0–29.5 ms per snapshot; 581,452 bytes of JSON. Offline gzip at
  `CompressionLevel.Fastest` produced 107,561 bytes. Deployed HTTP compression was
  not measured.

Single samples for the 5,000-entry playlist measured 508.7 ms to create,
620.5 ms for a 500-operation metadata batch including its transaction and one
library summary, and 16.9 ms to read/serialize the resulting 500-revision delta
page (55,995 bytes). These observations provide local size and timing examples;
they do not establish latency budgets or production capacity.
