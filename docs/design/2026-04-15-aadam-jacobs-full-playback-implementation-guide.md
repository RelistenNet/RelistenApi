# Aadam Jacobs Full Playback Implementation Guide

> **For agentic workers:** Use this as the implementation checklist for enabling the Aadam Jacobs Collection as playable Relisten content. Keep changes scoped to AJC and do not include Taper's Section in this pass.

**Goal:** Import the Aadam Jacobs Collection into normal Relisten artist/show/source playback while keeping collection browsing scoped, fast, and safe for mobile clients.

**Architecture:** AJC is a trusted Archive.org collection. The backend creates or reuses canonical artists, imports each collection item through an item-specific Archive.org importer, links imported sources back to the collection, and exposes collection-scoped browse endpoints that mirror artist browse endpoints across all artists in the collection.

**Tech Stack:** ASP.NET Core controllers, Dapper data services, SimpleMigrations, Hangfire jobs, PostgreSQL, Redis-backed popularity caches, NUnit/FluentAssertions tests.

**Non-goals:** No Taper's Section import, no public unresolved-item browse surface, no collection-level venues, no collection-level songs, no collection show-detail endpoint.

---

## Component Boundaries

Keep ownership narrow. Do not turn `ArchiveOrgImporter` or `CollectionsController` into orchestration catch-alls.

- `ArchiveOrgImporter`: owns Archive.org metadata parsing and source/review/link/set/track writes for one resolved artist and one archive identifier.
- `AadamJacobsCollectionImporter`: owns the AJC batch workflow: fetch items, resolve artists, call the item importer with rate limiting, rebuild affected artists, update collection item status, and rebuild collection years.
- `ArchiveCollectionResolver`: owns creator-to-artist decisions and writes `collection_artist_mappings`.
- `CollectionService`: owns collection, item, artist, year, and on-this-day queries.
- `PopularityService`: owns ranking and popularity metrics for collection-scoped show/year lists.
- `CollectionsController`: owns HTTP routing, query parsing, response status, and delegation only.
- `ArtistService`: owns artist filtering, delta queries, and `api_updated_at` writes for artist metadata/features; import jobs call it after show/source-count changes.

---

## Naming Decision

Use the long-term generic collection table names from the broader design: `collections`, `collection_items`, `collection_artist_mappings`, and `collection_years`.

AJC is the first imported collection, not a separate schema family. Do not introduce `archive_collections` or `archive_collection_items` tables that would need to be renamed or migrated when Taper's Section or single-artist Archive.org collections are added later.

---

## Endpoint Contract

Collection browse endpoints should use the same mental model as artist endpoints, but scoped to all imported artists/sources in one collection.

Implement these v3 endpoints:

- `GET /api/v3/artists/delta?since={timestamp}`
- `GET /api/v3/collections`
- `GET /api/v3/collections/{collectionUuidOrSlug}`
- `GET /api/v3/collections/{collectionUuidOrSlug}/artists`
- `GET /api/v3/collections/{collectionUuidOrSlug}/years`
- `GET /api/v3/collections/{collectionUuidOrSlug}/years/{yearUuidOrYear}`
- `GET /api/v3/collections/{collectionUuidOrSlug}/shows/popular-trending`
- `GET /api/v3/collections/{collectionUuidOrSlug}/shows/on-this-day?month={1-12}&day={1-31}`

Do not add `GET /api/v3/collections/{collection}/shows/{showUuid}`. Show detail should use the existing `GET /api/v3/shows/{uuid}` endpoint after collection rows return show UUIDs.

The collection endpoints should return normal `ArtistWithCounts`, `Show`, and source-linked show payload shapes where possible. Add a collection-specific year object because collection years aggregate across many artists.

