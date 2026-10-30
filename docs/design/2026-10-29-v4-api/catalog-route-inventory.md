# Catalog route coverage for v4

Status: proposed public contract for review, not implemented. Inspected checkout
`3fb0e0f` on 2026-10-29. This file covers Artists, Shows, Years, Venues, Songs,
Tours, Eras, Search, and Catalog controllers: **39 method/path registrations
across 35 actions**, including hidden authenticated artist writes. Recent,
popularity, playback history/reporting, operator imports, health, account, and
authentication routes are covered by the companion inventory maintained in
this design directory. Neither inventory is a claim of deployed behavior.

Every current registration has a row below. Multiple legacy paths can map to
one v4 capability; route aliases need not become separate implementations.
All paths shown are absolute HTTP paths. Proposed ID prefix spellings remain
defined by the approved prefix table; playable tracks use `track_`.

## Rules shared by these routes

- Public v4 catalog IDs are typed strings; decoded UUIDs remain internal where
  appropriate. Canonical venue/song IDs and imported venue/song IDs identify
  different resources. A `year` calendar number or date is a filter, not an ID.
- Preserve every current lookup capability. Canonical v4 detail routes use typed
  IDs. Extend the explicit catalog resolver to handle legacy UUIDs, numeric IDs,
  and slugs with artist/resource scope. Artist slug lookup is handled
  by the scoped resolver; a separate by-slug route is not part of this proposal. Do not pass a year-like
  tour slug through an ambiguous numeric-ID parser. Resolver aliases are inputs,
  not additional primary IDs in ordinary v4 responses.
- Show projections prefer show-specific normalized venue links and retain
  imported venue labels/references. Every returned track object has exactly one
  `normalization` object containing its available state, parts, and compact
  normalized song information; imported titles and playable file identity stay.
- Lists preserve filters, ordering, and the underlying meaning of counts. A
  normalized song's matched recording tracks are not automatically distinct live
  performances. Imported setlist plays and normalized track matches remain
  distinguishable evidence.
- Responses declare collection completeness and revision. A flat artist catalog
  bootstrap must not silently become a partial collection; pagination requires
  one snapshot revision before the client treats omitted rows as absent.
- Authenticated/operator writes remain separate from anonymous reads. Current
  artist writes have `[Authorize]` and are hidden from API Explorer; that is not
  evidence that an administrator role is enforced today. V4 must define and test
  operator authorization rather than exposing write functionality anonymously.

## Artists — 7 registrations, 5 actions

