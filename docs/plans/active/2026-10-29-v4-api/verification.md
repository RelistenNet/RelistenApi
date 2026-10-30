# Verification, migration, rollout and recovery

C1 has local runtime evidence in the [response review](review/README.md).
The remaining sections are the full-plan execution checklist, not claims that
C2–C5 or production rollout have passed. No account migration or deployment occurred.

Follow [C1–C5 in the review guide](review-guide.md) for Alec's checkpoints.
Automated checks support those reviews; a green test run does not approve the
response design or authorize a deployment.

## Shared enum foundation, verified 2026-10-29

`codex/normalization-enums` contains the normalization vocabulary and its existing
consumers; `codex/v4-api-c1` is based on that branch and uses its types directly.
The foundation needs no database migration. Its solution build, 365 normalization
tests, and 65 API/normalization tests passed. Two optional lab tests were skipped.
An independent review found no remaining correctness or readability issue.

After restacking, the solution build and all 51 focused v4/ID/Swagger tests passed.
The running local API returned identical JSON for all six real track scenarios,
the venue example show, and the entire v4 OpenAPI document compared with
captures taken before the change. Existing package advisories and nullable
warnings remain. No production changes occurred.

## Venue alias simplification, 2026-10-30

The venue-history fix and `codex/normalization-venue-history` branch were discarded.
The active stack is `codex/normalization-enums` → `codex/v4-api-c1`. V4 returns
`imported_venue` for show display and optional `venue` for normalized navigation,
grouping, and details. Venue names are deduplicated `{ name, kind }` aliases with
no date fields. UI does not resolve historical periods. No history backfill or
normalization data/schema change belongs to this revision.

Verify alias deduplication across stored periods and merged venues, full/list/year
response agreement, and OpenAPI agreement. Alias name/kind corrections must change
ETags; date-only edits must not. Imported labels remain unchanged in either case.

Verified the solution build, all 52 focused v4/ID/Swagger tests, and all 365
normalization unit tests. No tests skipped in these runs. Fresh review found no
remaining correctness or readability findings. Real show/track captures were
refreshed from the local API; the live Kaiser venue and OpenAPI both expose only
`name` and `kind` for each alias. Normalization source and its tests match the
enum foundation exactly. Existing package advisories and nullable warnings remain.

## Venue-to-playback review, 2026-10-30

C1 now includes seven venue reads and direct source detail, for 16 implemented
GET routes. Follow the [resource journey](review/venue-source-journey.md) alongside
the expanded request collection. The full 62-operation manifest remains the
release baseline, not a claim that all routes are implemented.

Verify distinct show membership, approved canonical grouping, raw fallback,
cross-artist global reads, imported rows with zero shows, merged IDs/aliases, and
direct-source parity. Imported venue counts use the actual show-level collection;
source-only venue observations cannot inflate it. Test malformed/foreign/missing
IDs, known empty collections, ETag corrections, database failure and recovery.
Source responses must omit orphaned files and sibling recording contents.
Normalizers, accounts, legacy routes, and catalog data remain unchanged.

Verified the full solution build and all 74 focused v4/ID/Swagger tests, including
54 HTTP cases, plus all 365 normalization tests. No tests skipped. The two-pass
review found no remaining correctness or readability findings. Existing package
advisories and nullable warnings remain.

Real captures followed artist → venue → show → source → track and imported
fallback paths. All six direct-source scenarios matched the corresponding nested
show source and direct track. Grateful Dead's 544 venue groups (479 canonical,
65 imported) partitioned all 2,081 shows. Red Rocks returned 176 shows across 12
approved artists. Count/detail and global/scoped-list parity were checked.

Two local captures observed artist venue grouping at 49–61 ms, venue detail at
7–8 ms, and the selected source at 5–6 ms. The 176-show Red Rocks response took
74 ms in the final capture. These are local HTTP observations, not production
capacity or percentile guarantees. Full captures and timings are ignored local
artifacts; smaller full examples are linked from the resource journey.

Shared venue loading now reads assignments, unique canonical merge paths, and
aliases in three batch queries rather than the prior two. This adds a roundtrip
to show metadata reads but avoids repeating venue graph rows for every show and
keeps global, artist, and nested venue resources on one resolution policy.

## Main edit locations

API/normalization paths below are relative to this repository. Mobile paths are
relative to `/Users/alecgorge/code/relisten/relisten-mobile`.

- Shared IDs: new `Relisten.Identifiers/`, solution/project references,
  known-vector fixture under `RelistenApiTests/Fixtures/v4/`; matching copied
  TypeScript fixture checked for identical contents.
