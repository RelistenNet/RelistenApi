# Private playlist API and recovery

The delivery-1 private API is implemented and locally verified. It has not been
deployed or qualified with the mobile playlist consumer. The
[frozen contracts](../plans/active/2026-11-16-v4-playlist-contracts.md) specify
payloads and outcomes; the [delivery plan](../plans/active/2026-11-06-v4-playlists.md)
records decisions and later work.

## Implemented behavior

`accounts.relisten.net/v1/playlists` supports create, active/archived lists,
owner snapshots, whole-revision changes, ordered operation batches and archive
commands. It uses the existing library read/write authorization policy and browser
CSRF/Origin protections. Snapshots and deltas contain authored track references
only. Catalog resolution stays with `POST /api/v4/catalog/resolve`.

`PlaylistWriteService` owns transactions. It locks submitted receipt/created IDs,
then the owner's library state, then the playlist. `PlaylistEditor` evaluates
ordered operations in detached state. Only after final quotas pass does the writer
persist that evaluated state, revision envelopes, receipts and one library summary.
Semantic rejections are receipted and siblings continue; infrastructure failures
roll back the entire transaction. Archived playlists accept all-replay batches but
refuse batches containing new operations.

Playlist reads use repeatable-read transactions. Library reads retain their
existing library-state share lock, which playlist and favorite writers respect.
Live ranks use bytewise ordering and partial unique indexes. The writer releases
affected old ranks to NULL inside the transaction before assigning final ranks;
no committed live row produced by the writer has a NULL rank. Tombstones and
compact receipts are retained for the playlist's lifetime. Revision compaction
is not enabled.

## Restore the User Service database

A restore can reuse playlist and library revision numbers for different content.
Always rotate `user_data.history_generation` after a backup/PITR restore, before
ordinary client traffic or writers resume. Do not rotate it during normal restarts
or deploys. After rotation, an isolated service instance may accept the controlled
recovery-validation requests below while public access and ordinary writers remain
blocked.

For a planned restore/rehearsal, capture a test account's library `next_cursor`,
generation, playlist snapshot, and exact successful create-command request before
the backup. That command's receipt must be included in the backup. Retain these
probes with the recovery evidence so step 3 can exercise the restored state. During
an incident without those probes, rotate anyway, verify the changed database
generation and fresh snapshots, and record the old-cursor/receipt probes as not
performed.

1. Stop User Service traffic and all writers. Keep them stopped throughout the
   database restore and any required forward migrations.
2. In the published User Service directory, with `ACCOUNTS_DATABASE_URL` supplied
   through the operator runtime's Secret and pointing to the restored database, run:

   ```sh
   dotnet RelistenUserService.dll rotate-history-generation --traffic-stopped
   ```

   The command updates only the singleton to a fresh UUIDv7, prints the new
   non-secret generation, and exits without starting a listener. It fails if the
   singleton is missing. `--traffic-stopped` acknowledges the operator prerequisite;
   the command cannot stop traffic itself. Do not paste database credentials into
   the command or recovery report.
   Its connection selection is the existing `DatabaseConnectionString.Resolve`:
   `ACCOUNTS_DATABASE_URL`, then `ConnectionStrings:Accounts`, then `DATABASE_URL`.
   It does not initialize OAuth providers or start the normal application.
3. Verify that the generation changed and authored rows/receipts present in the
   restored backup remain intact. Receipts newer than that backup may be lost.
   Start an isolated instance for the recovery client, with public traffic and
   ordinary writers still blocked. Using the retained test-account probes, check
   that an old protected library cursor returns `410 sync_cursor_expired`,
   even when the current library revision equals the old revision. Verify that a
   fresh playlist snapshot carries the new generation and replaying the retained
   create command returns its original outcome with that new generation. Use valid
   test-account credentials under the recovered authentication configuration.
4. Once the database's other recovery checks have passed, including any restored
   credential invalidation required by that recovery, reopen traffic. Rotation does not
   invalidate restored credentials or recover edits newer than the backup.

