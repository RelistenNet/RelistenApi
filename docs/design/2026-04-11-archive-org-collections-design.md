# Archive.org Collection Support

## Problem

Relisten is frequently asked to add two Archive.org collections:

- **Aadam Jacobs Collection (aadamjacobs)**: 2,458 items from 1,189 unique artists. A Chicago taper's archive spanning 1980s-2000s, documenting the local music scene at venues like Empty Bottle, The Metro, and Lounge Ax.
- **Taper's Section (taperssection)**: 21,266 items from ~6,175 raw creator strings. A community catch-all for taper-friendly artists without their own Live Music Archive collection. Many creator strings are taper/uploader names rather than performing artists — only a subset represent actual performers.

The current data model assumes 1 artist = 1 Archive.org collection. These collections break that assumption: a single collection contains recordings from many different artists.

## Goals

- **taperssection**: Fold recordings into existing Relisten artists where they match. For unmatched creators, auto-approve those with strong signals (fuzzy match, or likely performer with 5+ items) and block taper/uploader names. Only truly ambiguous creators need manual review. Relisten's value is as an aggregator — a show page should display all available sources regardless of origin.
- **aadamjacobs**: Make the collection browsable as a curated entity. Users should be able to browse by artist/creator, see what's popular, and discover recently added recordings.
- **Both**: Import all data without requiring mobile app changes first. Provide a mechanism to filter new artists out of the default artist list until the mobile app is ready.
- **Continuous model**: Every Archive.org collection (including existing single-artist ones like `GratefulDead`) becomes a Relisten collection. The data model is the same at every scale.

## Non-Goals

- Collection-scoped venue browsing (part of a larger data normalization effort spanning venues and songs across artists)
- Mobile app collection UI (will be special-cased for AJC and taperssection separately)
- Mobile app navigation placement for collections
- Search (separate feature)

## Data Model

### New Tables

```sql
CREATE TABLE collections (
    id          serial PRIMARY KEY,
    uuid        uuid NOT NULL UNIQUE,
    name        text NOT NULL,
    slug        text NOT NULL UNIQUE,
    description text,
    upstream_identifier text,          -- e.g., "aadamjacobs", nullable for non-archive collections
    upstream_source_id  integer REFERENCES upstream_sources(id),  -- nullable
    collection_type     text NOT NULL, -- "taper_archive", "community", "artist"
    created_at  timestamptz NOT NULL DEFAULT timezone('utc', now()),
    updated_at  timestamptz NOT NULL
);

-- "taper_archive": a single taper's collection (AJC)
-- "community": a community-curated collection (taperssection)
-- "artist": a single-artist LMA collection (GratefulDead, Mekons, etc.)

CREATE TABLE collection_items (
    collection_uuid uuid NOT NULL REFERENCES collections(uuid),
    source_uuid     uuid NOT NULL REFERENCES sources(uuid),
    PRIMARY KEY (collection_uuid, source_uuid)
);
CREATE INDEX idx_collection_items_source ON collection_items(source_uuid);

CREATE TABLE collection_artist_mappings (
    collection_uuid   uuid NOT NULL REFERENCES collections(uuid),
    creator_name      text NOT NULL,     -- raw creator string from archive.org
    artist_uuid       uuid REFERENCES artists(uuid),  -- nullable: null when blocked or pending
    canonical_name    text,              -- cleaned-up display name (e.g., "Gov't Mule" for "govt mule")
    manually_verified boolean NOT NULL DEFAULT false,
    blocked           boolean NOT NULL DEFAULT false,  -- skip this creator during import
    block_reason      text,                            -- e.g., "low quality", "not a performer", "taper name not artist"
    decision_source   text NOT NULL DEFAULT 'manual',  -- "manual", "analyzer_exact", "analyzer_fuzzy", "analyzer_title_heuristic", "auto_trusted"
    confidence        real,                            -- 0.0-1.0, null for manual decisions
    created_at        timestamptz NOT NULL DEFAULT timezone('utc', now()),
    last_analyzed_at  timestamptz,                     -- when the analyzer last evaluated this mapping
    PRIMARY KEY (collection_uuid, creator_name)
);

-- When blocked=true, artist_uuid is null and the importer skips all items
-- with this creator. This handles cases like:
--   - Creator is actually a taper/uploader name, not a performing artist
--   - Known low-quality recordings
--   - Content that shouldn't be in Relisten for other reasons
--
-- When artist_uuid is null AND blocked=false, the mapping is pending review
-- and the importer skips this creator until it is resolved.
```

### Modified Tables