- API: `RelistenApi/Models/Api/V4/`, `Controllers/V4/`, `Services/Catalog/`,
  dedicated validation/results and `Startup.cs` serializer/DI/Swagger registration.
  Add core catalog result models and legacy adapters under `Services/Catalog/`.
  Move read algorithms rather than maintaining a second copy. Audit existing
  callers before removing migrated legacy SQL. Reuse SQL concepts from
  `Services/Data/CatalogReferences/`, without opening
  separate connections inside a v4 snapshot. All 62 catalog/operator method/path
  entries in `endpoints.json` must be implemented, plus the three in-place library
  routes and typed profile GET/PATCH actions.
- Canonical ID retention: next catalog migration and embedded normalization SQL;
  `NormStore.Venues.cs`, `NormStore.cs` restore/mint behavior only as needed by
  triggers. Existing delete/revert semantics remain; persistent reservations preserve public ID
  retirement and block sequential/concurrent random reuse. Test rollback too.
- Accounts: `Relisten.Accounts.Contracts/Library/`, `RelistenUserService/Library/`,
  identity/session/favorite/receipt/change/username EF configurations and the
  next transactional EF migration/model snapshot,
  `Controllers/LibraryController.cs`, `Authentication/Browser/BrowserRouteBoundary.cs`
  `Identity/Entities/`, `Identity/Usernames/`, `Identity/AccountProfileFactory.cs`,
  `Authentication/Authorization/CurrentAccountContext.cs`, principal factories,
  authorization/token/web callbacks, session credential
  codec and cursor protector. Keep `/v1` paths/browser policy; change owned ID
  fields/storage and preserve external-provider/OAuth/security semantics.
- Mobile: `relisten/api/v4/`, `api/client.ts`, `realm/schema.ts`, additive model/
  migration files, `realm/models/repo_utils.ts`, venue/song repositories,
  `library/favorite_catalog_applier.ts`, sync contracts/service, hydrator,
  membership projection and `realm/library_index.ts`.
- Mobile account identity: `accounts/api/account_contract.ts`, `account_profile.ts`,
  `accounts/account_credentials.ts`, `account_scope_store.ts`,
  `accounts/auth/secure_tokens.ts`, `username_service.ts` and account/library Realm
  models. Canonical account IDs and their account scopes must migrate together.
- UI: existing artist venue/song screens and show cards; add global venue route
  and My Library section in `app/relisten/tabs/(myLibrary)/index.tsx`. Reuse existing
  styling and existing loading/empty/error behavior.

## Running API review before broad implementation

C1 delivers a repeatable local review setup, not only an automated test result.
Use the existing Development API host, controllers and serializer/Swagger
registration. Manual review uses the restored real catalog, as Alec requested;
deterministic synthetic fixtures remain in disposable regression-test databases.
Add one setup/run command, a `.http` request collection with fixture variables,
and captured actual JSON/headers. Proposed paths and scenarios are in the
[review guide](review-guide.md).

The review command uses the restored standard local catalog and never resets it. Label the database and listening
URLs. Startup runs the checkout migrations as usual; C1 adds none. No import workers,
schedules or upstream calls run to populate review data. Leave the host running for manual edits/requests.
Use the same fixture builder for HTTP tests; do not maintain a mock response
service, alternate DTO schema or custom review UI.

Expose v4 Swagger at `/api-docs-v4` and `/api-docs/v4/swagger.json`. Provide full
show/track responses, not abbreviated snippets. Include follow-up requests with
the returned ETag and a controlled local normalization edit. The change must
produce new JSON/ETag with identical imported timestamps. C4 extends this same
collection to route families and links representative actual captures to its
coverage report. Keep authorized command examples separate from default read
requests; use inert dependencies for command acceptance and start no live imports.

## Boundary acceptance tests

Use current NUnit/FluentAssertions suites and mobile Vitest/Realm tests. Mobile
AGENTS' statement that there is no test framework is stale: `package.json` and
`vitest.config.mts` provide real test commands. Do not invent a parallel framework.

### IDs and real HTTP contracts

Extend API `TestSwaggerGeneration` and add focused v4 HTTP tests. Use an isolated
local Kestrel host on an ephemeral loopback port with real production controller,
validation, serializer, and Swagger registration; inject fixture data connections
and inert background dependencies. Do not boot production `Startup` schedules,
run real providers, or assume service-only tests prove HTTP behavior.

Prove all of these through responses:

- Shared vectors cover all prefix kinds, arbitrary legacy UUID bits, byte order,
  multi-underscore prefixes, overflow, wrong prefix and short canonical IDs.
  C# and TypeScript encode/decode the same IDs.
