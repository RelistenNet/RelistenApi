# Normalize the full Relisten catalog

## Purpose and scope

Build a production normalization layer for Grateful Dead, Phish, Taper's Section and every current or future artist. Listeners must be able to find the same music, people and physical venues across inconsistent provider labels, inspect supported recording relationships, and report corrections. Preserve playable recordings and existing references while interpretations improve.

Required domains are artist repertoire, cross-artist compositions, people and credits, physical venues and their history, show performances, ordered setlist occurrences, recording-file contents, and capture/transfer/derivation/matrix lineage. Provenance, review, correction history, stable identities and incremental processing apply to every domain. Execution can proceed in dependent stages; none of these domains is an optional follow-up.

This plan is for a Relisten engineer and the reviewer/operator who will deploy and troubleshoot the system. The [domain contracts](../../../design/2026-10-17-normalization-contracts.md) define the proposed identities, service responsibilities and additive API. The [real-data evaluation corpus](../../../research/2026-10-17-full-normalization/evaluation/README.md) records examples and labels. These documents separate the intended contracts from the running API; normalization readers and workers remain to be implemented.

## Progress

- [x] 2026-10-17: establish the complete program scope and distinguish it from the completed local performance/import foundation.
- [x] 2026-10-17: census all 4,379 production artists through the read-only replica.
- [ ] Complete the targeted relationship and provider-input audit; preserve missing-data cases. The all-artist census does not need to be repeated to start implementation.
- [x] 2026-10-17: define proposed domain records, additive read APIs, decision/replay ownership and migration behavior against current code; fresh review fixes stale publication and cache/retry gaps.
- [x] 2026-10-17: capture 132 real evidence records and label 102 cases across 25 artists; reserve 36 cases for evaluation and run a separate developer review.
- [ ] Independently verify and expand held-out labels before using them to authorize automatic matching. Development review does not establish production accuracy.
- [ ] Implement required domains, input capture and incremental normalization with controlled verification.
- [ ] Backfill the full catalog, verify public behavior and recovery, then stage production rollout.

## Context and current evidence

The [research record](../../../research/2026-10-17-full-normalization/README.md) owns the census, schema evidence and follow-up audits. Its production snapshot at `2026-10-17T16:32:22Z` contains 292,476 sources, 5,063,161 tracks, 215,560 shows, 216,304 imported venue rows, 16,605 song rows and 38,475 provider setlists. Of the tracks, 5,010,402 are not flagged orphaned. Among those, 116,932 lack a remaining source and matching set, while 4,893,470 have matching catalog playback relationships and media references. Neither the flag nor those links prove remote audio availability. Preserve historical file evidence while restricting playback results to current valid relationships.

Only 53 artists have a repertoire dictionary. Another 4,309 artists have tracks not flagged orphaned but no dictionary. A generic artist must work without a curated fan database. Provider song IDs and specialist catalogs remain valuable evidence where available.

Source text is substantial: 229,568 sources have nonempty lineage; 267,878 have lineage or source text; 246,849 have taper text; 227,655 have transferrer text. These are populated fields, not normalized people or established capture families.

The replica reports PostgreSQL 17.10, recovery/read-only mode and migrations 1–12. The [performance and larger-import foundation](../2026-10-17-show-performances/README.md), including migrations 16–17, is locally verified; it is not evidence of production deployment. Resolve that release dependency explicitly before backfill.

## Required model and ownership

Keep imported facts as evidence. PostgreSQL owns canonical identities, accepted relationships, original supporting inputs and review history. Temporal coordinates bounded, restartable work; Redis is not durable ownership.

An artist's repertoire entry and a global composition are separate identities. Link them through evidence. Distinguish composers, lyricists, arrangers and original performers; equal titles or a cover credit do not establish all these roles. Preserve provisional title-derived entries and their basis without presenting them as verified works.

People have global identities; recording credits carry roles and original wording. Namesakes, unresolved handles and teams must remain distinguishable. Taper, transferrer, musician and matrix-builder credits are different roles. Collection membership does not establish a taping credit.

A physical venue is distinct from an imported name or city coordinate. Retain historical names, dates and supported location evidence. A `Show` represents a performance engagement under the foundation contract; a song occurrence represents a passage within it. Reprises, repeated songs, gaps and unknown passages need independent representation. File contents can span occurrences or performances without invented offsets.

Lineage represents captures, copy/digitization transfers, processing derivations and matrices with several inputs. Keep each relationship typed and independently evidenced. Unknown contributors or an uncertain chain do not remove lineage from scope. Metadata extraction, file hashes and acoustic fingerprints provide different evidence: verified file hashes support byte identity, provider-reported hashes remain assertions, fingerprints and alignment support bounded audio comparisons, and extracted text proposes names, equipment and relationships. None alone establishes every lineage edge or show identity. A default show can retain uncertain or combined recordings; sharing that primary association does not prove shared performance coverage or capture ancestry.

