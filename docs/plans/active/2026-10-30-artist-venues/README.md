# Store artist venue identities and refresh their counts

This living ExecPlan records the foundation for artist-specific venue resources.
Keep Progress, Surprises & Discoveries, Decision Log, and Outcomes & Retrospective
current as implementation and review proceed.

## Purpose and scope

Give every normalized artist/place pair a stable `artist_venue_…` identity and a
stored `artist_show_count`. Imports, normalization, and admin edits can briefly
leave counts behind the catalog. Background refresh must converge without losing
requests or changing an existing relationship's ID.

Alec decides the contract; Alec and Switz implement and review it. This base PR
adds storage and refresh behavior only. The later v4 branch can expose
`show.artist_venue.venue`; this PR changes no public API response or mobile model.
The intended stack is `codex/normalization-enums` → `codex/artist-venues` →
`codex/v4-api-c1`. Per-entity sync revisions, sync epochs, and venue timelines are
outside this work.

## Progress

- [x] 2026-10-30: Confirm stored counts and brief staleness as the chosen contract.
- [x] Implement migration, typed identity, and durable pending refreshes.
- [x] Connect best-effort hooks and scheduled retry/reconciliation.
- [x] Verify migration, count ownership, identity, concurrency, and failure recovery.
- [x] Complete independent review and prepare the base PR.
- [x] Restack v4 and verify its routes against the foundation.

## Context and interfaces

`norm.show_venues` records accepted show/place assignments. Join actual catalog
shows and resolve venue merges before counting. A recording's separate imported
venue observation does not create another show membership. Counts include accepted
assignments regardless of `features.norm_catalogue`; public reads apply that flag.

[Migration 15](../../../../RelistenApi/Migrations/15_ArtistVenues.cs) runs the
embedded [DDL](../../../../Relisten.Normalization/Sql/015_artist_venues.sql).
`norm.artist_venues` owns `id`, `artist_uuid`, `venue_id`, `artist_show_count`,
`created_at`, `updated_at`, and nullable `merged_into`. The pair is unique. The ID
uses the existing eight-character Crockford suffix and the `artist_venue` prefix.
Creation time stays fixed; refresh changes update time only when count or redirect
changes. These timestamps describe materialized rows; they are not sync cursors.

Keep a row when its count becomes zero. A venue merge creates or reuses the
surviving pair, sets the old pair's count to zero, and redirects it within the same
artist. Parent artist and venue references deliberately have no foreign keys:
existing catalog rebuilds and normalization retirement can physically remove those
parents. Their disappearance must not destroy relationship identity or prevent
retirement. A relationship row does not guarantee that its parent metadata remains
available. Public visibility and missing-parent behavior belong to the later API.

The derived rows have no human-edit or normalization-run provenance fields. Their
authority is effective show membership. Original decisions and their audit history
remain unchanged. Migration 15 seeds only these new derived relationships; their
creation time is materialization time, not the historical first performance date.
It does not add creation timestamps to other normalization tables.

## Refresh data flow

Source-table statement triggers enqueue affected artist UUIDs in
`norm.artist_venue_refresh_queue` inside the source transaction. They do not count
shows. They cover catalog show insertion/deletion/identity changes, normalized
assignment changes, artist identity removal, and venue merge, retirement, or
restoration. This also covers the admin app's independent SQL writes without
requiring a new admin build.

`norm.request_artist_venue_refresh(artists uuid[] DEFAULT NULL)` requests explicit
artists; null requests every owner found in assignments or stored relationships.
`norm.refresh_artist_venues(artist_filter uuid[] DEFAULT NULL, batch_size int
DEFAULT 100)` returns `artists_refreshed` and `relationships_changed` for one batch.
It claims pending artists with `FOR UPDATE SKIP LOCKED`, recomputes their groups,
upserts changed counts/redirects, zeros vanished groups, and acknowledges those
queue rows in the same short transaction. Other artists can refresh concurrently.

Queue insertion uses `ON CONFLICT DO UPDATE` to take the pending row's lock. A
source writer overlapping a claimed refresh waits at that mark, then leaves a new
request after the refresh commits. `DO NOTHING` could lose the overlapping mark.
This is brief serialization for the same artist, not a global writer lock or a
source transaction running the whole background job. Failed or canceled refreshes
roll back both materialization and acknowledgment.

[`ArtistVenueCounts`](../../../../Relisten.Normalization/Venues/ArtistVenueCounts.cs)
provides `RequestAsync`, one-batch `RefreshPendingAsync`, and finite targeted
`RefreshArtistsAsync`. SQL calls are cancellable and have a 30-second timeout.
Import and normalization activities attempt refresh after their source work;
database/timeout failures are logged and do not fail that completed source work.
Cancellation still propagates. Busy or failed artists remain pending.

[`ArtistVenueRefreshWorkflow`](../../../../Relisten.Normalization/Temporal/ArtistVenueRefreshWorkflow.cs)
uses retryable database activities. Its default request drains at most 20 batches
of 100 artists. An empty unlocked batch means no work was claimed, not that every
pending writer has finished. A later scheduled run handles remaining work.