- Every DTO resource/reference uses its target prefix; account IDs use their six
  approved prefixes, while provider values and era metadata retain their meanings. No numeric catalog
  FK leaks through reviews/links/upstream assignments.
- Compare real action descriptors and v4 Swagger operations with `endpoints.json`.
  Retained non-v4 service/middleware dispositions remain in the design inventory.
  Verify hidden operator actions deliberately; Swagger alone can omit them.
- V4 string ID schemas and single `normalization` object are correct. Existing
  v2/v3 documents/JSON remain compatible and do not accidentally include v4 actions.
  Snapshot the current mixed legacy operation sets and compare them, rather than
  tightening old documents to URL-version-only inclusion.
- Validation, unknown/retired IDs, body limits, query filters, anniversary versus
  exact date, ranking windows and operator admission behave as the contract states.
- Normalization-disabled, entirely raw, partial medley, matched and non-song
  tracks all remain playable. Exact normalized song matches return correct
  show/source/track parents; setlist-only evidence never manufactures a track.

### Legacy shims and source readability

Before refactoring each family, freeze representative v2/v3 responses from
local fixtures: numeric and UUID references, raw labels, nullable fields, counts,
source/set/track order, lookup quirks, and error shapes. Compare the shimmed
responses semantically with those fixtures. Keep existing cache headers/keys and
prefer-replica behavior; test v4 primary selection separately. Preserve both
legacy Swagger mixed operation sets. Operator fixtures cover legacy body-target
update semantics versus v4 route-target semantics and shared import admission.

Fresh review must trace normalized success, raw/partial fallback and recovery
through v4 and a corresponding legacy adapter. Confirm core catalog models/readers
reference no legacy controllers/DTOs/Identifier and contain no API-version
branches. Ensure common rules have one implementation, batching remains visible,
and source readability passes separately from formatting. Do not delete a data
service merely because its HTTP route moved; audit importer/worker callers.

### Snapshot and update behavior

Use `MigratedTestDatabase` on disposable local PostgreSQL. Read part of a
response, commit a controlled concurrent writer, then finish the read: all
fields must come from the original snapshot. A write attempted through the
read session must fail. Verify cancellation/timeout disposal releases its pool
connection and no existing auto-prepare setting changes.

Read a full show, change/delete normalized parts or its canonical venue, then
read with If-None-Match: return 200 with changed ETag/data even when imported
timestamps are unchanged. An unchanged deterministic read gives 304. Removing
the latest normalized row must still change the representation. Disabling the
read flag removes the overlay and changes the ETag. “Recently updated”
ordering does not change for these normalization-only operations.

Run conditional reads with identity and gzip encodings. A weak payload ETag
supports unchanged 304 in both cases without hashing the compressed body.
Verify `Vary: Accept-Encoding` when compressed, the validator on 200/304, standard
If-None-Match comparison/list behavior, and a browser's permission to send that
header and read ETag through CORS.

Test two artists sharing a venue, show-specific splits of one imported venue,
date-specific names, room/site relationships, zero matched-track counts, raw
song fallback and canonical merges. Retire/delete a canonical row, observe 410
and resolver retirement, prove sequential and concurrent delete/remint reject
reuse through the permanent reservation PK, and restore it
through the existing human restore context. A rolled-back deletion creates no
retirement change. Keep existing normalization guards/revert tests passing.

### Account/auth identity migration and in-place protocol

Use a pre-typed account database fixture with users, provider links, native/web/
SSO sessions, favorite/receipt/change rows and username receipts. Audit all
application references, including the identity-session composite self-FK and
explicit IDs in stored application JSON. Convert each owned ID/FK to its exact
prefixed U26 value. Verify FK/unique/check integrity and one-to-one identity,
membership, username/counter/timestamp and hash preservation. Assert that owned
ID columns are text, not Guid with wire-only formatting. Framework PKs and
`NativeSession.AuthorizationId` remain UUIDs; provider issuer/subject/client IDs
remain unchanged. Test repeatable migration/restore on disposable databases.

Through real HTTP test `/v1/me` GET/PATCH, `/v1/library` snapshot/changes/batch,
new native and browser login, refresh, logout and account switching. Profile
`user_id`/`native_session_id` must equal typed first-party token `sub`/`sid` and
mobile credentials. Wrong prefixes/old raw UUID payloads fail; old cookies,
refresh credentials and cursor formats cannot enter the revised protocol.
A cursor reset uses 410 with `/v1/library/snapshot`. No `/v2/library` is registered.

