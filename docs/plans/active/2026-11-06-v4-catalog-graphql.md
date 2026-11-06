# Deliver the full V4 catalog graph with Hot Chocolate

Status: agreed design and implementation plan; implementation has not started.

This ExecPlan is a living document. Update Progress, Surprises & Discoveries,
Decision Log, and Outcomes & Retrospective as work proceeds. Paths below are
relative to the repository root unless stated otherwise.

## Purpose / Big Picture

Expose `/api/v4/graphql` as a public, read-only alternative to the V4 REST catalog,
primarily for web clients that do not need the mobile sync engine. A web page can
select artist, venue, song, show, recording, and playable-file information in one
operation. It should not need direct database access or several REST round trips
to assemble those relationships.

The deliverable is the whole catalog graph, including browsing, search,
discovery, rankings, reference resolution, and existing public activity reads.
Song history is an implementation checkpoint, not the release scope. Keep REST
available with its existing payloads, complete-collection semantics, validators,
and consistency guarantees. Account membership and playlist writes remain owned
by the User Service. This work does not migrate clients or introduce GraphQL
mutations, subscriptions, federation, or persisted operations.

Reuse the catalog's rules and public field mappings. Imported recording facts,
approved canonical interpretations, UI counts, ranking context, and typed
identities must mean the same thing through either transport.

## Progress

- [x] (2026-11-06) Inspect V4 controllers, readers, projections, read sessions,
  integration fixtures, and current Hot Chocolate documentation.
- [x] (2026-11-06) Confirm scope and execution policies with Alec and write this plan.
- [x] (2026-11-06) Incorporate the validated review: shared ranking results,
  safe refresh collection, fixed read boundaries, and normalization error precedence.
- [ ] Freeze the complete schema, capability inventory, and representative operations.
- [ ] Add Hot Chocolate hosting and shared shallow/batch read capabilities.
- [ ] Implement the complete graph, pagination, filters, and bounded execution.
- [ ] Verify REST parity, batching, consistency, failures, and realistic workloads.
- [ ] Publish integration documentation and qualify the additive endpoint for release.

## Decision Log

- 2026-11-06, Alec: GraphQL is an alternative API, primarily for web; REST remains
  available for sync-oriented clients. Full catalog coverage is required.
- 2026-11-06, Alec: Use a connected domain graph while retaining our mappings,
  rules, denormalized counts, and other UI-driving fields.
- 2026-11-06, Alec: Use explicit typed filters for artist, date/year range, venue,
  song, soundboard/FLAC availability, and relevant rating/popularity ordering.
  No additional filter dimensions are required now.
- 2026-11-06, Alec: Paginate growing collections. Keep source sets/tracks and
  normalization parts complete and ordered. Cost limits must accommodate useful
  web queries rather than forcing artificial request splitting.
- 2026-11-06, Alec: Favor performance. Keep coherent recording and membership
  reads, but independent query branches may observe different snapshots.
- 2026-11-06, Alec: Return usable partial data with field-specific errors. Start
  with pagination bounds, cost limits, and timeouts; omit persisted operations.
- Plan implementation choice: use Hot Chocolate and its GreenDonut DataLoader
  integration over Dapper/Npgsql. Keep GraphQL execution types out of shared
  catalog readers. Exact package versions and numeric limits are selected and
  recorded during implementation, not represented here as verified defaults.
- Review resolution, 2026-11-06: adapt existing shared ranking results and refresh
  collection rather than adding GraphQL-only replacements. Keep selection
  look-ahead limited to a fixed source-recording bundle; do not plan arbitrary
  descendants. Normalization failure state takes precedence over projected defaults;
  splitting its projection is an implementation option, not a requirement.

## Surprises & Discoveries

- `V4CatalogProjection` already maps materialized facts without database reads.
  Its show/source/set summary projections can serve shallow GraphQL objects;
  detail projections currently recurse through complete children.
- `CatalogRecordingReader` already batches recording children in two round trips,
  but its public entry points load a whole source or all sources for one show.
  `CatalogResolverHydrator` has further batch reads worth sharing.
- `CatalogReadSession.Open` uses the primary, a read-only repeatable-read
  transaction, and a fixed 30-second command timeout. Sessions are currently
  owned by individual reader operations, not a transport-wide transaction.
- `CatalogBrowseReader` already has session-taking batch overloads.
  `CatalogSongReader` returns complete match collections; `CatalogSearchReader`
  has six categories with fixed limits. Wrapping their output in paging middleware
  would not bound SQL work or make later pages available.
