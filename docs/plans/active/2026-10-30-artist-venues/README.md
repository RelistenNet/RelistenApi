# Store artist venue identities and refresh their counts

This living ExecPlan records the foundation for artist-specific venue resources.
Keep Progress, Surprises & Discoveries, Decision Log, and Outcomes & Retrospective
current as implementation and review proceed.

## Purpose and scope

Give every normalized artist/place pair a stable `artist_venue_…` identity and a
stored `artist_show_count`. Admin venue edits refresh counts in the same
transaction; refresh failure rolls the edit back. Imports and normalization attempt
refresh after committing their source work. Relationships retain their IDs at zero
count while their artist and venue exist; deleting either parent deletes the
relationship.

Alec decides the contract; Alec and Switz implement and review it. This base PR
adds storage and refresh behavior only. The later v4 branch can expose
`show.artist_venue.venue`; this PR changes no public API response or mobile model.
The intended stack is `codex/normalization-enums` → `codex/artist-venues` →
`codex/v4-api-c1`. Per-entity sync revisions, sync epochs, and venue timelines are
outside this work.

## Progress

- [x] 2026-10-30: Confirm stored counts and brief staleness as the chosen contract.
- [x] 2026-10-30, Alec: add cascading parent foreign keys and simplify refresh.
- [x] Replace source triggers and the pending queue with direct recounts.
- [x] 2026-10-30, Alec: make admin edits and refresh atomic; use no recurring scan.
- [x] Connect atomic admin refresh and retain best-effort import hooks.
- [x] Verify referential integrity, identity, counts, permissions, and recovery.
- [x] Complete independent review and verify the restacked v4 branch.

## Context and interfaces

`norm.show_venues` records accepted show/place assignments. Join actual catalog
shows and resolve venue merges before counting. A recording's separate imported
venue observation does not create another show membership. Counts include accepted
assignments regardless of `features.norm_catalogue`; public reads apply that flag.

[Migration 15](../../../../RelistenApi/Migrations/15_ArtistVenues.cs) runs the
embedded [DDL](../../../../Relisten.Normalization/Sql/015_artist_venues.sql).
`norm.artist_venues` owns `id`, `artist_uuid`, `venue_id`, `artist_show_count`,
`created_at`, and `updated_at`. The pair is unique. The ID
uses the existing eight-character Crockford suffix and the `artist_venue` prefix.
The refresh preserves creation time and changes update time only when the count
changes. These timestamps describe materialized rows; they are not sync cursors.

Foreign keys reference `artists(uuid)` and `norm.venues(id)`, with `ON DELETE
CASCADE`. A relationship always has both parents. A zero-count pair keeps its ID
if those parents remain. `norm.venues.merged_into` is the sole authority for venue
merges. Counts roll up to that venue's canonical place. An existing pair for the
merged venue stays at zero; a canonical pair is created when actual shows count
toward it. The relationship table stores no copied merge pointer or self-reference.
Unmerging a venue therefore leaves no stale relationship redirect that could
block deletion of a former survivor before the next refresh.

The refresh function owns derived relationship writes; there is no identity or
timestamp trigger. These rows have no human-edit or normalization-run provenance
fields. Their authority is effective show membership. Original decisions and
their audit history remain unchanged. Migration 15 seeds only these derived rows;
their creation time is materialization time, not the first performance date. It
does not add creation timestamps to other normalization tables.

## Refresh data flow

`norm.refresh_artist_venues(artist_filter uuid[] DEFAULT NULL)` directly recomputes
canonical counts, inserts new pairs with shows, and zeros pairs that lost all shows.
It returns `artists_refreshed` and `relationships_changed`. An explicit artist list
limits the refresh. Null refreshes all owners found in current assignments or
stored relationships; an empty list does nothing. Stored relationships are
included so a pair whose final assignment disappeared still reaches zero.