```csharp
public sealed class CollectionSummary
{
    public Guid uuid { get; set; }
    public string slug { get; set; } = null!;
    public string upstream_identifier { get; set; } = null!;
    public string name { get; set; } = null!;
    public int item_count { get; set; }
    public int artist_count { get; set; }
    public int show_count { get; set; }
    public int source_count { get; set; }
    public DateTime? indexed_at { get; set; }
}

public sealed class CollectionDetail : CollectionSummary
{
    public string? description { get; set; }
}

public sealed class CollectionYear
{
    public Guid uuid { get; set; }
    public Guid collection_uuid { get; set; }
    public string year { get; set; } = null!;
    public int artist_count { get; set; }
    public int show_count { get; set; }
    public int source_count { get; set; }
    public long duration { get; set; }
    public double? avg_duration { get; set; }
    public double? avg_rating { get; set; }
    public PopularityMetrics? popularity { get; set; }
}

public sealed class CollectionYearWithShows : CollectionYear
{
    public List<Show> shows { get; set; } = new();
}

public sealed class CollectionPopularTrendingShowsResponse
{
    public Guid collection_uuid { get; set; }
    public string collection_slug { get; set; } = null!;
    public string collection_name { get; set; } = null!;
    public IReadOnlyList<Show> popular_shows { get; set; } = Array.Empty<Show>();
    public IReadOnlyList<Show> trending_shows { get; set; } = Array.Empty<Show>();
}

public sealed class ArtistDeltaResponse
{
    public DateTime server_timestamp { get; set; }
    public IReadOnlyList<ArtistWithCounts> artists { get; set; } = Array.Empty<ArtistWithCounts>();
}

public sealed class ArchiveItemImportResult
{
    public ArchiveCollectionItemImportStatus status { get; set; }
    public Guid? source_uuid { get; set; }
    public string? skip_reason { get; set; }
    public string? error_message { get; set; }
}
```

Use deterministic collection-year UUIDs based on stable collection identity, not the database integer id: `md5(collection_uuid || '::collection_year::' || year)::uuid`.

For `GET /api/v3/artists/delta`, capture `server_timestamp` from the database before reading changed rows, query `api_updated_at > since AND api_updated_at <= server_timestamp`, and return that captured timestamp. Clients advance their cursor to `server_timestamp`; artists updated after that bound are returned by the next delta request.

`include_collection_derived=true` includes artists with the `CollectionDerived` bit even when they also have `AutoCreated`. `include_autocreated=true` continues to include non-collection auto-created artists. With neither flag, default artist lists exclude both auto-created-only artists and collection-derived artists.

Use these integer-backed import statuses in `collection_items.import_status`:

```csharp
public enum ArchiveCollectionItemImportStatus
{
    Pending = 0,
    LinkedExistingSource = 1,
    ImportedSource = 2,
    Skipped = 3,
    ImportError = 4
}
```

---

## Data Model Contract

Use these tables as the source of truth for the feature. Keep `collection_years` derived and rebuildable from linked collection items; do not treat it as canonical import state.

Use UUIDs for relationships to Relisten domain entities and collection tables. The only integer foreign key in this plan is `upstream_source_id`, because the existing `upstream_sources` table has no UUID column.

### `collections`

- `uuid uuid primary key`
- `slug text not null unique`
- `upstream_source_id integer not null references upstream_sources(id)`
- `upstream_identifier text not null unique`
- `collection_type text not null` - for AJC use `taper_archive`
- `name text not null`
- `description text`
- `item_count integer not null default 0` - final number of active distinct item identifiers from the most recent completed index run
- `indexed_at timestamptz` - last successfully completed collection index run
- `last_imported_at timestamptz` - last successfully completed source import/link run
- `created_at timestamptz not null`
- `updated_at timestamptz not null`

### `collection_items`

- `collection_uuid uuid not null references collections(uuid) on delete cascade`
- `upstream_identifier text not null`
- `title text not null`
- `creator_raw text`
- `date_raw text`
- `display_date text`
- `year integer`
- `artist_uuid uuid references artists(uuid)`
- `show_uuid uuid references shows(uuid)`
- `source_uuid uuid references sources(uuid)`
- `import_status integer not null`
- `import_error text`
- `last_seen_at timestamptz not null` - latest index run that saw this item in Archive.org
- `removed_at timestamptz` - set after a successful index run when the item is absent from the latest scrape
- `last_imported_at timestamptz`
- `created_at timestamptz not null`
- `updated_at timestamptz not null`
- Primary key: `(collection_uuid, upstream_identifier)`

### `collection_artist_mappings`