Verify typed command/mutation retries and conflict arrays, quota, pages and
exact-target membership with unavailable metadata. Historical receipt hashes
are not rewritten or accepted as new typed mutations. Old pending alpha commands
are discarded during re-sign-in, not replayed under new IDs. Preserve numeric
library/username revisions and exact-target duplicate semantics.

Retest existing browser/native policies on the unchanged paths: wrong origin,
missing CSRF, wrong credentials, no-store and segment boundaries such as
`/v1/library-evil`. Test the new session cookie/protected cursor formats, not
merely controller outputs. Revocation must cover old native/web/SSO sessions
and linked first-party token families. Fresh provider login must attach to the
same migrated user, not create a duplicate account. External identity remains
provider-qualified and unchanged.

### Mobile behavior

First qualify the account-only C2 upgrade with v3 catalog and no normalized-table
dependency. Verify typed imported-favorite hydration through the unchanged v3
resolver; map its echoed references back before membership checks. Canonical
short targets without v4 metadata remain saved/unavailable, never sent to the
UUID-only resolver or erased. Re-sign-in must recover server memberships.

Then upgrade a schema-15 Realm fixture containing imported rows, an offline file,
queue/shuffle entries, history and private-alpha favorites. Verify identical
imported primary keys, file paths and parent links. New fields/models are additive;
the existing favorite migration still runs in the intended order. Test both a
pre-favorites schema and schema 15: legacy flags first become anonymous favorites,
then anonymous favorite IDs/targets map to native typed values and imported kinds. No raw UUID may acquire
normalized `song`/`venue` meaning during that conversion.

Apply a v4 normalization-only change with unchanged imported `updated_at` and
verify stored parts and canonical venue aliases/links change while the visible
imported venue label remains unchanged. Fail the Realm apply
and verify no new applied ETag or account cursor is committed. Simulate superseded
same-scope responses, account switch, response 304 with/without matching applied
local ETag, offline launch, reconnect, manual refresh and shallow resolver hydration.
Include a delayed legacy response arriving after the transport switches to v4;
the old generation cannot overwrite the new apply. Review one shared Realm/model/
repository set: screens/player/favorite rules contain no version branches, and
v4 updates bypass legacy timestamp guards through one apply coordinator.
Assert each mobile-used endpoint follows its manifest replacement policy. The
server coverage test, not a mobile generic interpreter, covers all 62 routes. Artist
collections replace only that artist/list; filtered/search/recent results replace
only their exact cached query result; resolver entity arrays only upsert. No
ordinary response contains `meta`, `data` or `included` wrapper fields, and no
body revision/completeness field controls deletion. Related summaries and limited
queries cannot tombstone unrelated entities or offline records.

Artist lists group canonical venues and preserve raw fallback. Counts remain
server counts. Canonical detail filters both artist and venue when artist-scoped;
global venue detail spans approved artists. My Library Venues does not change
ordinary show-favorite membership. Exercise exact-target merged/retired favorite
open/remove and alpha bootstrap; no promise of special merge consolidation.
Include an unfinished artist-only anonymous import with unchanged fingerprint:
discarded v1 mutations must reset its receipt to Deferred, not strand Importing.
Verify anonymous membership survives with deterministic `favorite_` IDs and
canonical typed targets; retry creates fresh typed mutation IDs. Normalized
song favorites add only the owning artist, with no inferred show membership.
Run v3 catalog transport with revised account library still enabled: imported song/venue hearts
and favorite filters use imported kinds, tracks use `track`, and all wire targets
pass typed-library validation. Stored membership targets must already be
canonical strings, not raw UUIDs encoded only at the HTTP boundary. Catalog fallback must not revert favorite-kind vocabulary.

## Commands during implementation

Run commands from the API repository root unless a different cwd is stated.
New test names below are planned, not existing tests that have already passed.

```sh
dotnet build RelistenApi.sln
dotnet test RelistenApiTests/RelistenApiTests.csproj --filter 'FullyQualifiedName~TestV4CatalogIds|FullyQualifiedName~TestSwaggerGeneration'
./test-local.sh --filter 'FullyQualifiedName~V4|FullyQualifiedName~TestNormStore|FullyQualifiedName~TestNormMigrations'
dotnet test RelistenUserServiceTests/RelistenUserServiceTests.csproj
dotnet csharpier check .
```

`./test-local.sh` starts/reuses local Postgres/Redis, enables API integration
fixtures, and runs normalization tests. The account fixture creates its own
disposable DB on the same local Postgres, independently of that script's test
selection. Check executed/skipped counts: unavailable Postgres can skip account
integration tests, and a green process with skipped acceptance tests is insufficient.

