# Verification, migration, rollout and recovery

C1 has local runtime evidence in the [response review](review/README.md).
The remaining sections are the full-plan execution checklist, not claims that
full C2–C5 or production rollout have passed. The [C2 account checkpoint](review/accounts.md) records local migration/API rehearsal; no production deployment occurred.

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
  Keep v4 result models/readers native. Existing legacy services remain in place;
  share low-level storage helpers selectively without imposing old wire behavior
  on v4. All 51 v4 method/path entries in `endpoints.json` must be implemented;
  eight operator routes remain at their v2 paths. Also verify the three in-place
  library routes and typed profile GET/PATCH actions.
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

### Native readability and legacy coexistence

Keep v2/v3 services on their current read paths. For shared schema/storage changes,
compare representative old-client responses: numeric/UUID references, raw labels,
nullable fields, counts, source/set/track order, lookup quirks and error shapes.
Retain cache headers/keys, prefer-replica behavior and the mixed legacy Swagger
operation sets. Do not create new operator aliases or legacy adapters for coverage.

Fresh review traces native normalized success, raw/partial fallback and recovery.
V4 readers/models must reference no legacy controllers/DTOs/Identifier, carry no
legacy policy switch, and preserve snapshot ownership and deterministic ordering.
Review readability separately from formatting. Existing data-service importer and
worker callers remain supported; they are not part of a migration backlog.

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

Then upgrade the current schema-17 account build and an older schema-15 Realm
fixture containing imported rows, an offline file,
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
server coverage test, not a mobile generic interpreter, covers the 51 v4 routes
and separately checks the eight retained operator routes. Artist
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
For shared schema/storage changes, compare unchanged v2/v3 on the same
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
   families and verify coexistence with existing legacy services. Confirm all
   51 v4 operations, eight retained
   operator operations and retained
   account/auth capabilities, legacy fixtures and measured performance. Verify
   catalog backup/restore, replica/ingress configuration and current capacity.
   Install reservation triggers/backfill atomically while canonical writes are
   briefly locked; no row may escape the baseline. Keep v2/v3 available and v4
   mobile activation off. Pin known-good server/core rollback builds against the
   additive schema. Bounded production read-only smoke checks start no imports.
5. **C5, complete mobile and controlled activation.** Finish normalized storage,
   browse screens and global venue favorites. Qualify current schema-17, direct schema-15
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
- Admin passed two refresh-wiring checks, its existing rollback check, and 27
  pure checks, plus formatting, lint, and type checks. The two dedicated count
  suites were removed: SQL algorithms, permissions, and concurrency are covered
  in the API foundation rather than duplicated in the admin. The broader
  lab-dependent admin suite was unavailable.
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


## Resource-with-shows contract review, 2026-10-30

The current contract has 20 local GET routes. Earlier 22-route captures above
predate this change. Metadata-only canonical detail and bare venue/song show-list
routes are superseded by the resource-with-shows details in [contracts](contracts.md).
The earlier metadata-only song-detail timings do not measure the new full detail.

Verify the public contract through focused HTTP tests and real-response review:

- Global venue, artist-venue, imported venue, canonical song, and imported song
  details contain flat metadata and complete `shows`.
  Empty imported/artist-venue/song details retain `artist_id` and `shows: []`.
  No detail response repeats artist resources; mobile caches them separately.
- Direct artist-venue ID and artist-plus-venue lookup return the same detail.
  Imported lookup needs only the imported ID. Wrong ID kinds return 422;
  valid missing IDs return 404.
- Browse and embedded resources contain neither detail `shows` nor `artists`.
  Song file-match detail contains song metadata and complete `tracks`,
  with no `shows` property. Removed routes are absent from live OpenAPI.
- One payload ETag covers metadata and membership from one read snapshot.
  Show-membership changes invalidate detail even with unchanged parent timestamps;
  unchanged requests return 304. Dependency failure returns a no-store 503.
- Counts keep their stored meaning and may lag live membership. Reads do not
  recount or mint identities. A detail array length never overwrites a count.

Mobile verification remains future work: apply resources and the detail owner’s
replacement membership, including empty lists, with the ETag in one transaction;
retain shared entities, downloads, favorites, and other memberships. File-match
apply cannot clear song-show membership. Failed fetch/apply retains prior state.

### Verified implementation and local review

Source commits `19686d4ee` and `3945ca65e` implement complete details and direct ID
lookups without embedded artist resources. All 82 focused v4 HTTP tests passed
against the final contract. A fresh review found no additional issues. Tests cover
direct lookup, resource/collection consistency, empty membership, approval and
merge behavior, validators, and dependency failures. They use disposable catalog
data and do not edit the restored review catalog.