- `collection_uuid uuid not null references collections(uuid) on delete cascade`
- `creator_name text not null`
- `artist_uuid uuid references artists(uuid)`
- `canonical_name text not null`
- `blocked boolean not null default false`
- `block_reason text`
- `decision_source text not null`
- `created_at timestamptz not null`
- `updated_at timestamptz not null`
- Primary key: `(collection_uuid, creator_name)`

### `collection_years`

- `collection_uuid uuid not null references collections(uuid) on delete cascade`
- `uuid uuid not null unique`
- `year text not null`
- `artist_count integer not null`
- `show_count integer not null`
- `source_count integer not null`
- `duration bigint not null`
- `avg_duration double precision`
- `avg_rating double precision`
- `updated_at timestamptz not null`
- Primary key: `(collection_uuid, year)`

`show_count` is `count(distinct show_uuid)`. `source_count` is `count(distinct source_uuid)`. Only active collection items (`removed_at is null`) with a non-null `source_uuid` contribute to collection years. The unique `uuid` supports API identity; the primary key supports deterministic rebuild/upsert by collection and year.

Do not configure automatic nulling on `artist_uuid`, `show_uuid`, or `source_uuid`. Those foreign keys should use normal restrict/no-action behavior. Delete and prune paths must make ownership explicit in application code: protect active collection-linked sources from artist-wide pruning, and only clear links for inactive or deliberately repaired collection items before deleting the referenced source/show/artist.

### Required indexes

- `collection_items(collection_uuid, removed_at, import_status)`
- `collection_items(collection_uuid, removed_at, year)`
- `collection_items(collection_uuid, removed_at, artist_uuid)`
- `collection_items(collection_uuid, removed_at, show_uuid)`
- `collection_items(collection_uuid, removed_at, source_uuid)`
- `collection_items(source_uuid)` for source-to-collection reverse lookups
- `collection_artist_mappings(collection_uuid, artist_uuid)`
- `collection_years(collection_uuid, uuid)`

---

## Step 1: Schema, Flags, And Delta-Sync Foundation

**Purpose:** Put the database and artist-list contract in place before importing new AJC artists. This prevents the mobile app from needing another full artist-list replacement after the collection import.

**Files:**
- Create: `RelistenApi/Migrations/11_AddCollectionsAndArtistDelta.cs`
- Modify: `RelistenApi/Models/Artist.cs`
- Modify: `RelistenApi/Services/Data/ArtistService.cs`
- Modify: `RelistenApi/Controllers/ArtistsController.cs`
- Test: `RelistenApiTests/Collections/TestArtistDeltaSync.cs`
- Test: `RelistenApiTests/Collections/TestArchiveCollectionSchema.cs`