- Counts can intentionally lag live membership. Popularity varies by comparison
  population. Neither transport should redefine these facts for convenience.
- Hot Chocolate resolver/DataLoader service scopes need explicit consideration;
  an ASP.NET request-scoped registration alone does not establish connection,
  snapshot, or cache ownership across GraphQL execution.
- `PopularityRefreshRequest` currently collects populations in an unsynchronized
  `HashSet`. Parallel graph readers need one shared, concurrency-safe collector.
- Show ranking/momentum readers currently put contextual metrics directly in
  `CatalogShow.Popularity`. GraphQL's ordinary nodes/contextual edges need those
  values separated in shared reader results, not just renamed GraphQL fields.
- `V4CatalogProjection.Track` eagerly projects normalization, and the internal
  track model defaults it to `unavailable/not_interpreted`. A failed enrichment
  must be carried separately and checked before publishing that default.

## Context and Orientation

Read `docs/api/README.md` for catalog meaning, `docs/normalization-counts.md` for
count ownership, and `docs/popularity/README.md` for score context and refresh.
The contracts needed to implement this plan are summarized below.

`RelistenApi/Controllers/V4/` contains the existing route families and
`V4CatalogRegistration.cs` registers their dependencies.
`RelistenApi/Services/Catalog/` contains database readers and internal models.
`RelistenApi/Models/Api/V4/` contains public REST resources and pure projections.
`CatalogReader`, `CatalogRecordingReader`, `CatalogBrowseReader`,
`CatalogSongReader`, `CatalogSongMatchReader`, `CatalogVenueReader`,
`CatalogSearchReader`, `CatalogRankingReader`, `CatalogShowDiscoveryReader`, and
the resolver readers are the main integration points. Check actual filenames
and signatures before edits; the names proposed below are design guidance.

A show groups sources (recordings). A source contains ordered sets and playable
tracks. A `track_` ID identifies a file; a `song_` ID identifies artist-owned
canonical music; an `imported_song_` ID identifies setlist evidence. A medley is
one playable file with multiple normalization parts, not multiple seekable songs.
Imported venues, canonical venues, and artist/venue memberships are distinct.
Eras are ordered artist metadata without a separate public ID. Do not invent IDs
for them or conflate setlist-only shows with recording-backed shows.

`Relisten.Identifiers/` defines UUID-backed and native short public IDs. Preserve
their canonical strings and validation. Internal numeric joins stay private.
Canonical links require approved normalization evidence; artist feature flags
permit normalized reads but do not promise complete coverage. Merges, retirement,
unavailability, and imported fallbacks must follow existing reader policy.

`RelistenApiTests/V4/V4CatalogFixture.cs` loads synthetic catalog data. Existing
`TestV4*Http` fixtures cover public contracts, while `TestCatalogReadSession`
already exercises isolation, cancellation, disposal, and read-only transactions.
Use the disposable local databases through `./test-local.sh`.

## Interfaces and Dependencies

### Hosting and ownership

Add `HotChocolate.AspNetCore` plus the compatible DataLoader/type-generation
packages required by the selected supported release. Pin compatible versions
together in `RelistenApi/RelistenApi.csproj` and verify .NET 10 support by restoring,
building, and executing the HTTP tests. Retain Dapper and Npgsql. Automatic
`IQueryable` projection/filter middleware is not our database integration.

Put transport code under `RelistenApi/GraphQL/`: registration, explicit object and
input types, root queries, DataLoaders, pagination adapters, error mapping, and
cost configuration. Register the additive route through the actual API hosting
pipeline. Do not modify the standalone ingestion worker or account service to
host this endpoint. Introspection should describe the public graph for clients.

Keep the dependency direction explicit:

- REST controllers call catalog reader operations and then V4 projections.
- GraphQL resolvers call the same reader capabilities, normally through loaders,
  then expose projected fields and lazy relationships.
- Readers own SQL, approval gating, identity resolution, membership, and metadata
  hydration. They accept ordinary domain inputs, never GraphQL AST/context types.
- Projections map already loaded facts. They neither query nor decide which
  collections to load. GraphQL type definitions own traversal and nullability.

### Complete schema and capability inventory

The first milestone produces a checked-in SDL schema and executable operation
examples under `docs/api/graphql/`. Cover every existing V4 public read capability,
including convenience lookups, rather than requiring one GraphQL field per REST
route. Record each controller action's graph equivalent in an inventory file.

The inventory must include these families:

- Artists, provider metadata, features, years, eras, tours, and their navigation.
- Shows, sources, sets, playable tracks, source links, and reviews; parent and
  child edges, UUID-backed lookups, artist/date and year-number lookups, and random
  show selection.