The local API has been restarted for final real-response review. No production
catalog changes, deployment, account migration, or mobile runtime changes occurred.
Alec’s next checkpoint is venue/song response review before account/auth work.
Production load and mobile transactional apply remain unverified.

The final real-data capture passed its assertions with no detail `artists` arrays.
The following are individual full-response samples, not medians or production
capacity measurements:

| Detail | Returned membership | Time |
| --- | --- | --- |
| Shared Red Rocks venue | 176 shows | 29.894 ms |
| Imported Ace’s Studio (`imported_venue_78331pxdqjdg434rrgm1d2pnrj`) | 1 show | 5.540 ms |
| Song `song_mbkeejfd` | 147 shows | 68.175 ms |
| Scarlet Begonias | 332 shows | 180.401 ms |
| Scarlet track detail | 3,355 files | 187.366 ms |

Revalidating captured ETags for a global venue and both user-reported examples
returned 304. Curated excerpts retain detail metadata and select the first show
or file match. Complete response bodies, headers, and timing samples remain in
ignored `local-dev/v4-review/.output/`; the live URLs return full collections.
Artists resolve by `artist_id` from mobile’s separately loaded artist catalog.

The simplified track contract passed all 82 focused v4 tests and a fresh review.
The latest capture verifies `tracks` contains direct `TrackResource` items, unique
by track ID, identical to direct track lookup, with matched parts referencing the
root canonical song ID. The user-reported `song_mbkeejfd` returns 1,483 direct
tracks; its captured ETag revalidated with 304. Matching parts come from each
track’s `normalization.parts`, with no wrapper or duplicated ordinal field.


## Tours, eras, and source reviews, 2026-10-30

All 88 focused v4 HTTP tests passed after adding artist tour inventory, direct
tour-with-shows detail, artist eras, and source reviews. The current local OpenAPI
has 24 GET paths and no direct source-set route. The complete planned manifest has
59 catalog/operator operations; source sets remain embedded in source/show detail.
The fresh correctness and readability review found no defects. Its suggested
cleanup moved review projection into the existing catalog projection class; all
six navigation tests passed again after that move.

The real-data capture passed tour list/detail/embedded count and metadata parity,
era field shape, and review parent/identity assertions. Phish’s 130 tours returned
in 9.949 ms; Fall Tour 1997 returned its 21 shows in 16.452 ms; four Phish eras
returned in 6.388 ms; 361 source reviews returned in 22.705 ms. These are individual
local full-response samples, not medians or production capacity measurements.
Tour detail and source review list both returned 304 for their captured ETags.