**Checklist:**
- [ ] Add `ArtistFeaturedFlags.CollectionDerived = 1 << 2`.
- [ ] Add `artists.api_updated_at timestamptz` as nullable first.
- [ ] Backfill `artists.api_updated_at = artists.updated_at`.
- [ ] Set the default to `timezone('utc', now())` for future rows.
- [ ] Set `artists.api_updated_at` to `not null`.
- [ ] Add an index on `artists(api_updated_at)`.
- [ ] Add `collections`.
- [ ] Add `collection_items` with nullable `artist_uuid`, `show_uuid`, and `source_uuid`.
- [ ] Add `collection_artist_mappings` keyed by `(collection_uuid, creator_name)`.
- [ ] Add `collection_years` keyed by `(collection_uuid, year)` for the derived collection-level year object.
- [ ] Use `on delete cascade` only for collection-owned rows via `collection_uuid`. Use normal restrict/no-action behavior for `artist_uuid`, `show_uuid`, and `source_uuid`.
- [ ] Add explicit delete/prune handling for collection-linked domain rows: active collection-linked sources are protected from artist-wide pruning; inactive or deliberately repaired collection item links are cleared by application code before deleting referenced sources/shows/artists.
- [ ] Ensure all new collection tables have `created_at` and `updated_at`.
- [ ] Ensure `collection_items` has import status fields: `import_status`, `import_error`, `last_seen_at`, `removed_at`, and `last_imported_at`.
- [ ] Add the required collection indexes from the data model section before API work begins.
- [ ] Use integer-backed import statuses in the database and serialize strings in API responses: `pending`, `linked_existing_source`, `imported_source`, `skipped`, `import_error`.
- [ ] Add `ArtistService.TouchApiUpdatedAt(int artistId)` and `ArtistService.TouchApiUpdatedAt(IEnumerable<int> artistIds)`.
- [ ] Update `ArtistService.Save` so artist metadata changes and feature updates bump `api_updated_at`.
- [ ] Grep for direct runtime writes to `features`; every runtime feature update path must call `TouchApiUpdatedAt`. Current expected path is `ArtistService.Save`.
- [ ] Ensure import/rebuild paths call `TouchApiUpdatedAt` after show/source-count-affecting changes. A changed `show_count`, `source_count`, year count, or source-derived artist payload must make the artist appear in the next delta response.
- [ ] Add `includeCollectionDerived` filtering to artist list queries. `include_collection_derived=true` must include `AutoCreated | CollectionDerived` artists even when `include_autocreated=false`; default behavior must exclude collection-derived artists unless explicitly requested.
- [ ] Keep `GET /api/v3/artists` response shape unchanged as `ArtistWithCounts[]`.
- [ ] Add `GET /api/v3/artists/delta?since={timestamp}` for `ArtistDeltaResponse`.
- [ ] Apply `include_collection_derived` to both full artist lists and delta artist lists.
- [ ] Implement delta as a bounded cursor: read `server_timestamp` from the database first, select rows with `api_updated_at > since AND api_updated_at <= server_timestamp`, then return that same `server_timestamp`.
- [ ] Return `ArtistDeltaResponse` only from `/api/v3/artists/delta`:

```json
{
  "server_timestamp": "2026-04-15T12:00:00Z",
  "artists": []
}
```

**Acceptance checks:**
- [ ] `GET /api/v3/artists` does not include collection-derived artists by default.
- [ ] `GET /api/v3/artists?include_collection_derived=true` can include them.
- [ ] `GET /api/v3/artists/delta?since=...` returns only artists with `api_updated_at > since`.
- [ ] `GET /api/v3/artists/delta?since=...&include_collection_derived=true` returns changed AJC-created artists.
- [ ] An artist updated after the delta query captures `server_timestamp` is not skipped; it remains eligible for the next delta request.
- [ ] `GET /api/v3/artists/delta?since=...&include_collection_derived=true` includes artists flagged `AutoCreated | CollectionDerived` without also requiring `include_autocreated=true`.
- [ ] Delta responses are merge contracts. Missing artists are not implicit deletes. No deletion field is included until a real tombstone mechanism exists.
- [ ] Updating features for an artist makes that artist appear in `GET /api/v3/artists/delta?since={before_update}`.
- [ ] Adding or removing a source/show for an artist makes that artist appear in `GET /api/v3/artists/delta?since={before_import}`.

---

## Step 2: Safe Item-Specific Archive.org Importer

**Purpose:** Import one Archive.org item under one resolved artist without running artist-wide collection refresh or source deletion.

**Files:**
- Modify: `RelistenApi/Services/Importers/ArchiveOrgImporter.cs`
- Modify: `RelistenApi/Services/Data/SourceService.cs`
- Modify: `RelistenApi/Vendor/ArchiveOrg.cs`
- Test: `RelistenApiTests/Collections/TestArchiveOrgItemImporter.cs`
- Fixture: `RelistenApiTests/Fixtures/archive-org/aadamjacobs-single-item.json`