In the mobile repository, Node 22+ and the existing Yarn installation:

```sh
yarn test relisten/api/v4 relisten/realm relisten/library relisten/accounts
yarn test:favorites
yarn lint
yarn ts:check
```

Broaden suites after targeted checks pass. Before commits/pushes restore the
repository-local tool if needed, check CSharpier, fix/review formatting, and check
again. No commit, push, TestFlight release or production deploy is authorized by
this planning document alone. For simulator interaction use the installed
RocketSim workflow and capture an offline cold-start/queue restoration journey.

## Measure before expanding rollout

Build representative local snapshots/synthetic fixtures for small raw artists,
large fully normalized artists and partial catalogs. Measure full artist catalog,
full show, direct track, grouped venues/songs, 500-reference resolver and discovery.
Also capture pre-refactor v2/v3 baselines and compare the shimmed API on the same
fixtures/workload: cold and warm artist bootstrap, show detail and discovery,
including latency and query counts. Sequential snapshot assembly must not hide
a legacy regression behind response-fixture parity. Warm legacy cache hits must
not perform full catalog materialization, and legacy reads must not gain primary
traffic except their existing failover behavior.

Record query count/plans, latency distribution, response size, memory, primary
CPU/I/O/connections and concurrent ingestion behavior. Source measurements and
workload definition belong in the release record, not invented numbers here.

Also record index/storage growth and query plans for account login/refresh,
library snapshot/delta and favorite mutation after text PK/FK conversion. The
pre-public identity cutover must meet its measured account latency/index budget;
canonical strings are a maintainability choice, not a claim of zero storage cost.

Before beta expansion, Alec/Switz must set acceptable primary-capacity and latency
budgets from those measurements and current service SLOs, including acceptance
of legacy pre/post latency and query-count results. If primary snapshots
cannot meet the agreed budget with ordinary query/index tuning and batching,
stop expansion and revisit read routing/caching. Do not add writer counters,
WAL-token protocols, silent truncation or replica reads during implementation
without updating the consistency contract. Existing v2/v3 traffic remains the
fallback while this is resolved.

## Ordered rollout

1. **C1, local API acceptance.** Deliver the running response-review setup before
   broad route extraction, account conversion or normalized mobile storage. Freeze
   accepted ID/resource fixtures; record Alec's response feedback and the tested
   commit. Independent implementation can continue, but dependent expansion waits
   for review of the response it will reuse.
2. **C2, isolated account/auth rehearsal and cutover.** Test migration plus mobile
   identity upgrade on disposable databases with v3 catalog reads. Before a live
   cutover, verify account backups/restore, deployed migration mode and admission
   controls. Stop old account/auth traffic/writes, convert owned IDs/FKs/targets,
   revoke old sessions/token families and deploy typed principals/codecs on the
   unchanged `/v1` paths. Require tester sign-in through the restartable mobile
   upgrade. Validate provider links and server memberships before reopening
   admission. This build needs no normalized catalog tables/UI. Review and obtain
   deployment authorization separately; no old/new credential support is added.
3. **C3, developer mobile catalog proof.** With account behavior held stable,
   prove one raw and one normalized show/track journey, normalization-only refresh,
   interrupted apply and offline playback against the local API. No broad rollout
   or complete route-coverage claim is made at this checkpoint.
4. **C4, full API acceptance and additive server release.** Complete/review route
   families and their legacy adapters. Confirm all 62 operations plus retained
   account/auth capabilities, legacy fixtures and measured performance. Verify
   catalog backup/restore, replica/ingress configuration and current capacity.
   Install reservation triggers/backfill atomically while canonical writes are
   briefly locked; no row may escape the baseline. Keep v2/v3 available and v4
   mobile activation off. Pin known-good server/core rollback builds against the
   additive schema. Bounded production read-only smoke checks start no imports.
5. **C5, complete mobile and controlled activation.** Finish normalized storage,
   browse screens and global venue favorites. Qualify both direct schema-15
   upgrades and staged C2→C5 upgrades. Deploy the schema-compatible mobile build
   with catalog v4 disabled; confirm v3 playback first, then enable v4 for developer
   testing. Review raw/approved/partial and global favorite journeys, and record
   offline/correction/load evidence. Expand only after Alec's review, full route
   coverage and the agreed capacity budgets pass.

C2 is a separate account/auth release event from C4/C5 catalog deployment and
activation. C1/C3 are local/developer review checkpoints, not partial public v4
releases. Server catalog work can progress independently of the account migration.
Use existing controls for account admission, artist approval and mobile catalog
transport; do not add per-endpoint/model flags or a release orchestration service.