- Canonical songs, imported songs, song aliases, exact file/show matches and
  timing statistics, imported setlist membership, and normalization parts.
- Global venues, imported venues, artist/venue memberships, aliases, parent
  venues, slug lookups, and their show collections. Expose existing catalog
  relationships, not speculative new people/taper identity models.
- Search categories, anniversary/recent/top discovery, global and artist-local
  popular/trending rankings, momentum, and grouped artist rankings.
- Saved-reference resolution for supported identity schemes, preserving input
  order, duplicates, survivor redirects, availability, and retirement reasons.
  Expose resolved targets as typed graph objects; the web client need not consume
  mobile's patch-array envelope to traverse them.
- Existing public live summary/history reads. Activity event selection and
  catalog hydration retain their best-effort consistency boundary. Play-event
  ingestion stays on its existing REST write route.

Use one public graph type per domain concept, such as `Show`, `Source`,
`SourceSet`, `SourceTrack`, and `Song`, with explicit relationship resolvers.
Summary/detail REST classes need not become separate GraphQL types. Browse
entries still express the server's canonical/imported choice through explicit
union/member types or a documented entry wrapper. Keep useful scalar ID fields
alongside traversable objects so callers can request either cheaply.

Bind existing ID value types explicitly to canonical string-valued GraphQL IDs,
with entity-specific argument validation. GraphQL's `ID` alone does not validate
prefixes. Define date/time and enum mappings explicitly. GraphQL field names use
camelCase; REST JSON names and converters stay unchanged. Popularity windows need
legal GraphQL names such as `hours48`, `days7`, and `days30`.

Retain all useful mapped scalar fields, including counts, features, URLs, titles,
durations, rating fields, aliases, and normalization state. A source's imported
venue observation must not silently become its show's canonical venue.

This proposed operation illustrates the target client experience. Names are to
be frozen in milestone 1; it is not executable against the current API:

```graphql
query SongHistory($id: ID!, $after: String) {
  song(id: $id) {
    id
    name
    matchedShowCount
    matchedTrackCount
    shows(first: 50, after: $after) {
      nodes {
        show {
          id
          displayDate
          sourceCount
          artist { id name }
          importedVenue { id name }
        }
        timings {
          singleSong { sourceCount median }
          multiSong { sourceCount median }
        }
        tracks(first: 20) {
          nodes {
            id
            title
            duration
            mp3Url
            source { id isSoundboard avgRating }
          }
          pageInfo { endCursor hasNextPage }
        }
      }
      pageInfo { endCursor hasNextPage }
    }
  }
}
```

Here `song.shows.nodes` are song/show relationship objects, not plain shows:
timings and matching-file membership belong to that relationship. A separate
playback query can select `source.sets { tracks { ... } }` as complete ordered
lists. Both use the same `SourceTrack` type and track projection.

### Shared readers and projections

Extend the current domain readers with session-taking batch and page operations.
Existing REST entry points continue to open a session and compose the complete
response in it. Both transports call the same membership predicates, count
queries, normalization metadata readers, ordering definitions, and projections.
REST must not become a loop over GraphQL pages or an internal HTTP client.

Extract reusable entity batches from `CatalogResolverHydrator` into the owning
readers when GraphQL needs them. Keep the resolver's lookup-plus-hydration
transaction intact. Avoid a second SQL copy under `GraphQL/` and avoid exposing
an entire resolver hydration just to fetch one entity type.

For shallow show/source/set values, use `ShowSummary`, `SourceSummary`, and
`SetSummary`. For complete tracks, use `Track`; reuse browse, song, venue, and
ranking projection functions similarly. Where a mapping only exists inside a
recursive detail projection, extract a coherent public-field projection shared
by REST and GraphQL. Do not maintain parallel field-mapping implementations.

GraphQL may wrap a projected value with materialized relationship results and
load-state information. An unrequested collection is not an empty collection.
Use explicit loaded/unloaded state in these internal wrappers so a shallow
resolver patch cannot seed a loader as though it contained complete children.
Expose only explicitly bound public fields; internal keys and load state must
never appear in introspection or JSON.

The normalization field resolver must check failed/unloaded state before reading
the projected normalization value. Failed enrichment returns null with an error
at that field; an unloaded value must be loaded before publication. Neither may
expose the track model's default as an observed interpretation. Keep `Track` as
the shared mapping when its eager projection is safe. Extract a shared normalization
projection only if needed to avoid eager work or projection exceptions; REST's
`Track` then composes the same mappings unchanged. Do not require a parallel
base-track DTO or copy playback-field assignments into GraphQL.

