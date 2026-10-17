# Next work: Gov’t Mule, collections, then Jerry Garcia

The next work follows three steps: verify the complete Gov’t Mule inventory locally; implement Relisten collections and membership; then resolve and import Jerry Garcia recordings by performing project. This is the execution sequence. The [collections design](../../../design/2026-10-17-relisten-collections.md) defines the model, Aadam Jacobs reuse, data ownership, and unresolved decisions.

This plan is for the Relisten engineer and reviewer/operator. The [current intake plan](../2026-10-16-taperssection-intake/README.md) remains authoritative for implemented Taper’s Section behavior. Collections, Aadam import, and Jerry performer resolution are future work. This documentation update does not start a live import or change production schedules.

## Progress

- [x] October 17, 2026: record the collections, Aadam, and performer-identity design separately from the execution sequence.
- [ ] Verify the complete Gov’t Mule catalog in isolated local services.
- [ ] Specify and implement collection identities and both membership rules.
- [ ] Specify per-item performer resolution and verify Jerry Garcia projects locally.

The complete indexed Pavement inventory passed a [local import and v3 check](../2026-10-16-taperssection-intake/full-artist-smoke.md): 107 recordings, 100 shows, and 1,993 tracks. A repeat preserved identities and date notes. Those results establish a recording-publication baseline, not assignment accuracy for another artist or support for another collection.

## 1. Test Gov’t Mule locally

Freeze a fresh inventory of Taper’s Section recordings for Gov’t Mule, including reviewed spelling variants such as `Gov't Mule`, `Govt Mule`, `Gov’t Mule`, and `Gov't. Mule`. Review expanded billing such as `Gov't Mule & Friends` separately rather than accepting it as another spelling. Retain the dated queries and per-item creator values so the inventory and its exclusions can be reproduced. Supplied dates are not verified performance identities, and the inventory count is not an expected publication count.

Confirm the artist is still absent from the full current Relisten registry, including empty and auto-created entries, before treating it as new. Use a read-only query over `artists`, `artists_upstream_sources`, and retained intake candidates, applying the registry’s normalization to the reviewed names, slugs, and provider labels. `/api/v3/artists?include_autocreated=true` is a useful browsing check, but still omits artists with no shows or sources; it cannot establish absence. Materialization must retain its locked registry recheck. A catalog match requires the existing-artist decision instead of a duplicate identity presented as production-ready.

