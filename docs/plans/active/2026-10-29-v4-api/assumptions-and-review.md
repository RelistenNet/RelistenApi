# Revalidated assumptions and fresh review

API checkout: `3fb0e0fc1`. Mobile checkout: `676ecd6`. These are code-level
observations from 2026-10-29, not production deployment or capacity measurements.

## Evidence that affects implementation

- [DbService](../../../../RelistenApi/Services/DbService.cs) explicitly selects
  primary versus prefer-standby connections and defaults primary commands to
  300 seconds. The new session chooses its target and bounded timeout explicitly.
  It does not change legacy Npgsql auto-prepare settings.
- [CatalogImportTransaction](../../../../RelistenApi/Services/Data/CatalogImportTransaction.cs)
  deliberately allows separate show imports to run concurrently. A global or
  artist revision lock would change that property.
- [Normalization change logging](../../../../Relisten.Normalization/Sql/013_norm.sql)
  excludes derived track/show links. A maximum change-log ID is not a complete
  public read revision; surviving timestamp maxima also miss deletions.
- [Artist bootstrap](../../../../RelistenApi/Controllers/ArtistsController.cs)
  uses separate service connections and a 24-hour cache. It is imported flat
  catalog data despite its `/normalized` route name. Reusing it cannot establish
  the required normalized snapshot.
- [API Startup](../../../../RelistenApi/Startup.cs) uses Newtonsoft and currently
  lacks per-version Swagger inclusion. Existing v2/v3 operation sets are mixed.
  V4 uses dedicated DTOs/settings while legacy adapters retain their wire schemas.
- [Normalization IDs](../../../../Relisten.Normalization/NormIds.cs) are eight-
  character short IDs with System.Text.Json converters. They are not U26 UUID
  encodings. The approved `track_` is a separate UUID-backed catalog kind.
- [Venue retirement](../../../../Relisten.Normalization/NormStore.Venues.cs) and
  [run reverts](../../../../Relisten.Normalization/NormStore.cs) can physically
  delete canonical rows. A permanent per-ID reservation plus retirement metadata
  preserves lookup safety without changing historical canonical row images.
- [Account targets](../../../../RelistenUserService/Library/CatalogReference.cs)
  and favorite/receipt/change entities use Guid values. Target migration must
  cover the three library tables plus owned user/session/command keys and FKs,
  and the revised in-place API contracts.
- [Browser route family](../../../../RelistenUserService/Authentication/Browser/BrowserRouteBoundary.cs)
  already recognizes `/v1/library`. The in-place change keeps that route family
  and its reviewed protections; no `/v2/library` admission is needed.
- Mobile `relisten/api/client.ts` calculates a UUID/timestamp pseudo-ETag and
  records request metadata before data application. `realm/repository.ts`
  applies timestamp guards and complete-replacement semantics. A dedicated v4
  transport/apply path is required.
- Mobile `realm/models/source_track.ts`, offline metadata, and `player_state.ts`
  persist UUID-based file/queue keys. Retaining them behind the ID adapter is
  smaller than rewriting their identities.
- Mobile `realm/migrations/favorite_migration.ts` creates raw `song`/`venue`
  targets, and anonymous-import completion depends on pending mutation replies.
  Upgrade all scopes before using new kinds and reset affected unfinished import
  receipts when discarding alpha outbox state.

## Fresh review and resolved findings

Independent readers checked feasibility/readability, migration, unnecessary
complexity, public-contract completeness and verification. They found:

1. A deletion-only tombstone check has a concurrent remint race. The plan now
   reserves canonical IDs atomically with a permanent primary key and tests
   concurrent deletion/minting. It adds no global or artist writer lock.
2. Strict version-only filtering would change old Swagger schemas. The plan
   preserves the mixed legacy operation sets and isolates only v4 actions.
3. A partial-track example lacked an unresolved part. The example and semantic
   fixture check now agree.
4. Existing anonymous favorites also need old-kind mapping. The plan maps all
   scopes after legacy-flag conversion, retaining catalog UUIDs and favorite identity bits.
5. Discarded pre-typed outbox entries could strand an anonymous import in Importing.
   The affected receipt resets to Deferred and any explicit retry uses new IDs.
6. Normalized song favorites lacked a library rule. They add their owning artist,
   preserving current song behavior without implicit matched-show membership.

The maintainability follow-up makes v4 native to the maintained application
model and v2/v3 compatibility adapters over common readers/commands. The second
review found shim/fallback details, now resolved:

- Shared-reader regressions require server/core rollback; mobile fallback only
  addresses v4 projection/transport problems. Verify the pinned server build
  against additive schema changes before release.
- Measure legacy cold/warm bootstrap, show and discovery performance against
  pre-refactor baselines; cached legacy reads must not materialize the core catalog.
- Imported favorite buttons/filters use revised imported kinds regardless of catalog
  transport. Test v3 catalog fallback with revised account library enabled, rather than
  accidentally sending raw UUIDs as canonical song/venue favorites.

## Remaining execution gates, not missing product decisions