The worker registers `relisten-artist-venues-refresh` every minute UTC and
`relisten-artist-venues-reconcile` at 11:00 UTC daily. Reconciliation first queues
all known owners, repairing a missed mark. Registration preserves existing saved
schedules and their paused state. No provider or model calls occur in either job.
Existing ingestion workflow command order is unchanged; hooks live in activity
bodies so existing Temporal histories can replay.

## Validation and acceptance

Run from the repository root, using disposable databases on the local services:

```sh
dotnet build RelistenApi.sln
./test-local.sh --filter 'FullyQualifiedName~TestArtistVenue|FullyQualifiedName~TestNormMigrations'
dotnet test Relisten.NormalizationTests/Relisten.NormalizationTests.csproj
dotnet csharpier check .
```

Prove that migration seeds existing memberships, counts ignore duplicate sources
and approval flags, merges redirect within an artist, and zero/returning groups
keep their IDs. Repeating refresh must preserve unchanged counts and timestamps.
Direct admin-style SQL must enqueue work with existing writer privileges. Test
show deletion and orphaned assignments, concurrent marks during a refresh, failed
refresh rollback followed by retry, and workflow retry/batch-limit behavior.
After restacking, run the existing v4/ID/Swagger contracts to detect unrelated
response regressions. Record actual commands, totals, and review findings here;
implementation is not proof that a check passed.

## Rollout and recovery

Apply migration 15 before deploying the new worker. Migration seeds the new table
from existing data before it becomes visible. Deploy workers with the new service,
activities, and workflow, then deliberately run `--register-schedules`; new
schedules are active immediately. Inspect their next actions before leaving them
unattended. No migration or schedule registration is part of read-only review.

Old workers remain compatible with the additive schema: source triggers leave
pending work until a new worker handles it. Refresh failure does not discard the
queue. Retry the refresh schedule; trigger reconciliation when rebuilding all
stored counts is intended. `NormalizationClient.RefreshArtistVenuesAsync` also
starts a manual run programmatically; no dedicated CLI flag is added.

To roll back application code, pause the two new schedules before stopping their
workers. Retain the schema and pending queue; old writers continue to enqueue.
Resume with a schema-compatible worker and reconcile. This migration follows the
repository's roll-forward policy and has no destructive down migration.

## Surprises & Discoveries

- Existing imported counts already use artist-scoped `GROUP BY` and conditional
  upserts after rebuilds. This foundation follows that eventual-refresh pattern.
- `norm.show_venues` has no foreign key to catalog shows, and `norm.changes` omits
  derived assignment writes. Neither cascading deletes nor the audit log alone
  can supply the pending work required here.
- Independent review found that audit restoration inserts a deleted venue rather
  than updating it. An INSERT trigger now queues retained relationship owners;
  the regression test proves a restored merge redirects the original pair ID.

## Decision Log

- 2026-10-30, Alec: store the count on `artist_venues`; brief staleness is acceptable.
- 2026-10-30: use a durable pending-artist queue with periodic reconciliation,
  rather than recounting inside source transactions or serializing all writers.
- 2026-10-30: retain the existing normalization model and audit semantics. New
  derived identities are not a venue-history repair or a new sync protocol.

## Outcomes & Retrospective

Implementation commit: `3b7580020`. The foundation adds no v4 response fields.
The next v4 change can use these stored rows for `artist_venue` resources and
their artist-specific counts.

Validation on 2026-10-30:

- Solution builds passed in both the foundation and restacked v4 worktrees.
  Existing dependency-audit and nullable-reference warnings remain.
- Focused database and migration checks: 18 passed on disposable databases.
- Broader normalization, Swagger, ingestion workflow, and catalog checks: 127
  passed; two opt-in model/corpus tests skipped.
- Normalization library: 366 passed.
- Restacked v4 plus the broader API/database/workflow checks: 184 passed, two
  opt-in model/corpus tests skipped. Typed catalog ID checks: 15 passed.
- Repository CSharpier and whitespace checks passed on both branches.
- Independent two-pass review confirmed and resolved the restoration gap above;
  no actionable findings remain. The simplification pass retained the direct
  SQL/service/activity responsibilities without adding another abstraction.

The v4 branch rebased cleanly onto the implementation commit. Range comparison
confirmed that its 13 commits carried over unchanged. The combined suite exposed
an existing v4 assertion that assumed no other fixture had added artists. A
separate v4 test commit scopes that assertion to all five controlled fixture IDs,
preserving membership and ordering checks. No API behavior changed.

Review the [foundation branch comparison](https://github.com/RelistenNet/RelistenApi/compare/codex/normalization-enums...codex/artist-venues)
for the base PR's complete diff. No production database, schedules, or deployment
changed. The restored local preview database was also left untouched; all
migration/count mutation checks used disposable databases.
