# Stored artist–venue counts

Migration 15 gives each normalized artist/place pair a stable `artist_venue_…`
identity and `artist_show_count`. Admin edits refresh atomically; imports and
normalization refresh after committing their source work. Brief staleness is
acceptable. Public reads never repair counts.

The foundation is [PR #108](https://github.com/RelistenNet/RelistenApi/pull/108).
The stack is normalization enums → artist venues → song counts → v4.
Alec and Switz can review this storage and maintenance contract independently of
the later API/mobile response changes.

## Data ownership

[Migration 15](../../../../RelistenApi/Migrations/15_ArtistVenues.cs) runs
[015_artist_venues.sql](../../../../Relisten.Normalization/Sql/015_artist_venues.sql).
`norm.artist_venues` contains `id`, `artist_uuid`, `venue_id`,
`artist_show_count`, `created_at`, and `updated_at`.

- `(artist_uuid, venue_id)` is unique. IDs use the existing eight-character
  Crockford suffix. A zero-count relationship retains its ID while both parents
  exist; deleting its artist or venue cascades the relationship.
- Count actual catalog shows with accepted `norm.show_venues` assignments,
  resolving venue merges with the 32-node bound. Imported recording venue labels
  do not add membership. Stored counts are independent of rollout approval;
  public reads apply the artist's approval flag.
- `norm.venues.merged_into` owns merge identity. Shows count toward the survivor;
  old relationships remain at zero. Copying redirects into the relationship would
  make deletion depend on a later refresh, so the table has no merge pointer.
- The refresh owns derived rows. It preserves creation time and updates
  `updated_at` only when a count changes. These are materialization timestamps,
  not sync cursors or normalization audit records. Recounts do not rewrite source
  decisions or their provenance.

## Refresh and concurrency

`norm.refresh_artist_venues(artist_filter uuid[] DEFAULT NULL)` returns
`artists_refreshed` and `relationships_changed`. It creates new pairs, updates
changed counts, and zeros existing pairs whose shows disappeared. An explicit
array limits the artist scope; an empty array does nothing. NULL reconciles all
owners found in source assignments or stored relationships.

The function uses the existing `norm-venues` advisory lock:

- Scoped refreshes take it in shared mode, then take exclusive per-artist locks
  in sorted, deduplicated hash-key order. Different artists can proceed together;
  overlapping artists wait. Read source state after the waits.
- Full refreshes and venue writers take the venue lock exclusively. Admin takes
  this lock before source edits. Ordinary readers use committed PostgreSQL
  snapshots and take no advisory locks.
- Locks last until transaction end. A retained transaction must acquire its full
  intended scope before source work. Do not acquire a subset then upgrade to a
  full refresh. `ArtistVenueCounts` uses one autocommitted statement per call.

`SECURITY DEFINER` uses a fixed safe search path and qualified source tables.
Public execution is revoked. The restricted admin role receives function
execution permission rather than direct writes to the derived table.

## Maintenance and recovery

[`ArtistVenueCounts`](../../../../Relisten.Normalization/Venues/ArtistVenueCounts.cs)
provides `RefreshArtistsAsync` and `RefreshAllAsync`, with cancellation and a
30-second command timeout. Import and normalization activities attempt refresh
once their source writes have committed. Database/timeout failures log an
explicit repair need and preserve the completed source work; cancellation still
propagates.

[Admin PR #3](https://github.com/RelistenNet/relisten-admin/pull/3) calls the shared
full refresh inside `withNormRun` for venue edits, before finishing the run.
Source edit, audit run, and counts commit together; refresh failure rolls them
all back. SQL/count/concurrency tests live in this foundation. Admin tests cover
only that orchestration boundary.

After direct SQL repairs or failed post-commit hooks, run:

```sql
SELECT * FROM norm.refresh_artist_venues();
```

This performs database work only. There are no schedules, queues, source-row
triggers, provider calls, or background repair guarantees.

Apply migration 15 and the execution grant before deploying consumers. Old
application versions remain schema-compatible but cannot maintain these rows.
On application rollback, keep the additive schema; refresh affected artists after
restoring maintenance. For an admin failure, retry the original edit after fixing
the database issue. Migrations follow the repository's roll-forward policy.

## Verification

Run the focused suite against disposable databases:

```sh
./test-local.sh --filter 'FullyQualifiedName~TestArtistVenueCounts|FullyQualifiedName~TestNormMigrations'
dotnet csharpier check .
```

Tests cover count/identity lifecycle, no-op timestamps, scope and reconciliation,
merge/unmerge retirement, cascading parent deletion, atomic failure/retry,
restricted execution, migration seeding, and meaningful lock/visibility races.
Migration cases share a disposable-database owner; count fixtures keep their data
local. Concurrency cases observe actual blocking and verify results after commit.

Migration 15 was applied to local `relisten_db`: 5,397 pairs across 12 artists
counted 13,418 shows, with no independent count mismatches. An unchanged recount
preserved IDs, counts, and timestamps. This is local evidence, not production
capacity validation. Production deployment remains separate.
