# Make shows distinct performances, then verify larger Archive imports

Update the show contract, implement it, verify Phish, then run larger local Jerry Garcia and Aadam Jacobs imports. Aadam also needs supported publication of missing recordings for existing artists. Complete that work before counting its sample as an import test.

This plan is for the Relisten engineer and reviewer/operator. It extends the [implemented collection and performer work](../2026-10-17-archive-collections/README.md). That work verified three Jerry recordings and discovered 100 Aadam items; it did not publish hundreds of either. The milestones below are future work.

The [show contract](contracts.md) specifies the migration, public fields and date-grouping route, durable assignments, combined-recording behavior and provider responsibilities. Keep its verified behavior aligned with the implementation as work proceeds.

## Intended behavior

A `Show` represents one performance by an artist. An artist can have several shows on the same date. A date page groups those shows for browsing. Sets within a concert remain subdivisions of that concert.

Keep `display_date` as a date or supported partial date, such as `1975-03-07` or `1975-XX-XX`. Add a structured performance type and optional label for wording such as “Early show.” Use a separate stable internal `performance_key` to distinguish shows within an artist/date. The intended uniqueness constraint is `(artist_id, display_date, performance_key)`; the type or label cannot be the uniqueness key. Two distinct performances can have the same type, or an unknown type.

Preserve show UUIDs through date, type and label corrections. A human-readable date-and-performance label can combine these values for display. Metadata continues to take precedence over identifiers; ambiguity notes stay internal. Equal partial dates do not establish that recordings cover the same performance.

An ordinary one-show date keeps its existing show row and UUID. When evidence supports splitting an existing show, retain that row and UUID for one performance, create new rows and UUIDs for the additional performances, and move their assigned sources. Record which performance retains the original UUID once; retries, label changes and sort order must not select a different one. Choose that assignment from supported evidence or review. An old show UUID continues to resolve to the retained performance after the split, whose source list can therefore shrink. Date browsing provides access to the other performances.

Moving a source changes its show association, including the `show_uuid` returned on source and track reads. It preserves the source, set and track UUIDs and media URLs. A split moves existing recordings; it does not import duplicate recordings. Sources whose performance remains uncertain retain their last accepted association while reviewed assignments are resolved; that association is not evidence for performance-specific enrichment.

Retain explicit recording-to-show and provider-setlist-to-show assignments with their supporting evidence. An otherwise eligible recording with unclear same-day performance coverage publishes under the day's default show. Internal unresolved or combined classification records that the primary reference does not establish exact coverage. Existing uncertain recordings keep their prior primary association. Move recordings when supported evidence identifies the performance; do not invent audio boundaries or withhold playback solely because performance identity is unclear.

## Progress

- [x] 2026-10-17: record the revised show model, dependencies and acceptance sequence.
- [x] 1. Finish the migration and public compatibility contracts below, including default-show publication for uncertain same-day coverage.
- [ ] 2. Implement show identity and assignments; pass synthetic contract tests.
- [ ] 3. Verify Phish through actual local imports and API reads.
- [ ] 4. Verify a larger Jerry sample.
- [ ] 5. Support missing recordings for existing artists, then verify a larger Aadam sample.

## 1. Finish the design and migration contract

Update the relevant design and ingestion contracts before schema changes. The [collection design](../../../design/2026-10-17-relisten-collections.md) still describes the implemented artist/date aggregate; this plan defines its replacement. The [broader normalization proposal](../../../design/2026-10-16-catalog-normalization-schema.md) supplies evidence and terminology. Its separate event model is not the implementation choice for this increment: use `Show` as the performance.

Specify the enum values, wire format, defaults, ordering and label behavior. Distinguish a performance's billed date from a known start time; a late show does not establish a timestamp or imply a date correction.

The [implemented contract](contracts.md) defines both split identity and default-show compatibility:

- **Old show references after a split.** Keep the existing UUID on one supported performance and allocate new UUIDs to the others. `GET /api/v3/shows/{showUuid}` continues to return the show bearing that UUID, with its current assigned sources. Define the durable split record, reviewed survivor choice and atomic source moves. An old show link remains valid but does not promise the former whole-day source list. Source and track references remain valid after moving.
- **Recordings spanning same-day performances.** `Source.show_id` and public `show_uuid` retain one primary association. Otherwise eligible new recordings publish on the default show with internal combined or unresolved coverage. Previously published recordings keep their current primary association. Date browsing and collection reads return each recording once. A primary reference does not authorize performance-specific enrichment or imply invented track boundaries.

