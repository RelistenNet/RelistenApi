# Maintaining derived catalog counts

Use this when adding an import/normalization write, editing canonical data, or
repairing stale venue/song counts. These counts make browsing cheap; source rows
remain authoritative. Counts can briefly lag current membership. Public GETs read
stored values and never repair them.

## What each count represents

Artist-venue counts follow accepted show-level venue assignments through canonical
merges. They count shows, not recordings or imported labels. Each artist/place
pair has a stable identity; a pair that loses all shows retains its identity at
zero while its parents exist. Merges move membership to the survivor without
rewriting saved pair identities.

Canonical song counts are distinct active playable files and their distinct shows
with resolved song parts. Repeated matching parts in one file count once. The
[recording-membership function](../Relisten.Normalization/Sql/016_song_counts.sql)
is shared by recounts and live discovery so they use the same evidence. Imported
song counts instead use setlist evidence matched to catalog shows by artist/date;
that evidence cannot identify a recording. Dates without catalog shows count none.

Stored counts include unapproved artists. Public canonical reads apply approval
separately; a venue's global count sums only approved artists. Count timestamps
describe materialization changes, not semantic source edits or synchronization
cursors. A no-op recount preserves them.

## Which writer refreshes, and when

Imports and normalization attempt refresh after their source work commits. A
count failure logs an explicit repair need without discarding a completed import
or repeating model work. Admin edits include refresh in their edit transaction;
a refresh failure rolls back the source edit and its audit record. Direct SQL
repairs must explicitly refresh the affected scope. There is no periodic repair.

[ArtistVenueCounts](../Relisten.Normalization/Venues/ArtistVenueCounts.cs) and
[SongCounts](../Relisten.Normalization/Songs/SongCounts.cs) are the .NET entrypoints.
Imports/rebuilds and normalization activities call them; the separate admin app
calls the shared SQL functions inside its edit transaction. Keep that ownership
when adding another writer rather than trying to repair during reads.

Venue refreshes take a shared venue gate and ordered per-artist locks. Disjoint
artists can refresh together; overlapping artists serialize. Venue edits and full
venue repair use the gate exclusively. Acquire the complete scope before editing;
upgrading a held shared scope to full scope can deadlock. Song refreshes use the
existing per-artist catalogue lock; a full song repair commits each artist separately.
Readers use committed snapshots and take no advisory locks.

## Repair after a failed refresh or direct edit

Use database artist UUIDs, not public typed IDs. Run as an authorized maintenance
role. For one artist:

```sql
SELECT * FROM norm.refresh_artist_venues(ARRAY['artist-uuid'::uuid]);
SELECT * FROM norm.refresh_song_counts('artist-uuid'::uuid);
```

For a full venue repair, call `norm.refresh_artist_venues()` with no argument.
For all song owners, run this in `psql` with autocommit enabled so each artist's
locks release separately:

```sql
SELECT format('SELECT * FROM norm.refresh_song_counts(%L::uuid);', artist_uuid)
FROM (
    SELECT artist_uuid FROM norm.songs
    UNION
    SELECT a.uuid FROM public.setlist_songs s JOIN public.artists a ON a.id=s.artist_id
) owners
\gexec
```

`SongCounts.RefreshAllAsync` provides the same per-artist commit behavior for .NET
callers. If interrupted, completed artists stay committed and repair can safely
be retried. Do not wrap a full song repair in one transaction when its locks must
release between artists.

After repair, compare stored counts with the relevant membership and revalidate
the affected endpoint. A count correction changes its payload ETag even when
imported timestamps did not change. Initial table installation/backfill is a
migration transaction and can retain locks longer than ordinary scoped repair.
The [SQL definitions](../Relisten.Normalization/Sql/015_artist_venues.sql) own
schema details, grants and lock implementation; keep operational guidance here.