Use a separate local PostgreSQL catalog, Redis instance, and Temporal server, following the [live-smoke setup](../2026-10-16-taperssection-intake/live-smoke.md#reproduce-the-bounded-smoke). Keep schedules unregistered. Review the exact candidate and finite creator variants before approving them in the test database. Approval does not waive item-level context, date, or playback checks.

Run the current workflow against the complete frozen inventory. Capture upstream queries, identifiers, terminal-page behavior for that inventory, selected dates, publication/hold/error outcomes, and workflow result. Verify the running v3 default artist list, normalized artist, year details, every published show, source ownership, track order, and playable URLs. Sample media access and distinguish it from a full listening audit.

Repeat the artist refresh. Compare artist, source, set, and track identities; confirm rebuilt revisions and retained date notes. Review held items and a fresh sample of accepted assignments independently. Correct a confirmed wrong assignment and add a meaningful regression before widening the affected rule. Record the result beside the Pavement evidence without replacing it.

**Outcome:** a second, larger artist exercises the current publication path. Every frozen item has an explained outcome, and repeat imports converge. This does not verify the collection-wide scrape protocol; that remains a separate release check.

## 2. Add collections and membership

Turn the agreed [design](../../../design/2026-10-17-relisten-collections.md) into a concrete migration and service contract. Keep collection identity, upstream discovery selectors, selected-recording membership, and artist-wide membership separate from canonical artist and source ownership. Do not introduce collection-as-artist mappings.

Selected-recording membership must support Aadam without including unrelated recordings from the same artists. Artist-wide membership includes present and later recordings for explicitly selected projects. Their union returns each canonical source once. Retain unresolved provider items before a source exists, and preserve independent editorial/provider membership bases.

Generalize the collection selector and row validation in `TapersSectionSearchClient` while preserving pagination checks and the current Taper’s Section workflow contract. Discovery progress belongs to each selector; Archive item identity and observations are shared. Extend `ArchiveIntakeService` and its recovery/ownership contracts rather than creating an Aadam-only writer.

Define exact existing-source linking and existing-artist publication before enabling Aadam imports. Reusing a native source only adds membership and leaves its importer in charge. A missing recording for an existing artist still needs a reviewed assignment and a supported write owner. The present new-artist restriction remains until that extension is implemented and verified.

Specify any collection read API before coding it: routes, DTOs, ordering, pagination, distinct counts, unresolved-item visibility, and cache behavior. Preserve existing listener identities and payloads. Collection implementation does not require a person/musician graph or the full venue/song normalization schema.

**Outcome:** service tests prove both membership rules, deduplication, overlap, unchanged playback identities, and recovery. Aadam fixtures prove that an existing source is linked without another import and that one selected recording does not pull in its artist’s catalog. A full Aadam live import is separate work after the shared discovery and publication extensions pass.

## 3. Resolve Jerry recordings by performing project

Use collections to group reviewed projects. Define the Jerry collection’s explicit membership and decide whether it includes Grateful Dead or only other projects. Preserve JGB, Legion of Mary, Garcia/Saunders, and other performing identities independently.

Extend reviewed-label assignment with retained per-item performer decisions. Freeze a reviewed sample that includes explicit JGB billing under a generic Jerry creator, other projects, compilations, unknown dates, early/late ambiguity, and contradictory metadata. Establish which evidence selects the performer, when it is insufficient, and how an operator approves a correction. Do not alias all `Jerry Garcia` items to JGB or assign a project by date alone.

Keep performer decisions separate from membership and original observations. Protect published artist assignments; a correction requires the reviewed catalog repair contract because current source/track identity rules depend on artist identity. Continue using the shared writer and artist rebuild sequence.

**Outcome:** representative recordings land under their actual performing projects and can be grouped through the Jerry collection. The same raw creator can produce different supported assignments. Unresolved or multi-project items stay held. Local v3 and collection-service checks establish stable references across retries and conflicting updates before a larger pilot.

## Decisions and constraints

The sequence was agreed on October 17, 2026. Gov’t Mule verifies intake before collections change its scope. Collections provide a shared browsing model for Aadam and Jerry. Jerry follows because its broad creator label needs per-recording performer resolution, which artist approval alone cannot express.

Bulk database artist approval, automatic later clear items, metadata precedence over identifiers, supported `XX` dates, internal date notes, and retained evidence remain in force. Alec’s bespoke provider agreements are the confirmed permission basis. Keep existing identities and ordinary importer ownership intact.

Production rollout is separate. Collection-wide terminal pagination, production backup coverage, matching API/worker deployment, workflow compatibility, and a reviewed production pilot remain pending. Follow the [deployment and rollback guide](../2026-10-16-taperssection-intake/verification-and-operations.md); a completed local step does not authorize enabling schedules.

## Verification and outcomes

For implementation, use `./test-local.sh --filter 'FullyQualifiedName~TapersSection'` for current intake behavior and add focused public-contract tests for changed boundaries. Run the broader Archive/registry suites when changing shared identity or publication code. Run `dotnet build RelistenApi.sln` and `dotnet csharpier check .` before committing code. Keep live upstream probes distinct from automated fixture coverage.

This revision records design and work order. The Gov’t Mule import, collection migration/read contracts, existing-artist extension, and Jerry assignment rules remain to be executed or specified in that order. Update progress and linked evidence as each outcome is verified.