**Checklist:**
- [ ] Add `creator` to `Relisten.Vendor.ArchiveOrg.Metadata.Metadata`.
- [ ] Extract the current single-source write path in `ArchiveOrgImporter` so it can be called with an already-fetched `RootObject`.
- [ ] Add a public importer method similar to `ImportSingleArchiveIdentifierForArtist(Artist artist, string identifier, ArchiveOrgImportContext archiveContext, PerformContext? ctx)`, where `ArchiveOrgImportContext` is a non-persisted value containing `upstream_source_id = 1` and source-link settings. Do not require an `artists_upstream_sources` row for the artist.
- [ ] Return `ArchiveItemImportResult` from the item-specific importer so the collection job can distinguish imported, skipped, and failed items and can persist source UUIDs or skip reasons.
- [ ] Fetch `https://archive.org/metadata/{identifier}` directly in the item-specific path.
- [ ] Validate `is_dark`, missing date, invalid date, and no VBR MP3 exactly like the current Archive.org import path.
- [ ] Reuse source, link, source set, source track, review, FLAC, and venue-writing behavior from the existing importer.
- [ ] Do not call the collection search URL.
- [ ] Do not compute "sources to keep."
- [ ] Do not call `RemoveSourcesWithUpstreamIdentifiers`.
- [ ] Replace `RemoveSourcesWithUpstreamIdentifiers(IEnumerable<string>)` with an artist-scoped method such as `RemoveSourcesWithUpstreamIdentifiers(Artist artist, IEnumerable<string> upstreamIdentifiers)`.
- [ ] Update existing artist-wide Archive.org imports to call the artist-scoped delete method.

**Acceptance checks:**
- [ ] Importing one fixture creates one source and its tracks.
- [ ] Re-importing the same fixture updates the existing source idempotently.
- [ ] A fixture with no VBR MP3 is skipped and does not create a source.
- [ ] The item-specific importer can import for an artist with no `artists_upstream_sources` archive.org row.
- [ ] No unrelated sources are deleted during item-specific import.
- [ ] Existing artist-wide Archive.org refresh still prunes sources that disappeared from that artist's own Archive.org collection.

---

## Step 3: AJC Trusted Resolver And Artist Creation

**Purpose:** Turn every AJC creator into a canonical Relisten artist before import. AJC is trusted enough to auto-create unmatched creators; Taper's Section is not part of this step.

**Files:**
- Create: `RelistenApi/Services/Collections/ArchiveCollectionResolver.cs`
- Create: `RelistenApi/Services/Data/CollectionService.cs`
- Modify: dependency registration in `RelistenApi/Startup.cs`
- Test: `RelistenApiTests/Collections/TestArchiveCollectionResolver.cs`

**Checklist:**
- [ ] Insert or update the AJC collection row with `slug = 'aadam-jacobs'`, `upstream_identifier = 'aadamjacobs'`, and `collection_type = 'taper_archive'`.
- [ ] Resolve creators in this order:
  - [ ] Existing non-blocked `collection_artist_mappings` row.
  - [ ] Existing source match by `sources.upstream_identifier = archive item identifier` only when exactly one source matches.
  - [ ] Exact existing artist name match.
  - [ ] Normalized artist name match.
  - [ ] Auto-create new artist with `AutoCreated | CollectionDerived`.
- [ ] If an existing mapping is `blocked = true`, mark the item `skipped` with `block_reason` and do not import it.
- [ ] When multiple existing sources share the same archive identifier, defer source linking until after creator resolution. Choose the source whose artist matches the resolved creator. If no single source can be chosen, mark the item `import_error` with a conflict reason instead of guessing.
- [ ] Use `ArchiveOrgArtistDefaults.ArchiveOrgDefaultFeatures()` for auto-created AJC artists.
- [ ] Generate unique artist slugs with numeric suffixes on collision.
- [ ] Store final decisions in `collection_artist_mappings`.
- [ ] Do not create `artists_upstream_sources` rows pointing AJC-created artists at `aadamjacobs`.
- [ ] Do not create `artists_upstream_sources` rows for existing artists that receive their first Archive.org source from AJC. AJC is collection membership, not artist-level Archive.org ownership.
- [ ] Source-level Archive.org links still use `upstream_source_id = 1`; collection provenance lives in `collection_items`.
- [ ] Mark every created or changed artist's `api_updated_at`.

**Acceptance checks:**
- [ ] Existing artists such as Mekons and Jon Langford resolve to the existing artist records.
- [ ] Unknown AJC creators create collection-derived artists.
- [ ] A blocked AJC creator mapping skips its items without code changes.
- [ ] Re-running resolver does not create duplicate artists.
- [ ] Created AJC artists remain out of the default artist list.

---

## Step 4: AJC Bulk Import Job And Collection-Year Rollup

