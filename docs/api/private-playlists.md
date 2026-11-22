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

## Deploy guide content

Ship a mobile build that reads text entries and raise the minimum OTA version
so TestFlight 6055 updates. Then deploy the guide-capable User Service with its
accounts migration. Once text entries exist, recovery builds must still read
their snapshots, deltas and receipts.

## Deploy playlist track validation

Deploy the Catalog API with `playable` on the track entities returned by
`POST /api/v4/catalog/resolve` before deploying the User Service that checks it.
Fresh playlist creation returns `422 track_unavailable` when any submitted track
is unavailable. A fresh `insert_segment` or `insert_entries` operation records
`track_unavailable` without changing the playlist when any of its tracks is
unavailable. Later operations in the same batch can still apply.

A Catalog failure returns `503 catalog_unavailable` before any changes or new
receipts are committed. Retry the same request after Catalog recovers. Existing
receipts, saved playlist references and copies work independently of Catalog.

## Connection routing and sync cost

Ordinary account traffic uses `ACCOUNTS_DATABASE_URL`, with its host replaced by
`PGBOUNCER_WRITE_HOST` when that override is set. Use the RW pooler for account
reads as well as writes: the sync protocol needs a consistent current history,
without replica lag making a client appear ahead of the server.

Startup migrations, EF tools and restore generation rotation use
`ACCOUNTS_MIGRATION_DATABASE_URL`, falling back to the direct accounts connection
when absent. They do not inherit the runtime pooler override. Refresh-token
replay locks use `ACCOUNTS_LOCK_DATABASE_URL` and a separate, direct pool with
`ACCOUNTS_REFRESH_MAX_POOL_SIZE=16` by default. The setting must be a positive
integer; invalid values fail startup. This limit applies per User Service process
and does not limit playlist sync requests. Refreshes hold a lock connection while
OpenIddict uses the ordinary pool, so the pools must remain separate.

Ordinary User Service connections explicitly default to 100 per Npgsql pool;
an explicit maximum in a key/value accounts connection string takes precedence.
Catalog API and worker connections also explicitly use 100. These are client-pool
ceilings, not PostgreSQL backend allocations. Read/write routing, timeout variants
and fallback hosts can create additional pools within a process.

The performance review removed work from the common sync paths:

- An unchanged library poll executes one SQL statement for generation and user
  revision, down from four service statements. This count excludes authentication
  and transaction control. Reads no longer initialize library state or take a
  row lock; one repeatable-read snapshot keeps the head and response coherent.
- A name-only batch can skip loading the playlist structure. It counts live
  tracks only when it publishes a changed summary. A playlist description edit
  loads the live structure to check the total description byte limit.
- Empty ID families skip existence queries. Non-increasing entry counts skip the
  account-wide sum, relying on the unchanged quotas and serialized valid starting
  state. Growing final states still check the owner's aggregate quota.
- A current-head playlist delta skips its history queries. Read transactions end
  after row materialization, before change-envelope JSON is deserialized.
- Repeated unanchored appends of an already-last entry or segment return a no-op
  without allocating and sorting all siblings. Actual reorders retain their
  existing evaluation path.

Owner write locks still enforce shared quotas and contiguous library revisions;
playlist and identity locks still serialize conflicting edits and ID reuse.
They are keyed by owner, playlist or submitted ID, rather than a global sync lock.