- Verify actual deployed migration/replica/ingress configuration and backups.
- Measure primary capacity and agree rollout budgets before expanding traffic.
- Run the specified runtime, migration, HTTP and device/offline checks after code
  exists. No such release gate is claimed passed by this planning exercise.

If those checks invalidate a design assumption, update the decision log and
revisit only that decision with Alec. Do not silently weaken full route coverage,
normalization precision, fallback support, or v4 maintainability to pass a check.

Both independent reviewers rechecked the corrected sections and confirmed their
findings resolved, including the final core-rollback/performance and imported-
favorite fallback requirements. Final local document checks validate links,
JSON examples, partial-state semantics and the route manifest. They do not
qualify runtime behavior.

## Direct-response simplification

Alec subsequently approved returning ordinary resources/lists directly, HTTP
ETags only, and replacement rules fixed per endpoint. The plan removes the
generic metadata envelope and duplicate body hash. `endpoints.json` now assigns
each catalog and library endpoint a response shape and sync policy. Bootstrap
and resolver keep purpose-specific domain collections; the account library's
membership revisions/cursors remain independent and unchanged.

A fresh targeted mobile/account review found no remaining response-shape or
replacement-policy contradictions. Local checks validate endpoint policy
assignments, unchanged route identities, direct JSON examples and local links.

## Approved native identity cutover

Alec then approved six account prefixes and canonical stored typed strings for
account/auth application IDs. `/v1/library` changes in place; `/v1/me` and auth
paths remain. This supersedes wire-only formatting and the previously proposed
separate library route version. Testers will re-sign-in after old native/web/SSO
sessions and linked first-party credentials are retired.

Preserve the same users, provider links, usernames and memberships through
deterministic ID conversion. Keep OpenIddict-private Guid keys and external
provider values. Auth claims, cookie/cursor formats, mobile account scopes and
credential comparisons now use canonical owned IDs. Imported catalog UUID/file
keys still do not migrate. The coordinated rollback boundary is the whole typed
account/auth schema, not only library endpoints.

A fresh account/auth reader checked the native-identity revision and found no
remaining actionable issues: in-place paths, string-backed owned IDs/FKs, opaque
framework keys, first-party claims, re-sign-in, restartable mobile fencing and
whole-account rollback agree. This confirms plan feasibility, not runtime behavior.

## Whole-thread check and overall simplification review

Reread all chat turns through the account-storage and re-sign-in approvals,
then checked the saved plan against those decisions. The latest decisions remain:
full capability coverage; approved 13 catalog/six account prefixes; every track's
normalization under one key; exact recording matches; imported playback identity
and titles; raw/partial fallback; historical venue names; existing approval flag;
global venue favorites; direct resources/lists and ETags; in-place library with
canonical owned account/auth IDs and tester re-sign-in. No product decision
needs reopening based on this review.

Two independent readers found that the component plan was clearer than its
review/deployment order. The [review guide](review-guide.md) now puts running HTTP
acceptance first, isolates account/auth on v3, proves a small mobile integration,
then completes API families and UI. Legacy extraction happens by family after
the shared response contract is reviewed. One Realm/repository set and explicit
apply operations avoid a second mobile implementation or generic sync engine.

The isolated account checkpoint is feasible using the current
[v3 catalog resolver](../../../../RelistenApi/Controllers/CatalogController.cs)
and its [frozen UUID request contract](../../../../RelistenApi/Models/Api/CatalogResolveRequest.cs).
Only the mobile catalog adapter translates imported target IDs/kinds. Short
canonical metadata can remain unavailable without affecting membership.

One HTTP correction is also required: [Startup](../../../../RelistenApi/Startup.cs)
compresses responses, so hashing uncompressed JSON is not a strong validator for
both content encodings. Use a weak ETag for conditional GET instead of adding
compression-dependent hashing. [RFC 9110](https://www.rfc-editor.org/rfc/rfc9110.html#section-8.8.3.3)
explains the content-encoding distinction. Verification now covers gzip/identity,
304 headers, CORS access and delayed mobile responses after transport switches.

This pass changes sequencing and removes implementation machinery. It does not
add a service, public release version, catalog identity migration or product scope.
Runtime qualification and Alec's five implementation reviews remain unperformed.

## C1 execution against real data

Alec paused implementation for a fresh production catalog snapshot and approved
enabling local `norm_catalogue` for every artist with normalized data. The
restored snapshot has migration 14, 5,071,877 tracks and 756,587 normalized parts;
all 12 qualifying artists were enabled locally. The archive remains unchanged.
Original flags and restore SQL are saved with ignored local captures. The
[real-data review](review/README.md) links the runnable host and actual payloads.

The fresh code review exposed three integration errors, now fixed and covered:
Swagger used camelCase while JSON used snake_case; nullable typed IDs serialized
as objects; legacy global CORS blocked browser conditional-read preflight.
Full solution build and 33 focused ID/HTTP/session/legacy Swagger tests pass.
Existing package advisory and importer-nullability build warnings remain outside
this change. Live restored-record checks separately verify full/partial shows,
parent identity, historical names, gzip, 304s and CORS. No production capacity
claim follows from these local results.
