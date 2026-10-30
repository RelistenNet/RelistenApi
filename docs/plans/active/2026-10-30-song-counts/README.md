# Store song discovery counts

Store song counts during catalog maintenance so the v4 artist song list reads
counters instead of scanning recording matches on every request. This foundation
sits above the artist–venue count branch and below v4. Alec and Switz can review
the schema, refresh ownership, and failure behavior here without following the
API conversation.

The local v4 prototype took 5.7 seconds to return Grateful Dead's song list.
Canonical recording counts accounted for about 3.9 seconds; imported setlist
counts added about 1.4 seconds. These are individual local measurements, not a
production load test. Persist both count families; persisting only canonical
counts would leave an expensive imported browse path.

## Data and count meaning

Migration 16 adds two derived tables:

| Table and key | Stored counters | Meaning |
| --- | --- | --- |
| `norm.song_counts.song_id` | `matched_track_count`, `matched_show_count` | Distinct active playable files and catalog shows with a resolved part linked to the surviving artist-owned song. |
| `public.setlist_song_counts.setlist_song_id` | `setlist_show_count` | Distinct catalog show IDs supported by this imported song's setlist evidence, matched by artist and date. |

Each key is also a foreign key with `ON DELETE CASCADE`. There is no separate
counter identity or duplicated artist key. Counts are nonnegative. Rows retain
zero values while their parents exist. Both tables have `created_at` and
`updated_at`; a no-op recount preserves both, and only a changed count advances
`updated_at`.

The imported count can differ from the normalized count. Setlist evidence alone
does not identify a playable file. Multiple setlists on one date do not duplicate
a catalog show, but distinct catalog shows sharing that date remain distinct.
A setlist date without a catalog show contributes no result or count. Match the
count to the imported detail route's complete show collection.

These counters are derived state, not normalization decisions. Updating them
must not rewrite `norm.songs` provenance or create moderator/model audit changes.
No additional per-track count table or materialized song–file relationship is
needed: `norm.track_songs` already stores the ordered interpretation.

## One recording membership definition

`norm.song_recording_matches(artist_uuid, canonical_ids)` supplies the membership
used by the recount and v4 file/show discovery. It resolves same-artist song
merges with the existing 32-node bound, includes resolved parts in non-orphaned
files, and validates source/set/show/artist parents. If another resolved song
part has an invalid canonical link, the whole file is unavailable and contributes
no discovery result. Unresolved parts can coexist with a valid matched part, but
do not establish matches themselves.

A file is returned once for each matched canonical song, with its sorted matching
part ordinals. Count files rather than parts, and deduplicate shows across files
and recordings. Merged-away songs have zero stored counts; the surviving song
receives their valid matches. Broken merge chains contribute none.

Rollout approval does not change stored counts. V4 checks the artist's existing
`norm_catalogue` flag before exposing canonical song discovery. Imported song
reads continue to work without normalization.

## Refresh ownership and locking

`norm.refresh_song_counts(artist_uuid)` recomputes both count families for one
artist in one transaction. It upserts changed values and zeros rows whose last
match disappeared. It does not create artists, songs, or imported evidence.
The function uses a fixed safe search path and grants the restricted admin role
execution access without direct counter write permission.

The only advisory lock is the artist's existing catalogue lock:

```sql
SELECT pg_advisory_xact_lock(
    hashtext('norm-catalogue'), hashtext(artist_uuid::text)
);
```

Song normalization and admin song edits already use this lock. Refreshes reuse
it before reading source state; no new global lock or full-refresh gate is added.
Two different artists can refresh concurrently. Same-artist refreshes and edits
serialize. Ordinary readers use PostgreSQL snapshots and take no advisory locks.
An advisory hash collision can serialize unrelated artists but cannot mix data.

`SongCounts.RefreshArtistsAsync` processes the requested artists in separate
autocommitted statements. `RefreshAllAsync` enumerates live owners from normalized
and imported song rows and uses the same operation. It does not hold every
artist's lock until a full repair finishes. Cancellation stops further artists;
previously completed artists remain committed. Rerunning the operation is safe.

Migration seeding is a one-time exception: the migration's DDL and backfill share
its transaction, so acquired artist locks last until migration commit. Measure
that deployment step separately from normal targeted refreshes.

