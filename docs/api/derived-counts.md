# Derived venue and song counts

Migration 15 materializes artist/place relationships; migration 16 stores song
discovery counts. Public GETs read these values and never recount. Brief lag
after imported or normalization work is acceptable. Admin edits refresh inside
the edit transaction; a refresh failure rolls the edit back. There is no scheduled
repair or per-source-row trigger.

## Artist–venue relationships

### Data ownership

[Migration 15](../../RelistenApi/Migrations/15_ArtistVenues.cs) runs
[015_artist_venues.sql](../../Relisten.Normalization/Sql/015_artist_venues.sql).
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

### Refresh and concurrency

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

### Maintenance and recovery

[`ArtistVenueCounts`](../../Relisten.Normalization/Venues/ArtistVenueCounts.cs)
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

Apply migration 15 and its grants before deploying consumers. Old writers do not
maintain these rows. After an admin failure, fix the database issue and retry the
edit. Fix forward while retaining stable relationship IDs and source decisions.

## Song discovery

### Counters and ownership

- `norm.song_counts`, keyed by `song_id`, stores `matched_track_count` and
  `matched_show_count`: distinct active files and catalog shows with a resolved
  part linked to the surviving artist-owned song.
- `public.setlist_song_counts`, keyed by `setlist_song_id`, stores
  `setlist_show_count`: distinct catalog shows supported by the imported song's
  setlist evidence, matched by artist and date.

Both keys have cascading parent foreign keys. Counters are nonnegative and
retain zero values while their parents exist. `created_at` is materialization
time; `updated_at` changes only when a count changes. Recounts update derived
rows, without rewriting accepted normalization decisions or their audit history.
There is no extra counter identity, artist key, or per-track count table.

Imported evidence never identifies a playable file. Duplicate setlists on a date
do not duplicate one catalog show; distinct shows sharing that date remain
separate. A setlist date without a catalog show contributes no result or count.
The imported counter therefore matches imported detail's show collection when
maintenance is current, and can differ from the canonical recording count.

### Shared recording membership

`norm.song_recording_matches(artist_uuid, canonical_ids)` serves recounts and
live file/show discovery. It resolves same-artist merges within 32 nodes, includes
resolved parts in non-orphaned files, and validates source/set/show/artist parents.
An invalid canonical link in another resolved part invalidates the whole file.
Unresolved parts may coexist with matched parts but establish no match themselves.

Each file appears once per canonical song, with ordered matching part ordinals.
Shows are deduplicated across files and recordings. Merged-away songs have zero
counts; valid matches roll up to the survivor. Broken chains contribute none.
Stored counts are independent of rollout approval. V4 applies `norm_catalogue`
before exposing canonical discovery; imported reads work without normalization.

### Refresh and failure behavior

`norm.refresh_song_counts(artist_uuid)` refreshes both counter families for one
artist. It creates counters for current parents, updates changed values, and
zeros vanished memberships. Its safe security-definer function grants admin
execution without direct counter writes.

The function reuses the existing artist lock before reading source state:

```sql
SELECT pg_advisory_xact_lock(
    hashtext('norm-catalogue'), hashtext(artist_uuid::text)
);
```

Same-artist edits/recounts serialize; different artists proceed independently.
Ordinary readers take no advisory locks. `SongCounts.RefreshArtistsAsync` and
`RefreshAllAsync` commit each artist separately. Cancellation or failure leaves
earlier completed artists committed; retrying is safe. Migration seeding is a
one-time exception: its DDL and backfill share a transaction and retain locks
until commit. No global gate, queue, schedule, or source-row trigger is added.

- **Imports:** the shared `CatalogRebuildService.InvalidateArtistCache` refreshes
  after successful Redis invalidation, covering legacy and Temporal rebuilds once.
  Source work already committed. Redis failure throws before recount and can be
  retried; SQL/timeout failure logs an explicit repair need without undoing imports.
- **Normalization:** `SongActivities` refreshes after committed catalogue chunks,
  forgetting decisions, and track matching. SQL/timeout failures do not repeat
  paid model work. Cancellation propagates from both maintenance paths.
- **Admin:** `withNormRun` holds the same artist lock before editing and refreshes
  before commit. Source edit, audit run, and counters commit or roll back together.
- **Direct SQL or failed hooks:** operators explicitly refresh affected artists.
  There is no promised background repair. GETs never repair counts, so stored
  values can briefly lag live discovery. Count corrections invalidate response
  ETags even when source metadata timestamps did not change.

### Repair and rollout

For one artist:

```sql
SELECT * FROM norm.refresh_song_counts('artist-uuid'::uuid);
```

For all owners in `psql`, keep autocommit enabled; do not wrap these generated
statements in `BEGIN` if locks must release after each artist:

```sql
SELECT format('SELECT * FROM norm.refresh_song_counts(%L::uuid);', artist_uuid)
FROM (
    SELECT artist_uuid FROM norm.songs
    UNION
    SELECT a.uuid FROM public.setlist_songs s JOIN public.artists a ON a.id=s.artist_id
) owners
\gexec
```

Apply migration 16 and grants before deploying hooks/admin, then switch v4 reads.
Fix forward while retaining the tables and explicitly repair counts after
restoring maintenance. Source rows remain authoritative.


## Verification and initial migration

Venue tests cover identity/count lifecycle, no-op timestamps, merges, cascading
parents, scoped refreshes, atomic failure/retry, restricted execution and actual
concurrent blocking/visibility. Song tests cover recording versus setlist
membership, invalid parent/merge recovery, scope, no-op timestamps, permissions,
post-commit failure/retry and migration seeding. Admin tests cover transaction
orchestration rather than duplicating SQL/concurrency fixtures.

```sh
./test-local.sh --filter 'FullyQualifiedName~TestArtistVenueCounts|FullyQualifiedName~TestSongCounts|FullyQualifiedName~TestNormMigrations'
```

Migration DDL and initial backfills share a transaction; they retain locks until
commit. Normal full song repairs commit each artist separately. The local real
catalog backfills were compared with independently derived venue memberships
and saved song counts without mismatches. This does not establish production
backfill duration or load. See [deployment](deployment.md) for writer coordination.