```sql
-- artists: new column for delta sync
ALTER TABLE artists ADD COLUMN api_updated_at timestamptz;

-- Initialize for existing artists: set to updated_at so that the first
-- delta sync with ?since= treats all existing artists as "already seen"
UPDATE artists SET api_updated_at = updated_at;

-- Then make it NOT NULL
ALTER TABLE artists ALTER COLUMN api_updated_at SET NOT NULL;

-- api_updated_at is bumped whenever anything affecting the artist's API
-- representation changes: show/source count changes after import,
-- feature changes, metadata changes.
-- Convention: updated_at reflects the row itself; api_updated_at reflects
-- the full API response.
```

### Artist Featured Flags

Existing values (bitflags):
- `0` (`None`): manually curated (224 artists)
- `1` (`Featured`, `1 << 0`): top featured (2 artists)
- `2` (`AutoCreated`, `1 << 1`): auto-created from LMA (4,004 artists)

New flag:
- `4` (`CollectionDerived`, `1 << 2`): created via taperssection/AJC import, no own LMA collection

An artist can have multiple flags (e.g., `AutoCreated | CollectionDerived = 6` if an auto-created LMA artist is also referenced in a collection). The `/artists` endpoint gets a new parameter `?include_collection_derived=true` (default `false`) which includes artists where the `CollectionDerived` bit is set. The existing `?include_autocreated=true` parameter is unchanged.

### UUID Generation

Collections use deterministic UUIDs following the existing convention:

```sql
md5(upstream_source_id || '::collection::' || upstream_identifier)::uuid
```

For retroactively created artist collections, this is derived from the existing `artists_upstream_sources` data.

## Import Pipeline

### Scenario 1: Retroactive Collection Creation (Migration)

For every existing `artists_upstream_sources` row where `upstream_source_id = 1` (archive.org):
1. Create a `collections` row with `collection_type = 'artist'` and the same `upstream_identifier`
2. Populate `collection_items` by linking all existing sources for that artist

This is a one-time data migration. Going forward, the existing `ArchiveOrgImporter` also writes to `collection_items` when it creates/updates sources.

### Scenario 2: taperssection Import

**Prerequisite**: The daily analyzer job (or initial analysis) must have populated `collection_artist_mappings` for this collection. Only creators with an approved mapping (non-blocked, `artist_uuid` set) will be imported.

1. Fetch all items from `collection:taperssection` via archive.org cursor-based scrape API
2. For each item, look up `collection_artist_mappings` for the `creator` field:
   - No mapping or `blocked=true` → skip
   - Mapping with `artist_uuid` → proceed with that artist
3. Check for duplicate: if a source with the same `upstream_identifier` (archive.org item ID) already exists for this artist, just add the `collection_items` link
4. Otherwise: import the source under the resolved artist using the existing import logic
5. Write the `collection_items` row linking source to the taperssection collection
6. After all items: rebuild shows/years for each affected artist

### Scenario 3: aadamjacobs Import

Same pipeline as taperssection, with two key differences:

1. **Trusted collection**: AJC is a `taper_archive` type. Unmatched creators that pass basic validation (not a taper pattern) are auto-created as new artists with the `CollectionDerived` flag. No manual review needed for the long tail.
2. **Higher overlap**: Many items already exist in Relisten under their own LMA artist collections (e.g., all 27 Mekons recordings in aadamjacobs are also in the Mekons LMA collection). For these, step 3 catches the duplicate and just adds the `collection_items` link.

Items only in aadamjacobs (artists without their own LMA collection) get full import.

### Import Order

1. Existing single-artist collection imports (already running, unchanged)
2. Retroactive collection creation migration
3. taperssection import (higher value, more overlap with existing artists)
4. aadamjacobs import

Order matters: running existing artist imports first ensures maximum overlap detection in steps 3-4.

## Artist Matching Strategy

The importer resolves archive.org `creator` fields to Relisten artists. Creator names are inconsistent across archive.org items.

Known variations:
- `"Medeski Martin & Wood"` vs `"Medeski, Martin & Wood"` (punctuation)
- `"Gov't Mule"` vs `"Govt Mule"` (apostrophe)
- `"Trey Anastasio"` vs `"Trey Anastasio Band"` (band suffix)
- `"The Black Crowes"` vs `"Black Crowes"` (article prefix)

### Two-Track Creator Policy

Collections have different trust levels based on creator data quality:

**Trusted collections (aadamjacobs):** A single taper's archive — every creator string is a performing artist name. Unmatched creators are auto-created as new artists with the `CollectionDerived` flag. This is safe because AJC has clean creator data (1,189 unique creators, all performers).