For show rankings and momentum, adapt `CatalogRankingReader` to return selected
ranking metrics separately from ordinary show metadata, following its existing
`CatalogRanked<T>` pattern for artist/year results. Hydrate ordinary show metadata
and contextual metrics in the same read session. REST's ranking projection composes
the contextual `Popularity` exactly as today; GraphQL projects ordinary nodes and
uses the shared metrics projection on ranking edges. Extract a projection overload
where necessary rather than duplicating score mappings or issuing a second
GraphQL-only ranking query. Ordinary catalog objects must not be mutated into
contextual ranking objects or cached as such.

Domain request/result types belong in `Services/Catalog/`, for example
`CatalogPageRequest`, `CatalogPage<T>`, typed show filters, and sort enums.
Keep them small and specific. GraphQL adapters convert arguments/cursors into
these types. REST can reuse the primitives without adopting GraphQL pagination
or changing its existing response contract.

For example, an owning reader can offer a session-taking
`ReadShowPage(session, artistUuid, filters, order, page, loadOptions, cancellationToken)`
and a `ReadSourceRecordings(session, sourceUuids, loadOptions, cancellationToken)`
batch. The caller owns/disposes the supplied session; the reader returns only
materialized domain data. Reader methods that open their own session remain
convenient transport-independent entry points. Prefer cohesive filter/load
records to growing lists of boolean arguments. Implement only load options that
save a meaningful domain read; ordinary scalar field selection need not change
the SQL column list.

### Batching and request-local caches

Use Hot Chocolate DataLoaders for entity IDs and relationship requests. A batch
fetch owns its database session and disposes it before returning materialized
results. Group duplicate keys and use parameterized array reads or bounded
per-parent SQL. Chunk unusually large batches by an explicit batch-size limit.

Use separate loader responsibilities for shallow entities, complete source
recordings, and paginated relationships. A relationship cache key includes parent
identity, normalized filters, sort, cursor/page size, and relevant scoring/load
context. Entity-only keys are safe only for a fixed representation and policy.

Do not issue one database operation per parent and call it batching. For nested
`artists { shows(first: ...) }`, select a bounded page per artist, for example
using parameterized parent inputs with `LATERAL` subqueries or partitioned SQL.
Do not apply one global `LIMIT` and accidentally divide it between parents.
Materialize selected IDs first, then batch metadata only for those IDs.

Loaders are scoped to one GraphQL operation, including when an HTTP request could
contain multiple operations. Prefer disabling HTTP operation batching initially
so budgets and cache ownership remain unambiguous. Resolver DI scopes and loader
fetch scopes must not accidentally construct duplicate operation caches. Test the
selected Hot Chocolate registration through the real execution pipeline.

Loaded children can satisfy parent/back-reference traversal from their owning
materialized result. Prime caches only when representation, completeness, scoring
context, and consistency boundary agree. Never reuse a global ranking score as
an ordinary show's artist-local popularity. Prefer ranking edges containing
`rank` and contextual `metrics`, with a node exposing ordinary catalog fields;
use the shared ranking result and projections described above. Separating graph
fields alone does not correct an already overwritten `CatalogShow.Popularity`.

### Pagination, filtering, and counts

Growing lists use forward cursor connections with `first`, `after`, `nodes`,
`edges`, and `pageInfo`. Implement paging inside SQL; retrieve at most the page
size plus one membership row to establish `hasNextPage`. Use Hot Chocolate's
manual connection adapter for already paged Dapper results. Do not attach paging
to a fully materialized REST collection and imply the database work is bounded.

A cursor encodes a version, connection/parent scope, canonical filter/sort
fingerprint, and the complete ordering tuple. Validate length, format, version,
scope, and value types before SQL. Treat decoded values as untrusted parameters.
Use deterministic keyset ordering with a stable identity tie-breaker, explicit
null ordering, and a stable collation where text participates. A different parent
or filter must reject the cursor instead of returning a misleading page.

Date-ordered show pages might use `(display_date, uuid)`; source ranking needs
the rating value, null category, and UUID. Choose and test each actual ordering.
Rankings with changing scores and ordinary pages across multiple HTTP requests
are live views: inserts, deletes, or sort-key changes may move results. No cursor
holds a database transaction between pages or promises an export snapshot.

Bound artists, shows, sources-per-show, reviews, song matches, venue memberships,
and other growing navigation. Keep a source's sets and each set's tracks complete
and ordered, along with normalization parts and small value lists such as aliases.
Song-show matching tracks are a growing filtered relationship, not a complete
source recording, and therefore use a connection too. Never silently truncate a
source recording to satisfy a page or response budget.

