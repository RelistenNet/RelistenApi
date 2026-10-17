# Archive collections and reviewed performers

The collection and reviewed-performer implementation has local workflow and API evidence. The current checkout also adds distinct same-day performances and explicit existing-artist publication; their contracts and verification are tracked in the [show-performance plan](../2026-10-17-show-performances/README.md). The earlier smoke results below retain their original scope. Gov’t Mule’s complete 381-item inventory produces 359 recordings, 285 shows and 6,533 tracks; 22 items remain held. Collections provide both inclusion rules, exact existing-source reuse, configured discovery and read-only v3 routes. A bounded Jerry check publishes three recordings under three reviewed projects and holds one mixed acoustic/electric item. Repeats preserve playback identities and URLs.

This overview is for the Relisten engineer and reviewer/operator. Read the [collection contract](contracts.md) for schema, service writes, read APIs and discovery recovery, and the [performer contract](performer-contract.md) for project review and publication. The [design](../../../design/2026-10-17-relisten-collections.md) explains why collection membership, performer identity and import ownership stay separate. The original [intake plan](../2026-10-16-taperssection-intake/README.md) remains the ordinary recording-publication contract.

## Progress

- [x] October 17, 2026: retain the collections and performer-identity design separately from this execution record.
- [x] Verify the [complete Gov’t Mule inventory](govt-mule-smoke.md), then repeat under [rules version 8](evidence/govt-mule-v8-repeat.json) after performer-review implementation.
- [x] Verify collection identities, both membership rules, exact reuse and durable discovery through focused and registered-worker tests; retain [actual HTTP evidence](evidence/collection-http-smoke.json) and a [bounded live selector check](aadam-first-page-smoke.md).
- [x] Verify [three Jerry performing projects](jerry-projects-smoke.md), their collection fixture and one held mixed recording through normal artist admission and a real-API repeat.

These are local results. Production rollout and a full Aadam or Jerry inventory import remain separate work. The final [API verification](evidence/final-api-read.json) passed 300 HTTP checks after restarting both test APIs from commit `7a65d46`; it preserved the Mule and Jerry playback results without restarting workers or importing again.

The [show contract](../2026-10-17-show-performances/contracts.md) preserves an existing show UUID for one performance and assigns new UUIDs to additional same-day performances. The [existing-artist publication contract](existing-artist-publication.md) defines missing-recording writes without taking native ownership. Verify these changes with Phish before the larger Jerry and Aadam samples. The earlier show totals below count artist/date aggregates and do not prove distinct-performance support. Current publication keeps otherwise eligible audio under the default show when its artist and date are supported but early/late coverage is unclear. Internal `unresolved` or `combined` assignments prevent that fallback from authorizing performance-specific enrichment. Artist conflicts, unsupported dates and compilations across different days retain their existing holds.

## 1. Reproduce the complete Gov’t Mule verification

The [Mule record](govt-mule-smoke.md) retains the frozen query, finite spelling variants, exclusions, per-item outcomes and workflow IDs. It distinguishes the search’s 298 supplied date values from the final 285 published shows. Two `Gov't Mule & Friends` items were excluded from the 381-item selection rather than accepted as spelling variants.

Before treating an artist as new, inspect the full live registry: `artists`, Archive provider labels and retained candidates, using registry normalization. The artist-list API can omit empty artists, so its absence alone is insufficient. Mule’s read-only production check found no matching identity; local materialization still performed its locked recheck. A catalog match requires the existing-artist decision instead of a duplicate.

Use isolated PostgreSQL, Redis and Temporal services, with schedules unregistered. Retain the selected observations, review the candidate and finite labels, then run the ordinary artist workflow. Verify current artist visibility, normalized artist, year details, every published show, source ownership, ordered playback tracks and media references through the running v3 API. Review holds against their evidence. A sampled media request proves only sampled access.

Repeat the named artist refresh and compare source, show, set and track UUIDs, dates and complete URLs. The v8 Mule repeats each made 321 successful GETs. All 381 decisions matched the prior policy apart from the rules version, with all 359 sources, 359 sets and 6,533 tracks preserved. Both rebuilds completed with zero failures. Confirmed parser errors were corrected with regressions before widening their affected rules; the remaining holds stayed held.

## 2. Reproduce collection and discovery verification

