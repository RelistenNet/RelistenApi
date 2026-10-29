# Evidence index

API checkout inspected at `3fb0e0f` on 2026-10-29. Line numbers are navigation
aids at that revision. These observations describe code, not verified production
schema, deployment, coverage, or released mobile behavior.

## API and normalization

- `Relisten.Normalization/NormIds.cs:12-58,94-145`: eight-character IDs, validation,
  typed records, and System.Text.Json string converters.
- `RelistenApi/Migrations/13_NormSchema.cs:15-24` and
  `Relisten.Normalization/NormFlags.cs:8-12`: intended approved-read switch
  `norm_catalogue` is separate from venue write-maintenance switch `norm_venues`.
- `Relisten.Normalization/Sql/014_norm_ids.sql:83-139`: normalized UUID retirement
  and conversion of normalized links; not a legacy public-catalog identity map.
- `Relisten.Normalization/Sql/013_norm.sql:47-140`: artist-scoped songs, imported
  song evidence, ordered track interpretations and unresolved/non-song states.
- `Relisten.Normalization/Sql/013_norm.sql:144-248`: global venues, room/site
  distinction, imported venue evidence and show-specific venue rulings.
- `Relisten.Normalization/Sql/014_norm_ids.sql:237-253`: show-date venue naming.
- `Relisten.Normalization/Sql/013_norm.sql:370-381`: derived show/track link
  mutations are excluded from the change log, including deletions.
- `Relisten.Normalization/NormStore.cs:858-928`: track part replacement/removal.
- `Relisten.Normalization/NormStore.Venues.cs:703-735`: physical retirement of
  internally unreferenced machine venues.
- `Relisten.Normalization/Songs/CatalogueState.cs:135-149` and
  `Relisten.Normalization/Venues/VenueState.cs:143-168`: merge-chain resolution.
- `RelistenApi/Startup.cs:70-76`: MVC Newtonsoft configuration.
- `RelistenApi/Services/Ingestion/Workflows/CatalogSteps.cs:36-61`: cache
  invalidation precedes best-effort venue matching.
- `RelistenApi/Services/Data/CatalogRebuildService.cs:231-233`: current artist
  invalidation removes the full-artist response key.
- `RelistenApi/Models/SourceTrack.cs:9-81`: playable source-track contract.
- `RelistenApi/Models/Api/CatalogResolveRequest.cs:6-45`: current bounded UUID
  resolver contract; `CatalogResolveResponse.cs:16-24`: availability is separate
  from favorite membership.
- `RelistenApi/Controllers/LiveController.cs:67-70`: legacy playback reporting
  validates Guid syntax.
- `RelistenUserService/Library/CatalogReference.cs:3` and
  `RelistenUserService/Library/Entities/Favorite.cs:8`: account identity uses Guid.
- `RelistenUserService/Library/Entities/FavoriteMutationReceipt.cs` and
  `RelistenUserService/Library/Entities/LibraryChange.cs`: catalog identity is
  also persisted in mutation receipts and change history.
- `RelistenUserService/Library/FavoriteMutationHasher.cs:8-21`: versioned hash
  includes catalog type and canonical UUID text; changing identity representation
  affects receipt replay.
- `RelistenUserService/Library/LibraryReadService.cs:86-105`: retained underlying
  history is checked for contiguous revisions before a page is returned.
- `RelistenUserService/Persistence/Configurations/FavoriteConfiguration.cs:25-48`:
  membership uniqueness and allowed target types also constrain the database.

The investigation initially considered compatibility projections for old
favorite clients. Alec subsequently clarified that favorites are private-alpha
developer functionality and a breaking change is acceptable. The recommendation
therefore does not require an old-client projection or dual outbox transport.

## Mobile

Paths below are relative to the sibling `relisten-mobile/relisten` directory.
Mobile checkout inspected at `676ecd6`.

- `library/favorite_sync_contract.ts:238-240` and `util/postgres_uuid.ts:1-10`: favorite
  sync validates catalog UUID syntax.
- `realm/models/source_track.ts:152-154`: offline filename derives from UUID text.
- `realm/models/venue.ts:8-19,34` and `realm/models/song.ts:8-17,31`: artist-owned
  local entities; canonical global venue identity needs explicit adaptation.
- `realm/repository.ts:162-172`: increasing incoming `updated_at` drives ordinary
  existing-row refresh.
- `realm/repository.ts:218-268`: collection replacement tombstones rows absent
  from a response. Pages and partial results need different application semantics.

- `realm/models/venue_repo.ts:20-24`: artist venue lists query ownership and count
  on the venue row itself.
- `realm/models/shows/venue_shows_repo.ts:40-52`: API request includes artist,
  but local show filtering currently uses venue identity alone.
- `realm/models/repo_utils.ts:92-102`: show venue hydration upserts the venue row.
- `components/show_card.tsx:136-142`: show display reads the linked venue name;
  historical naming needs a show-specific field when sharing canonical venues.

## Discussion and external specification

- [Conceptual compatibility](https://discord.com/channels/395033814008594436/466023446590259220/1565221446928830586).
- [128-bit legacy migration](https://discord.com/channels/395033814008594436/466023446590259220/1565412028107989013).
- [Normalized-only first ID change](https://discord.com/channels/395033814008594436/466023446590259220/1565412691508731985).
- [V4 work and cross-review](https://discord.com/channels/395033814008594436/466023446590259220/1565436294375673879).
- [TypeID specification](https://github.com/jetify-com/typeid/blob/main/spec/README.md),
  version 0.3.0 retrieved from the repository's raw source on 2026-10-29. New
  generation is UUIDv7; provided variants may be encoded; byte order is big-endian.

Earlier normalization research informed which questions to check. Current
checkout evidence supersedes older architectural proposals. No recommendation
here assumes the older broad normalization framework was implemented.

## Complete contract refinement

Alec required complete v4 route coverage, all track interpretation fields under
one `normalization` object, and approval of the full prefix table before
implementation planning. The earlier representative routes were insufficient
as a complete API contract.

The current [static route registrations](current-route-registrations.json)
record 78 API and 22 account/auth registrations with source lines. The
[catalog inventory](catalog-route-inventory.md) and
[companion inventory](other-route-inventory.md) assign each a disposition.
Middleware families are listed separately; source enumeration is not a runtime
reachability test. Prefix exceptions are documented in [prefixes.md](prefixes.md).

Local checks confirmed all 100 static paths are represented, local Markdown
targets exist, and JSON examples parse with the single normalization key. A
fresh reader independently checked method/path coverage. The HTML prefix review
shows 13 rows and fits desktop and mobile viewport widths, with table scrolling
contained in its wrapper. No API implementation or deployment was performed.

## Final prefix decision

On 2026-10-29 Alec clarified that the sole requested naming change was
`source_track` → `track`. The table records `track_` for the same existing
playable source-track UUID. Embedded eras without a prefix and functional
coverage with duplicate-alias consolidation were also explicitly selected.
The runtime model/field names and database identities do not change as a
consequence of that prefix spelling decision.