## Rollback and restart rules

- V4 projection/transport issue: disable v4 transport/overlays in the schema-compatible mobile
  build and use v2/v3. Preserve new tables/fields and imported UUID keys/files.
  An old binary/OTA that cannot open the new Realm schema is not a safe rollback.
- Shared catalog reader/command regression: mobile fallback alone is insufficient
  because both wire versions use the same core. Disable affected admissions and
  roll the API back to a pinned known-good core/pre-refactor build verified against
  the additive catalog schema/reservations. Keep v4 clients disabled until the
  forward fix. Test that rollback compatibility before release; preserve catalog
  rows and identity reservations. A command bug also requires stopping affected
  command admission while its accepted jobs/data effects are assessed.
- V4-only server result/serializer regression: disable v4 admission or roll that
  build back; legacy projections remain an available fallback only if core reads
  pass their compatibility probes.
- Account/auth issue: stop the affected account/auth admission and use a
  schema-compatible build or forward fix. Converted user/session PKs make the
  pre-migration account binary incompatible, not only its library routes. Do not
  downcast keys/targets or discard user/favorite data. A pre-migration binary
  requires restoring the coordinated account backup before new-format writes
  are accepted; after writes exist, preserve them and forward-fix instead.
- Interrupted account migration: keep admission closed; resume its transaction/
  verified map or restore the coordinated backup before reopening auth/account
  writes. Verify actual deployed migration mode and startup behavior.
- Interrupted mobile bootstrap/apply: transaction rollback leaves old applied
  state; rerun from new snapshot. A one-time version marker and account fence prevent
  repeated account resets and cross-account data application. Test interruption
  between SecureStore clearing and Realm commit: auth stays fenced until complete,
  and a later retry cannot clear newly issued credentials.

Keep migration `Down` guarded against destructive identity/target loss; prefer
forward repair after typed-account traffic exists. Repeat read/apply operations safely;
retry mutations only with their original typed mutation ID and canonical payload.

## Evidence to attach to the implementation handoff

Record passing route/OpenAPI/HTTP reports, executed integration counts, migration
before/after invariants, shared codec fixture hashes, mobile schema-upgrade and
offline journey results, representative query/load measurements, and exact
rollout/rollback switches and versions. Passing source-level documentation checks
is not evidence that these runtime release gates have passed.


## Stored artist–venue response review, 2026-10-30

The latest contract replaces flat `show.venue` with nullable `artist_venue`, whose
`artist_show_count` belongs to the artist/place relationship. Nested and direct
`venue.global_show_count` belongs to the shared venue and includes approved-read
artists only. Earlier captures and counts above describe their recorded revision;
refresh the saved evidence before treating it as proof of the new resource graph.

Local migration 15 and full recount were applied to `relisten_db`. Independent
per-show verification found 5,397 pairs across 12 artists, 13,418 memberships, and
zero mismatches. A subsequent recount changed no IDs, counts, or timestamps.
Both cascading parent foreign keys were verified; source assignments were unchanged.

Verification for this v4 update must show:

- One persisted relationship ID and artist-wide count across show detail, year
  summaries, artist venue list child, and direct artist venue detail.
- One global count across those nested venues and direct global detail, with
  disabled artists excluded and no narrowing from artist/year query filters.
- Imported fallback for disabled, unmatched, and unmaterialized relationships.
- Read requests leave count storage unchanged; explicit count corrections and
  approval changes invalidate affected payload ETags, while no-op refreshes do not.
- Alias merges use the canonical venue and relationship without inventing IDs.
- OpenAPI advertises the actual nullable graph, string IDs, and integer counts.

Implementation commit: `dfaf7e580`. Verification completed on 2026-10-30:

- Solution build and repository CSharpier checks passed. Existing dependency-audit
  and nullable-reference warnings remain.
- Focused HTTP, ID, and Swagger checks: 79 passed, including five new stored-count
  contracts and the then-current imported subset-count contract. That count
  contract was later replaced by full imported `show_count` in browse entries;
  use the current contracts and latest verification for that behavior.
- Combined API, v4, normalization, migration, ingestion, catalog, ID, and Swagger
  checks: 209 passed; two opt-in model/corpus checks skipped.
- Normalization library: 366 passed.
- Independent two-pass review found no unresolved issues. The reader cleanup
  separates lightweight membership, canonical resources, and imported fallbacks.
- The local API was restarted through the client-only review script, with local
  database routing, Hangfire workers disabled, and no recurring job registration.