Document the existing date route, UUID route, year/artist listings, random-show selection, collection recording references, counts, cache invalidation and client expectations. Date pages must remain usable. Any additive route or response field must have a precise schema and error behavior before coding. These decisions may require a focused product review; the implementer must not improvise them during a rebuild.

Define durable assignment ownership and correction behavior. PostgreSQL must own accepted show identities and assignments. Catalog rebuilds derive aggregates from them; they must not recreate identities from mutable display text. Provider observations and operator decisions remain separate. Unknown or conflicting assignments must not contaminate another performance's venue, setlist or reviews. Specify how existing published references remain available while a correction awaits review.

**Outcome:** a reviewed schema/API migration contract, including a worked split of an existing date aggregate and a combined recording. This resolves the remaining product choices before implementation.

## 2. Implement and verify the contract

Use additive migrations and backfill existing identities before enabling split publication. Change `RelistenApi/Models/Show.cs`, source/assignment models, and `RelistenApi/Services/Data/CatalogRebuildService.cs` together. The rebuild currently derives UUIDs from artist/date, joins provider setlists by date, and assigns sources by date. All three behaviors need replacement.

Update `ShowService.cs` and `ShowsController.cs`, including random selection, to retrieve the intended performance. Update collection queries and references in `CollectionService.Reads.cs`. Keep source, set and track UUIDs and media URLs stable; their returned show association must reflect a reviewed move. A collection continues to select canonical recordings and never takes ownership of their import.

Update provider adapters and their retained contracts, not only the final SQL. Phish.in currently keys recordings by upstream show ID but setlists by date. Phish.net chooses show metadata by date and fetches reviews/ratings by date. Preserve upstream identifiers and attach enrichment only when the provider evidence supports the target performance.

Add meaningful PostgreSQL/service and HTTP tests: unchanged UUIDs on ordinary dates; a split retaining one existing UUID and allocating another; moved sources with unchanged source/set/track UUIDs and updated show associations; multiple recordings of one performance; ordinary multi-set concerts; unknown dates; corrected metadata; a migrated old date reference; combined recordings under the chosen compatibility contract; and ambiguous provider enrichment. Repeat import and rebuild, interrupt between durable writes and rebuild, then recover. Verify IDs, associations, playback, counts and ordering through public service/API contracts.

**Outcome:** the new identity and migration behavior works on controlled fixtures, including partial failure and recovery. Do this before real provider samples.

## 3. Verify Phish first

Run in an isolated local catalog using the supported worker. Phish.in must run before Phish.net, which enriches its recordings. Start with a bounded inventory containing ordinary single-performance days, multi-set concerts and any same-day distinct performances supported by retained upstream evidence. Select dates from actual responses; do not assume the current provider DTOs can distinguish early and late shows.

Inspect provider evidence before claiming a live same-day split. If Phish supplies no usable discriminator, retain the synthetic split proof and report the live limitation. Date-only ratings, reviews or setlists must not be assigned arbitrarily when several performances are possible.

Read artist/year lists, every selected show and its sources through the running API. Exercise the preserved date destinations and old UUID references. Check set/track order, songs, venue assignment, collection deduplication and sampled media access. Repeat both providers and compare complete identities and media references. Record selected, published, reused, held and failed counts separately.

**Outcome:** Phish's existing ingestion and playback remain correct, with distinct performances demonstrated wherever evidence permits. Fix regressions before widening Archive imports.

## 4. Run a larger Jerry sample

Aim to demonstrate **hundreds of playable recordings**. Start with about 250 Archive items and retain the selection. Include several performing projects, explicit early/late labels, supported partial dates, broad creator labels and combined or conflicting items. Expand the bounded selection if holds leave fewer than 200 playable recordings. Do not relax assignment rules to meet the count; report a publication blocker if the supported cases cannot reach it.

Use the [reviewed performer contract](../2026-10-17-archive-collections/performer-contract.md). Keep historical Jerry Garcia Band separate from the existing Melvin Seals `JGB` identity. Resolve billing from retained evidence; do not add a global `Jerry Garcia` alias. Use an explicit local collection membership list and report its contents. Production membership, including whether Grateful Dead belongs, remains an editorial decision.

Verify the new show assignments, holds, collection reads and all published recording/track identities through the actual API. Repeat the sample and compare identities and URLs. If a selected item needs missing-recording publication for an existing artist, complete the ownership work in step 5 before publishing it.

