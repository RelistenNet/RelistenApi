# Stored song discovery counts

Migration 16 stores song totals so v4 browse requests do not rescan recordings
and setlists. Grateful Dead's prototype song list took 5.7 seconds locally;
stored reads later measured a 19 ms median across five warm requests, with the
same payload and ETag. These are local observations, not production load tests.

[PR #111](https://github.com/RelistenNet/RelistenApi/pull/111) sits above the
artist–venue foundation and below v4. [Admin PR #3](https://github.com/RelistenNet/relisten-admin/pull/3)
uses the shared refresh inside its edit transaction.

## Counters and ownership

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

## Shared recording membership

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

## Refresh and failure behavior

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

## Repair and rollout

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
On application rollback, retain the additive tables and explicitly repair counts
after restoring maintenance. Source rows remain authoritative.

## Verification

Run `./test-local.sh --filter 'FullyQualifiedName~TestSongCounts'` against the
disposable test database. Cases cover recording versus imported membership,
no-op/audit/timestamp stability, zero counts, broken-merge recovery, parent
hierarchy, service scope, rebuild failure/retry, permissions, lock/visibility
behavior, and migration backfill. SQL/concurrency coverage stays here; admin
checks only refresh orchestration and its existing transaction rollback.

Local migration/backfill seeded 4,539 canonical and 16,606 imported counters in
10.208 seconds including worker startup. All saved 402 canonical and 557 imported
Grateful Dead counts matched. Normalization update watermarks and audit totals
were unchanged; a repeat recount changed zero rows and preserved counter checksums.
Production deployment and capacity validation remain separate.