## Processing approach

Use one shared evidence and decision system, with domain-specific extractors and matchers. Processing follows retained input, structured extraction, candidate retrieval, matching, acceptance or bulk review, then public read projection. Do not require an artist to have a curated repertoire dictionary. Artist context helps interpret titles and credits; people, venues and compositions resolve globally.

Deterministic parsers handle explicit identifiers and recognizable syntax. Indexed candidate retrieval uses names, aliases and relevant context to avoid all-pairs comparison. Start matching with explainable rules; add lightweight classifiers when independent evaluation shows a benefit. Similarity scores and a model's stated confidence are not calibrated probabilities. Contradictory relationships must prevent unsafe transitive merges.

Use this Codex session for development analysis and labeling. Use selective GPT-6 Luna API extraction for difficult prose in runtime jobs, with structured proposals and exact supporting text. A name extraction does not establish a person's identity. Cache by the exact relevant input, model, prompt and response-schema version; changing one invalidates reuse. Validate returned evidence spans and references. Limit retries, concurrency and spend per job, retain usage receipts and preserve failed inputs for retry. Reuse durably cached successful responses after downstream publication failures. A crash before that cache commit leaves an uncertain provider outcome; reserve its possible cost and apply a bounded retry policy rather than claiming exactly-once billing.

The first paid evaluation run has a $25 cap. Measure useful extractions, errors, tokens and cost, then estimate and set a larger backfill cap from the observed workload. The available account credit is not a per-job budget. A model access failure preserves local results and creates retryable unresolved work; it cannot remove accepted data. No paid run has occurred as part of the initial corpus work.

People and recording credits take priority within lineage. Extract tapers, transferrers and supported copy, digitization, processing and matrix assertions from text and structured provider fields. Keep uncertain contributor identities and ancestry explicit. Run audio comparisons for specific unanswered questions, with evaluated methods and measured costs. Reconstructing every recording family or scanning all audio is not an initial backfill prerequisite.

## Plan of work

1. **Finish targeted analysis and label real examples.** Use the existing full census to select recordings, venue rows, titles and provider setlists across catalog sizes and providers. Include GD, Phish, Jerry, Aadam and unrelated artists. Preserve exact inputs, original row/provider identities, capture time and query hashes. Keep related evidence together in development/evaluation partitions. Label extraction separately from identity and relation decisions. An unresolved answer is a valid label. Development labels created by Codex require independent review before they establish acceptance accuracy; they are not a blind production benchmark.
2. **Implement retained inputs and the shared decision model.** Use the domain contracts to add versioned evidence, stable canonical identities, proposed and accepted relationships, correction history and merge redirects in PostgreSQL. Preserve structured provider information before lossy conversion. Refresh only missing metadata through bounded adapters. Define catalog ownership, change detection and publication transactions before enabling writes. Keep imported values and all existing playback references.
3. **Build extraction, retrieval and evaluated matching.** Implement deterministic extraction and indexed candidate retrieval, then domain matchers and selective cached Luna extraction. Store the evidence supporting each proposal. Measure parsing accuracy, candidate recall and accepted-match precision separately. Tune only on development cases. Set automatic acceptance thresholds per task from independent evaluation, with ambiguous or conflicting matches entering bulk review.
4. **Implement the domain services and readers.** Resolve physical venues and historical names globally; build artist repertoire and ordered occurrences with many-to-many file contents; link repertoire to cross-artist compositions and role-specific work credits; resolve global people with recording roles; retain supported capture and transfer relationships. Extend ShowPerformanceService to create/select a supported setlist-only engagement without a source; retain unassigned editions until engagement identity is supported. These services share evidence and review ownership. Add the public queries specified in the contracts without replacing legacy v3 identifiers or withholding unresolved playable recordings.
5. **Backfill representative cohorts, then every artist.** Verify GD, Phish, Jerry, Aadam and unrelated cohorts through running APIs. Include sparse artists, unknown dates, same-day performances, repeated songs, compound files and conflicting credits. Estimate full Luna usage from this run. Process the complete artist registry with resumable Temporal jobs, bounded concurrency, PostgreSQL checkpoints and progress/error receipts. New artists and changed inputs follow the same pipeline. A completed run records an outcome for every input; it does not require an invented answer for every fact.
6. **Release and rehearse recovery.** Compare normalized and legacy reads, investigate disagreements and exercise corrections before enabling readers gradually. Verify migration dependencies, backup/restore and rollback on a restored catalog. Read rollback preserves canonical identities, evidence and reviewed decisions. Drain incompatible Temporal histories or version the workflows. Production writes and deployment require their own rollout step.