**Outcome:** hundreds of playable recordings exercise performer assignment and performance identity, with separate published/reused/held totals and reasons. Retained observations alone do not meet this outcome.

## 5. Support existing artists and run a larger Aadam sample

First define and implement publication ownership for an Archive recording missing from an existing artist. Current intake deliberately defers these writes. Exact existing-source reuse already works and must retain the native importer's ownership. Do not bypass artist admission, disable ownership guards or introduce a competing writer for the same item. Verify concurrent/repeated native import, intake publication, metadata updates and recovery before the live sample.

Then discover and assess about **250 Aadam items** in an isolated catalog, expanding the bounded selection if needed to demonstrate at least 200 playable recordings. Include exact native-source reuse, missing recordings for existing artists, reviewed new artists and unresolved billing where available. Verify real publication as well as discovery: the previous 100-item proof retained observations and published no recordings. Aadam membership selects recordings; it does not include every recording by each represented artist.

Resume at least one persisted discovery checkpoint and repeat the selected work. Read the playable collection, artist filters and individual sources/shows through the running API. Verify that overlap with another collection produces one recording, ownership remains correct, and unresolved items remain available for review.

**Outcome:** hundreds of playable recordings demonstrate discovery, reuse and actual supported publication, with separate totals for each result. A sample containing only reused sources cannot verify the missing-recording writer. Report sample coverage and any publication blocker honestly; this is not full collection traversal.

## Normalization scope and dependencies

Distinct shows and explicit provider assignments come first. Keep provider venue names, song identifiers, track order and setlist evidence available for later normalization. Shared physical venue identities and artist-scoped, visibly title-derived song search are subsequent work. Repeated-song occurrences, alignment between files and setlists, and cross-artist compositions need their own contracts; a track title alone cannot establish a composition or an exact performed passage.

The broader normalization schema is not a prerequisite for these local samples. Preserve evidence and refrain from unsupported assignments so later normalization can use it. Do not claim that the performance change implements venue, song or setlist normalization.

## Verification, rollout and recovery

Run the fastest relevant tests first with `./test-local.sh --filter ...`, then build `RelistenApi.sln` and run the affected ingestion, catalog, collection and provider regressions. Run `dotnet csharpier check .` before each commit. Add acceptance commands and fixture identifiers as the contracts and tests are implemented.

Keep each local sample isolated from production and leave schedules unregistered. Save sanitized selection evidence, exact revision/configuration, workflow outcomes, HTTP results and before/after identity comparisons alongside the existing smoke records. Report recordings separately from distinct performances and tracks. Discovery success alone does not prove playback.

Before production rollout, verify backfill and backup/restore against a representative catalog. Stage matching API and worker releases and account for open Temporal histories. Define rollback compatibility before enabling new assignments: old binaries assume one show per artist/date and cannot safely resume writes after a split. Pausing writers while retaining accepted assignments is safer than a lossy reverse migration. Follow the existing [operations guide](../2026-10-16-taperssection-intake/verification-and-operations.md), extended with the reviewed split/rollback procedure.

## Surprises and discoveries

- Current Phish.in source identity uses provider show IDs, but its setlist identity still uses date. A rebuild-only change is insufficient.
- Current Phish.net enrichment matches by date. More show rows would make an existing first-match policy ambiguous.
- Existing source responses require one `show_uuid`. Combined recordings and old aggregate UUIDs need explicit compatibility behavior.

## Decision log

- 2026-10-17, Alec: make `Show` the performance and allow several shows per date. Preserve date browsing and saved references.
- 2026-10-17, Alec: an existing show keeps its UUID through a split; additional performances receive new UUIDs and their sources move over. The old UUID resolves to the retained performance, with its current source list.
- 2026-10-17, Alec: uncertainty between performances on the same day must not withhold an otherwise eligible recording. Publish under the default show and retain internal unresolved or combined evidence.
- 2026-10-17: separate stable identity from performance type and presentation. Retain unresolved evidence and internal ambiguity notes.
- 2026-10-17: verify Phish before the larger Jerry and Aadam runs. Treat existing-artist publication as an explicit dependency.

## Outcomes and retrospective

The identity and existing-artist publication implementation is committed in `735b94f`, with a fixture follow-up in `d6ee0b8`. The 88-case gate passed. Phish.in and Phish.net each completed 42 recordings twice with zero failures; 258 actual API checks confirmed stable identities and URLs across the repeat. The latest default-show policy is being applied before the larger Jerry and Aadam publication runs. Those samples remain incomplete.