**Purpose:** Import the full AJC collection into playable Relisten sources and maintain collection-scoped aggregate rows.

**Files:**
- Create: `RelistenApi/Services/Collections/AadamJacobsCollectionImporter.cs`
- Modify: `RelistenApi/Services/ScheduledService.cs`
- Modify: `RelistenApi/Services/Data/ArtistService.cs` for `api_updated_at` bumping after rebuild.
- Test: `RelistenApiTests/Collections/TestAadamJacobsCollectionImporter.cs`
- Test fixture: `RelistenApiTests/Fixtures/archive-org/aadamjacobs-scrape-page.json`
- Test fixture: `RelistenApiTests/Fixtures/archive-org/aadamjacobs-import-summary.json`

**Checklist:**
- [ ] Fetch `collection:aadamjacobs` using `https://archive.org/services/search/v1/scrape` with cursor pagination.
- [ ] Create a stable index run timestamp before the first scrape page.
- [ ] Persist each Archive.org item in `collection_items`, setting `last_seen_at` to the index run timestamp and clearing `removed_at`.
- [ ] After all scrape pages succeed, mark active rows for this collection that were not seen in the current run as removed by setting `removed_at`.
- [ ] Store the final active distinct fetched count on the collection row so indexing completeness can be checked. Do not use the Archive.org scrape page `total` as the completed collection count.
- [ ] Update `collections.item_count` and `collections.indexed_at` once, at the end of a successful completed index run, after all active distinct item rows for the run are known. Do not update either field after a failed or partial index run.
- [ ] Resolve the creator through Step 3 before deciding whether to link or import.
- [ ] Look for an existing source using `(resolved_artist_id, upstream_identifier)`, not `upstream_identifier` alone.
- [ ] Link `artist_uuid`, `show_uuid`, and `source_uuid` when a source already exists for the resolved artist.
- [ ] Import via the Step 2 item-specific importer when no source exists for the resolved artist.
- [ ] Rate-limit Archive.org metadata fetches. Start with sequential or very low concurrency imports plus the existing Archive.org user-agent headers; do not fan out thousands of metadata requests in parallel.
- [ ] Coordinate with regular artist refresh so the same artist is not rebuilt/imported concurrently by AJC and `ScheduledService.RefreshArtist`. Use Hangfire `DisableConcurrentExecution` and/or a PostgreSQL advisory lock for the AJC job; do not rely only on in-process sets.
- [ ] Track affected artist ids during the run.
- [ ] Rebuild shows and years once per affected artist after item imports complete.
- [ ] After rebuild, call a `CollectionService.LinkImportedItemsForArtist`-style method for every affected artist. This method must reload sources and shows by `(artist_id, upstream_identifier)` and fill `artist_uuid`, `source_uuid`, and `show_uuid` before items are marked `imported_source`.
- [ ] Set `api_updated_at` for every affected artist after rebuild.
- [ ] Recompute `collection_years` from linked collection items after import.
- [ ] Mark item import status as `linked_existing_source`, `imported_source`, `skipped`, or `import_error` from actual current link state. Active rows with missing source/show links must be eligible for relink or reimport on the next run.
- [ ] Store skip reasons for non-imported items so they can be fixed without log archaeology.
- [ ] Set `collections.last_imported_at` only after the source link/import pass and collection-year rebuild complete successfully.
- [ ] Add a Hangfire/manual entrypoint such as `ImportAadamJacobsCollection`.

**Acceptance checks:**
- [ ] Indexing completeness: `collections.item_count` equals the number of active distinct fetched AJC identifiers (`removed_at is null`) from the latest successful index run.
- [ ] Indexing idempotence: running the index/import job twice does not create duplicate collection item, mapping, artist, source, show, source set, or source track rows.
- [ ] Import accounting over active rows: `linked_existing_source + imported_source + skipped + import_error = collections.item_count`.
- [ ] Link completeness: every `linked_existing_source` and `imported_source` item has non-null `artist_uuid`, `show_uuid`, and `source_uuid`.
- [ ] Normal artist-wide refresh does not delete active AJC-linked sources just because they disappeared from the artist's own Archive.org collection.
- [ ] If a source linked only to inactive/removed collection items is pruned, the prune path explicitly clears those inactive collection links before deleting the source.
- [ ] Full-data check: run against the current live AJC scrape in a local/staging database, not only toy fixtures, before production import.
- [ ] Re-running the job is idempotent.
- [ ] Importing AJC never deletes non-AJC sources.
- [ ] AJC-only artists have playable shows through normal `/api/v3/shows/{uuid}`.
- [ ] `collection_years` matches the linked/imported source set.

