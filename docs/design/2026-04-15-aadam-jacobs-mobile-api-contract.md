# Aadam Jacobs Collection Mobile API Contract

This document is the mobile-facing contract for building the Aadam Jacobs Collection
experience. The collection API provides scoped browse lists and uses the existing
show detail endpoint for playback.

## Artist Sync

The mobile app should keep the normal artist catalog as the source of artist
display metadata.

### Initial / Full Artist Load

```http
GET /api/v3/artists?include_autocreated=true&include_collection_derived=true
```

Use this when the local artist catalog is empty, stale, or being seeded and a
full repair response is required. Normal mobile runtime sync should prefer the
bundled artist seed followed by delta requests. The response shape is the
existing `ArtistWithCounts[]` shape.

Important behavior:

- Default `/api/v3/artists` excludes collection-derived artists.
- `include_collection_derived=true` includes `AutoCreated | CollectionDerived`
  artists without also requiring `include_autocreated=true`.
- App-wide canonical artist sync should also send `include_autocreated=true` so
  existing all-artist and direct-artist flows keep their full canonical catalog.
- Collection-derived artists may have empty `upstream_sources`; that is expected
  because AJC membership is not artist-level Archive.org ownership.

### Differential Artist Sync

```http
GET /api/v3/artists/delta?since={lastServerTimestamp}&include_autocreated=true&include_collection_derived=true
```

Response:

```json
{
  "server_timestamp": "2026-04-15T13:29:10.000000Z",
  "artists": []
}
```

Client behavior:

- Send the last stored `server_timestamp` as `since`.
- Store the new `server_timestamp` after a successful response.
- Merge returned artists by `uuid`.
- Use `include_autocreated=true&include_collection_derived=true` for the normal
  app-wide canonical artist sync.
- If the client does not have a previous cursor, use the full artist load or an
  old timestamp such as `1970-01-01T00:00:00Z`.

## Collection Browse

### List Collections

```http
GET /api/v3/collections
```

Returns `CollectionSummary[]`.

Fields:

- `uuid`
- `slug`
- `upstream_identifier`
- `name`
- `item_count`
- `artist_count`
- `show_count`
- `source_count`
- `indexed_at`

Local AJC verification after import:

- `item_count`: `2477`
- `artist_count`: `1444`
- `show_count`: `2431`
- `source_count`: `2477`

### Collection Detail

```http
GET /api/v3/collections/aadam-jacobs
GET /api/v3/collections/{collectionUuid}
```

Returns `CollectionDetail`, which is `CollectionSummary` plus `description`.

Use this to render the collection landing header and to decide whether the
collection has been indexed.

### Collection Artists

```http
GET /api/v3/collections/aadam-jacobs/artists
GET /api/v3/collections/{collectionUuid}/artists
```

Returns `ArtistWithCounts[]`.

The `show_count` and `source_count` fields are scoped to active AJC-linked
sources, not global artist totals. Use the mobile app's local artist catalog for
artist images, cached metadata, and artist display joins where that is already
available.

### Collection Years

```http
GET /api/v3/collections/aadam-jacobs/years
GET /api/v3/collections/{collectionUuid}/years
```

Returns `CollectionYear[]`.

Fields:

- `uuid`
- `collection_uuid`
- `year`
- `artist_count`
- `show_count`
- `source_count`
- `duration`
- `avg_duration`
- `avg_rating`
- `popularity`

Use this for the main chronological browse surface.

### Collection Year Detail

```http
GET /api/v3/collections/aadam-jacobs/years/{year}
GET /api/v3/collections/aadam-jacobs/years/{yearUuid}
```

Returns `CollectionYearWithShows`.

Additional field:

- `shows`: `Show[]`

The show rows are list rows, not playback detail rows. They include `uuid`,
`artist_uuid`, `year_uuid`, venue fields, rating/duration fields, popularity
when available, and collection-scoped source availability fields:

- `source_count`
- `has_soundboard_source`
- `has_streamable_flac_source`
- `most_recent_source_updated_at`

Open playback with:

```http
GET /api/v3/shows/{showUuid}
```

