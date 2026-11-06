# V4 catalog GraphQL

Send a JSON `POST` to `/api/v4/graphql` with `query` and optional `variables` and
`operationName`. Hot Chocolate 16.7.3 executes the public, read-only catalog graph.
The endpoint serves web clients that want selected catalog relationships without
a local sync engine. Responses use `Cache-Control: no-store`.

```graphql
query ArtistShows($artist: ID!, $after: String) {
  artist(id: $artist) {
    id name showCount
    shows(first: 100, after: $after) {
      nodes { id displayDate sourceCount }
      pageInfo { endCursor hasNextPage }
    }
  }
}
```

Use the existing opaque V4 IDs as variables. Fields use camelCase; domain enums
use GraphQL enum values. The [executable schema](schema.graphql) defines the wire
contract. [Client operations](operations/) cover artist navigation, playback,
song history, venue browsing, playlist hydration, search, rankings, references,
and public activity. These documents are validated by the HTTP integration suite;
clients may send their own operations. There is no persisted-operation registry.

## Navigation and collections

The [capability inventory](capabilities.md) maps the graph to the REST readers.
Artists, years, shows, sources, playable source tracks, tours, canonical/imported
songs, canonical/imported venues, artist-venue relationships, sets, reviews, and
setlist shows have direct ID lookups and relevant parent/child relationships.
`sourceTracksByIds` preserves input order and duplicate IDs, with nulls for missing
files. `resolve` preserves input order and exposes availability, redirect targets,
and reasons for supported V4 and legacy identities. Legacy integer and slug
references require artist scope except for global artist/venue identities.

Growing collections have `first` (default 50, maximum 500), `after`, `nodes`,
`edges`, and `pageInfo`. Resume with `endCursor` while `hasNextPage` is true.
Cursors bind the owner, filters, ordering, and connection kind; reuse with different
arguments returns `invalid_cursor`. `first` can change between pages. SQL applies
the keyset and limit before loading nodes, independently for each parent.

Each page is a fresh live view. Unchanged data yields a complete traversal without
duplicates or skipped ties. Inserts, removals, or changed sort keys between requests
can move entries across the cursor. There is no export snapshot or transaction
held between requests. Restart a traversal when a refreshed list is required.

Source `sets`, set `tracks`, and normalization `parts` remain complete ordered
arrays. They have no page arguments and are never silently truncated. Page the
show's sources to select recordings. Song history offers paged shows and matching
playable files; `selection: TOP` chooses the best matching source using the shared
catalog source ranking, while `ALL` includes every matching source.

The named `popular*`, `trending*`, `momentumShows`, and `artistRankings` fields
return bounded top-N views (`limit` defaults to 50, maximum 500), rather than an
exhaustive ranked catalog export. `limit` on `artistRankings` is per artist for each
of its two rankings. General catalog connections remain independently pageable.
Public `liveHistory` is a bounded event view, with `afterSequence` and maximum
2,000 events; it is not an authenticated listener's history.

## Filters, ordering, and counts

Connections accept typed `filter` input. Supported combinations are checked;
unsupported filters or ordering return `invalid_argument`.

- `artistId` scopes a collection. Nested ownership and an artist filter intersect.
- `from` and `through` are inclusive show-date bounds on show/source/track lists.
  Use an ISO date-time, such as `"1995-01-01T00:00:00Z"`.
- `venueId` and `songId` select approved canonical membership on show/source/track
  lists. Imported collections remain separate graph relationships.
- `soundboard` and `flac` test show availability or individual source/track facts.
  Filtering shows does not prune their subsequently selected source connections.
- `search` performs literal, case-insensitive substring matching where supported.
  Fewer than three characters yields no matches. Year and review lists do not
  support it. `search(q:)` applies the same query to selected categories only;
  an optional category artist scope is retained, and conflicting scopes fail.
- `includeAutoCreated` defaults to false and applies only to artist browsing.
  Artist search retains REST's existing inclusion policy.

`DEFAULT` preserves each collection's domain order. Shows also support
`DATE_DESCENDING`, `RATING`, `POPULARITY`, `UPDATED`, and `ADDED`; artists support
`POPULARITY`; years support `RATING` and artist-scoped `POPULARITY`; sources
support `RATING`, `DATE_DESCENDING`, `UPDATED`, and `ADDED`; tours support
`DATE_DESCENDING`. Every ordering includes stable identity tie-breaks.
Anniversary discovery uses the database date. Recent performed/updated windows
are optional and capped at 90 days; recently added uses source timestamps.