**Untrusted collections (taperssection):** A community catch-all where creator strings are a mix of performer names, taper names, equipment descriptions, and usernames. Of ~6,175 raw creator strings, a significant portion are tapers/uploaders rather than performing artists. The analyzer auto-approves creators that meet confidence thresholds (exact/fuzzy match to existing artists, or likely performer with 5+ items). Only truly ambiguous creators need manual review.

The `collections` table encodes this via `collection_type`:
- `"taper_archive"` → trusted (auto-create unmatched)
- `"community"` → untrusted (require approved mapping)
- `"artist"` → trusted (single artist, no creator resolution needed)

### Matching Pipeline (per source)

The importer itself is deterministic — it only imports creators with an approved mapping in `collection_artist_mappings`:

1. **Mapping lookup**: check `collection_artist_mappings` for `(collection_uuid, creator_name)`.
   - If `blocked=true` → skip this item entirely
   - If `artist_uuid` is set → use that artist
   - If no mapping exists and collection is trusted → auto-create artist, write mapping with `decision_source='auto_trusted'`
   - If no mapping exists and collection is untrusted → **skip** (do not import, do not create artist)
2. Import the source under the resolved artist using the existing import logic
3. Write the `collection_items` row linking source to the collection

For trusted collections, the importer handles the simple case (create artist + write mapping) inline. For untrusted collections, the importer is purely a consumer of pre-existing mappings — all classification happens upstream in the analyzer.

### Daily Analyzer Job

A scheduled job runs daily (or on-demand) to classify new creator strings and populate `collection_artist_mappings`:

1. **Scan**: Query archive.org for all unique creator strings in each collection
2. **Filter**: Skip creators that already have a mapping in `collection_artist_mappings`
3. **Classify** each new creator using a pipeline:
   - **Exact match** against `artists.name` → `decision_source='analyzer_exact'`, `confidence=1.0`
   - **Normalized match** (strip "The ", punctuation, whitespace) → `decision_source='analyzer_fuzzy'`, `confidence=0.95`
   - **Fuzzy match** (rapidfuzz score ≥ 90) → `decision_source='analyzer_fuzzy'`, `confidence=score/100`
   - **Title heuristic** (does the creator name appear in ≥80% of their item titles?) → `decision_source='analyzer_title_heuristic'`, classify as likely performer
   - **Taper pattern match** (equipment terms, username patterns, "taper"/"recording" in name) → auto-block with `block_reason`
   - **Insufficient signal** → leave as pending (no mapping row, or mapping with `artist_uuid=null` and `blocked=false`)
4. **For trusted collections**: auto-create artists for unmatched creators that pass basic validation (not a taper pattern)
5. **For untrusted collections**: auto-approve and create artists when:
   - Exact or normalized match to an existing Relisten artist (any item count)
   - High-confidence fuzzy match (score ≥ 90) to an existing artist (any item count)
   - Classified as likely performer (creator appears in ≥80% of titles) with 5+ items
   - Only truly ambiguous creators (insufficient signal, low item count) stay pending for manual review
6. **Update** `last_analyzed_at` for all processed mappings

For untrusted collections, the analyzer creates artists (with `CollectionDerived` flag) as needed, so by the time the importer runs, all approved creators already have artist records. The importer just looks up the mapping and imports. For trusted collections, the importer handles artist creation inline for unmapped creators.

### Pre-seeded Canonical Mapping (Prerequisite)

Before the first import, run the analysis tooling in `tools/collection-analysis/`:

1. `fetch_collection.py` — pull all items from archive.org via cursor-based scrape API
2. `fetch_relisten_artists.py` — pull existing Relisten artists from production DB
3. `analyze_creators.py` — classify creators into matched/likely_performer/likely_taper/ambiguous
4. Human review of the output files in `review/`
5. `generate_mappings.py` — produce final `mappings.json` for seeding `collection_artist_mappings`