### Recently Added Recordings

```http
GET /api/v3/collections/aadam-jacobs/shows/recently-added
GET /api/v3/collections/aadam-jacobs/shows/recently-added?limit=25
GET /api/v3/collections/{collectionUuid}/shows/recently-added?limit=25
```

Returns `Show[]`.

Ordering:

- Distinct collection-linked shows.
- Newest active collection-linked source first, using source `updated_at`.
- Ties fall back to newest show `display_date`.

Limits:

- Default: `25`
- Maximum: `25`
- Values `<= 0` use the default.

Use this for the collection landing page section that surfaces newly uploaded or
newly refreshed AJC recordings.

### Popular / Trending

```http
GET /api/v3/collections/aadam-jacobs/shows/popular-trending
GET /api/v3/collections/aadam-jacobs/shows/popular-trending?limit=25&window=30d
```

Returns:

```json
{
  "collection_uuid": "784087f9-7d7f-c82a-2195-21380411ac2b",
  "collection_slug": "aadam-jacobs",
  "collection_name": "Aadam Jacobs Collection",
  "popular_shows": [],
  "trending_shows": []
}
```

Supported `window` values:

- `48h`
- `7d`
- `30d`

The endpoint is scoped to collection-linked sources. It can return empty arrays
when play aggregate tables have no recent data.

### On This Day

```http
GET /api/v3/collections/aadam-jacobs/shows/on-this-day?month={1-12}&day={1-31}
GET /api/v3/collections/{collectionUuid}/shows/on-this-day?month={1-12}&day={1-31}
```

Returns `Show[]`.

Behavior:

- `200` with an empty array when no shows match.
- `400` for invalid month/day combinations.
- Leap day is accepted.

Use this for date-based discovery inside the collection.

## Playback

```http
GET /api/v3/shows/{showUuid}
```

Returns `ShowWithSources`.

Playback data lives under:

```text
sources[].sets[].tracks[]
```

Collection endpoints intentionally do not return full source graphs. The mobile
app should navigate from any collection show row to this existing show detail
endpoint before playback.

## Error Handling

Missing collection or year:

```json
{
  "success": false,
  "error_code": 404,
  "data": false
}
```

Invalid on-this-day date:

```json
{
  "success": false,
  "error_code": 400,
  "data": false
}
```

## Local Curl Smoke Checks

```bash
BASE_URL=http://localhost:3823

curl -fsS "$BASE_URL/api/v3/artists/delta?since=1970-01-01T00:00:00Z&include_autocreated=true&include_collection_derived=true" \
  | jq '{server_timestamp, artist_count: (.artists | length)}'

curl -fsS "$BASE_URL/api/v3/collections/aadam-jacobs" \
  | jq '{uuid, slug, item_count, indexed_at, artist_count, show_count, source_count}'

curl -fsS "$BASE_URL/api/v3/collections/aadam-jacobs/artists" \
  | jq 'length'

curl -fsS "$BASE_URL/api/v3/collections/aadam-jacobs/years" \
  | jq '.[0] | {uuid, collection_uuid, year, artist_count, show_count, source_count}'

curl -fsS "$BASE_URL/api/v3/collections/aadam-jacobs/shows/recently-added?limit=5" \
  | jq '.[] | {uuid, artist_uuid, display_date, source_count, most_recent_source_updated_at}'

curl -fsS "$BASE_URL/api/v3/collections/aadam-jacobs/shows/on-this-day?month=7&day=20" \
  | jq '.[0] | {uuid, artist_uuid, display_date}'
```

## Suggested Mobile Screens

1. Collection list / entry point: `GET /api/v3/collections`.
2. AJC landing: collection detail, recently added, on-this-day, popular/trending
   when non-empty, and year list.
3. Year list: `GET /api/v3/collections/aadam-jacobs/years`.
4. Year detail: `GET /api/v3/collections/aadam-jacobs/years/{year}`.
5. Show playback: `GET /api/v3/shows/{showUuid}`.

Artist taps can use the normal global artist flow. Reused artists may show
non-AJC content after drilldown; that is an intentional product behavior.
