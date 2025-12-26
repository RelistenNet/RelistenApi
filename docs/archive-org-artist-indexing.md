# Plan: Archive.org Live Music Archive Artist Indexing

## Goal
Create a reliable, non-disruptive pipeline that indexes all archive.org Live Music Archive (etree) artists with at least 5 items, auto-creates missing artists, and keeps existing API listings stable unless callers opt in.

## Data Source & Fetching Strategy
- Use the scrape endpoint: `https://archive.org/services/search/v1/scrape?q=collection:etree%20AND%20mediatype:collection&fields=identifier,title,item_count,month&count=10000`.
- Parse and filter results where `item_count >= 5`.
- Store the raw response (or a normalized list) in a cache file or table to reduce repeat API calls and make runs deterministic.
- Map `identifier` to the archive.org collection slug (used in `artists_upstream_sources.upstream_identifier`).

## Artist Auto-Creation Workflow
- Add a focused service (e.g., `ArchiveOrgArtistIndexer`) that:
  - Fetches the artist list from archive.org.
  - Compares against existing artists and `artists_upstream_sources` for upstream source `archive.org`.
  - Creates missing artists with:
    - `name`: archive.org `title`.
    - `slug`: `ImporterBase.Slugify(title)` (or an extracted utility for shared slug logic).
    - `sort_name`: `name` without leading “The ” (matches `ArtistService.Save`).
    - `features`: defaults aligned to current archive.org-only artists.
    - `artists_upstream_sources`: `upstream_identifier = identifier`.
- Run this as a separate indexing job ahead of imports (see Scheduling & Integration) so auto-creation happens before the daily artist refresh.
- Use existing examples for upstream identifiers (`JoeRussosAlmostDead`, `Guster`, `GodspeedYouBlackEmperor`) to confirm naming/slug behavior.

## Defaults (From Local DB)
For artists that only have archive.org as an upstream source (`upstream_source_id = 1`), the most common feature defaults are:
- `true`: `descriptions`, `multiple_sources`, `reviews`, `ratings`, `taper_notes`, `source_information`, `per_source_venues`, `years`, `track_md5s`, `review_titles`, `track_names`, `reviews_have_ratings`, `track_durations`, `can_have_flac`.
- `false`: `eras`, `sets`, `jam_charts`, `setlist_data_incomplete`, `venue_past_names`.
- Mixed in existing data; default these to `false`: `tours` (72/206 true), `per_show_venues` (71/206 true), `venue_coords` (70/206 true), `songs` (90/206 true).

## Featured Flag & Opt-In Listings
- Treat `artists.featured` as a bitmask with a new flag (e.g., `FeaturedFlags.AutoCreated = 2`), keeping `Featured = 1` unchanged.
- Mark auto-created artists with `featured |= AutoCreated` (existing featured artists remain `1`).
- Update artist list queries/endpoints to exclude auto-created artists by default, with an opt-in query param (e.g., `?include_autocreated=true`) for `GET /v2|v3/artists`.
- Keep single-artist lookups untouched (users can still fetch by slug/uuid).

## Refactoring & Separation of Concerns
- Create a small vendor client for archive.org scraping responses (e.g., `Relisten.Vendor.ArchiveOrg.CollectionIndexClient`).
- Keep API fetching, mapping, and DB creation in separate classes to simplify testing.
- Add utility methods for slug derivation and feature defaults if they are reused.

## Scheduling & Integration
- Add a new Hangfire recurring job in `RelistenApi/Services/ScheduledService.cs` that runs the archive.org artist indexer.
- Schedule it before `RefreshAllArtists` (currently a daily 07:00 UTC job), e.g., 06:30 UTC, so new artists exist before the import pass.
- Keep the importer focused on per-artist data; the indexing job should be idempotent and safe to run independently.

## Tests (Helpful, Non-Fragile)
- Unit tests for the new client/parser using saved JSON fixtures; assert `item_count` filtering and identifier/title mapping.
- Service-level tests with a mocked HTTP handler and a small seeded DB:
  - Verifies that new artists are created once with the expected upstream source mapping.
  - Verifies `featured` bitmask for auto-created artists.
- API-level tests for `GET /v2|v3/artists` with a minimal dataset:
  - Default response excludes auto-created artists.
  - `include_autocreated=true` includes them.
- Skip standalone slug normalization tests unless slug logic is modified; rely on existing `ImporterBase.Slugify` behavior.

## Notes From Local DB
- The `artists` table already stores all current artists, and `artists_upstream_sources` ties archive.org to `upstream_source_id = 1` with `upstream_identifier` slugs (e.g., `Guster`, `GodspeedYouBlackEmperor`, `JoeRussosAlmostDead`).
- Existing featured values are `1` or `0`; no other bit flags are in use today.
## Open Questions / Inputs Needed
- Confirm desired default `features` values for auto-created archive.org-only artists.
- Confirm whether existing clients can be updated to pass the opt-in query param.