---

## Step 5: Collection Browse APIs

**Purpose:** Let mobile browse AJC by artist, by year, by popular/trending, and by on-this-day using collection-scoped endpoints.

**Files:**
- Create: `RelistenApi/Controllers/CollectionsController.cs`
- Modify: `RelistenApi/Services/Data/CollectionService.cs`
- Extend: `RelistenApi/Services/Popularity/PopularityService.cs`
- Test: `RelistenApiTests/Collections/TestCollectionsController.cs`
- Test: `RelistenApiTests/Collections/TestCollectionQueries.cs`

**Checklist:**
- [ ] `GET /api/v3/collections` returns collection summaries.
- [ ] `GET /api/v3/collections/{collectionUuidOrSlug}` returns AJC metadata and aggregate counts.
- [ ] `GET /api/v3/collections/{collectionUuidOrSlug}/artists` returns artists with counts scoped to collection-linked sources only.
- [ ] `GET /api/v3/collections/{collectionUuidOrSlug}/years` returns `CollectionYear[]`.
- [ ] `GET /api/v3/collections/{collectionUuidOrSlug}/years/{yearUuidOrYear}` returns `CollectionYearWithShows`.
- [ ] `GET /api/v3/collections/{collectionUuidOrSlug}/shows/popular-trending` returns `CollectionPopularTrendingShowsResponse`, ranked over collection-linked shows only.
- [ ] `GET /api/v3/collections/{collectionUuidOrSlug}/shows/on-this-day?month={month}&day={day}` returns distinct collection-linked shows whose show date matches that month/day across all artists.
- [ ] Every collection browse query filters to active rows with `collection_items.removed_at is null`.
- [ ] Validate `month` and `day`; invalid dates return 400 instead of an empty success response.
- [ ] Collection popularity queries should compute from collection-linked source-track plays by joining `source_track_plays_daily` and `source_track_plays_hourly` through `collection_items.source_uuid`, then grouping by `show_uuid`. Do not use global top-N caches or global show popularity metrics for collection ranking.
- [ ] Every show returned by collection endpoints includes `artist_uuid`, `year_uuid` where applicable, source counts, venue fields, and popularity fields when available.
- [ ] Show rows returned by collection endpoints must use collection-scoped source availability fields. Either project a collection-specific show list model or override `Show` fields such as `source_count`, `has_soundboard_source`, and `has_streamable_flac_source` from active collection-linked sources. Do not leak global `show_source_information` counts into collection browse lists.
- [ ] Do not add collection-level venues or songs endpoints.
- [ ] Do not add collection show-detail endpoints.

**Acceptance checks:**
- [ ] The AJC artist list does not count non-AJC sources for the same artist.
- [ ] The AJC year list aggregates across all artists in the collection.
- [ ] AJC year and show list source counts do not count non-AJC sources for the same show.
- [ ] AJC popular/trending excludes shows that are not linked to AJC collection items.
- [ ] AJC popular/trending scores only plays from AJC-linked sources for a show, not every source attached to that show.
- [ ] AJC popular/trending includes lower-volume AJC shows when they rank within the collection, even if absent from global popularity caches.
- [ ] AJC on-this-day returns cross-artist results and each row can navigate to `/api/v3/shows/{uuid}`.
- [ ] API response-shape tests assert required fields, stable casing, non-null UUIDs, and no accidental nested full source graphs in list endpoints.
- [ ] API query tests cover both collection UUID and slug lookup for every collection endpoint.

---

## Step 6: Verification, Backfill, And Rollout

**Purpose:** Prove the import is safe, ship it behind controlled API/mobile behavior, and leave agents a clear recovery path.

**Files:**
- Update: `docs/design/2026-04-15-aadam-jacobs-full-playback-implementation-guide.md` as implementation details change.
- Add or update any runbook file if deployment requires manual production commands.

