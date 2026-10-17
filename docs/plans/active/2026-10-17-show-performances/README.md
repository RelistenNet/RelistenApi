# Distinct show performances and larger Archive imports

All five milestones passed locally. The implementation preserves show UUIDs through supported performance splits. Phish passed its bounded provider repeat; Jerry has 348 playable recordings with a verified early/late split; Aadam has 299 playable recordings, including publication under an explicitly bound existing artist. Repeated imports preserved playback identities and URLs. The affected regression suite and an isolated backup/restore check also passed. Production rollout and broader normalization remain separate work.

This plan is for the Relisten engineer and reviewer/operator. The [show contract](contracts.md) defines schema, API, identity and provider responsibilities. The [existing-artist publication contract](../2026-10-17-archive-collections/existing-artist-publication.md) defines approval and recording ownership. The [sample runbook](live-samples/README.md) gives the retained selection, review, workflow and HTTP commands. Earlier [collection and performer proofs](../2026-10-17-archive-collections/README.md) remain historical evidence with their original counts.

## Intended behavior

A `Show` represents one performance by an artist. Several shows can share a date. Sets within a concert remain subdivisions of that concert; a date page groups its shows for browsing.

An ordinary one-show date retains its existing row and UUID. When evidence supports a split, retain that UUID for one performance, create new shows for the others, and move their sources. The survivor choice persists: retries, label edits and sort order cannot change it. An old show UUID continues to return the retained performance with its current source list. Moved sources retain source, set and track UUIDs and media URLs; their returned show association changes.

Keep `display_date` as a date or supported partial date, such as `1975-03-07` or `1975-XX-XX`. `performance_type` describes early, late, afternoon, other or unknown billing; an optional label preserves specific wording. Stable internal keys identify performances independently of these mutable fields. Metadata takes precedence over identifier dates. Internal evidence retains date disagreements and uncertainty. Equal partial dates do not establish a shared performance.

An otherwise eligible recording with unclear same-day coverage publishes under the default show. Internal `unresolved` or `combined` coverage records the limits of that association. Previously published unclear recordings retain their primary show. Playback remains available; the association cannot authorize performance-specific enrichment or imply audio boundaries. Conflicting artists, unusable dates and compilations across different days keep their separate eligibility checks.

Collections select canonical recordings without owning their import. Jerry uses an explicit performing-project membership list. Aadam selects individual recordings, rather than every recording by each represented artist. Historical Jerry Garcia Band remains separate from the existing Melvin Seals `JGB` identity.

## Progress

- [x] 1. Define the migration, UUID-survivor rule, public compatibility and default-show uncertainty policy.
- [x] 2. Implement show identity, assignments and existing-artist publication; pass the focused contract tests.
- [x] 3. Verify Phish through actual local imports, API reads and repeat identity comparison.
- [x] 4. Verify the larger Jerry publication and repeat, including a reviewed live split and stable playback identities.
- [x] 5. Verify existing-artist publication, the larger Aadam sample, discovery checkpoint recovery and repeat behavior.
- [x] Finish the full affected regression run, fresh review, local backup/restore and evidence updates.

Implementation commits are `735b94f` (identity and publication), `d6ee0b8` (test fixture dependency) and `5a34337` (publish unclear same-day coverage). The final affected regression run passed 483 tests with no failures or skips. The solution built with zero errors and 18 existing NuGet warnings; CSharpier passed for 435 files. The [verification record](evidence/verification.json) retains commands, results and review scope.

## 1. Schema and compatibility contract

Migration 16 retains every existing show and UUID, adds stable artist-scoped performance keys and recording/setlist assignments, and permits several shows per artist/date. PostgreSQL owns accepted identities and evidence. Redis and Temporal history do not own them. Migration 17 distinguishes an intake-created artist from an explicit binding to an existing artist.

The [show contract](contracts.md) gives exact fields, defaults, ordering and errors. Existing UUID reads continue to select one show. The legacy date route keeps its single-show response and chooses the retained survivor, or the oldest recorded show if there is no survivor. The additive v3 date-performance route returns all recorded shows on the literal date, including supported `XX` dates. Random selection uses the selected UUID. Empty show rows retain identity for recovery but disappear from public recorded-show lists and counts.

Combined recordings retain one primary show because existing source and playback contracts require it. Date and collection reads return each recording once. Internal coverage records uncertainty; this increment adds no public coverage array. A billed late show does not establish a start timestamp or time zone.

**Completed outcome:** migration and public compatibility are explicit, including UUID survival, source moves, partial dates and combined-recording fallback. The [broader normalization proposal](../../../design/2026-10-16-catalog-normalization-schema.md) remains reference material; its separate event model is not this increment's implementation.