- All 46 real-data capture requests succeeded. Red Rocks returned a global count
  of 176; Grateful Dead's stored relationship `artist_venue_344wrm28` returned 20.
  Its detail, show, year summary, and nested global venue matched exactly.
- An ordered checksum of all stored relationship IDs, parent keys, counts, and
  timestamps was unchanged before and after the live requests. Reads did not
  refresh or mutate count storage.

Live examples and curated responses are linked from the
[review overview](review/README.md). Full captures, response headers, and timings
remain in the ignored `local-dev/v4-review/.output/` directory. Production and
mobile/account rollout remain outside this local response review.


## Full imported venue counts, 2026-10-30

Imported venues now carry the same full `show_count` in mixed browse entries,
raw inventory, detail, show/year metadata, and sources. The browse wrapper has
no count field. Include an imported entry when any show needs an imported
fallback, but open its full collection; partial normalization can therefore
produce overlap with canonical entries. Mobile must not sum these browse rows.

Verification completed locally:

- All 65 targeted v4 HTTP and Swagger checks passed. The partial
  fixture has two shows at one imported venue, one normalized: browse, raw
  inventory, show metadata, and imported detail all report two. Assignment-only
  normalization changes preserve the full imported count.
- A new HTTP test moves the normalized show to another imported venue without
  changing imported timestamps. The remaining browse entry stays present, its
  full count changes from two to one, and its ETag changes. Detail, source,
  year, and show reads agree; restoring the assignment recovers the count of two.
- Source-only venue observations report zero shows. Missing materialized pairs,
  disabled normalization, broken merge chains, and unused imported rows retain
  their existing fallback/empty-result behavior.
- The local API was restarted through `local-dev/v4-review/run.sh`. All 46
  real-data capture requests succeeded. Grateful Dead's 544 mixed entries omit
  the removed wrapper count, and every imported child exactly matches its entry
  in the full imported inventory. A selected detail count matches its show list.
- Live OpenAPI exposes an integer `ImportedVenueResource.show_count`; the
  redundant count-only resource and wrapper count are absent. Revalidating a
  pre-change response returns 200 with a new ETag; revalidating the new response
  returns 304.
- The full imported inventory count query used the existing
  `(artist_id, venue_id)` index with zero heap fetches for Grateful Dead's 2,003
  imported venues. One local `EXPLAIN ANALYZE` completed in 10.211 ms; this is a
  local query measurement, not a production latency target.
- Two independent reviews found no unresolved correctness or readability issues.
  Repository CSharpier, whitespace, and review-script syntax checks passed.
  The ordered checksum of stored artist–venue rows was unchanged after HTTP reads.

The updated curated response examples are in [review/responses](review/responses/).
Mobile persistence changes remain planned; no production catalog data changed.


## Song browsing and recording discovery, 2026-10-30

Implemented six song reads, bringing the local v4 review to 22 GET operations:
artist song browse, canonical detail, matched files, matched shows, imported
inventory, and imported detail. [Song-to-playback review](review/song-source-journey.md)
links the real URLs and saved examples. Imported setlist evidence remains separate
from exact recording membership.

Verification completed:

- All 79 v4 HTTP checks passed after the shared-reader refactor and final
  simplification. The 18 new song cases cover repeated song parts in one file,
  multiple recordings per show, merged identities/aliases, partial interpretation,
  orphan/unresolved exclusions, zero-match songs, raw/disabled artists, imported
  date-and-artist membership, duplicate setlist dates, invalid IDs, count-only and
  alias ETag changes, dependency failure, and broken-merge recovery.
- Existing show/source/track HTTP contracts still pass. Discovery uses the same
  track projection and batched interpretation policy; compact embedded song
  references retain their four-field contract.
- The client-only local API was restarted against the restored `relisten_db`.
  All 62 real-data capture requests passed, including browse/detail equality,
  one file per result, exact matching part ordinals, complete file/show count
  equality, exact source/show traversal, imported list/detail equality, an
  unapproved artist, and a zero-match canonical song.
- Grateful Dead browse returned 547 entries: 402 canonical songs and 145 imported
  fallbacks. Scarlet Begonias returned 3,355 files across 332 distinct shows.
  Its mapped imported song returned 315 catalog shows from setlist evidence.
  Ryan Adams returned 786 imported entries through the same artist browse route.
- Normalization table row counts and maximum update timestamps were unchanged
  before and after the HTTP captures. Readers use read-only snapshots; tests
  inserted and changed edge cases only in their disposable database.
- Two independent correctness/readability reviews found no unresolved issues.
  The simplification pass extracted named evidence-mapping and alias-read phases;
  the 79 checks passed again after that change. CSharpier, whitespace, response
  JSON, documentation links, and review-script syntax checks passed.