## Write paths and failures

- **Imports and rebuilds:** `CatalogRebuildService.InvalidateArtistCache` runs the
  song refresh after cache invalidation. Both legacy importers and Temporal's
  final catalog activity use this operation, so one completed rebuild refreshes
  song counts once. Source writes have already committed. Database or timeout
  failures are logged and leave the successful import intact. Cancellation still
  propagates. Redis failure preserves existing behavior: invalidation throws
  before recount; Temporal can retry that activity.
- **Song normalization:** `SongActivities` refreshes the artist after committed
  catalogue chunks, forgetting decisions, and matching tracks. A recount failure
  is logged without repeating paid model work. Cancellation still propagates.
- **Admin song edits:** `withNormRun` takes the same artist lock before editing
  and refreshes counts before finishing the run and committing. Refresh failure
  rolls back the edit, run record, and derived counters together. Venue edits
  retain their separate artist–venue refresh path.
- **Direct SQL and failed post-commit hooks:** an operator must refresh affected
  artists explicitly. There is no schedule, queue, source-row trigger, or promised
  background repair cadence.

Counts may briefly lag live recording or setlist membership after imports or
out-of-band edits. Public GETs never repair counts. ETags cover represented
stored values, so a count correction changes affected responses even when
imported or canonical metadata timestamps stay unchanged.

## Repair

Repair one artist with:

```sql
SELECT * FROM norm.refresh_song_counts('artist-uuid'::uuid);
```

For a full repair in `psql`, keep autocommit enabled and run one statement per
owner. `\gexec` executes each generated statement separately; do not wrap this
operation in `BEGIN` if separate artist commits are required.

```sql
SELECT format('SELECT * FROM norm.refresh_song_counts(%L::uuid);', artist_uuid)
FROM (
    SELECT artist_uuid FROM norm.songs
    UNION
    SELECT a.uuid FROM public.setlist_songs s
    JOIN public.artists a ON a.id = s.artist_id
) owners
ORDER BY artist_uuid
\gexec
```

## Verification and rollout

Verify the foundation through the SQL refresh and C# service boundaries:

- Counts match exact file/show membership and imported detail membership, including
  repeated song parts, multiple recordings, partial interpretation, duplicate
  setlists, orphan files, merges, broken chains, and transition to zero.
- Unchanged recounts preserve timestamps and produce no normalization audit edits.
  Parent deletion cascades derived rows.
- Same-artist refreshes wait and read the committed edit after waiting; disjoint
  artist refreshes proceed independently. Readers see the last committed counts
  while a recount is uncommitted.
- The restricted admin role can recount without counter write grants. A forced
  recount failure rolls back its source edit and moderator run.
- V4 uses stored counts without changing response field meanings; count changes
  invalidate ETags, and reading a resource never mutates stored counters.

Apply migration 16 before deploying consumers. Then deploy the refresh hooks and
admin change before switching v4 reads to counters. An older application remains
schema-compatible but cannot maintain these new rows after later writes. If a
hook or admin deployment is rolled back, rerun affected artists after restoring
maintenance. Keep the additive counter tables during an application rollback;
source normalization and imported data remain authoritative.

### Verified locally on 2026-10-30

- 51 foundation checks passed, including 14 song-count SQL/service/migration and
  concurrency cases plus existing artist–venue and rebuild checks. The shared
  rebuild completion test covers failed refresh and explicit retry.
- The admin companion passed 38 targeted song, venue, and pure checks, plus
  formatting, lint, and type checks. Its broader lab-dependent suite was not run
  because the template databases were unavailable.
- Two independent correctness/readability reviews found no unresolved issues.
- The official worker applied migration 16 to local `relisten_db` and seeded
  4,539 canonical and 16,606 imported counters in 10.208 seconds, including worker
  startup. Every existing song parent received a counter row.
- All 402 canonical and 557 imported Grateful Dead counts matched the saved
  pre-change live responses. Normalization row counts, update watermarks, and
  audit totals/watermarks were unchanged.
- A repeated Grateful Dead refresh changed zero rows. Ordered checksums of both
  complete counter tables, including timestamps, stayed unchanged.

The local v4 follow-up measures the stored reads after restacking this foundation.
These checks did not deploy code or change production data.