## 2. Implementation and controlled verification

`ShowPerformanceService` owns accepted keys and assignments. `CatalogRebuildService` derives recorded-show aggregates from those assignments. `ShowService`, `ShowsController` and collection reads return the current show association while preserving playback identity. Source assignments and moves commit together; repeating a supported assignment reuses the accepted UUID.

Phish.in uses its provider show ID for recording and setlist identity and upgrades unambiguous legacy date-keyed setlists in place. Phish.net retains provider identity and official links, checks enrichment eligibility before fetching, and rechecks it under the catalog lock before applying results. Ambiguous date matches skip enrichment. Archive's shared writer handles native and reviewed intake publication, title billing and supported reviewed performance keys.

Existing-artist publication requires `BindExistingArtistAsync` after review of the exact registry identity. The binding changes no native artist flags or provider mappings. Exact native sources retain their importer; missing recordings acquire intake ownership only when their publication commits. Native update and deletion checks preserve that ownership after approval pauses. Legitimate native renames can hold intake publication until an explicit reviewed rebind to the same immutable artist refreshes its saved labels.

Focused tests cover ordinary UUID survival, splits and moved playback references, partial dates, date corrections, combined and unresolved coverage, provider setlist assignment, song reads, collection counts and deduplication, existing-artist binding, native ownership, pause, rollback and rebuild recovery. The focused 146-case run and final 483-case affected regression run passed without failures or skips.

**Completed outcome:** controlled tests and the affected regression suite establish identity, compatibility, ownership and recovery behavior. Production migration rehearsal remains release work.

## 3. Phish proof

The [Phish proof](phish-smoke.md) retains the selected upstream references, workflow results and [sanitized evidence](evidence/phish-smoke.json). In an isolated catalog, Phish.in imported 42 recordings and Phish.net enriched them; both completed again with zero failed or skipped items. Two API passes made 258 successful GETs covering every selected show, date-performance response and legacy date response. The repeat preserved 42 shows, 128 sets, 746 tracks and complete playback references. Eight pre-existing recordings also retained their earlier identities. A sampled media range returned HTTP 206.

All selected dates contained one performance. The live Phish result therefore establishes ordinary-date compatibility and repeat stability, not an early/late split. Synthetic tests cover splits; the larger Jerry run supplies the reviewed live example.

**Completed outcome:** the bounded Phish provider and listener contracts passed at the revision named in its proof. This is not a full artist-catalog import.

## 4. Jerry publication, split and repeat proof

The [Jerry proof](jerry-smoke.md) records 348 playable recordings across six performing projects, 269 shows and 3,700 tracks. Review selected 349 items: 347 published and two remained held. One previously published recording outside that selection brings the collection to 348. Five Legion of Mary recordings dated `1975-04-11` supplied a reviewed split: three early-show sources retained show UUID `8f949dd1-526a-4074-8da3-4986785068e8`; two late-show sources moved to new UUID `8963bf0a-2819-408e-bc36-a9f54c67b9fd`. All five retained their source, set and track UUIDs and complete media URLs through the split.

The 348 source assignments comprise 319 legacy, five accepted, 20 combined and four unresolved assignments. The latter 24 recordings remain playable under the uncertainty policy; their primary association does not claim exact performance coverage. Report these assignments separately from held performer decisions and distinct shows.

Six repeat artist workflows returned 348 unchanged outcomes, three review evaluation outcomes and zero failures; every rebuild completed. The three review outcomes concern two distinct held items, rather than three additional recordings. The API passed 843 checks before the split, 844 after it and 844 after the repeat. Complete post-split and repeat identity manifests matched, including playback URLs. The old UUID returned the early performance and the date route returned both shows. A sampled media range returned HTTP 206. The [sanitized evidence](evidence/jerry-smoke.json) separates selected items, distinct recordings and workflow counters.

**Completed outcome:** a repeat-stable sample above 200 playable recordings, with a UUID-preserving live split and retained evidence. Production Jerry membership, including whether to include Grateful Dead, remains an editorial decision outside the local fixture.

## 5. Existing-artist publication and Aadam proof

The [Aadam proof](aadam-smoke.md) records 299 playable recordings under 252 artists from 307 reviewed items across 257 artist targets. Both passes completed all 257 artist workflows and rebuilds. The first returned 299 published outcomes and eight holds; the repeat returned 299 unchanged outcomes, the same holds and zero failures. Each API pass made 1,672 successful GETs. Complete identity manifests matched, including all show, source, set and track references and playback URLs. A sampled media range returned HTTP 206.