Clients compare generation before applying a response. On mismatch they retain
pending intent, discard old bases/cursors, and follow the frozen restore recovery
sequence. The API cannot recover edits already retired from every client and absent
from the backup. Account deletion and deletion-tombstone recovery remain deferred.

## Rollout and rollback

Apply the additive migration with the service's normal migration process. It
preserves existing favorites. Old library cursors intentionally expire because the
new protected payload includes history generation; clients must fetch a snapshot.

For initial enablement, ship a mobile library reader that understands
`playlists`, `playlist_summary`, generation-aware snapshots/cursors, and unknown
future change types. Qualify lost-create retries, interleaved receipt/delta pulls,
quota correction, archived outboxes, and restore recovery through the real mobile
path. The API implementation does not establish this client qualification.

The checked-in Flux account-host ingress already sends all paths to the User
Service. Same-origin production browser calls through `relisten.net` need exact
`/v1/playlists` and segment-safe `/v1/playlists/` routes to the User Service, matching
the existing library ingress rules, before enabling a web playlist consumer.
The application browser boundary is implemented here; no Flux resources were
changed or deployed for this delivery.

After initial enablement, retain that compatible client/server protocol during
recovery as well. Once `playlist_summary` rows exist, a favorite-only application is an unsafe rollback
target. Keep a previous compatible playlist/library reader build available. If a
writer defect appears, stop that writer while preserving authored rows, revisions
and receipts, then deploy a compatible fix or rollback build. The migration refuses
schema downgrade because dropping these tables would destroy retry knowledge and
user-authored data. Restoring an older backup is a separate recovery action with
its own data-loss consequences and mandatory generation rotation.

## Verification and local capacity evidence

Run the User Service suite directly:

```sh
./start-local-databases.sh
dotnet test RelistenUserServiceTests/RelistenUserServiceTests.csproj
dotnet build RelistenApi.sln
dotnet csharpier check .
```

Check that PostgreSQL integration cases actually ran. The fixture skips them if it
cannot create a disposable database; `./test-local.sh` runs the catalog suite.
The playlist fixture exercises real MVC JSON, controllers, services, migrations,
constraints and transactions; it substitutes identity authentication. Existing
authorization/browser tests cover the shared policy and route boundaries.

On 2026-11-17, implementation `d0c7d3131` passed the solution build, all 223
User Service tests with zero skips, and a final 10-case API run after strengthening
the last-entry move scenario. EF found no pending model changes. Two independent
code/readability review passes reported no remaining findings. The recovery command
was also invoked through the published application's entrypoint against a disposable
database: it changed generation to UUIDv7 while preserving the playlist and receipt.
These checks include simulated restored revision reuse; no production backup restore
or mobile end-to-end exercise was performed.

A one-off local diagnostic used .NET SDK 10.0.100, Debug build, an Apple M4 Pro
(12 CPUs), macOS 27.2.1 and local Docker PostgreSQL 17.10. Each playlist had one
segment and repeated the same track under distinct entry IDs. Snapshot samples
used a fresh DbContext, included database reads and UTF-8 JSON serialization,
and excluded HTTP, catalog hydration, mobile parsing and rendering. One warm-up
preceded five measured reads at each size.

- 50 entries: 2.3–3.5 ms per warm snapshot; 6,170 bytes of JSON.
- 5,000 entries: 22.0–29.5 ms per warm snapshot; 581,452 bytes of JSON.
  Offline gzip at `CompressionLevel.Fastest` produced 107,561 bytes; this does
  not claim that the deployed HTTP response is compressed.
- Creating the 5,000-entry playlist took 508.7 ms. A 500-operation metadata batch
  against it took 620.5 ms, including the transaction and one library summary.
  Its 500-revision delta page took 16.9 ms to read/serialize and was 55,995 bytes.

These are local measurements, not latency budgets or production load qualification.
Before release, name budgets for API latency, owner-lock contention and mobile first
render on representative devices, then measure them. The local results supply no
reason to add a second catalog cache or change the frozen paging contract.

The solution build passed with existing cryptography dependency advisories and
compiler warnings in unrelated catalog/normalization code.