Map GraphQL filter inputs to explicit domain filters: artist, date/year range,
venue, song, soundboard and streamable-FLAC availability. Support relevant rating
and popularity sorts with a declared comparison context. Document soundboard/FLAC
show filters as existence tests over sources, not implicit pruning of all sources
returned beneath the show. Source filters apply to source collections explicitly.
Retain imported/canonical differences in venue and song filters. Song filters
use exact recording matches; imported setlist evidence cannot invent playable
files. Fix combinations and parameter validation in the schema inventory before
implementation. Do not expose arbitrary SQL columns/operators.

Keep literal-substring search and existing canonical/imported fallback rules.
Give each selected search category its own SQL-backed connection and cursor;
unselected categories should not execute. Share category predicates and hydration
with REST, which keeps its existing bounded response shape.

Domain counts retain their meaning: a venue's global count ignores a filtered
`shows` field, artist-venue counts retain artist scope, and imported groups may
overlap canonical groups. Song match counts remain stored facts that can briefly
lag live membership. Never compute a count from the returned page length.
Provide a separate selected `totalCount` for connections where useful; calculate
it with the same filters and membership snapshot as the page, and charge its
database cost separately. Do not force an exact count when it was not selected.

### Snapshot consistency and parallel execution

The consistency unit is a materialized domain read, not the entire GraphQL
operation. A unit owns one `CatalogReadSession`; commands within it execute
sequentially on its Npgsql connection. Independent units may run concurrently
under bounded connection concurrency. Never register one open Npgsql transaction
for every resolver or parallelize commands on a shared connection.

Use these explicit units:

- A shallow entity batch loads its scalar facts and required policy/approval
  decisions together. A track's normalization state and requested parts share
  its recording facts' snapshot.
- A complete source read loads source metadata, sets, tracks, requested parts,
  imported observation metadata, and ordering in the same snapshot.
- A relationship page selects membership and hydrates immediate node metadata,
  edge statistics, and requested filtered count in one snapshot. Song-show
  timing statistics use the same membership rules as REST; page statistics
  should be restricted to selected show IDs where possible.
- Reference resolution keeps identity lookup, survivor selection, availability,
  and target hydration together. Ranking selection, contextual scores, and node
  hydration likewise share one unit.

Keep those boundaries fixed. A show page does not absorb its nodes' source
connections or any deeper descendants. Each independently resolved relationship
uses its own batched page/read unit, with its own arguments and snapshot.

Limit selection look-ahead to choosing the fixed source-recording bundle at a
source-producing resolver. If an immediate source node selects `sets`, materialize
its header and complete ordered recording children together before exposing it.
A source page can use that bundle for its immediate nodes within the page's
session; a show page cannot recursively plan it through descendant connections.
Honor fragments, directives, and aliases when choosing the bundle, but do not
plan their arbitrary descendant reads. If a recoverable child read fails after
the header loads, retain the header and mark the recording field failed using
the error rules below. Never substitute children from another snapshot to make
the bundle appear complete. Do not build a general query planner or pass GraphQL
selections into shared domain readers.

Materialized values carry their already loaded relationships through resolution;
nested fields consume them without reopening sessions. Do not overwrite their
children with a request cache result from another unit. Independent traversal
such as `track.show` may load a new show unit; it does not retroactively make the
track and that show's entire graph one snapshot. Repeated aliases with different
arguments can therefore legitimately observe different catalog versions.

Close each session as soon as the unit materializes. There is no exported
snapshot, transaction per page lifetime, or global request transaction. DataLoader
deduplication is an execution optimization, not a whole-response consistency
promise. Public activity reads retain their separate event/catalog boundary.

### Errors, cancellation, refresh, and execution budgets

Make independently fallible relationships nullable at their error boundary,
including connection fields and complete recording lists where necessary.
Non-null propagation must not erase a valid source merely because optional
canonical enrichment fails. Empty means a successful read with no members;
failure means null plus a path-specific GraphQL error. Null without an error is
valid for an absent optional link, such as no approved canonical mapping.

Map existing invalid-ID, retired, normalization-unavailable, and catalog-unavailable
conditions to stable `extensions.code` values. Unknown root IDs may return null
without an error; document that convention. Explicit reference resolution retains
its richer per-input availability results. Validation and over-budget operations
must fail before catalog SQL executes; execution failures use partial-data
semantics rather than REST ProblemDetails envelopes. Mask database details.