[Migration 14 and the collection contract](contracts.md) define collection identities, selectors, both inclusion rules, shared exact-item resolution and read APIs. Collection identity never substitutes for a canonical artist or an importer mapping. The playable union deduplicates source UUIDs while retaining independent editorial/provider bases.

The shared regression suite passed all 402 cases across Taper’s Section, collections, ingestion, Archive.org, catalog rebuild and artist registry behavior. The final native receipt/status regression also passed after its cleanup. The solution build completed with zero errors and 18 existing NuGet warnings; the repository CSharpier check passed. The suite includes two collection end-to-end cases with actual registered Temporal workers, PostgreSQL and loopback HTTP. Tests cover present and later artist-wide content, selected-recording isolation, overlapping inclusion, explicit retirement/reactivation, missing or identity-mismatched references, protected intake visibility, seek cursors, metadata holds and checkpoint recovery.

The [running HTTP check](evidence/collection-http-smoke.json) passed 55 requests, including valid reads, malformed requests, missing/unpublished collections and artist filtering. Artist-wide, editorial and two provider bases returned the same 359 Mule sources once. Selected membership returned four sources across two artists, with three under the Mule filter. All 413 sources, 429 sets and 7,561 tracks in that isolated catalog retained their identities and media references.

For discovery, persist a whole received page, its memberships and pending identifiers before advancing its cursor. Resume retained pending work before fetching another page. Explicitly inactive provider membership stays inactive on rediscovery; absence never retires it. A completed traversal with failed observations remains visibly partial in its result. Exact source reuse writes no recordings and leaves the native importer in charge. The [bounded live Aadam selector check](aadam-first-page-smoke.md) fetched one real page through configured discovery: 100 items observed, 100 unresolved, zero failures and no canonical catalog writes. It retained its cursor with `Limited=true`, `Completed=false` and no pending items. The unpublished fixture remained hidden from public collection reads. Continuation and terminal traversal remain unverified against live upstream behavior.

## 3. Reproduce reviewed Jerry project publication

The [Jerry record](jerry-projects-smoke.md) retains four current items whose provider creator is `Jerry Garcia`. Exact item-level billing supports historical Jerry Garcia Band, Legion of Mary and Garcia/Saunders. An acoustic/electric recording retains unresolved alternatives and stays held. No broad creator alias was added to any project.

The existing production `JGB` artist (`a3ffe2d0-a626-cd1d-56bf-13c617eb94be`) is the Melvin Seals continuation. Preserve its 480 sources, including earlier-dated recordings. The new historical full-name project is separate; the local proof does not audit or repair existing assignments.

Follow the [performer operator sequence](performer-contract.md#operator-sequence-and-verification): retain immutable observations, propose specific project candidates, review exact field spans, approve resolved item decisions and start normal named artist refreshes. Artist approval and ordinary date, context and playback checks still apply. A review cannot reparent an already published item. Existing native matches are reused without intake ownership. The [existing-artist contract](existing-artist-publication.md) now permits missing-recording publication after an explicit reviewed binding; this earlier three-project proof did not exercise that extension.

Six normal artist executions published three recordings and then reported them unchanged, with all rebuilds complete and zero failures. Forty actual HTTP checks verified three artists, three shows, three sets and 31 tracks, plus the explicit local collection fixture. Complete identities and media URLs matched across the repeat. The fixture excludes Grateful Dead; it does not decide the final product collection’s contents.

## Remaining decisions and release work

- Decide the Jerry collection’s explicit project membership before production publication, including whether Grateful Dead belongs. Item review does not supply membership.
- Verify the explicit existing-artist binding and publication contract in the larger Aadam sample. Keep observed, reused, newly published and held counts separate; the earlier discovery and three-project proofs do not establish that coverage.
- Complete live configured discovery continuation and collection-wide terminal traversal. The successful 100-item Aadam first-page check cannot establish full pagination or recovery from every upstream listing change.
- Complete production backup/restore, matching API/worker deployment, history compatibility and a reviewed pilot. Follow the [deployment and rollback guide](../2026-10-16-taperssection-intake/verification-and-operations.md). Keep new production schedules paused until those checks pass.

Bulk database artist approval, automatic later clear items, supported `XX` dates, metadata precedence over identifiers and retained date notes remain in force. Alec’s bespoke provider agreements are the confirmed permission basis. The complete [Pavement proof](../2026-10-16-taperssection-intake/full-artist-smoke.md) remains historical evidence for the earlier publication policy; these new records supplement it.