This ensures the big names (Phish, Grateful Dead, Gov't Mule, etc.) are correctly mapped before any import runs. For untrusted collections, the analyzer auto-approves creators with strong signals (exact/fuzzy matches, likely performers with 5+ items) and blocks taper patterns. Only truly ambiguous creators (insufficient signal, low item count) stay pending for manual review.

The admin interface can show pending mappings for ongoing review.

## API Endpoints

### New Collection Endpoints (v3)

All accept UUID as primary identifier, slug as fallback.

```
GET /api/v3/collections
  Returns all collections with summary stats (item count, artist count).

GET /api/v3/collections/{collectionUuidOrSlug}
  Collection metadata and summary stats.

GET /api/v3/collections/{collectionUuidOrSlug}/artists
  Artists in this collection with per-collection source counts.
  Query path: collection_items → sources → artists (distinct).

GET /api/v3/collections/{collectionUuidOrSlug}/recently-added
  Sources recently added to this collection.
  New v3-only controller (existing RecentController is v2-only).
  Query path: collection_items → sources, ORDER BY created_at DESC.

GET /api/v3/collections/{collectionUuidOrSlug}/popular-trending
  Popular/trending shows within this collection.
  Extends existing PopularityService (v3-only, Redis-cached, Hangfire-refreshed).
  Query path: collection_items → sources → shows, scored via source_track_plays_daily/hourly.
```

### Modified Existing Endpoints

```
GET /api/v3/artists
  New parameter: ?include_collection_derived=true (default false)
  Filters out artists with the CollectionDerived bit set from the default response.

GET /api/v3/artists?since={ISO8601 timestamp}
  Delta sync: returns only artists where api_updated_at > since.
  Also returns newly created artists since that timestamp.
  Response includes server_timestamp for the client to use on next request.
  
  IMPORTANT CONTRACT: The delta response means "here's what changed,"
  NOT "here's the complete list." Mobile apps must MERGE the delta
  into their local store. An artist absent from the delta means
  "unchanged," not "deleted." If artist deletion is ever needed,
  it must be an explicit signal (e.g., deleted_artist_uuids array),
  never implicit.

GET /api/v3/sources/{sourceUuid}
  Add optional collections array to response, populated via
  collection_items join. Shows which collections a source belongs to.
```

## Artist List Scaling

### Current State

- 4,230 artists, all loaded by the mobile app on launch
- Adding ~2,000 collection-derived artists makes this worse

### Delta Sync

Based on production data analysis:
- ~20-45 unique artists receive new sources on any given day
- ~134 artists touched per week (3.2% of total)
- Typical delta sync returns 20-50 artist objects

The `api_updated_at` field on artists is bumped when:
- Source imports complete for that artist (show/source counts changed)
- Features change
- Artist metadata changes (name, slug, etc.)

Convention: `updated_at` reflects the artist row itself. `api_updated_at` reflects whether the API response for this artist would differ from the last time a client synced.

### Mobile App Pre-seeding

Ship a bundled snapshot of the artist list with the app binary:
- Generated during CI/build process by calling the API
- On first launch: load from bundled snapshot (instant, no network)
- Then delta sync with `?since={snapshot_timestamp}` to catch changes since build
- Subsequent launches: delta sync from last known timestamp

Even with 5,000+ artists, first-launch experience is instant. The delta on first real sync is small.

## Implementation Phases

### Phase 1: Schema & Migration
- Create `collections`, `collection_items`, `collection_artist_mappings` tables
- Add `api_updated_at` to `artists`
- Add `CollectionDerived = 1 << 2` to `ArtistFeaturedFlags` enum and bitmask queries
- Retroactive migration: create collection rows for all existing archive.org artist collections, populate `collection_items`

### Phase 2: Artist Matching Prep & Analyzer
- Run initial analysis using `tools/collection-analysis/` tooling
- Manual review of top ambiguous creators from taperssection
- Seed `collection_artist_mappings` from reviewed `mappings.json`
- Build daily analyzer as a Hangfire `RecurringJob` (fits existing scheduling pattern) that:
  - Scans for new creator strings not yet in `collection_artist_mappings`
  - Classifies using the matching pipeline (exact → normalized → fuzzy → title heuristic → taper pattern)
  - Auto-approves exact/fuzzy matches and likely performers with 5+ items
  - Auto-blocks taper patterns
  - Leaves truly ambiguous creators (insufficient signal, low item count) pending for manual review
  - For trusted collections (AJC): auto-creates artists for all unmatched non-taper creators
  - For untrusted collections (taperssection): auto-creates artists only when thresholds are met

### Phase 3: Import Pipeline
- Add `creator` field to `Metadata` class in `ArchiveOrg.cs` and include it in search URL field list
- Extend or create importer for multi-artist collections
- Import flow: look up mapping → skip if blocked/unmapped (untrusted) or auto-create (trusted) → import source → link to collection
- Add `artist_id` guard to `RemoveSourcesWithUpstreamIdentifiers` (currently unscoped — safe today but dangerous with multi-artist collections sharing identifiers)
- Wire `api_updated_at` bump into the post-import pipeline (after `RebuildYears`, or in `ScheduledService.RefreshArtist` after import returns)
- Import taperssection first, then aadamjacobs
- Ensure existing `ArchiveOrgImporter` also writes `collection_items` going forward

### Phase 4: API Endpoints
- Collection endpoints (list, detail, artists, recently-added, popular-trending)
- Delta sync (`?since=`) on `/artists`
- `?include_collection_derived=true` filter on `/artists`
- Collection membership on source responses

### Phase 5: Mobile App
- Pre-seed artist snapshot in app bundle
- Implement delta sync for artist list
- Collection UI for AJC and taperssection (timing and design TBD, will be special-cased)

Phases 1-3 are the core work. Phase 4 makes it accessible via API. Phase 5 is the user-facing experience.

## Codebase Implementation Notes

### Existing Code Changes Required

- **`ArchiveOrg.cs`**: Add `creator` field to `Metadata` class. Add `creator` to the search URL field list (`fl%5B%5D=creator`). Currently the importer has no awareness of the creator field at all.
- **`ArchiveOrgImporter.cs`**: Currently assumes all items belong to one artist (artist passed in as fixed context). Multi-artist mode needs a different top-level loop that routes items by creator. `PreloadData()` loads `existingSources` for a single artist — needs per-creator partitioning or multi-artist awareness.
- **`SourceService.cs`**: `RemoveSourcesWithUpstreamIdentifiers` has no `artist_id` filter in the DELETE — add one to prevent cross-artist deletion when the same archive.org identifier appears under multiple artists.
- **`ImporterBase.cs`**: Add `api_updated_at` bump to the end of `RebuildYears()` or after it returns. Current `RebuildShows`/`RebuildYears` are artist-scoped and safe to run per-artist.
- **`Artist.cs`**: Add `CollectionDerived = 1 << 2` to `ArtistFeaturedFlags` enum.
- **`ArtistService.cs`**: Add `includeCollectionDerived` parameter to `AllWithCounts()`, mirroring the existing `includeAutoCreated` bitmask filter pattern.
- **`ArtistsController.cs`**: Add `?include_collection_derived=true` query parameter.

### New Code

- **Migration 10**: New SimpleMigrations class (`10_AddCollections.cs`) with all DDL.
- **Collection service**: New `CollectionService` for CRUD and queries.
- **Collection controller**: New v3-only controller (existing `RecentController` is v2-only — collection endpoints are v3-only from the start).
- **Analyzer job**: New Hangfire `RecurringJob` in `ScheduledService`, scheduled before the import job (e.g., 06:00 UTC, before the 07:00 UTC import).
- **Multi-artist importer**: Either extend `ArchiveOrgImporter` or create a new `CollectionImporter` that handles creator routing.

### UUID and Slug Generation

- Source UUIDs: `md5(artist_id || '::source::' || upstream_identifier)` — same archive.org item imported under different artists produces different source UUIDs (correct behavior).
- Show UUIDs: `md5(artist_id || '::show::' || display_date)` — artist-scoped, no cross-artist dedup. A taperssection source imported under an existing artist for an existing date merges into the existing show via `ON CONFLICT DO UPDATE`.
- Artist UUIDs: `md5('root::artist::' || slug)` — slug must be unique. Auto-created artists need slug generation that avoids collisions (e.g., `slugify(canonical_name)` with numeric suffix on conflict).
- Collection UUIDs: `md5(upstream_source_id || '::collection::' || upstream_identifier)`.

## Key Data Points

From production analysis (April 2026):
- 4,230 total artists (4,228 from archive.org)
- 210,283 shows, 284,858 sources
- aadamjacobs: 2,458 items, 1,189 unique creators. Top: Mekons (27), Eleventh Dream Day (24), Jon Langford (23)
- taperssection: 21,266 items, ~6,175 raw creator strings. Many are taper/uploader names rather than performers. Top performers: Phish (1,082), Jerry Garcia (335), Gov't Mule (95). Significant taper contamination in the long tail.
- AJC overlap: items by artists with their own LMA collection (Mekons, Eleventh Dream Day, etc.) are already in both collections on archive.org
- taperssection overlap: Phish exists in Relisten via phish.in (not archive.org), Grateful Dead via its own LMA collection
- Artist churn: ~20-45 artists touched per day, ~134 per week (3.2%)
- Creator classification breakdown (taperssection, from initial analysis):
  - Matched to existing Relisten artists: exact + fuzzy matches
  - Likely performers: creator name appears in ≥80% of item titles
  - Likely tapers: username patterns, equipment terms, creator absent from titles
  - Ambiguous: insufficient signal, needs manual review