An optional enrichment failure must not publish a partial or false normalization
state. Load optional enrichment within a guarded savepoint if a recoverable
database error must preserve already read base facts in the same snapshot; roll
back to it before any further command. Return a failed-field marker, not an empty
mapping. Connection loss, command cancellation, or a failed base read fails its
whole unit. Never continue using an aborted transaction. Keep this mechanism
local to the readers that need it, not a generic exception-swallowing framework.

Link HTTP abort and the GraphQL execution deadline into every reader/loader call
and Dapper command. Allow a per-session command timeout bounded by remaining
execution time without changing REST's existing default. A timeout must cancel
database work and dispose connections, not merely stop awaiting it. No automatic
SQL retries are introduced; retries could change the unit's observed snapshot.
If the remaining deadline is exhausted, reject the command before dispatch;
never translate it into Npgsql's zero/infinite timeout value.

Preserve existing demand-driven popularity refresh behavior by adapting
`PopularityRefreshRequest`, not replacing it. Supply exactly one collector per
GraphQL operation to every participating reader, including readers resolved in
separate resolver/fetch DI scopes. With HTTP operation batching disabled, the
HTTP request can own that instance, but registration must explicitly reuse it
across those child scopes; a scoped registration by itself is insufficient.

Synchronize its small requirement set with a lock and copy the set under that
lock before freshness lookup. Finalize once, after all participating read units
finish and close their sessions, so every required population is included. Do
not enqueue per field or hold a snapshot while talking to Hangfire. Keep REST
using the same collector and current enqueue behavior. A GraphQL refresh
scheduling failure is reported/logged separately and must not replace available
catalog data. No new refresh framework or global collector is needed.

Configure explicit page/ID-batch ceilings, query field/type costs, execution and
SQL timeouts, document size/depth limits, and bounded database concurrency. Tune
these against the workloads below. No persisted-operation requirement or
unconditional conservative library defaults should block ordinary web pages.

Charge actual work categories: shallow scalar projections are cheap; relationship
reads, counts, statistics, and search have database weights. Account for list
fan-out, repeated aliases, and complete sets/tracks/parts. Static size estimates
for complete recordings must be accompanied by runtime materialized-row/response
budgets; exceeding a budget fails the relevant field explicitly and never
silently slices its tracks. Test unusually long real-shaped synthetic recordings
so complete lists remain practical. Cost rejection should explain the applicable
limit. Return GraphQL HTTP responses with an explicit cache policy, initially
`no-store`; REST ETags cannot validate arbitrary selection sets.

## Plan of Work

### Milestone 1: freeze the graph and observable workloads

Write `docs/api/graphql/schema.graphql`, `capabilities.md`, and named `.graphql`
operations. The capability inventory maps all `Controllers/V4` read actions to
the schema and records preserved semantics, collection policy, reader ownership,
count meaning, and consistency unit. It is a coverage aid, not a source-string
test. Document typed filter combinations and scoring contexts.

Examples must cover artist landing/navigation, show playback with all selected
sources, venue browsing across artists, song history with playable match files,
mixed-ID playlist-builder hydration, search, rankings, reference redirects, and
public activity. These are client operations, not persisted-operation setup.
Proof: validate every example against the proposed schema; account for every
existing public read capability. Resolve any incompatible naming or filter
semantics here before building resolvers.

### Milestone 2: host Hot Chocolate and share the read primitives

Add hosting/registration, canonical ID and scalar mappings, explicit graph types,
error handling, and configurable budgets. Add shared shallow entity and complete
source batches, domain page/filter types, and projection extractions where needed.
Include operation-wide refresh ownership/safe collection and the shared ranking
result adaptation. Keep current REST entry points composing complete results.
Exercise artist, show, source, and track queries through actual GraphQL HTTP execution.

Proof: GraphQL and REST agree on IDs, public scalar values, playback URLs,
ordering, and approved normalization for synthetic fixtures. A shallow show
selection does not read source sets/tracks; a requested complete source has all
its ordered tracks. Existing REST contract tests still pass.

### Milestone 3: complete relationships and SQL pagination

Implement the rest of the inventory using shared readers: navigation and parents,
canonical/imported overlays, song timings, venues, paginated search, discovery,
rankings, reference resolution, and public activity. Add loaders keyed by the
appropriate representation/arguments, per-parent paging, and the fixed consistency
units above. Maintain a complete schema as internal pieces become executable; do not
ship song history as the finished catalog endpoint.