**Checklist:**
- [ ] Run `dotnet build RelistenApi.sln`.
- [ ] Run `dotnet test RelistenApiTests/RelistenApiTests.csproj`.
- [ ] In a local or staging database, run the AJC import job twice and compare counts.
- [ ] Query the database after full import and record these counts: collection item count, distinct item identifiers, mapping count, reused artists, created collection-derived artists, linked existing sources, imported sources, skipped items, import errors, collection years, collection-linked shows.
- [ ] Confirm full-data invariants with SQL:

```sql
select
    c.item_count,
    c.indexed_at,
    count(distinct i.upstream_identifier) filter (where i.removed_at is null) as active_distinct_items,
    count(*) filter (where i.removed_at is not null) as removed_items,
    count(*) filter (where i.removed_at is null and i.import_status = 1) as linked_existing_source,
    count(*) filter (where i.removed_at is null and i.import_status = 2) as imported_source,
    count(*) filter (where i.removed_at is null and i.import_status = 3) as skipped,
    count(*) filter (where i.removed_at is null and i.import_status = 4) as import_error,
    count(*) filter (where i.removed_at is null and i.source_uuid is not null and (i.artist_uuid is null or i.show_uuid is null)) as broken_links
from collections c
join collection_items i on i.collection_uuid = c.uuid
where c.slug = 'aadam-jacobs'
group by c.uuid;
```

- [ ] Verify no source rows disappear between the two AJC runs.
- [ ] Verify default `/api/v3/artists` response size does not jump with AJC collection-derived artists.
- [ ] Verify `/api/v3/artists/delta?since={before_import}` returns changed and newly created AJC artists.
- [ ] Verify collection endpoints return only AJC-linked rows.
- [ ] Verify a linked/imported AJC show opens through `/api/v3/shows/{uuid}` and plays from normal source tracks.
- [ ] Run `EXPLAIN (ANALYZE, BUFFERS)` on the full-data collection artist, year, popular/trending, and on-this-day queries in staging and confirm they use the collection indexes and do not scan unrelated source/play tables excessively.
- [ ] Run API smoke checks against local or staging and save the JSON samples with the rollout notes:

```bash
BASE_URL=http://localhost:5000

curl -fsS "$BASE_URL/api/v3/collections" | jq '.[0] | keys'
curl -fsS "$BASE_URL/api/v3/collections/aadam-jacobs" | jq '{uuid, slug, item_count, indexed_at, artist_count, show_count, source_count}'
curl -fsS "$BASE_URL/api/v3/collections/aadam-jacobs/artists" | jq 'length'
curl -fsS "$BASE_URL/api/v3/collections/aadam-jacobs/years" | jq '.[0] | {uuid, collection_uuid, year, artist_count, show_count, source_count}'
curl -fsS "$BASE_URL/api/v3/collections/aadam-jacobs/shows/popular-trending" | jq '{popular: (.popular_shows | length), trending: (.trending_shows | length)}'
curl -fsS "$BASE_URL/api/v3/collections/aadam-jacobs/shows/on-this-day?month=1&day=1" | jq '.[0] | {uuid, artist_uuid, display_date}'
```

- [ ] API smoke output should show valid UUIDs, non-zero artist/year lists after import, stable snake_case JSON fields, and show rows that can be opened through `/api/v3/shows/{uuid}`.
- [ ] Capture final production counts: collection items fetched, existing sources linked, sources imported, artists reused, artists created, skipped items.
- [ ] Keep the job manual/on-demand until the first production import is validated; add a recurring schedule only after the first run is clean.

**Rollback and recovery notes:**
- If collection import fails mid-run, re-run the AJC job after fixing the error. The import must be idempotent.
- If an artist mapping is wrong before source import, update `collection_artist_mappings` and re-run the resolver/import for affected items.
- If an artist mapping is wrong after source import, do not silently move sources between artists in-place. Write a deliberate correction migration or repair script because source UUIDs are artist-scoped.
- If mobile sync behaves badly, collection-derived artists can remain hidden from default artist fetches while the collection endpoint stays available for targeted testing.