| Current registration | Proposed v4 coverage | Inputs, output, and behavior to retain | Evidence |
| --- | --- | --- | --- |
| `GET /api/v2/artists` | `GET /api/v4/artists` | `include_autocreated=false`; artist metadata/counts. V4 includes popularity metadata consistently with current v3. | [Artists:55](../../../RelistenApi/Controllers/ArtistsController.cs#L55) |
| `GET /api/v3/artists` | `GET /api/v4/artists` | Same filter, counts, and artist popularity. | [Artists:56](../../../RelistenApi/Controllers/ArtistsController.cs#L56) |
| `GET /api/v2/artists/{artistIdOrSlug}` | `GET /api/v4/artists/{artistId}` plus legacy lookup | Preserve artist numeric/slug lookup via explicit resolver; return typed ID and counts. | [Artists:77](../../../RelistenApi/Controllers/ArtistsController.cs#L77) |
| `GET /api/v3/artists/{artistIdOrSlug}` | `GET /api/v4/artists/{artistId}` plus legacy lookup | Preserve UUID/numeric/slug lookup and popularity through the typed resource and lookup adapter. | [Artists:78](../../../RelistenApi/Controllers/ArtistsController.cs#L78) |
| `GET /api/v3/artists/{artistUuid}/normalized` | `GET /api/v4/artists/{artistId}/catalog` | Complete bootstrap of artist, years, venues, songs, tours, and shows, with v4 typed references and normalized overlays. Retain imported venue/song entities needed by stored references; grouped normalized browse entries are additional projections. | [Artists:110](../../../RelistenApi/Controllers/ArtistsController.cs#L110) |
| `POST /api/v2/artists` | `POST /api/v4/artists` — operator write | Typed create body with artist features and upstream-source assignments; current create saves the artist then replaces upstream assignments. Define failure/transaction semantics and generated identity; do not accept legacy numeric IDs as public identity. | [Artists:177](../../../RelistenApi/Controllers/ArtistsController.cs#L177) |
| `PUT /api/v2/artists/{artistIdOrSlug}` | `PUT /api/v4/artists/{artistId}` — operator write | Typed update body preserving feature/upstream assignment capability. Make the route identity authoritative; current action saves the body and does not bind the route identifier. | [Artists:193](../../../RelistenApi/Controllers/ArtistsController.cs#L193) |

Despite its name, the current `/normalized` action reads legacy catalog tables
and builds a flat `FullArtist`; it does not read `norm.*`. It applies artist/year
popularity and caches the result for 24 hours. Its current payload has no source
or track arrays: see [FullArtist](../../../RelistenApi/Models/Artist.cs#L137).
The v4 catalog route replaces that bootstrap function; per-track normalization
is required whenever other routes actually return tracks. Cache identity and
refresh must include normalized edits as described in the main design.

## Shows and source reviews — 14 registrations, 14 actions

| Current registration | Proposed v4 coverage | Inputs, output, and behavior to retain | Evidence |
| --- | --- | --- | --- |
| `GET /api/v2/shows/today` | `GET /api/v4/shows/today` | Optional `month`, `day`; all-artists anniversary results. Current fallback uses database current date components. Make the default clock/time zone explicit. | [Shows:33](../../../RelistenApi/Controllers/ShowsController.cs#L33) |
| `GET /api/v2/shows/on-date` | `GET /api/v4/shows/on-date` | Required `month`, `day`; same month/day across years, **not** an exact-date lookup. | [Shows:51](../../../RelistenApi/Controllers/ShowsController.cs#L51) |
| `GET /api/v2/shows/recently-performed` | `GET /api/v4/shows/recently-performed` | Preserve `shows`, `days`, and multi-artist filter; change legacy `artistIds[]` values to typed artist IDs and document the v4 query name. | [Shows:66](../../../RelistenApi/Controllers/ShowsController.cs#L66) |
| `GET /api/v2/shows/recently-updated` | `GET /api/v4/shows/recently-updated` | Preserve `shows`, `days`, multi-artist filter, and source/catalog update meaning. Normalization-only edits refresh metadata but do not change this recording-update ordering. | [Shows:80](../../../RelistenApi/Controllers/ShowsController.cs#L80) |
| `GET /api/v2/artists/{artistIdOrSlug}/shows/today` | `GET /api/v4/artists/{artistId}/shows/today` | Artist-scoped anniversary query, optional `month`, `day`. | [Shows:94](../../../RelistenApi/Controllers/ShowsController.cs#L94) |
| `GET /api/v2/artists/{artistIdOrSlug}/shows/recently-performed` | `GET /api/v4/artists/{artistId}/shows/recently-performed` | Artist scope, `shows`, `days`. | [Shows:122](../../../RelistenApi/Controllers/ShowsController.cs#L122) |
| `GET /api/v2/artists/{artistIdOrSlug}/shows/recently-updated` | `GET /api/v4/artists/{artistId}/shows/recently-updated` | Artist scope, `shows`, `days`; retain catalog update behavior. | [Shows:137](../../../RelistenApi/Controllers/ShowsController.cs#L137) |
| `GET /api/v2/artists/{artistIdOrSlug}/shows/on-date` | `GET /api/v4/artists/{artistId}/shows/on-date` | Artist-scoped month/day anniversary query. | [Shows:152](../../../RelistenApi/Controllers/ShowsController.cs#L152) |
| `GET /api/v2/artists/{artistIdOrSlug}/shows/top` | `GET /api/v4/artists/{artistId}/shows/top` | `limit=25`; rating-weighted ranking currently orders `cnt.max_avg_rating_weighted DESC`. Keep distinct from popularity/trending. | [Shows:179](../../../RelistenApi/Controllers/ShowsController.cs#L179) |
| `GET /api/v2/artists/{artistIdOrSlug}/shows/random` | `GET /api/v4/artists/{artistId}/shows/random` | Return a full random show with sources/sets/tracks, not only a summary. Define an empty artist's no-result behavior explicitly. | [Shows:202](../../../RelistenApi/Controllers/ShowsController.cs#L202) |
| `GET /api/v2/artists/{artistIdOrSlug}/shows/{showDate}` | `GET /api/v4/artists/{artistId}/shows/by-date/{showDate}` | Preserve exact imported display-date lookup and full show/source hierarchy. Use explicit `by-date` so dates do not share the typed-ID detail slot. | [Shows:235](../../../RelistenApi/Controllers/ShowsController.cs#L235) |
| `GET /api/v3/shows/{showUuid}` | `GET /api/v4/shows/{showId}` | Typed show identity; same full show → sources → sets → tracks capability, with normalization on each track. | [Shows:249](../../../RelistenApi/Controllers/ShowsController.cs#L249) |
| `GET /api/v2/artists/{artistIdOrSlug}/sources/{sourceId}/reviews` | `GET /api/v4/sources/{sourceId}/reviews` plus legacy lookup | Preserve source reviews; resolve legacy source integer with artist scope. Preserve text, rating, date, reviewer/source information rather than turning reviews into normalized song evidence. | [Shows:264](../../../RelistenApi/Controllers/ShowsController.cs#L264) |
| `GET /api/v3/sources/{sourceUuid}/reviews` | `GET /api/v4/sources/{sourceId}/reviews` | Same reviews capability using losslessly wrapped source UUID. | [Shows:275](../../../RelistenApi/Controllers/ShowsController.cs#L275) |

Full source/set/track materialization is already part of show detail:
[ShowWithSources](../../../RelistenApi/Models/Show.cs#L80),
[SourceFull](../../../RelistenApi/Models/Source.cs#L131),
[SourceSet.tracks](../../../RelistenApi/Models/SourceSet.cs#L35), and
[SourceService](../../../RelistenApi/Services/Data/SourceService.cs#L95).
Keeping show detail without nested playable data would fail existing coverage.

## Years — 5 registrations, 4 actions

| Current registration | Proposed v4 coverage | Inputs, output, and behavior to retain | Evidence |
| --- | --- | --- | --- |
| `GET /api/v2/artists/{artistIdOrSlug}/years` | `GET /api/v4/artists/{artistId}/years` | Artist years, counts, and metadata; v4 also preserves v3 popularity metadata. | [Years:39](../../../RelistenApi/Controllers/YearsController.cs#L39) |
| `GET /api/v3/artists/{artistIdOrSlug}/years` | `GET /api/v4/artists/{artistId}/years` | Same year collection with popularity. | [Years:40](../../../RelistenApi/Controllers/YearsController.cs#L40) |
| `GET /api/v2/artists/{artistIdOrSlug}/years/{year}` | `GET /api/v4/artists/{artistId}/years/by-number/{year}` | Calendar year lookup returns year metadata and shows. The numeric calendar value is retained as a lookup, not disguised as a typed entity ID. | [Years:64](../../../RelistenApi/Controllers/YearsController.cs#L64) |
| `GET /api/v3/artists/{artistIdOrSlug}/years/{yearUuid}` | `GET /api/v4/artists/{artistId}/years/{yearId}` | Typed year identity with shows and popularity; resolver retains legacy UUID/slug lookup capability. | [Years:81](../../../RelistenApi/Controllers/YearsController.cs#L81) |
| `GET /api/v2/artists/{artistIdOrSlug}/years/{year}/{showDate}` | `GET /api/v4/artists/{artistId}/shows/by-date/{showDate}` | Consolidate with exact-date show lookup. Current action ignores `year` and loads by `showDate`; do not preserve a misleading unused path parameter as a new v4 requirement. | [Years:107](../../../RelistenApi/Controllers/YearsController.cs#L107) |

## Venues — 3 registrations, 3 actions

| Current registration | Proposed v4 coverage | Inputs, output, and behavior to retain | Evidence |
| --- | --- | --- | --- |
| `GET /api/v2/artists/{artistIdOrSlug}/venues` | `GET /api/v4/artists/{artistId}/venues` plus `GET /api/v4/artists/{artistId}/imported-venues` | Clients use the main list for every artist and follow each entry's kind. Canonical entries group by place; imported entries keep unmatched shows reachable and include full imported counts. These collections can overlap during partial normalization. The separate imported inventory preserves all original rows/counts. Never sum imported venue counts to infer canonical counts. | [Venues:32](../../../RelistenApi/Controllers/VenuesController.cs#L32) |
| `GET /api/v2/artists/{artistIdOrSlug}/venues/{idAndSlug}` | `GET /api/v4/imported-venues/{importedVenueId}` plus resolver | Preserve the original venue and its original show scope. A legacy raw row is not assumed to have one canonical target. | [Venues:46](../../../RelistenApi/Controllers/VenuesController.cs#L46) |
| `GET /api/v3/artists/{artistIdOrSlug}/venues/{venueIdOrSlug}` | Same imported detail route plus resolver | Preserve UUID/numeric/slug access to imported venue/show data. Canonical venue detail has a different route/ID. | [Venues:61](../../../RelistenApi/Controllers/VenuesController.cs#L61) |

## Songs — 3 registrations, 3 actions

| Current registration | Proposed v4 coverage | Inputs, output, and behavior to retain | Evidence |
| --- | --- | --- | --- |
| `GET /api/v2/artists/{artistIdOrSlug}/songs` | `GET /api/v4/artists/{artistId}/songs` plus `GET /api/v4/artists/{artistId}/imported-songs` | Artist-scoped mixed browse uses approved canonical songs plus imported rows without valid evidence mappings. Canonical counts describe exact active files/shows; imported setlist counts remain separate. The raw inventory preserves mapped and unused imported rows. | [Songs:32](../../../RelistenApi/Controllers/SongsController.cs#L32) |
| `GET /api/v2/artists/{artistIdOrSlug}/songs/{idAndSlug}` | `GET /api/v4/imported-songs/{importedSongId}` plus resolver | Preserve imported song's setlist-derived show list. Normalized song matches and file membership are additional data, not proof of an identical show list. | [Songs:46](../../../RelistenApi/Controllers/SongsController.cs#L46) |
| `GET /api/v3/artists/{artistIdOrSlug}/songs/{songIdOrSlug}` | Same imported detail route plus resolver | Preserve UUID/numeric/slug lookup and setlist show links; canonical normalized song detail uses its own short ID. | [Songs:61](../../../RelistenApi/Controllers/SongsController.cs#L61) |

Current setlist show membership comes from `setlist_songs_plays` through
`setlist_shows`, not normalized track interpretation:
[SetlistSongService](../../../RelistenApi/Services/Data/SetlistSongService.cs#L67).
The song checkpoint is documented in the
[song-to-playback walkthrough](../../plans/active/2026-10-29-v4-api/review/song-source-journey.md).
Canonical `/songs/{songId}` exposes `matched_track_count` and `matched_show_count`;
its `/tracks` detail returns each complete file once, retaining all normalized parts, and its `shows`
deduplicates recording-supported catalog shows. Imported detail exposes
`setlist_show_count`, counting distinct catalog show IDs matched by artist/date,
and its corresponding show list. Setlist-only dates without a catalog show do
not enter that count. These evidence sources remain separate.

## Tours and eras — 4 registrations, 4 actions

| Current registration | Proposed v4 coverage | Inputs, output, and behavior to retain | Evidence |
| --- | --- | --- | --- |
| `GET /api/v2/artists/{artistIdOrSlug}/tours` | `GET /api/v4/artists/{artistId}/tours` | Artist-owned tour metadata and counts of assigned catalog shows. | [Tours:29](../../../RelistenApi/Controllers/ToursController.cs#L29) |
| `GET /api/v2/artists/{artistIdOrSlug}/tours/{idAndSlug}` | `GET /api/v4/tours/{tourId}` plus resolver | Direct tour metadata and complete shows by typed tour ID; preserve legacy numeric lookup without making integers v4 entity IDs. | [Tours:43](../../../RelistenApi/Controllers/ToursController.cs#L43) |
| `GET /api/v3/artists/{artistIdOrSlug}/tours/{tourIdOrSlug}` | Same typed tour route plus resolver | Preserve full year-leading/numeric slug lookup, including the current preference for full slug before legacy `id-slug` interpretation. | [Tours:58](../../../RelistenApi/Controllers/ToursController.cs#L58) |
| `GET /api/v2/artists/{artistIdOrSlug}/eras` | `GET /api/v4/artists/{artistId}/eras` | Artist eras in configured `order`, retaining names. Approved: return embedded era metadata with no public era ID or prefix; existing integer keys stay internal. | [Eras:28](../../../RelistenApi/Controllers/ErasControllers.cs#L28), [Era model](../../../RelistenApi/Models/Era.cs#L6) |

## Search and resolver — 3 registrations, 2 actions

| Current registration | Proposed v4 coverage | Inputs, output, and behavior to retain | Evidence |
| --- | --- | --- | --- |
| `GET /api/v2/search` | `GET /api/v4/search` | Preserve `q`, artist scope, and per-category booleans `artists`, `shows`, `songs`, `sources`, `tours`, `venues`; rename UUID filter to typed `artist_id`. Empty/short queries and all-disabled categories yield empty results as today. | [Search:28](../../../RelistenApi/Controllers/SearchController.cs#L28) |
| `GET /api/v3/search` | `GET /api/v4/search` | Same categories/filter capability, with normalized venues/songs and explicit imported fallback/reference kinds. Returned track objects follow the common `normalization` contract. | [Search:29](../../../RelistenApi/Controllers/SearchController.cs#L29) |
| `POST /api/v3/catalog/resolve` | `POST /api/v4/catalog/resolve` | Bounded batch, per-reference availability, target entities and parents, not-found and malformed-request distinctions. Preserve current bounds (500 references, 128 KiB) unless explicitly revised. Add typed IDs, merge resolution, contextual normalized candidates and scoped legacy numeric/slug lookups required by this table. | [Catalog:23](../../../RelistenApi/Controllers/CatalogController.cs#L23) |

The expanded resolver is a proposal. The current resolver only accepts typed
resource names plus canonical UUIDs; it does not already support integer/slug
schemes. Specify allowed reference kinds and require artist scope where legacy
identifiers are not global. Avoid an unbounded arbitrary-query endpoint.

## Added v4 capabilities

These extend the legacy capability mappings above. The current implementation
status is in the [plan](../../plans/active/2026-10-29-v4-api/README.md); the full
[manifest](../../plans/active/2026-10-29-v4-api/endpoints.json) also includes pending
routes. Detail reads use one snapshot and payload ETag.

| V4 route | Purpose and data contract |
| --- | --- |
| `GET /api/v4/artists/{artistId}/shows` | Complete artist show-summary collection outside bootstrap. |
| `GET /api/v4/sources/{sourceId}` | Direct full source lookup with sets/tracks and typed parents. |
| Source sets (embedded only) | Deliberately omit direct set lookup; full source/show resources retain typed set IDs and complete tracks. |
| `GET /api/v4/source-tracks/{sourceTrackId}` | Direct playable track, normalization, and typed parents. |
| `GET /api/v4/venues/{venueId}` | Shared venue metadata, aliases, global count, complete approved cross-artist `shows`. |
| `GET /api/v4/artist-venues/{artistVenueId}` | Relationship metadata, artist count, complete scoped `shows`. |
| `GET /api/v4/artists/{artistId}/venues/{venueId}` | Pair lookup returning the same complete artist-venue detail. |
| `GET /api/v4/songs/{songId}` | Song metadata, stored recording counts, complete distinct recording `shows`. |
| `GET /api/v4/songs/{songId}/tracks` | Song metadata and complete `TrackResource` items in `tracks`. Each file appears once; matching parts come from its normalization. No `shows` field. |

Imported venue/song details likewise include metadata and complete `shows`,
with direct imported-ID lookup. Artists come from a separately loaded catalog;
details retain `artist_id` references without embedding artist resources. Browse
and embedded resources stay compact. No bare venue/song `/shows` routes or `detail_url` fields
are needed. Mobile replaces only the returned detail owner’s membership;
shared entities, downloads, and favorites remain independent.

## Decisions that the inventory makes visible

The full API contract needs to settle these before implementation, rather than
discover them while writing controllers:

1. Apply the approved prefix/type vocabulary, including `track_` and distinct
   imported/canonical song and venue identities. Era metadata is embedded with no prefix.
2. Approve the route table and legacy lookup policy. Literal numeric/slug route
   aliases may consolidate, but their functionality must remain accessible.
3. Define imported versus normalized song-list fallback and count semantics;
   setlist plays, distinct shows, and matched recording tracks are different.
4. Define full artist snapshot collection keys and completeness; preserve imported
   rows and normalized browse projections without making mobile guess which list
   is safe to replace.
5. Define update-list treatment of normalization-only edits, default anniversary
   time zone, and operator create/update authorization/failure behavior.

Endpoint-specific pagination limits, SQL/index choices, implementation ordering,
and rollout mechanics belong in the subsequent implementation plan. This
inventory establishes intended coverage; it does not claim those choices or
route spellings have already been approved.
