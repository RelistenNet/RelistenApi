# Catalog contract

All paths here are under `/api/v4`. Use [OpenAPI](http://localhost:3823/api-docs-v4/index.html)
for fields, parameter bounds and enums; [endpoints.json](endpoints.json) defines
collection replacement per operation. [Public IDs](ids.md) and
[mobile sync](mobile-sync.md) describe identity and persistence.

## Responses, reads and failures

Ordinary GETs return a resource or array directly. Details with a show collection
put `shows` beside the resource's fields. Search and grouped rankings use named
result groups. There is no generic `meta`, `data`/`included` envelope or `detail_url`.
Lists are complete unless the endpoint explicitly has a filter/limit. Never infer
global deletion from an omitted result.

An ordinary read uses one primary READ ONLY REPEATABLE READ transaction for
metadata, normalization, membership and stored scores. Readers receive the same
[CatalogReadSession](../../RelistenApi/Services/Catalog/CatalogReadSession.cs).
Arrays have deterministic ordering with identity tie-breakers. Materialize the
result, dispose the transaction, then serialize. No per-record HTTP lookup or
query is needed to hydrate normalization or show metadata.

[CatalogJsonResult](../../RelistenApi/Controllers/V4/CatalogJsonResult.cs) hashes
the deterministic UTF-8 JSON buffer and sends those same bytes. Ordinary GETs
return a weak ETag and `Cache-Control: public, no-cache`. A matching
`If-None-Match` returns a bodyless 304 after the current read/serialization.
The validator detects equality of that response, not an ordered catalog revision.
It includes represented score/normalization corrections even when imported
`updated_at` is unchanged. Compression uses `Vary: Accept-Encoding`; public CORS
permits `If-None-Match` and exposes `ETag`. Cloudflare owns downstream caching;
there is no origin JSON cache.

Random shows, public history, POSTs and failures use no-store. Live summary has
its own minute-boundary policy below. Accounts have separate no-store rules.
V4 errors use ProblemDetails with `code`: semantic input/wrong-kind IDs → 422,
valid unknown resources → 404, known retired canonical IDs → 410, excessive body
size → 413, dependency/read timeout → 503. Malformed resolver JSON is 400; malformed
play JSON is 422. A failed collection query never returns a partial success.

## Artist → year → recording

`/artists` and `/artists/{artistId}` return identity, counts, `features` and
provider assignments. `features.normalization` means approved normalized reads,
not complete coverage. Provider labels remain open strings. `featured` retains
the imported bitmask (featured = 1, autocreated = 2). The default artist list
excludes autocreated entries; `include_autocreated=true` includes them when they
have shows or sources. Load that inclusive list for artists referenced elsewhere.

`/artists/{artistId}/years` returns chronological year resources. Both
`/years/{yearId}` and `/years/by-number/{year}` under that artist return year
metadata with complete show summaries. They enforce artist ownership. Calendar
years are 1–9999; the resource's existing `year` label is a string.
`/artists/{artistId}/shows` returns the full chronological summary list.
An existing empty collection returns `[]`; an unknown owner returns 404.

Show summaries omit `sources`; they do not represent an empty source list.
`/shows/{showId}` loads the complete source/set/active-track hierarchy.
`/sources/{sourceId}` loads the same source resource directly, without siblings.
`/source-tracks/{sourceTrackId}` returns the same track resource with exact typed
artist/show/source/set parents. Sets remain embedded; there is no direct set GET.
Imported file titles, URLs, checksums, duration and ordering remain unchanged.

`/artists/{artistId}/tours` supplies compact tours and catalog `show_count`;
`/tours/{tourId}` adds complete `shows`. `/artists/{artistId}/eras` returns ordered
`{ order, name }` metadata without an era ID. `/sources/{sourceId}/reviews` returns
the complete review list, newest update first, retaining original text/ratings.

## Venues and partial normalization

Read `features.norm_catalogue` in the request snapshot. Missing/false disables
canonical artist discovery and track interpretation; `norm_venues` is not the
public-read switch. Approved artists may still have partial coverage. Imported
browsing and playable data remain usable in either case.

A show has `imported_venue` plus nullable `artist_venue`:

```text
show.artist_venue
  id: artist_venue_<8 characters>
  artist_id
  artist_show_count
  created_at, updated_at
  venue
    id: venue_<8 characters>
    global_show_count
    names: [{ name, kind }]
```

Use imported venue name/locality for show display, falling back to canonical
metadata only when imported data is absent. Link to `artist_venue.venue.id`.
A source retains its own imported venue observation. There is no `venue_display`,
venue-name timeline or historical-name selection rule. Alias kinds are `official`,
`short`, `spelling`; distinct `(name, kind)` pairs include merged names and are
ordered deterministically. Stored spelling evidence does not become official
merely because it is the venue's only alias.

Resolve each show's accepted assignment and venue merges before finding its
stored artist/place relationship. Missing assignment/relationship gives
`artist_venue: null`, never an invented ID or request-time recount.
`artist_show_count` covers that artist at that place; `global_show_count` sums
stored counts only for approved artists, regardless of the current route's scope.
Imported venues have their own full `show_count`, including when embedded in a
show or source. Counts can briefly lag live membership; see [derived counts](derived-counts.md).

`/artists/{artistId}/venues` is the normal browse list for every artist. Entries
have `kind` and exactly one populated target: `artist_venue` or `imported_venue`.
An imported entry remains if any of its shows lack an available approved
relationship. Its count/detail covers the entire imported group, so partial
normalization can make its shows overlap a canonical entry. The wrapper has no
separate count or identity. Clients follow the target kind; features do not
select a different list route. `/artists/{artistId}/imported-venues` is the full
imported inventory, including unused or entirely mapped rows.

- `/artist-venues/{artistVenueId}` returns relationship metadata plus complete artist-scoped `shows`.
- `/artists/{artistId}/venues/{venueId}` is the equivalent pair lookup after merge resolution.
- `/venues/{venueId}` returns canonical metadata, aliases, global count and complete cross-artist `shows`.
- `/imported-venues/{importedVenueId}` returns imported metadata, count and complete `shows`, using the show's own imported assignment.

Canonical venue detail remains readable with no approved shows. Missing/disabled
artist relationships return 404; retained zero-count relationships return 200
with `shows: []`. Imported detail works without normalization. Details use
`artist_id` references rather than repeating artist arrays. There are no separate
venue `/shows` routes.

## Songs and playable tracks

Every track carries one `normalization` object: `state`, nullable `reason` and
ordered `parts`. State is `unavailable`, `partial` or `interpreted`.
Unavailable has empty parts and reason `disabled`, `not_interpreted` or
`inconsistent`. Partial has unresolved parts; interpreted means all parts are
resolved, without claiming perfect inference.

Each part has `ordinal`, `text`, `state`, nullable compact `song`, `qualifiers`,
`notes`, `segue_after`. Part state is `matched`, `non_song` or `unresolved`.
Matched song references have `id`, `artist_id`, `name`, `kind`. Preserve the
imported playback title and one file identity for a medley. Parts imply no seek
offset, stable occurrence ID or extra playable file. Shared vocabularies live in
[normalization](../../Relisten.Normalization/CatalogueVocabulary.cs); API-only
states and recording FLAC values live in [CatalogEnums](../../RelistenApi/Models/Api/V4/CatalogEnums.cs).
OpenAPI publishes their closed enums, including nullable values.

`/artists/{artistId}/songs` returns entries with exactly one `song` or
`imported_song` target and a matching `kind`. Approved artists include canonical
root songs (even with zero matches) and imported entries without a valid evidence
mapping to a surviving same-artist song. Unapproved artists get imported entries.
`/artists/{artistId}/imported-songs` retains the full imported inventory.

`/songs/{songId}` returns canonical metadata, distinct aliases/merged names,
`matched_track_count`, `matched_show_count` and complete deduplicated `shows`.
These shows require exact recording matches; imported setlist evidence alone
cannot identify a source or file. `/songs/{songId}/tracks` returns the song
metadata with `tracks`, without a `shows` property. Each file appears once, even
if several parts match. Find matching parts by the returned root song ID in each
track's interpretation; no wrapper or duplicate ordinal list is needed.
Load this potentially large list only when requested.

[Shared recording membership](../../Relisten.Normalization/Sql/016_song_counts.sql)
requires resolved parts, valid same-artist canonical chains, active non-orphaned
files and consistent source/set/show/artist parents. Partial tracks can match;
a corrupt canonical interpretation cannot. Stored counts use the same rule.
Canonical routes follow merge survivors; unknown/unapproved songs return 404,
known songs with corrupt chains return 503. Imported fallback remains available.

`/imported-songs/{importedSongId}` returns imported metadata, `setlist_show_count`
and complete setlist-supported catalog `shows`. Match evidence by artist/date;
deduplicate each show, retaining distinct shows on the same date. Dates with no
catalog show contribute none. Imported membership does not imply a recording
match. Song aliases are strings; the schema has no song alias kind.

## Search, dates and ranking context

`/search` returns named groups of artist/show/venue/song results; it reuses the
ordinary resource projections and approved-normalization rules. Filters use
snake_case. See OpenAPI for supported filters, ordering and limits.

Artist and global `/shows/on-date` and `/shows/today` are month/day anniversary
queries. Artist `/shows/by-date/{showDate}` selects an exact imported display
date. Default anniversary values follow the database session date; send explicit
month/day when needed. Recently performed uses concert dates; recently added
uses imported additions; recently updated stays about imported recording changes,
not normalization-only corrections. Artist `/shows/top` and `/shows/random` keep
their explicit selection behavior.

All artist resources include nullable global popularity; ordinary year resources
include artist-local popularity. Ordinary show summaries/details (including
search, resolver, venue and song results) use artist-local metrics.
Global `/popular/*` and `/trending/*` use global comparisons; artist
`/shows/popular-trending` uses artist-local metrics. `/shows/momentum` uses organic
momentum with raw fallback. A null popularity clears a previously stored score.
Scores do not depend on normalization approval.

Rankings are transient online views. Mobile must not persist their contextual
scores or membership as ordinary offline catalog data. Open ordinary detail
routes for catalog apply. V4 selects stored PostgreSQL scores and enriches only
the returned rows. After closing snapshots, the request checks required
populations' freshness in one query and queues at most one batch. Missing scores
serve null/empty pending refresh. SQL/enqueue failure returns 503/no-store.
Calculations and dual Postgres/Redis publication belong to [popularity](../popularity/README.md).

## Public activity

`POST /live/play` accepts typed `track_id`, `app_type` (`ios`, `web`, `sonos`,
`android`) and optional anonymous `client_uuid`. It records a track start, not
account history or a qualified listen. Retrying can record another event. A
successful telemetry write remains accepted if later queue work fails.

`GET /live/history` returns the latest one-hour window in ascending `sequence`,
with a maximum limit of 2,000. Repeated `artist_ids` filter before the limit;
`after_sequence` polls subsequent events. Sequence is an increasing public event
key and exposes approximate insertion volume. Rows include hydrated track
resources, not client UUIDs. Event selection uses the existing replica path,
then batched catalog hydration: this is explicitly best effort, not one snapshot.
It is no-store and does not define personal playback history.

`GET /live/summary` uses a completed minute-aligned window and one primary
snapshot for aggregation/hydration. Defaults: `minutes=15` (clamped 5–60),
`shows_limit=20`, `artists_limit=10`, `sort=surprise` (or `plays`),
`max_per_artist=2` (0 disables), `fresh_days=7`; `month_day` defaults to UTC MM-DD.
It returns totals, app counts, a zero-filled minute timeline, artists/top show,
headline shows, deep cuts, recent concerts and on-this-day IDs. A show entry has
ordinary `show`/`source` summaries plus `latest_play: { track, played_at }`.
That latest track can belong to another recording than the most-played source.
Artist metadata is not repeated. `fresh` means recent concerts, not new tapes.

Current-window totals exclude unsupported apps, missing tracks and inconsistent
recording parents. Historical hourly/daily baselines retain their existing
population (which does not retain app type). `listened_seconds` sums started-file
durations, using 420 seconds for missing durations; it is an estimate, not measured
listening time. Summary has a payload ETag and shared caching until the next
minute, then up to 60 seconds stale while revalidating. It omits request-time
`generated_at`; failure is 503/no-store. It never owns catalog membership.

## Compatibility and operators

Legacy v2/v3 readers, Redis response caches and serializers stay separate.
There is no legacy adapter integration or rollout flag. Share low-level storage
or pure domain rules when semantics match; do not make v4 wrap legacy DTOs.
Operator artist/import commands keep their eight existing v2 paths and authorization
(listed in the endpoint map). The built-in admin uses them; the separate
`relisten-admin` app uses direct Postgres actions. Those commands are not new v4 routes.
