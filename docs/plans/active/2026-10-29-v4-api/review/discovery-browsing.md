# Review date and recent browsing

The local API now has 39 operations: 38 GETs and saved-reference resolver POST.
This batch adds 13 discovery operations. `artist.features` restores the familiar
name for the existing flags; `features.normalization` still reflects approved
normalization reads. There is no parallel `capabilities` property.

## Follow a date into playback

1. Open [Grateful Dead on July 7 across all years](http://127.0.0.1:43823/api/v4/artists/artist_3qmp7zjbg1rpebhtyfy43096vj/shows/on-date?month=7&day=7).
   These are ordinary show summaries. Imported venues remain readable, and
   approved normalized links appear under `artist_venue`.
2. Open [the exact 1978-07-07 show](http://127.0.0.1:43823/api/v4/artists/artist_3qmp7zjbg1rpebhtyfy43096vj/shows/by-date/1978-07-07).
   It returns the same complete recording hierarchy as direct show-ID lookup.
   Follow a source and track as in the playback review; every track retains its
   imported title and `normalization` object.
3. Compare [all artists on July 7](http://127.0.0.1:43823/api/v4/shows/on-date?month=7&day=7).
   Artists are referenced by `artist_id` into the separately loaded artist list.
4. Open [today across all years](http://127.0.0.1:43823/api/v4/shows/today).
   Defaults come from the database session date. Explicit `month`/`day` overrides
   are available here and on the artist-scoped `today` route. `/on-date` requires
   both components; February 29 is valid, February 30 returns 422.

Exact lookup uses the imported display label, so partial labels remain valid
lookups. It does not guess a complete calendar date from `1995-12-xx`.

## Compare the lists

- [Recently added recordings](http://127.0.0.1:43823/api/v4/artists/artist_3qmp7zjbg1rpebhtyfy43096vj/shows/recently-added?limit=5)
  uses the newest source creation timestamp per show. Change `sort_by=updated_at`
  to use the newest source update timestamp instead. Both values are listed in
  Swagger. `previous_days` filters this timestamp; `limit` still applies.
- [Recently updated imported shows](http://127.0.0.1:43823/api/v4/artists/artist_3qmp7zjbg1rpebhtyfy43096vj/shows/recently-updated?shows=5)
  follows the existing imported show-update meaning. A normalization-only
  correction changes metadata and its ETag without promoting a show in this list.
- [Most recently performed](http://127.0.0.1:43823/api/v4/artists/artist_3qmp7zjbg1rpebhtyfy43096vj/shows/recently-performed?shows=5)
  sorts by imported performance date. `days` selects a window and overrides
  `shows`, preserving the old route behavior.
- [Top rated](http://127.0.0.1:43823/api/v4/artists/artist_3qmp7zjbg1rpebhtyfy43096vj/shows/top?limit=5)
  uses the highest weighted source rating, independent of popularity/trending.
- [Random full show](http://127.0.0.1:43823/api/v4/artists/artist_3qmp7zjbg1rpebhtyfy43096vj/shows/random)
  includes recordings and playable tracks. It is `no-store`, without an ETag;
  an empty artist returns 404.

Global recently-added/performed/updated lists use the same response models.
Global performed/updated lists accept repeated `artist_ids` values to restrict
artists. Unknown well-formed filter IDs contribute no matches; an unknown artist
in the path returns 404. The [contracts](../contracts.md#date-and-recent-show-browsing)
give exact defaults, bounds, clock, ordering and failure behavior.

The saved [anniversary excerpt](responses/artist-anniversary-excerpt.json) and
[recently added excerpt](responses/artist-recently-added-excerpt.json) contain
the first two real results. Full lists and response headers stay in ignored
`local-dev/v4-review/.output/`.

## What mobile should apply

Lists replace only their exact cached query membership and upsert returned show
summaries. A five-show result cannot clear the rest of an artist's shows or a
venue's detail membership. Empty results clear only that query's cached list.
Full exact-date and random results apply the selected show's complete recording
associations; they retain offline files and favorite references.

Ordinary responses have ETags; repeating a list with its `If-None-Match` validator
returns 304 when its represented data is unchanged. Dates/rankings can change
membership without imported metadata changes, so mobile must revalidate the
query itself. Offline browsing uses separately loaded collection memberships; there is no
combined artist snapshot.

The [search and ranking review](search-rankings.md) now completes the ten
mobile-facing operations that followed this discovery checkpoint. The remaining
unimplemented routes are operator/import and live activity.