Proof: every documented operation executes through HTTP against fixtures. Traverse
forward and backward edges, request aliases with different filters, follow merges,
and verify disabled/partial normalization and setlist-only identities. Walk all
connection pages on a fixed dataset and compare exact membership/order with the
equivalent existing reader result. No missing or duplicated boundary rows.

### Milestone 4: qualify execution, failure, and performance behavior

Instrument GraphQL operation duration, field/type cost, SQL command count/time,
batch sizes, rows materialized, response bytes, timeout/rejection counts, and
connection concurrency. Avoid logging raw query variables or SQL parameters by
default. Extend the application's existing telemetry rather than introducing a
separate reporting system.

Use synthetic small and large fixtures: artist landing pages with 100 show
summaries, 50 song-history shows with matching playback files, a complete show
with several source hierarchies, and 250 mixed track IDs for playlist building.
These sizes are acceptance workloads, not proposed universal page ceilings.
Include unusual long recordings, nested parents, and combined filters. Compare
with REST requests needed to obtain equivalent information. Record actual costs,
SQL counts, bytes, and latency percentiles with dataset size and concurrency.

Set page limits and budgets so all intended operations succeed with documented
headroom. Start with at least twice the largest representative operation's
measured field/type costs, then verify that deliberately excessive fan-out is
rejected and that allowed traffic stays within the database/latency budget.
If those requirements conflict, improve the read/query before lowering useful
page sizes. Document chosen numeric limits, measurement environment, and reasons
in `docs/api/graphql/performance.md`; do not claim synthetic latency is production
capacity. Validate selected indexes/query plans with disposable local data; any
production checks remain read-only.

### Milestone 5: document and release the additive API

Update `docs/api/README.md` and add `docs/api/graphql/README.md` with operations,
error examples, pagination/live-view behavior, count meanings, consistency,
limits, and web integration guidance. Export the executable schema and reconcile
it with the reviewed SDL. Update the capability inventory with verification.

Proof: a web caller can assemble the representative pages with the documented
operations and no required REST fallback for catalog information. REST regression
checks and all GraphQL acceptance cases pass. Record qualification results and
remaining operational gaps before normal deployment; writing this plan does not
authorize production deployment or client migration.

## Concrete Steps

Run commands from `/Users/alecgorge/code/relisten/RelistenApi`.

1. Inventory `rg -n '\[Http(Get|Post)' RelistenApi/Controllers/V4` and inspect each
   action to distinguish public reads from writes. Inspect reader SQL and existing
   tests, not just route names. Create the milestone 1 schema and operations.
2. Add the selected compatible Hot Chocolate packages, then run `dotnet restore
   RelistenApi.sln` and `dotnet build RelistenApi.sln`. Record actual versions.
3. Implement focused milestones and use `./start-local-databases.sh` followed by
   `./test-local.sh --filter 'FullyQualifiedName~RelistenApiTests.V4'`. Add new
   GraphQL fixtures under `RelistenApiTests/V4/GraphQL/` using the shared fixture
   and synthetic upstream data. Expected result: all selected tests pass with
   integration cases executed, not skipped.
4. Run `dotnet run --project RelistenApi/RelistenApi.csproj` and submit the example
   documents to `/api/v4/graphql`. For example, use the repository's actual local
   base URL (currently documented as `http://localhost:3823`) and a JSON body with
   `query` and `variables`. Save representative success/error responses after
   implementation; examples here are not claims of a running endpoint.
5. Run `dotnet build RelistenApi.sln` and the full `./test-local.sh` after shared
   reader changes are complete. Restore the local formatter with `dotnet tool
   restore`; use `dotnet csharpier check .`, format changed C# if needed, and
   review the final diff before any commit or push. Run `git diff --check` for docs.

## Validation and Acceptance

Tests must exercise public behavior through GraphQL execution and real local SQL
where SQL semantics are at risk. SDL snapshots supplement those tests; they do
not prove batching, pagination, or domain parity.

- **Mapping parity:** compare REST/GraphQL fixture values for imported facts,
  canonical links, counts, durations/timing buckets, aliases, playback URLs,
  orphan exclusion, survivor IDs, and each score context. Keep REST complete-list
  and ETag behavior covered by its existing HTTP tests.
- **Ranking reuse:** request the same show through ordinary, global-ranking, and
  organic-momentum paths in one operation. Its ordinary node popularity stays
  artist-local while edge metrics retain their declared contexts. REST rankings
  retain their current contextual fields through the same shared reader results
  and metric mappings; no GraphQL-specific ranking SQL is introduced.