## Concrete steps and acceptance

The [evaluation format guide](../../../research/2026-10-17-full-normalization/evaluation/format.md) gives executable integrity and failure-path checks for the first corpus. Run its `validate.py` command and `python3 -B -m unittest discover -s docs/research/2026-10-17-full-normalization/evaluation -p 'test_*.py'` from the repository root. Expected results are a verified receipt with all five domains and passing capture/validator tests; these do not measure normalization accuracy.

Run `docs/research/2026-10-17-full-normalization/scripts/run_live_census.sh` when a refreshed read-only census is needed. Store audit commands and receipts beside that research. Use `dotnet build RelistenApi.sln` and targeted `./test-local.sh --filter ...` checks for each implementation increment; run the affected regressions and `dotnet csharpier check .` before commits.

Verify through service contracts and running public APIs: the same composition across artists; role-filtered recordings for one person across artists; namesakes; historical venue names; repeated songs and multi-song files; early/late and partial-date shows; several transfers of a capture; matrices; unresolved text; reviewed corrections; changed provider evidence; repeated imports; cancellation and restart. Compare IDs, ordering, counts, relationships and complete playback URLs. Use artist cohorts drawn from the complete census, including sparse and unfamiliar catalogs. GD and Phish are reference cases, not the entire acceptance population.

Any audio validation must report the evaluated material, false matches and missed matches. No universal correlation threshold, overlap percentage or disjoint-cluster rule can be assumed to establish capture or show identity.

## Recovery and rollout

Commit evidence, accepted decisions and checkpoint advancement atomically where required. Before publishing an automatic result, compare its consumed input, policy, dependency and editorial revisions with current state. A stale run retains its evidence but cannot replace a later result or correction. Durable invalidation generations and fan-out checkpoints make correction refresh resumable. A partial fetch cannot imply deletion. Failed extraction preserves prior accepted playback and records unresolved work. Resume safely after losing execution history. Backfill and correction jobs need bounded resource use, progress receipts and cancellation.

Rehearse migrations, backup/restore and API compatibility on a representative restored catalog. Stage matching workers and readers, drain incompatible histories, and document rollback for each migration. Read rollback must preserve identities, evidence and reviewed decisions; it cannot rely on dropping the normalization schema. This plan authorizes no production write or deployment.

## Surprises and discoveries

The accepted approach prioritizes tapers and transferrers over exhaustive ancestry. The earlier requirement for broad audio analysis during initial backfill is superseded. A database credit field can contain equipment or unrelated text, so field names alone cannot supply person labels. Legacy unique show/song memberships cannot recover occurrence order or repeats; the evaluation must retain that unknown rather than sort rows into a fictional setlist.

Fresh review identified stale publication, candidate-dependent cache reuse and uncertain paid-request outcomes as required contracts. Publication now compares consumed revisions, the cache hashes the complete request, and billing recovery guarantees apply to durably cached successes.

The earlier 30-table proposal excludes normalized people/lineage and defers composition reconciliation. It cannot be implemented unchanged for this scope. The colleague's catalogue terminology and audio thresholds also require adaptation rather than adoption. Populated text and non-orphan flags measure available evidence, not normalized truth or proven playability.

## Decision log

- 2026-10-17, Alec: use local parsers and lightweight matching, with selective paid Luna fallback for difficult inputs; use Codex reasoning during development. Automatically accept validated matches and review ambiguity.
- 2026-10-17, Alec: prioritize tapers and transferrers within lineage. Supported ancestry remains in scope; exhaustive audio processing does not block initial backfill.
- 2026-10-17: cache model responses by input and extractor configuration, measure an initial run capped at $25, and size subsequent jobs from observed cost and accuracy.
- 2026-10-17, Alec: full normalization covers every artist; lineage and cross-artist compositions are required in the same program.
- 2026-10-17: separate feature scope from execution dependencies and unknown facts. Complete the audit before fixing schema/API details.
- 2026-10-17: preserve stable playback references, evidence and reviewed corrections; use the locally verified performance contract as a deployment dependency.

## Outcomes and retrospective

The scope and all-artist census are established. The first increment delivers domain contracts and a provenance-backed, development-labeled evaluation corpus with automated integrity and capture-failure checks. All 102 labels received a second Codex development review and remain provisional. No normalization matcher, new HTTP reader or paid API run is claimed by this increment. Independent label review, remaining provider-input recovery, domain services, public readers, full backfill and production verification remain open. The [people brief](../../../design/2026-10-17-people-and-recording-credits.md) and [earlier schema](../../../design/2026-10-16-catalog-normalization-schema.md) are design inputs; this plan supersedes their narrower scope and deferrals.