The function joins the existing `norm-venues` advisory transaction lock used by
venue normalization and admin writes. Admin venue operations already take this
lock before editing source rows. Sharing it avoids a second lock order and keeps
merge resolution consistent with the edit being committed. Venue writers and
other refreshes can wait for this lock. Ordinary read queries use PostgreSQL
snapshots and do not acquire it.

The function uses `SECURITY DEFINER` with a fixed safe search path, and its SQL
qualifies source and target tables. Public execution is revoked. A narrow
`EXECUTE` grant lets the existing `relisten_admin` role call this derived refresh
without granting it general write access to `norm.artist_venues`.

[`ArtistVenueCounts`](../../../../Relisten.Normalization/Venues/ArtistVenueCounts.cs)
provides targeted `RefreshArtistsAsync` and full `RefreshAllAsync`. Each performs
one SQL call with a 30-second timeout and cancellation. Import and normalization
activities attempt refresh after their source work commits. Database and timeout
failures are logged and do not fail that completed source work. Cancellation
still propagates. Existing workflow command order stays unchanged; these hooks
live in activity bodies. There is no count workflow, schedule, pending queue, or
source trigger.

In the companion [admin PR #3](https://github.com/RelistenNet/relisten-admin/pull/3),
the centralized `withNormRun` helper calls the full refresh for venue-locked edits
before finishing the run and committing. Edit, run record, and counts commit
together. Any refresh failure rolls back all three. The companion change also
updates `sql/relisten_admin_roles.sql` with the function's execution grant.

Out-of-band SQL changes and failed post-commit hooks have no background repair
guarantee. A later import or normalization operation may refresh them, but no
cadence is promised. An operator can explicitly repair all counts with:

```sql
SELECT * FROM norm.refresh_artist_venues();
```

Run this after a direct SQL repair that affects show membership or venue merges.
It executes database work only, with no provider or model calls.

## Validation and acceptance

Run from the repository root, using disposable databases on the local services:

```sh
dotnet build RelistenApi.sln
./test-local.sh --filter 'FullyQualifiedName~TestArtistVenue|FullyQualifiedName~TestNormMigrations'
dotnet test Relisten.NormalizationTests/Relisten.NormalizationTests.csproj
dotnet csharpier check .
```

Prove that foreign keys reject nonexistent parents and cascade relationship
deletion when a parent is removed. Verify migration seeding, canonical counts,
approval independence, venue merge/unmerge counts, and stable zero/returning IDs
while parents exist. Repeated refresh must preserve unchanged counts and
timestamps. Verify that unmerging and deleting a former survivor succeeds before
the next count refresh. Test explicit refresh after source changes, full refresh
after direct SQL, concurrent venue writes and recounts, and failed refresh rollback
followed by explicit retry. Use the restricted admin role to prove it can call the
function without direct derived-table writes. Through the admin transaction
boundary, prove that a successful edit commits refreshed counts and a forced
refresh failure rolls back the edit, run, and counts together.

After restacking, run existing v4/ID/Swagger contracts to detect unrelated response
regressions. Record actual commands, totals, and review findings here. Earlier
queue and scheduled-workflow test totals do not validate this implementation.

## Rollout and recovery

Migration 15 has not been deployed to production or the restored local preview.
The PR can replace its original DDL; it does not need migration 16 for an
unreleased design. Earlier migration checks used disposable databases.

Apply migration 15 first. It seeds the derived table from existing data before it
becomes visible. Ensure `relisten_admin` has the function's narrow execution grant
using the migration and companion role-provisioning script. Then deploy the admin
transaction change and the API/worker post-commit hooks. The admin build depends
on that function and grant; do not deploy it first. No count schedule registration
or separate worker workflow is required.

Old applications remain schema-compatible but do not refresh the new counts. If
an import hook fails, recover the database issue and run the repair SQL above.
For an admin refresh failure, the edit was rolled back; retry the original admin
operation after recovery.

To roll back application code, retain the additive schema and grants. A reverted
admin build or worker can leave counts stale after later source edits. Run an
explicit full refresh when repairing those counts. This migration follows the
repository's roll-forward policy and has no destructive down migration.

## Surprises & Discoveries

- Existing imported counts already use artist-scoped `GROUP BY` and conditional
  upserts after rebuilds. This foundation follows that eventual-refresh pattern.
- Venue retirement physically deletes unused machine-created venues. Keeping
  orphan relationships was an extra retention policy, not a requirement of stable
  IDs while an artist and venue exist.
- A read-only canonical count query on the restored local snapshot took 52.4 ms
  for 13,418 assignments, 5,478 venues, and 5,397 artist/place groups across 12
  artists. This was a read-only query measurement, not production timing.
- A disposable reduced-schema write experiment copied 4,387 artists, 5,478
  venues, and 13,418 assignments from the local snapshot. The SQL seeded
  5,397 pairs for 12 artists in 318 ms; an unchanged repeat took 123 ms and changed
  no rows. Counts totaled 13,418. Source access was read-only. The disposable
  database and temporary files were removed. These are local measurements on a
  reduced schema, before the switch to the shared lock and security-definer
  execution. They are not final permission/locking or production latency estimates.
- Independent review reproduced a foreign-key failure when deleting a former
  merge survivor after unmerge but before refresh. The relationship's copied
  redirect was stale. Removing that field leaves merge ownership in
  `norm.venues` and removes the deletion dependency on a prior count refresh.

## Decision Log

- 2026-10-30, Alec: store the count on `artist_venues`; brief staleness is acceptable.
- 2026-10-30, Alec: use parent foreign keys with cascading deletes. Retain
  zero-count IDs only while their parents remain.
- 2026-10-30, Alec: refresh admin venue counts atomically with the edit. A refresh
  failure rolls the edit back. Remove recurring scans, source triggers, the
  pending queue, and the count-specific Temporal workflow.
- 2026-10-30: use the existing venue-write advisory lock. Give the restricted
  admin role narrow execution permission on the security-definer refresh function.
- 2026-10-30: preserve best-effort post-commit refreshes for imports and
  normalization. Recover failed hooks or out-of-band edits through explicit SQL.
- 2026-10-30: keep merge authority in `norm.venues`. Store counts and pair identity
  in `artist_venues`, without a derived redirect or self-referencing foreign key.
- 2026-10-30: retain the normalization model and audit semantics. Derived
  identities do not add venue history or a new sync protocol.

## Outcomes & Retrospective

Implementation commits are `d0c71fdfc` (foreign keys and direct recount) and
`8cb329e7a` (atomic admin contract and removal of recurring work). The companion
admin commit is `327f070` in [PR #3](https://github.com/RelistenNet/relisten-admin/pull/3).

Final verification on 2026-10-30:

- Foundation and restacked v4 solution builds passed. Existing dependency-audit
  and nullable-reference warnings remain.
- Database and migration checks: 24 passed, including public service calls,
  cascading deletion, restricted-role execution, atomic source/count rollback,
  and nonblocking reads of committed counts during an uncommitted recount.
- Restacked API, v4, typed-ID, Swagger, normalization, ingestion workflow, and
  catalog checks: 197 passed, two opt-in model/corpus tests skipped.
- Normalization library: 366 passed.
- Admin transaction tests: four passed on a disposable projection of real data.
  The updated bootstrap installed the exact shared migration. Type checking,
  lint, formatting, and whitespace checks passed.
- The existing admin lab-dependent suite was not run because its source template
  databases are absent. The new transaction and permission checks ran separately.
- Independent two-pass review found no unresolved correctness or readability
  issues. The copied-redirect deletion bug found earlier has its own regression.
- All 14 v4 commits rebased unchanged, verified by range comparison. Repository
  CSharpier checks passed.

The foundation adds no v4 response fields; the next v4 change can use these
stored relationships for `artist_venue` resources and artist-specific counts.

Review [PR #108](https://github.com/RelistenNet/RelistenApi/pull/108) for the base
change. Production, real schedules, and the restored preview database remain
outside the implementation's mutation scope. All migration and count-write checks
use disposable databases.