Stored UI counts retain their existing meaning and can lag membership. Select
`totalCount` only when needed: it counts the connection's filters in the same
snapshot as that page and causes an additional count evaluation. Canonical and
imported fallback groups can overlap; their counts cannot safely be added.

Ranking `metrics` belong to that ranking's comparison population. A rank's `node`
keeps ordinary catalog popularity: artist-local for shows/years, global for
artists. Organic momentum never replaces a node's ordinary score.

## Read consistency and failures

Every entity batch, page with immediate node hydration/count/statistics, ranking,
and reference lookup owns a short primary, read-only repeatable-read session.
A complete source materializes its header, sets, tracks, and normalization in one
session. Selected source children consume that materialized bundle. Independent
relations use their own units and may observe newer catalog data; the entire
response is not a single snapshot.

`normalization.parts.song` is a `TrackPartSong`: its ID, name, kind, and artist ID
were materialized with the recording's interpretation. Follow its `catalog` field
for full song details and traversal in an independent unit. Optional canonical
links can be absent according to the existing approval/mapping rules.

A failed normalization, SQL child read, or projection fails its reader unit.
There are no savepoints, recovered half-recordings, or SQL retries. Hot Chocolate
uses ordinary nullability/error propagation; independent fields may still succeed.
A following request opens fresh sessions. Known errors have `extensions.code`:
`invalid_argument`, `invalid_cursor`, `retired`, `normalization_unavailable`,
`catalog_timeout`, `catalog_unavailable`, or `catalog_row_limit`. Hot Chocolate
also supplies its own validation/cost/depth error codes. Database details are
masked. Unknown IDs normally return null; explicit `resolve` returns richer
availability reasons. Clients must inspect `errors`, even on HTTP 200 responses.

A malformed legacy UUID or integer is an invalid argument. A canonical merge
cycle is an existing normalization-unavailable condition. These use established
domain behavior; they do not trigger special partial recovery.

Popularity requirements from all resolver/fetch scopes share one request collector.
It queues at most one freshness handoff after execution, with sessions closed and
before response serialization. A refresh scheduling failure is logged and leaves
available catalog data usable, matching the existing REST behavior.

## Execution limits and operations

Defaults are configurable under `GraphQL` in ASP.NET configuration:

- `MaxPageSize`: 500; `MaxIds`: 500; `MaxConcurrentReads`: 4 per operation.
- `ExecutionSeconds`: 20; `MaxDocumentBytes`: 262,144 including the JSON body;
  `MaxDepth`: 24.
- `MaxFieldCost`: 200,000; `MaxTypeCost`: 500,000;
  `MaxMaterializedRows`: 100,000 across the operation.

These limits allow the [measured workloads](performance.md) with substantial
headroom. List costs include fan-out and estimated complete recording sizes;
runtime accounting also bounds materialized entities, tracks, links, and parts.
Direct bounded views reserve their result ceiling; grouped rankings reserve the
combined artist × per-ranking limit before querying. A row limit raises an error
and never returns a truncated recording. This is not a serialized-byte ceiling.

The HTTP deadline and abort token reach loader waits, session opening, and Dapper
commands. The shared SQL command timeout remains 30 seconds; the graph's shorter
linked token cancels outstanding work. REST retains its current timeout behavior.
GET execution, operation batching, multipart uploads, mutations, and subscriptions
are disabled. Introspection remains available through POST. Oversized known-length
bodies return HTTP 413; streaming-body rejection may use the GraphQL HTTP parser's
error envelope. CORS permits public POST and the optional `GraphQL-Cost` header.

Send `GraphQL-Cost: report` to include operation estimates in response extensions,
or `GraphQL-Cost: validate` to validate and estimate without executing the query.
Existing ASP.NET/Npgsql telemetry covers HTTP/SQL work. `Relisten.GraphQL` adds
read duration, batch size, active-read, and row-budget instruments;
`Relisten.Catalog` counts domain SQL commands. The row counter includes direct-view
reservations, so it measures budget accounting rather than exact SQL row counts.
Raw variables are not logged by these additions.

This is an additive route with no migrations, imports, backfill, or client cutover.
Remove the route registration to roll back exposure; shared reader changes retain
REST contracts. The implementation has local qualification only; production query
plans and capacity still need observation during normal rollout.