- **Batching and selection:** scale the same operation from one to many parents.
  Commands grow by batch chunks/selected relationship kinds, not one per entity.
  Assert bounds from recorded database executions rather than private call order.
  Aliases with different pagination/filter/score contexts do not contaminate one
  another; omitted children/counts/search categories cause no corresponding reads.
- **Pagination:** use equal sort keys, nulls, multiple parents, empty collections,
  zero/negative/excessive page sizes, malformed/cross-scope cursors, and filtered
  counts. Rows with the same date on opposite sides of a page boundary survive.
  Stable data gives a complete duplicate-free traversal with SQL-bounded pages.
- **Consistency:** coordinate a disposable-database writer between membership
  selection and hydration. One source or page stays on its original snapshot;
  an independent later unit can see the update. Fragments/directives/aliases
  select only the fixed source bundle when needed; source connections with
  different cursors/filters remain independent. A show page does not prefetch
  its descendants. Reusing an ID from another unit's loader cache must not
  replace an already materialized coherent child collection.
- **Failure and recovery:** inject normalization failure, a failed relationship,
  SQL cancellation, connection failure, and refresh-enqueue failure. Unaffected
  fields survive with correct error paths; failures never become empty results.
  After disposal, a fresh operation succeeds using a small connection pool.
  Savepoint recovery, where used, proves the transaction remains usable; whole
  unit failures never publish half a recording. On recoverable normalization
  failure, playback fields survive, normalization is null with the correct error
  path, and the default `not_interpreted` value is never exposed. Successful
  disabled/not-interpreted states still use their existing shared mapping.
- **Refresh collection:** execute parallel branches requiring distinct populations
  through actual resolver/fetch scopes. The final freshness lookup receives their
  union and enqueues at most one refresh job. A following operation starts with
  a fresh collector. Existing REST refresh behavior remains covered.
- **Budgets:** intended workload documents pass; variable-driven large pages,
  recursive fan-out, repeated expensive aliases, huge ID inputs, and over-depth
  documents are bounded/rejected. Static rejection executes no catalog SQL.
  Runtime limits cancel work, release leases, and return explicit errors without
  silently truncating complete lists. Run allowed operations concurrently to
  verify pool pressure stays bounded.
- **Readability:** trace artist-to-show-to-source-to-track success, a failed
  optional canonical edge, and a successful retry after cancellation from the
  HTTP entry point. Session ownership, load completeness, membership selection,
  projection, and error publication should be visible without reconstructing a
  generic framework. Review readability separately from passing tests.

## Idempotence and Recovery

Development and test runs use disposable local databases and synthetic data.
Do not import real catalogs, start production workflows, backfill counts, or
change normalization approval to test this endpoint. Index migrations, if query
plans establish a need, require a concrete non-destructive rollout plan before
being included; this design requires no data rewrite or identity migration.

The API addition is reversible by removing/disabling its route registration.
Shared reader changes must continue to satisfy REST contracts. Keep meaningful
refactoring and GraphQL additions in reviewable commits so rollback does not
depend on restoring discarded data. Failure does not require catalog repair;
each new read starts a new unit, and canceled sessions release their leases.
Retain existing popularity refresh deduplication and ownership.

## Artifacts and Notes

Implementation evidence belongs under `docs/api/graphql/`: schema, capability
inventory, named operations, selected success/error responses, and performance
results with configuration. Document any capability gaps explicitly; the release
must not describe a partial graph as complete.

Primary library references checked while drafting (2026-11-06):

- [Hot Chocolate DataLoader](https://chillicream.com/docs/hotchocolate/fetching-data/batching/dataloader): batching and cache integration.
- [Hot Chocolate pagination](https://chillicream.com/docs/hotchocolate/fetching-data/pagination): connection types and manual integration.
- [Hot Chocolate dependency injection](https://chillicream.com/docs/hotchocolate/resolvers/dependency-injection): resolver/fetch service lifetimes.
- [Hot Chocolate cost analysis](https://chillicream.com/docs/hotchocolate/security/cost-analysis): list-size metadata and configurable operation budgets.

These references guide library wiring; the catalog and consistency contracts in
this plan remain application-owned. Verify concrete API signatures against the
package version actually selected.

## Outcomes & Retrospective

Planning completed on 2026-11-06. No packages, readers, endpoints, tests, deployed
configuration, or production data were changed by this planning task. Numeric
limits, measured capacity, and implementation evidence remain work for the
milestones above. Product scope and consistency/error policies are settled.
The validated review is incorporated: shared ranking results and safe refresh
collection are explicit implementation tasks, look-ahead is limited to the fixed
source bundle, and normalization error state overrides projected defaults. These
changes preserve the shared REST reader/projection design.