Four Wire recordings demonstrate missing-recording publication under an explicitly bound existing artist. Wire was an ordinary identity created as a local fixture before binding; it kept its UUID, flags and zero provider mappings. This does not establish that Wire was already present in production. All 299 sources are intake-owned. The live selection contained no qualified pre-existing native source, so native reuse remains supported by controlled ownership tests rather than a claimed live reuse result. The [sanitized evidence](evidence/aadam-smoke.json) preserves these distinctions.

The configured selector resumed its earlier 100-item checkpoint and retained the next 2,984 identifiers, for 3,084 collection memberships. After 2,000 total acknowledgements, discovery was deliberately canceled; its final execution settled as `Canceled`. PostgreSQL retains 1,084 pending identifiers and no error. No discovery workflow remains active. This proves checkpoint recovery and durable interrupted work without claiming completed metadata traversal or treating memberships as playable recordings.

Collection pages, artist filters, counts and playback relationships passed. Aadam uses individual provider-backed recording memberships and no artist-wide inclusion. The API contained 298 shows, 299 sets and 3,098 tracks. The sample included one explicit late-performance label; Jerry and controlled tests supply the split and partial-date evidence.

**Completed outcome:** more than 200 playable recordings with repeat-stable identities, existing-artist publication and checkpoint recovery. The earlier 100-item discovery-only proof remains separate historical evidence. The remaining collection items are durable pending work outside this bounded acceptance sample.

## Normalization scope

This increment distinguishes shows and retains provider assignments. Shared physical venue identities and visibly title-derived, artist-scoped song search are later work. Repeated-song occurrences, file/setlist alignment and cross-artist compositions need separate contracts. Preserve original venue names, song identifiers, track order, provider setlists and supporting evidence for that work. A track title alone does not establish a composition or an exact performed passage.

[People and recording credits](../../../design/2026-10-17-people-and-recording-credits.md) are also normalization requirements: stable person identities, supported aliases and role-specific credits must enable taper and transferrer search across artists. Preserve imported credit text and source UUIDs. That search can precede a complete capture/transfer lineage graph. The linked design reconciles the colleague's “catalogue” terminology with Relisten's imported catalog and current show model.

The broader schema is not a prerequisite for the local samples. Do not claim that the performance change implements venue, song or full setlist normalization.

## Verification and production rollout

Use `./test-local.sh --filter ...` for focused behavior, then build `RelistenApi.sln` and run the affected ingestion, catalog, collection and provider regressions. Run `dotnet csharpier check .` before each commit. The [sample runbook](live-samples/README.md) supplies the reproducible live commands and explains the service contracts behind them.

Keep local samples isolated from production and leave schedules unregistered. Save sanitized selections, revision/configuration, execution handles, typed outcomes, HTTP results and before/after identity comparisons. Raw metadata and full media manifests remain outside Git. Discovery or workflow completion alone does not establish playable coverage.

A consistent snapshot of the isolated Jerry catalog was restored into a new database. All 17 identity/evidence fingerprint categories matched, including 348 sources, 269 shows, 348 sets, 3,700 tracks, reviewed assignments and migration 16/17 schema. The [local restore record](evidence/local-restore.json) retains the comparison. Both local workers were stopped after verification; the APIs remain available.

Before production rollout, rehearse backfill and backup/restore against a representative production-sized catalog. Drain incompatible Temporal histories and stage matching API and worker releases. Old writers assume one show per artist/date and cannot safely resume after splits. Rollback pauses writes and retains accepted identities and evidence; collapsing shows requires a separately reviewed data migration. Follow the migration, canary and rollback sequence in the [operations guide](../2026-10-16-taperssection-intake/verification-and-operations.md). Local acceptance does not complete this production work.

## Decision log

- 2026-10-17, Alec: `Show` represents the performance; preserve date browsing and saved references.
- 2026-10-17, Alec: a split keeps the existing UUID on one performance, allocates new UUIDs to additional performances, and moves their sources without replacing playback identities.
- 2026-10-17, Alec: unclear same-day performance coverage must not withhold otherwise eligible audio. Use the default show and retain internal unresolved or combined evidence.
- 2026-10-17: keep stable identity separate from performance type and presentation; retain date ambiguity internally.
- 2026-10-17: verify Phish before the larger Jerry and Aadam runs; explicitly bound existing-artist publication is required for the Aadam proof.

## Outcomes and remaining work

All five local milestones are complete: implemented contracts, controlled verification, Phish repeat, Jerry publication and reviewed split, and Aadam publication with checkpoint recovery. The affected regression suite, fresh review and isolated backup/restore passed. Production rollout, production Jerry membership and broader venue/song/setlist normalization remain separate work. No production writes or schedules were used for these proofs.