### Capacity check before rollout

Real local `EXPLAIN ANALYZE` measured 50.675 ms for Scarlet's exact membership
and counts, and 3,910.799 ms for all 402 Grateful Dead canonical song counts.
Batched invalid-file validation replaced a correlated per-file check. Moving
ordinal aggregation did not improve the full-artist query and was discarded.

The HTTP capture measured 5.704 seconds for the complete artist song list,
including imported setlist counts; imported inventory took 1.394 seconds.
Scarlet detail took 47 ms, the 3,355-file response 200 ms, and matched shows 77 ms.
These are individual local observations, not a production load test. The
artist-wide count cost remains an explicit rollout gate: simplify or optimize
those reads and verify primary capacity before enabling the mobile v4 transport.
No generic count cache, new count table, background writer, or extra migration
was added for this API review checkpoint.

Full JSON, headers, and timings remain in ignored `local-dev/v4-review/.output/`.
Alec's response acceptance, mobile apply, account/auth migration, and production
rollout remain pending. Production data and credentials were not changed.


## Stored song counts and latency, 2026-10-30

The [song-count foundation PR #111](https://github.com/RelistenNet/RelistenApi/pull/111)
is stacked on artist–venue PR #108, below v4. [Admin PR #3](https://github.com/RelistenNet/relisten-admin/pull/3)
now refreshes song counts atomically with catalogue edits. The foundation's
[maintenance contract](../2026-10-30-song-counts/README.md) defines storage,
locking, repair, and rollout. V4 reads stored counters; recording discovery and
recounts share `norm.song_recording_matches`.

Completed verification:

- 133 combined API/foundation checks passed: 82 v4 HTTP/snapshot cases and 51
  song-count, artist–venue, and rebuild checks. Three new HTTP cases prove that
  stored counter-only corrections invalidate ETags, reads never recount, and
  live discovery can lead the stored count until explicit refresh repairs it.
- Admin passed 38 targeted checks, formatting, lint, and type checks. The tests
  cover atomic source/count commit and rollback, narrow execution grants,
  same-artist serialization (including uppercase UUID input), disjoint progress,
  and nonblocking readers. Its broader lab-dependent suite was unavailable.
- Both independent correctness/readability reviews found no unresolved issues.
  Recounts reuse the existing artist catalogue lock. There is no new global gate,
  recurring scan, queue, or per-row source trigger. Ordinary full repairs commit
  each artist separately; the one-time migration retains its locks until commit.
- The official worker applied migration 16 to local `relisten_db`, including its
  seed, in 10.208 seconds. It created 4,539 canonical and 16,606 imported counters,
  covering every current parent. All 402 canonical and 557 imported Grateful Dead
  counts matched the saved pre-storage live responses. Normalization row counts,
  update watermarks, and audit totals/watermarks stayed unchanged.
- A repeated Grateful Dead recount returned zero changed rows. Ordered checksums
  of both counter tables, including timestamps, stayed unchanged. Those checksums
  also remained unchanged after all HTTP captures and timing requests.
- V4 was restacked with all 19 earlier commits preserved; range-diff showed every
  replayed patch unchanged. The local client-only preview was restarted. All 62
  real-data capture requests passed, complete browse/imported bodies matched the
  pre-storage responses, and the pre-storage artist-list ETag still returned 304.
  Live Swagger retains the same 22 routes and response schemas.

### Measured response times

Five successive full-body 200 GETs per endpoint used `curl` against the restarted
local API. These are warm local samples, not production capacity measurements:

| Endpoint | Earlier capture | Stored-read median | Stored-read range |
| --- | --- | --- | --- |
| Grateful Dead song browse | 5,704 ms | 18.751 ms | 14.446–27.799 ms |
| Full imported song inventory | 1,394 ms | 6.252 ms | 5.519–6.627 ms |
| Scarlet Begonias detail | 47 ms | 6.612 ms | 5.271–8.093 ms |

The separate 62-request capture measured 27.716 ms for artist song browsing and
10.619 ms for imported inventory. File/show discovery remains live: the complete
3,355-file response took 269.897 ms and the 332-show response took 87.244 ms in
that capture. An initial browser revalidation after restart logged 344.670 ms;
warm medians do not describe cold startup or production load.

Raw timing samples and captures are in ignored
`local-dev/v4-review/.output/stored-count-timings.json` and
`request-timings.json`. The expensive artist-wide count aggregation is removed from
GETs. Production load/capacity validation and mobile apply remain pending.
No production deployment or database mutation occurred.