The 2026-11-17 live check found User Service connecting directly to PostgreSQL,
with one pod capped at 1 CPU and 512Mi. Both production poolers used transaction
mode with 30 backends per user/database pool and a 1,000-client limit;
PostgreSQL allowed 100 connections. The matching Flux change, `d9b6830` on
`codex/v4-playlist-perf` in `relisten-flux`, prepares the RW route and both network
policy directions. It sets User Service requests to 500m/512Mi and limits to
2 CPU/1Gi; each pooler requests 100m/128Mi with limits of 1 CPU/512Mi.
The follow-up sizing change raises the candidate RW client ceiling to 2,000,
keeps RO at 1,000, and retains 30 backend connections per user/database pair on
each pooler. The User Service manifest explicitly sets the refresh pool to 16.
The follow-up read-only snapshot found RW/RO pooler memory at 44/59Mi and file
descriptor limits at 1,048,576, against candidate memory limits of 512Mi each.
That supports trying the higher client ceiling; peak memory and queueing still
need workload validation. Increasing client admission does not increase the
configured backend pool size. See [PgBouncer configuration](https://www.pgbouncer.org/config).

After ordinary User Service traffic moves through PgBouncer, the shared RW
pooler plus one User Service refresh pool need at most `30 + 16 = 46` primary
connections. Budgeting for two User Service processes gives
`30 + 2 × 16 = 62`, leaving 35 of PostgreSQL's 97 non-superuser slots for other
direct clients and operations. This assumes one RW pooler instance and one
application user/database pair. Extra pooler instances or pairs multiply backend
allocations. The current deployment uses one User Service replica with no surge;
the two-process budget is headroom, not an instruction to change that strategy.

On the 12-CPU, roughly 31Gi app/database node, the candidate requests total
4.733 CPU and 22.37Gi memory including the other workloads. These are reservations,
not measured demand.

These resource values are initial budgets, not a throughput guarantee. Before
applying the manifests, pin a compatible User Service image digest containing
the routing support; the current manifest digest is intentionally unchanged.
No production configuration was changed during this review.

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
directory, supply `ACCOUNTS_MIGRATION_DATABASE_URL` through the operator runtime's
Secret, pointing to the restored primary confirmed above, then run:

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
and [DatabaseConnectionString.ResolveMigrations](../../RelistenUserService/Persistence/DatabaseConnectionString.cs):
`ACCOUNTS_MIGRATION_DATABASE_URL` takes precedence over `ACCOUNTS_DATABASE_URL`,
then `ConnectionStrings:Accounts`, then `DATABASE_URL`. The factory reads JSON
settings relative to the working directory and environment variables. Supply the explicit runtime Secret so a file default
cannot select another database. Keep credentials out of commands and recovery evidence.

### 3. Validate the recovered protocol before reopening traffic

Start an isolated instance with the intended recovery build and recovered
authentication configuration. Normal service startup initializes OAuth and may
run migrations; only the rotation command bypasses that startup. Keep public
access and ordinary writers blocked. Verify that the runtime accounts connection
and any `PGBOUNCER_WRITE_HOST` override also reach the restored database; do not
inherit a pooler that still routes to the former primary. With valid recovered
account credentials,
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
before enabling a web playlist consumer. The separate Flux performance change
prepares database routing and resource budgets; it does not enable that web route
or deploy the playlist API.

Once `playlist_summary` rows exist, keep a compatible playlist/library reader
available for rollback and recovery. A favorite-only application cannot safely
read that feed. If a writer defect appears, stop the affected writer, preserve
authored rows, revisions and receipts, and deploy a compatible fix or rollback
build. The migration refuses schema downgrade because dropping these tables
would destroy user data and retry knowledge. Restoring an older backup has separate
data-loss consequences and requires generation rotation.

## Recorded local evidence: 2026-11-17

The production-aware performance changes passed all 229 User Service tests with
zero skips. The read-interleaving assertions failed when temporarily changed to
read committed, then passed after repeatable read was restored. Local PgBouncer
accepted repeatable-read transactions and transaction-scoped advisory locks.
The solution built and EF found no pending model changes. Flux manifests rendered
and passed strict schema checks, including both Poolers against the installed CRD.
These checks establish the changed behavior and configuration; they do not model
thousands of concurrent clients or qualify production capacity.

An editor-only Release comparison used 500 moves on 5,000 entries, with three
samples after warmup. Already-last, unanchored no-ops fell from a 119.87 ms median
and 86.17MiB allocated to 12.74 ms and 0.054MiB. Actual reorderings measured
152.22 ms before and 157.67 ms after, with allocations essentially unchanged at
105.5MiB. The guard helps the no-op case; this sample does not establish an
improvement for real reorders. The temporary harness and raw results are not
checked in, and the measurements exclude HTTP and database work.

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