The tour-count query uses an artist-bounded index scan and measured 10.35 ms in
the local database plan. Tour counts describe actual catalog show assignments;
legacy setlist counts remain unchanged. No new counter maintenance or locks were
introduced. The [review overview](review/README.md#tours-eras-and-source-reviews)
links the live responses and selected excerpts. Complete bodies, headers, and
samples remain in ignored `local-dev/v4-review/.output/`.

No production changes, account/auth migration, or mobile runtime changes occurred.
Review these responses before proceeding to auth. Production load and the future
mobile transactional replacement behavior remain unverified.


## Artist snapshots and saved-reference resolution, 2026-10-30

Migration 17 was applied to local `relisten_db` through the worker’s normal
migration command. Version 17 is verified. Its 4,539 song and 5,478 venue
reservations match current canonical row counts; no known deleted rows were
available to backfill as retired reservations. Real-data captures do not establish
410 behavior; retirement and restore need the disposable regression fixtures.
No production database or normalization jobs were changed.

The restarted API exposes 26 paths and 26 operations: 25 GETs and resolver POST.
The planned manifest remains 59 catalog/operator operations. Positive real-data
capture passed snapshot/list parity, identical raw/approved snapshot field sets,
imported fallback, shallow recording references, and unchanged 304 checks for both
artist snapshots. Full response sizes below are uncompressed JSON. Times are
individual local full-response samples, not medians or production capacity tests:

| Response | Membership | Body size | Time |
| --- | --- | --- | --- |
| Grateful Dead catalog | 2,081 shows, 479 artist-venue rows, 402 songs | 6,509,939 bytes | 234.430 ms |
| Ryan Adams raw catalog | 581 shows; canonical entity arrays empty | 2,118,717 bytes | 160.747 ms |
| Mixed resolver batch | 19 references; all 14 entity groups populated | 39,937 bytes | 81.924 ms |

The resolver returned 17 available, one redirected, and one unavailable result in
input order. Duplicate input references retain duplicate results while entity IDs
deduplicate. Explicit legacy schemes, complete normalization on returned tracks,
shallow parent resources, preserved imported identity, and no-store/no-ETag headers
passed capture assertions. The mixed request includes a partial track. The local
catalog has no retired reservations, so that status is not claimed by this capture.

[Live snapshot URLs and the resolver request](review/README.md#review-offline-catalog-sync)
are ready for Alec’s review. Saved JSON selects one entry per domain/entity group;
complete bodies and timing/header evidence remain in ignored
`local-dev/v4-review/.output/`. The snapshot intentionally omits full recording
hierarchies; their validators own track-only changes outside represented catalog
fields. Mobile transactional apply, production load, account/auth migration, and
deployment remain unperformed.

Final verification passed 131 combined normalization-store, migration, public-ID,
and v4 checks. All 12 artist-snapshot/resolver HTTP checks passed again after the
final fixes. Review found a detached source could be reported available without
an entity; lookup now requires its show parent, and the focused test covers that
unavailable result. Fresh SQL and API review found no remaining concerns.

Live OpenAPI requires the root `references` field, limits it to 1–500 items, and
requires item `kind`, `scheme`, and `id` with closed kind/scheme schemas. Manual
oversized-body checks passed with both Content-Length and chunked requests: each
returned a no-store 413 ProblemDetails with `request_too_large`. The resolver’s
size limit and exception filter own that boundary. Positive response captures
remain valid after these validation-only fixes. No auth or mobile phase began.

### Date and recent browsing checkpoint — 2026-10-30

Commit `ba194f4ae` restores `artist.features` and implements 13 discovery
operations, bringing the running local contract to 39 operations (38 GETs and
resolver POST). No auth, account, migration, or production changes were made.
See the [review walkthrough](review/discovery-browsing.md) and regenerated
[requests](review/requests.http). Full responses, headers and single-request
local timings are in ignored `local-dev/v4-review/.output/`; checked-in
anniversary/recent excerpts select only the first two results.

`RELISTEN_LOCAL_INGESTION_TESTS=1 dotnet test
RelistenApiTests/RelistenApiTests.csproj --no-restore --filter
'FullyQualifiedName~RelistenApiTests.V4'` passed all 113 checks. The strengthened
recent-order test also passed after adding repeated artist filters and asserting
that normalization corrections leave imported update timestamps unchanged.
The new tests share the existing HTTP host and fixture, with no separate harness.
They exercise anniversary versus exact/partial display-date lookup, raw and
normalized summaries, playback equivalence, source-vs-show timestamps, day-window
precedence and caps, rating rank, deterministic ties, ETag refresh, random no-store,
empty/unknown artists, invalid queries, and public Swagger enum/error contracts.

The solution build passed. `dotnet csharpier check .` and `git diff --check` passed.
A two-pass independent review confirmed two issues, both fixed and verified:

- Controller JSON metadata overrode the shared error media type. Shared failures
  now use explicit JSON ProblemDetails; discovery OpenAPI declares the same
  `application/problem+json` type. Runtime invalid sort returns 422/no-store.
- An extreme but valid `previous_days` overflowed PostgreSQL timestamps. The
  indexed timestamp predicate now saturates at year 1, the minimum supported
  catalog timestamp. The int32 maximum returns 200; ordinary windows retain
  their existing inclusive cutoff and limit behavior.

The code-simplifier pass retained one discovery reader and the existing batched
metadata/full-recording loaders. No new query framework, read repair, Redis cache,
aggregate refresh, or database schema was added. Date windows use database
`CURRENT_DATE`; normalized metadata shares the request's primary repeatable-read
snapshot. Invalid input and dependency failures are uncacheable; a retry opens
its own snapshot.

After restarting the production composition locally, live OpenAPI exposes 39
operations and the two allowed `sort_by` values. The capture script succeeded
against the restored real catalog. Grateful Dead's July 7 anniversary list has
eight shows, and exact 1978-07-07 resolves to
`show_0gmtpk4g0pf5yfayb76m0j0k7g`. One local capture measured artist recent lists
at 17–35 ms, global five-show recent lists at 9–74 ms, top-five at 24 ms, exact
full show at 74 ms, random full show at 37 ms, and the 702-show global today list
at 82 ms. These are individual development-machine samples, not production load
results or percentile estimates.

Search and popularity/trending/momentum account for the remaining 10 mobile-facing
catalog operations. Another 10 planned operations cover operator/import and
live activity. Account/auth work remains a separate checkpoint after response
review.

### Search and listening rankings checkpoint — 2026-10-30

Commit `2471dadbf` implements all ten remaining mobile-facing catalog operations:
search; popular/trending artists, years and shows; one/multiple-artist combined
popular/trending groups; momentum. Live OpenAPI now exposes 49 paths/operations
(48 GETs, resolver POST). Ten operator/import/live operations remain planned.
No auth/account work or schema migration was included. The
[review walkthrough](review/search-rankings.md) links each new route and explains
ranking-input provenance and mobile query replacement.

The solution build passed. The final focused command
`RELISTEN_LOCAL_INGESTION_TESTS=1 dotnet test
RelistenApiTests/RelistenApiTests.csproj --no-restore --filter
'FullyQualifiedName~RelistenApiTests.V4|FullyQualifiedName~RelistenApiTests.Popularity'`
passed all 148 checks. Search/ranking tests use the shared HTTP host and production
SQL/serializers, with isolated Redis DB 15 and restored fixture keys. They cover
six-category empties, category toggles, artist scope, source summaries, approved
song/venue overlay and imported fallback, inherited venue aliases, typed parents,
window order, trend thresholds, duplicate artist filters, independently limited
groups, organic momentum, invalid inputs, and conditional GET. Existing scoring
tests protect the shared algorithms; they were not duplicated in the new tests.

A two-pass review confirmed and fixed three issues:

- An initial owner cap could hide better global search hits. The final queries
  limit matched resources before hydration, with no full artist-inventory loops.
  Live venues-only `Center` search returns 20 hits, including The Ashkenaz
  Community Center (123 shows), which the owner cap excluded.
- An alias on a merged predecessor could be displayed but not searched. Reverse
  lineage now finds inherited song/venue names. The live Grateful Dead query
  `The Crystal Ballrom` resolves to canonical `venue_xzh8yyp9`. A focused fixture
  also verifies imported labels assigned through a merged venue ID.
- Explicit legacy score refreshes could reuse stale global maps and relabel
  them fresh. Legacy compute methods now call fresh map builders directly;
  v4 retains cached input maps and hydrates current catalog metadata separately.

The code-simplifier pass removed dead extracted row fields, duplicate momentum
sorting and unused search parameters. Shared trend floors remain owned by the
existing scoring service. Public ranking DTOs reuse artist/year/show projections
and add only rank and a compact score object. No response-cache framework,
additional normalization tables, scheduled catalog refresh, or new locking was added.

Alec approved a real aggregate snapshot for review because the local catalog
backup contains zero daily/hourly play rows. Production reads used the verified
`relisten3-k3s` context and read-only replica, with TLS required and
`default_transaction_read_only=on`. `psql` confirmed read-only admission. An ad hoc
macOS Npgsql client failed its TLS handshake, so the export used `psql` with the
same SELECT queries and existing score builders. `AGENTS.md` now records the
correct context and this fallback (`1fe15a8a0`). Production was unchanged.

Only real ID/metric maps were written into local Redis DB 0: 2,817 artist,
38,628 show and 9,341 year entries; 2,081 Grateful Dead and 1,892 Phish show entries
for per-artist momentum. The maps were marked fresh for 24 hours locally.
Capture time, source and counts are saved in ignored
`local-dev/v4-review/.output/ranking-snapshot-provenance.json`, with previous cache
values in `ranking-cache-before-snapshot.json`. Raw aggregate files are retained
there. The one-time SQL export and scoring runner remain under `/private/tmp/`;
they are review tooling rather than a production deployment path. No synthetic
play events were loaded, and no catalog rows were modified.

Manual live checks returned populated ranking arrays, all six search categories,
the inherited venue alias, and typed artist groups. A global `Scarlet` search took
841 ms in one local sample; venues-only `Center` took 192 ms. Five-result artist/year
rankings took 43–114 ms, per-artist grouped/momentum requests 55–67 ms, and global
show rankings 389–439 ms in initial samples. Global show reads deserialize the
38,628-entry score map before selecting IDs; they do not hydrate that full catalog.
These development-machine samples are not load tests or latency percentiles.
Current timings and complete response headers/bodies are refreshed by the review
capture script. Production input-cache miss/stale behavior remains asynchronous
and separate from the request's catalog snapshot. Redis/SQL failures return
503/no-store; no partial successful result or validator is published.

### Shared artist-local show popularity — 2026-10-30

Commit `c90c069a9` publishes nullable `popularity` on the shared show summary and
full-show resource. Artist/year lists, song/venue/tour details, date/recent lists,
search, snapshots, resolver parents, full shows and rankings use one enrichment
and projection. A missing artist-map entry stays `null`; normalization approval
does not control metrics. Ranked shows inherit the field and add only `rank`.
Global scores and organic momentum choose ordering without overwriting the
returned raw artist-local momentum score.

`CatalogShowPopularityReader` is request-scoped and shares each artist's raw map
between ranking selection and show enrichment. Catalog SQL retains its existing
primary repeatable-read transaction. The score cache remains an asynchronous
input with its existing miss/stale refresh behavior. A failed request does not
retain its memoized fault into a later request. Cancellation stops waiting while
an already-started input refresh may complete. No schema, auth, account, or
production configuration changed.

The focused v4/popularity suite passes all 148 checks. The existing ranking HTTP
fixture was extended rather than adding another host or fixture builder. It uses
deliberately different global, raw local and organic scores to verify cross-route
field equality and unchanged window/organic ordering. It also checks a raw
artist with activity, a missing entry, score-only ETag changes on rankings and
artist snapshots, and fresh-request recovery. The later test simplification
reuses cache DTOs and names the cross-route assertion phase; its focused rerun
passes. Redis fixture keys are restored and remain isolated in DB 15.

Two-pass independent review found one error-boundary gap: stale score inputs can
trigger a Hangfire refresh enqueue, whose wrapped storage exception escaped as
500. The focused regression reproduced 500 with the handler excluded; adding
`BackgroundJobClientException` to the existing dependency boundary produces
503/no-store without an ETag and then recovers on a fresh request. No new retry,
queue, or best-effort refresh policy was introduced. No other review findings
remained. Formatter and diff checks passed.

Alec's approved local real-data review snapshot was expanded using the production
read-only replica and the existing show aggregate/scoring rules. The batch export
was partitioned by artist before computing momentum: 62,650 show entries across
2,814 artists, plus valid empty maps for 1,552 other local catalog artists. Only
local Redis DB 0 was updated. The capture at 2026-10-31 00:02 UTC (October 30,
5:02 p.m. Pacific) is recorded separately under `artistLocalShows` in ignored
`ranking-snapshot-provenance.json`; original local values remain backed up.
Production was not changed and no synthetic play events were added.

The regenerated capture checks every encountered show for a `popularity` field
and compares repeated IDs across routes against the fixed local input snapshot.
It verified 4,513 unique show IDs, 3,188 with metrics; remaining entries are null.
Full captures and `shared-popularity-review.json` are ignored local artifacts;
checked-in excerpts now show the shared field. Explicit HTTP comparison with
direct show details also confirmed equality for artist, song, venue, date and
ranking results.

Single warm capture samples: artist shows 78 ms, complete artist snapshot 146 ms,
Scarlet Begonias detail 93 ms, shared Red Rocks detail 53 ms, July 7 anniversary
15 ms, global today 273 ms, five popular shows 100 ms and momentum 27 ms. These
are development-machine samples, not percentiles or production load evidence.
The global default date crossed UTC midnight during this review; it uses the
unchanged database-session date contract. No server-side response cache, generic
revision mechanism or additional locks were added.

### Contextual popularity simplification — 2026-10-30

Commit `6bfd82dc3` supersedes the uniform artist-local ranking rule from the
previous checkpoint. Alec clarified that mobile ranking views are transient and
do not sync into the offline catalog. Ordinary show resources and artist ranking
groups retain raw artist-local metrics. Global popular/trending resources publish
the global metrics already used for their selection. Momentum publishes its
organic selection score with the existing raw fallback.

Shared show enrichment now accepts an optional loaded score map. Rankings supply
it; ordinary batches load once per artist. The request-scoped map reader and its
registration were removed. There is still one nullable field and one projection,
with no new response wrapper, context field, schema migration, or mobile runtime
change. The nine ranking operations now declare `transient_ranking` in the plan's
endpoint manifest; their context-dependent scores and entity membership must not
be applied to persisted mobile catalog records. ETags remain available for online
revalidation. Search and ordinary resource/detail apply policies remain intact.

All 148 focused v4/popularity checks pass. The existing fixture now verifies:

- Ordinary copies remain artist-local; global responses return deliberately
  different global scores, and momentum publishes its organic scores in order.
- An artist-score refresh changes the ordinary snapshot ETag while leaving the
  global ranking ETag unchanged. A global-score refresh changes that ranking ETag.
- Global ranking reads succeed while an unused stale artist map would fail its
  refresh enqueue; ordinary reads still return 503/no-store and recover on retry.
- Shared normalization, raw fallback, typed identities, ownership, and missing
  activity behavior remain unchanged.

Two-pass independent review found no code issues. One contradictory sentence in
the search/ranking apply documentation was corrected to distinguish search-query
apply from transient rankings. The simplicity pass retained the supplied-map
interface and removed the request-lifetime machinery. Formatter, diff, JSON and
local-link checks passed.

The API was restarted against the existing real-data catalog and metric snapshots.
Direct live comparisons against local Redis confirmed global popular metrics match
the global input map, momentum fields match organic/fallback inputs, and ordinary
show details still match raw artist-local inputs. No production data was changed.
The capture script was refreshed: it requires a popularity field on show resources
and compares score equality only among ordinary catalog copies, not transient
ranking contexts. Resolver checks target entity shows rather than input-reference
objects. It verified 4,508 ordinary show IDs, 3,183 with metrics. Complete bodies,
headers and input provenance remain in ignored output; checked-in ranking excerpts
now reflect the restored global/organic semantics.

## Stored popularity checkpoint — October 30

The [stored-popularity review](review/stored-popularity.md) supersedes the Redis
read/refresh behavior described in the earlier ranking checkpoints. V4 now
selects and enriches from stored Postgres rows, in its catalog snapshot. The
foundation calculations continue Redis publication for legacy readers.

All eight selected real responses and their ETags matched. Five popular shows
measured 4.8 ms / 0.23 MiB allocated instead of 123 ms / 82.4 MiB; July 7 across
artists measured 28 ms / 7.72 MiB instead of 266 ms / 122.6 MiB, with no Redis
commands. The focused suite passes 180 v4, popularity, and saved-reference
checks. Runtime, input provenance, query counts, independent review, and measurement limits are recorded in the
linked checkpoint. Production full-refresh duration remains a rollout check.


## Demand refresh and three-table schema — October 30

The final [popularity checkpoint](review/stored-popularity.md) replaces the
scheduled refresh with stale-read/background refresh. One freshness query and
at most one queued batch cover a response after catalog snapshots close. Scores
are still selected from Postgres and published to both stores by existing jobs.

The separate freshness table is removed. Every target has a generated row;
inactive targets have zero counters and null momentum. Conditional sorted
upserts protect newer rows, without a publication gate or deletion phase.
The focused suite passes 183 checks, including inactive freshness, zero IDs,
composed-request batching, queue failure/304 recovery, and cross-store retry.

## C2 migration and account API rehearsal — October 30

The [account checkpoint](review/accounts.md) records the native-text identity migration,
169 passing account tests, 183 passing catalog checks, real native/browser HTTP
flows, backup restore/remigration, and rejection of actual old-binary credentials
and cursors. The API is available against the separate local `relisten_c2_review`
database; the catalog preview is unchanged. C2 mobile work waits for response
review. No production migration, deployment or mobile change occurred.
## 2026-10-31 — Anonymous play reporting and public activity

Implemented `POST /api/v4/live/play` and `GET /api/v4/live/history`. The running
local OpenAPI document has 51 operations: 49 GETs and two POSTs. Eight planned
operator actions remain; this change adds no import or account behavior.

The focused v4/popularity/reference suite passes **197 tests**, including 14 new
HTTP cases in the existing disposable-database fixture. Coverage includes all
four app types and their persisted enum values, typed track decoding, raw/partial/
interpreted track responses, telemetry identity omission, missing/orphaned track
rejection, malformed JSON/fields, sequence paging with backward timestamps, the
one-hour window, and dependency 503/recovery. OpenAPI rejects null/numeric app
values consistently with the HTTP boundary. No new test host or mocking layer
was added.

The independent two-pass review found one polling defect: eligibility filtering
after LIMIT could block a valid event behind an orphaned event. The mixed
orphan/null/unsupported-app → valid-event test failed before the fix and passed
after filtering in SQL before LIMIT. The final review confirmed the correction.

A real-data `EXPLAIN (ANALYZE, BUFFERS)` rejected an uncorrelated membership
subquery that scanned 4.9 million tracks in 7.9 seconds. The final validation
query puts the UUID predicate inside EXISTS and uses `source_tracks_uuid_key`:
19 shared buffer hits and 0.64 ms execution in that local sample. Planning time
was 16.8 ms. The code comment preserves the reason for that predicate placement.

The production API composition was restarted against local `relisten_db` and
tested through actual HTTP using real track
`track_7b6v6pgvzqfvhd0s7avr18bdw5`. Reporting without `client_uuid` returned 200
and sequence 255697580. Both initial and cursor history returned its imported
title, partial normalization, and the matching 1970-05-15 show. Polling after the
event returned `[]`. All responses used `no-store` without ETags. The exact local
event was deleted afterward; no fixture events remain in the restored catalog.
No worker ran or replaced the saved real popularity aggregate snapshot.

Single-request local timings were 388 ms for the first POST, 321 ms for the first
history read, 16.6 ms for the subsequent history poll with one event, and 2.7 ms
for the empty poll. These are review samples, not production latency estimates.
The request/response walkthrough is [live-activity.md](review/live-activity.md).

No migration, scheduler, queue protocol, or lock was introduced. V2 request and
response shapes remain; shared recent-event selection now follows its integer
cursor order. Tests and the rehearsal wrote only to local databases. Production
data and clients are unchanged.

## 2026-10-31 — Filter public activity by artist

History now accepts repeated `artist_ids` keys. Typed IDs are parsed once and
deduplicated; the existing event eligibility query filters by artist before its
limit. Omitted IDs keep the global feed. Unknown valid IDs contribute no events;
empty, malformed, and wrong-kind values return 422. No extra query, schema change,
or client change was introduced.

The existing HTTP fixture passes **137 cases**. Seven focused cases cover an
interleaved-artist regression plus invalid query inputs: initial and cursor
limits, any-of selection, duplicate IDs, unknown IDs, imported-only and normalized
artists, and unchanged unfiltered behavior. Both independent review passes found
no issues; the simplicity pass retained direct parsing and the existing SQL.

After restarting the local API, real-data requests returned all four review plays,
the one older Grateful Dead event with `limit=1`, and both Grateful Dead/Phish events
when selected together. Polling past the selected artist's event returned `[]`;
empty and wrong-kind filters returned 422. OpenAPI exposes `artist_ids` as a query
array. The four local review plays remain until the one-hour window expires.
The public integer sequence remains the legacy event ID; its counter disclosure
is now explicit in the contract. Opaque cursor design is separate from this filter.

## 2026-10-31 — Retain operator routes; gate the first legacy recording adapter

Historical experiment, superseded by the native-only decision below. Its adapter,
flag and rollout artifacts were removed; the batching optimization remains.

The current manifest has 51 v4 catalog/playback operations and eight separately
retained v2 operator operations. No artist-write/import aliases were added. Alec
approved keeping integer event sequences and using Postgres popularity in the
legacy adapter. That experiment used a default-off switch and backfill checklist.
Those rollout instructions are withdrawn; the
[native-only decision](review/catalog-read-isolation.md) describes the current policy.

Native full-show/source reads and the enabled legacy full-show adapter now share
the imported recording loader. It reads sources first, then batches sets, playable
tracks and links by those source IDs. This avoids repeated source descriptions
and repeated parent-query planning. The first joined and single-batch prototypes
preserved payloads but regressed native performance; neither remains in the code.
Legacy show metadata, feature gates, date aliases, serializers and HTTP caching
remain in place. Other legacy families retain their current readers.

The adapter flag defaults off in appsettings and options. Off retains the old
recording loader and Redis popularity. On reads selected artist-local Postgres
scores using the same lookup as v4 and requests stale refreshes after reads.
Legacy database reads, including freshness, prefer replicas. The old loader is
temporarily retained for rollout rollback and has an explicit removal criterion.

**254 focused catalog, popularity, reference and ingestion tests passed.** The
two new HTTP cases reuse the existing host/fixture. They compare the complete
legacy resource across numeric-artist date routes, year/date aliases and UUID
routes for raw and normalization-approved artists. Distinct Redis/Postgres values
prove that the flag selects the intended store. They verify warm-Redis rollback
with the Postgres score table unavailable and missing-show/cache-error behavior.
That rollback test does not promise a cold dual-write calculation can work without
the schema. The broader run also exposed a preexisting test assumption that the
global popularity population was empty; its setup now creates that missing state
explicitly, without weakening its queue assertions.

One error behavior deliberately improves: an enabled adapter returns `sets: []`
for a source with no playable tracks, where the old dictionary lookup throws.
The regression case restores orphan flags afterward. Native reads retain empty
sets. Both independent correctness/readability passes found no actionable issues.

For real-data comparison, an untouched `92b4ff5e5` build ran on port 43824 beside
the candidate on 43823, against the same restored database and Redis. Workers and
imports stayed off. For each of nine v2/v3/v4 routes, four warmups preceded 15
interleaved samples with alternating build order. Complete JSON, array order,
popularity, content/cache headers and response byte sizes matched in every case.
The scratch baseline server/worktree was removed afterward. Captures and samples
are in ignored `local-dev/v4-review/.output/legacy-recordings/`; the checked-in
comparison script makes the workload repeatable.

Representative median response times in that controlled run:

- 38-source legacy v3 show: **68.74 → 28.05 ms**; native v4 **23.57 → 20.97 ms**.
- Partial-show legacy v3: **12.19 → 9.14 ms**; native v4 **12.31 → 11.69 ms**.
- One-source raw legacy v3: **3.32 → 4.71 ms**; native v4 **5.26 → 4.94 ms**.

The small raw legacy response adds 1.39 ms in this sample. Enabled legacy detail
uses seven command round trips versus five: selected Postgres scores and
freshness add two bounded reads while removing the Redis map read/deserialization.
Native detail drops from 20 to 18 round trips for approved artists and 12 to 10
for raw artists. Its child batch contains three SELECTs; these command counts are
network round trips, not claims of fewer SQL statements. No production latency or
allocation estimate is inferred from this local workload.

The local readiness query found **215,939/215,939 show rows populated, zero
missing**, including 153,297 unscored rows. All publications were older than the
one-hour refresh window: this verifies snapshot coverage, not freshness. No local
calculation/backfill replaced the saved real aggregate snapshot. Production was
not queried or changed. The local API runs with the flag enabled for response
review; the checked-in default remains disabled pending production warmup.

## 2026-10-31 — Keep native optimization; remove legacy integration

Alec chose independent v4 evolution over a blanket legacy-shim migration. The
preceding gated-adapter experiment is historical and no longer describes the
running implementation. V2/v3 retain their existing services, serialization,
lookup behavior, cache headers and Redis popularity. Existing operator paths
remain unchanged. Future shared schema/storage changes still require compatibility
checks, but converting old routes is not a v4 completion requirement.

Removed the adapter/projection, option/configuration, legacy-only DI, extra model
fields, ordering/artist-scope switches, replica freshness option, readiness SQL
and adapter-only HTTP cases/host setup. Those two cases protected a flag and
integration contract that no longer exists. Legacy services, constructors, DI and
configuration match the pre-adapter `92b4ff5e5` revision. The independent freshness
test-isolation fix remains because it protects combined suite execution.

V4 retains source-first loading and the batched set/track/link reads, plus selected
Postgres popularity lookup. `CatalogRecordingReader` now takes the caller's
`CatalogReadSession` directly. It has deterministic ordering, one snapshot and
timeout/cancellation ownership, with no legacy mode or nullable connection choices.
The native recording path still needs two round trips instead of the original
four. Public response models and normalization policy are unchanged.

**252 combined catalog/popularity/reference/ingestion checks passed**, including
201 focused native checks. Both independent reviews found no issues. All nine
real v2/v3/v4 captures matched the original complete JSON, array order, response
sizes and content/cache headers after restart. The API runs without any adapter
flag. No production, account or client changes were made.

The renamed GET-only comparison tool is `compare-catalog-reads.py`; it remains
useful for native optimization and coexistence checks. Results live in ignored
`local-dev/v4-review/.output/catalog-read-comparison/`. The
[native isolation review](review/catalog-read-isolation.md) describes the current
ownership and repeatable workload. Prior adapter-enabled legacy timing gains
must not be attributed to the current implementation.

After the combined tests finished, a quiet run with four warmups and 15 samples
measured native v4 medians of 21.34 ms (large), 10.85 ms (partial) and 4.78 ms (raw).
Command round trips remained 18 for approved full shows and 10 for raw full shows;
legacy full shows returned to their original five commands. These local samples
confirm that the native optimization remains, without claiming a production
latency guarantee or using the removed adapter's timing improvements.

## Final local contract, allocation and origin review — 2026-10-31

The [qualification report](review/api-qualification.md) records the current
51-operation inventory, recursive real-payload schema audit, conditional reads,
query plans, allocation comparison and complete-response concurrent workload.
All 49 GETs succeeded with zero schema mismatches after correcting popularity
and grouped-ranking naming. All 48 deterministic bodies/ETags were unchanged.
The two no-store GETs are random show and public history; 47 ordinary GETs
revalidated with bodyless 304. A real six-reference resolver POST returned ordered
availability results with no-store/no ETag. Existing operator registrations remain
unchanged and hidden from OpenAPI; no operator/import request was executed.

The fixes avoid a full UTF-16 JSON copy, bound selected artist counts using the
existing resolver query, and remove duplicated source selection SQL. Final
focused validation passed 202 checks; one test was added and existing ranking
and Unicode/correction assertions were extended. Both new schema regressions
failed against the original implementation. Existing fixture, migration and
failure/recovery coverage is reused. Independent reviews found no actionable
code issue; the simplicity pass added no pooling, cache, policy mode or new
runtime switch.

The final Release workload measured 162 requests with no failures. Artist
catalog medians were 123.5/134.1 ms, the large show 20.8 ms, Scarlet detail 52.4 ms,
Red Rocks 18.7 ms and Drums file matches 372.2 ms. Measured allocation fell about
28–30% in matched before/prototype large-response samples. Allocations include
in-process HTTP client/diagnostic overhead, not just the server or peak heap.
A 304 still reads and serializes current data. Cloudflare owns downstream caching;
no additional application response cache or zero-allocation requirement is planned.

The [mobile handoff](review/mobile-handoff.md) identifies current schema-17 account
code, exact review URLs, additive storage/identity rules, authoritative apply,
failure/retry and offline acceptance. C3 mobile execution, phone memory/apply time,
production capacity budgets and deployment qualification remain open. No mobile
files or production data were changed by this review.
