# Normalize the full Relisten catalog

## Purpose and scope

Build a production normalization layer for Grateful Dead, Phish, Taper's Section and every current or future artist. Listeners must be able to find the same music, credited tapers/transferrers and physical venues across inconsistent provider labels, with corrections retained as evidence improves. Preserve playable recordings and existing references while interpretations improve.

Required domains are artist repertoire, cross-artist compositions, people and recording credits, physical venues and their history, show performances, ordered setlist occurrences and recording-file contents. Recording provenance in this release means identifying tapers and transferrers across artists. Full capture/transfer/derivation/matrix ancestry and audio-family reconstruction are outside the release scope. Provenance, review, correction history, stable identities and incremental processing apply to every domain. Execution can proceed in dependent stages; none of these domains is an optional follow-up.

Automatic matching is the active implementation step. Storage, processing,
reviewed publication and additive API reads work locally. Independent accuracy
evaluation, catalog-scale cost checks, full backfill and rollout remain required.
No automatic matching policy is active.

This plan is for a Relisten engineer and the reviewer/operator who will deploy and troubleshoot the system. The [domain contracts](../../../design/2026-10-17-normalization-contracts.md) define identities, responsibilities and additive APIs. The [research record](../../../research/2026-10-17-full-normalization/README.md) owns the data audit; the [evaluation corpus](../../../research/2026-10-17-full-normalization/evaluation/README.md) retains examples and labels. Progress below distinguishes implemented local services from the automatic resolver, evaluation, backfill and rollout still required.

## Next milestone: expanded matching evaluation

Complete an expanded real-data comparison next, while scale and regression checks continue. Preserve the completed experiments: rules currently outperform the small credit taggers; Luna adds supported credits; explicit source aliases improve people matching; both trained venue models expose a same-name/different-city failure.

The first 600-field credit comparison and 200-question people comparison are complete. Luna improves credit extraction on the shared labeled fields, mainly for Local and Phish.in notes; the fixed small extractors still trail rules. People identity evidence remains concentrated in a few groups. Use the completed 162-question people comparison to address false matches and misses, map reviewed venue premises to the catalog with dated context, and acquire supported composition links. Compare extraction, retrieval and identity matching separately, then measure complete pipeline results. The [evaluation milestone and coverage ledger](evaluation-milestone.md) records results, domain gaps and the next expansion.

The largest gaps are independent people identities and hard negatives, verified input formats beyond Archive, dated venue premises/history, and supported catalog-to-composition links. Each gap requires acquisition and evaluation, or a specific retained reason the evidence is unavailable. Unknowns remain in coverage counts.

Continue through every required domain and the full-catalog shadow pass, audit successes and failures, then complete backfill and rollout preparation. Infrastructure work interrupts evaluation only for a demonstrated defect that prevents trustworthy experiments or risks accepted data. Production writes and deployment still require approval.

## Progress

Local storage, processing, review, accepted publication and additive reads are
implemented. Automatic matching remains inactive. The release still requires
qualified matching across physical venues and their history, artist repertoire,
cross-artist compositions, global people and taper/transferrer credits, together
with performances, ordered setlist occurrences and recording-file contents.
Recording provenance covers the credited people; full audio ancestry is outside
this release. Local implementation and synthetic scale results do not establish
release accuracy, catalog throughput or rollout readiness.

### Implemented services and authority

[Retained provider inputs](provider-capture.md), [typed decoding](provider-decoding.md)
and [catalog binding](catalog-binding.md) preserve original facts across the seven
configured importers and Archive intake. [Complete-input processing](provider-processing.md),
[Local processing](local-processing.md), [setlist processing](setlist-processing.md)
and [imported catalog snapshots](imported-catalog.md) retain every contributor,
checked file scope and original producer revision. [Durable lookup outcomes](lookup-enrollment.md)
and [recording processing](recording-processing.md) support interruption, retry and
exact replay. Service registration starts no imports or schedules.

Typed people, venue and music storage retains stable UUIDs, original assertions,
exact citations, review history and corrections. [Atomic candidate facts](candidate-facts.md),
[candidate-universe generations](candidate-universes.md), [observed owners](owner-observations.md)
and [negative lookup fences](lookup-fences.md) protect complete positive and
negative decisions. Input generations also fence corrections that later return
to earlier bytes. [Unchanged publication](unchanged-publication.md) preserves
existing assertions and identities while retaining genuinely new citations.
Reviewed decisions remain separate from source facts and machine proposals;
normalized public reads remain disabled by default.

The [people proposal processor](people-resolution.md) and separate
[accepted-credit consumer](people-acceptance.md) are implemented. They use
whole-pool decisions, guarded identity creation, contributor-scoped
[credit reconciliation](credit-reconciliation.md) and resumable publication.
Explicit complete clearing can retire managed credit support; ambiguous or
incomplete extraction preserves it. Missing playback or show context does not
block independently evidenced credits. The [configuration recovery receipt](../../../research/2026-10-17-full-normalization/scale/evidence/people-configuration-recovery-v1.json)
records 183 passing affected checks, including authentication of the original
contributed Work request before checkpoint attachment. People membership uses an
explicit 100-contributor/100,000-binding profile; Work's defaults are unchanged.
The [people capacity contract](../../../research/2026-10-17-full-normalization/scale/people-candidate-capacity.md)
retains all 6,789 owner observations across 6,788 recording owners, with the
service-published versus relational-copy scope stated in its receipts.

[Source actor aliases](source-actor-aliases.md) identify an existing person or group
without inferring a recording credit, attendance or actor kind. Their
[source contracts](source-actor-alias-contracts.md) keep machine proposals, trusted
review and accepted alias support separate. The [publisher](source-actor-publication.md)
reconciles complete source scopes through durable stages; reviewed replacements,
withdrawal and incomplete fields preserve their distinct authority. Migration 58
makes [canonical review visibility](canonical-alias-review-visibility.md) consistent
across merged originals without rewriting assertions or support. Its
[receipt](../../../research/2026-10-17-full-normalization/scale/evidence/canonical-alias-review-visibility-v1.json)
retains 28 final focused checks, 336 broader checks and 168 synthetic query plans,
with their separate execution and warm-plan qualifications.

The [repertoire resolver](repertoire-resolution.md) authenticates imported
dictionaries, recording titles, setlist passages and retained provider dictionaries.
It preserves imported mappings and marks title-derived identities provisional.
[Setlist and file-content consumers](repertoire-consumers.md),
[complete set structures](setlist-structure.md), [display selection](setlist-selection.md),
[recording-independent performances](setlist-performances.md) and
[public set reads](setlist-public-reads.md) are implemented. [Correspondence/removal
review](setlist-correspondence.md) and [annotation reads](setlist-annotations.md)
retain source receipts and reviewed authority. Automatic correspondence and
removal still require evaluated policies, including revised unkeyed accounts.

The [venue resolver](venue-resolution.md), [venue review context](venue-review-context.md)
and [composition resolver](composition-resolution.md) retain complete alternatives,
policy observations and authenticated publication stages. [Composition authorship](composition-authorship.md)
keeps full author sets and roles distinct. [MusicBrainz decoding](musicbrainz-decoding.md)
and [bounded acquisition](musicbrainz-acquisition-contract.md) retain exact responses,
cache age, alternatives and recovery. The historical [live search probe](../../../research/2026-10-17-full-normalization/scale/evidence/musicbrainz-search-local-v1.json)
timed out before headers. The [current typed health check](../../../research/2026-10-17-full-normalization/scale/evidence/musicbrainz-live-typed-search-health-v2.json)
retains successful work, recording and artist HTTP responses, including recovery
of the same work plan from HTTP 503 to 200. The v6 work result reports decoding
issues. The [v7 relationship correction](musicbrainz-search-relationships.md) removes
those false diagnostics while preserving the same response and all relationships;
it also preserves historical negative review across the producer change. Its 624
distinct targeted checks pass. Recording pagination and entity hydration remain
incomplete; complete acquisition and identity matching require their own evidence.
Acquisition outage/process-termination checks remain open. Venue and composition acceptance currently have fixture-only rule adapters;
production matching implementations and independent qualification remain required.
People's linear scorer and repertoire's qualified-identifier path do not complete
matching across the other domains.

### Verification and current scale work

Real-source rehearsals verify playback compatibility independently of matching
accuracy. The [recording rehearsal](../../../research/2026-10-17-full-normalization/scale/materialization-local-inputs.md)
publishes 257 contents across 256 files from 25 recordings and checks 2,165 API
responses, including 1,409 unchanged legacy responses. Original legacy and accepted
music relation fingerprints survive replay and fresh retry. The [Jerry setlist
rehearsal](../../../research/2026-10-17-full-normalization/scale/setlist-local-rehearsal.md)
checks three sets and 18 occurrences through 1,699 API requests; all 21 set/occurrence
UUIDs survive retry without creating a show, moving a recording or selecting the
source for display.

Current source publication retains complete proof within the existing 32 MiB
checkpoint bound. The [checkpoint format](checkpoint-format.md),
[person cohorts](../../../research/2026-10-17-full-normalization/scale/evidence/source-person-cohort-v1.json),
[same-snapshot fact reader](candidate-facts.md#reuse-people-facts-within-one-snapshot),
[text reader](recording-text-scan.md), [target batches](source-target-batches.md)
and [typed-stage read receipt](../../../research/2026-10-17-full-normalization/scale/evidence/source-stage-typed-json-v1.json)
record the implemented cost corrections and their focused compatibility checks.
They preserve every query's hits, full owner observations, original evidence and
independent proof reconstruction. Their historical test counts overlap and must
not be summed. The receipts distinguish source-derived command bounds, extraction
timings and allocation observations from whole-application cost.

The [current 1,000-claim repeated lifecycle](../../../research/2026-10-17-full-normalization/scale/evidence/source-current-thousand-repeat-v1.json)
passes on production code `0cf5977`: both applications complete, all 1,000 aliases
remain visible, and every stage reads back with complete structural equality and
stays within its canonical byte bound. The test takes 16m02.90089s on a shared host
using fixture policies. This does not establish isolated throughput, peak memory
or matching accuracy. The earlier [checkpoint result](../../../research/2026-10-17-full-normalization/scale/evidence/source-actor-checkpoint-v2.json)
and [one-hour distinct-person failure](recording-text-scan.md) remain separate
historical evidence.

The ordinary [incomplete-source control](../../../research/2026-10-17-full-normalization/scale/evidence/source-incomplete-preservation-v1.json)
passes with three sources and all six independent owner UUIDs. It verifies exact
assignment/support preservation and the held source's original scope, bindings and
last complete observation. It does not exercise interrupted recovery or replace
the required 1,001-source case. The [1,000-distinct-person target-batch run](../../../research/2026-10-17-full-normalization/scale/evidence/source-person-thousand-target-batch-v1.json)
reached its one-hour cancellation limit before completion on older binaries. Its
last retained snapshot has 1,195 committed stages; that is not a terminal count.
The strengthened [1,001-source run](../../../research/2026-10-17-full-normalization/scale/evidence/source-owner-thousand-one-terminal-v1.json)
also reached its one-hour cancellation limit while publishing the initial sources.
It did not reach the incomplete-source mutation or preservation assertions. No final
committed-source count was captured. Publication-cost repairs remain required before
backfill and proceed separately from offline matching evaluation.

The [current broad API suite](../../../research/2026-10-17-full-normalization/scale/evidence/full-suite-current-v1.json)
passes with 3,644 tests, no failures and 28 actual unexecuted entries. It excludes
`SourceAliasScale` and `PeopleFactsSnapshotScale`; the console's 24 skips omit four
explicit cases. This run includes production code `0cf5977` and the strengthened
source-retention fixture at `002f0d4`. The new evaluation tools at `6b474bd` pass
81 distinct focused .NET cases and 60 Python cases separately. Counts across
verification receipts overlap and must not be summed. Scale failures and remaining
explicit checks retain their own scope; broad regression does not establish
matching accuracy or catalog throughput.

### Evidence available for matching evaluation

The [complete metadata frame](../../../research/2026-10-17-full-normalization/scale/full-metadata-frame.md)
contains 4,379 artists, 292,522 recordings and 215,594 shows, with all 582,272 output
rows and 69,728 requested coordinate results reconciled. The separate
[complete domain capture](../../../research/2026-10-17-full-normalization/scale/full-domain-metadata-frame.md)
retains 5,064,076 files, 216,331 venue records and repertoire relationships; all
6,719,411 metadata rows pass offline verification and independent audit. Each
capture retains its own snapshot authority. These inventories support reservation
and sampling; they confer no identity labels, untouched status or publication authority.

Preserve the [original census and sampling evidence](../../../research/2026-10-17-full-normalization/scale/README.md),
[blind annotation pack](../../../research/2026-10-17-full-normalization/scale/annotation/README.md)
and all exposure/reservation receipts. Reconcile known and unknown exposure,
duplicate/recording families and identity families before new plaintext, extraction,
training or model access. Do not join separate captures into an invented common
snapshot or infer provider, verified format or historical knowledge from current
metadata. Temporal evaluation needs the candidate records and aliases actually
available at its cutoff.

The [people development comparison](../../../research/2026-10-17-full-normalization/training/people-adjudicated-training-v1/README.md)
retains 322 source views and 295 comparisons across 219 declared relationships.
Complete bags yield 100 correlated relationship units: 74 positive and 26 negative.
These include 27 positive and only two negative cross-artist units. Logistic and
boosted-tree diagnostics retain two false negatives and no false positives at the
diagnostic cutoff; all 295 logistic scores agree with the public C# evaluator
within 1e-12, with review-only actions. The earlier [people labels](../../../research/2026-10-17-full-normalization/training/people-development-v1/README.md),
[independent adjudication](../../../research/2026-10-17-full-normalization/training/people-identity-adjudication-v1/README.md),
[source-view receipt](../../../research/2026-10-17-full-normalization/scale/evidence/people-source-views-v1.json)
and [prior-exposed selection frame](../../../../tools/RelistenNormalization/people-evaluation-selection/README.md)
retain the provenance and limitations. These comparisons do not establish catalog
candidate recall, calibrated probabilities or release precision.

[Whole-field actor annotations](../../../research/2026-10-17-full-normalization/training/actor-facts-development-v1/README.md),
[credit labels](../../../research/2026-10-17-full-normalization/training/credits-development-v1/README.md)
and the [matched rules/tagger/Luna comparison](../../../research/2026-10-17-full-normalization/training/credits-development-v1/matched-experiment-v1.json)
retain exact spans, independent blinded reviews, unresolved disagreements and
provider limitations. The frozen 543-field development corpus and original
100-field comparisons remain development evidence. The [Luna comparison and
budget history](../../../research/2026-10-17-full-normalization/training/credits-luna-development-v1/README.md)
retain validation failures, unavailable fields, sensitivity checks and 200 admitted
attempts with $0.0146575 configured accounting. These extraction experiments grant
no global identity authority or production routing policy.

The [venue drafts](../../../research/2026-10-17-full-normalization/training/venues-development-v1/README.md)
contain 34 judgments: 11 resolved and 23 insufficient-evidence cases, with independent
reviews still awaiting adjudication. [Venue model experiments](../../../../tools/RelistenNormalization/venue-matcher-experiment.md)
show contextual and hard-negative gaps; unknown judgments cannot become negatives.
The [composition inventory](../../../research/2026-10-17-full-normalization/scale/composition-development-selection.md)
has only two eligible strict-intersection questions and neither has sufficient
catalog-to-work evidence. The [MusicBrainz pilot](../../../research/2026-10-17-full-normalization/composition-enrichment-pilot.md)
retains external recording/work bridges, but does not connect them to Relisten
catalog rows or establish complete authorship. Expanded
[venue context](../../../research/2026-10-17-full-normalization/scale/expanded-venue-context.md)
supplies dates for all 29 eligible development recordings while preserving original
partitions; it does not qualify venue acceptance.

### Required before release

- Finish full-size distinct-person and 1,001-source lifecycle verification, the
  final-code broad regression run and catalog-wide cost measurements. Retain
  cancellation, partial progress and recovery evidence; synthetic successes do
  not establish catalog throughput or peak memory.
- Complete and independently evaluate matching capabilities for people, venues,
  compositions and repertoire, plus setlist correspondence/removal. Preserve
  original facts, stable playback, complete candidate pools and reviewed authority.
  Qualify creation, actor kind and source-local co-reference separately; source
  alias matching needs its own evaluated capability and cannot borrow credit accuracy.
- Follow [the evaluation strategy](evaluation-strategy.md): reconcile exposure and
  reservations; keep train, tune and untouched test partitions separate; group
  duplicate recordings and known identities; run distinct artist, identity, time
  and format suites. Independently check labels and adjudicate uncertainty.
  Evaluate actual candidate recall separately from conditional matcher errors.
  Compare rules, logistic regression, boosted trees and selective Luna on the same
  retained task definitions; measure accepted precision, automatic coverage,
  review volume, latency, cost and uncertainty. Choose thresholds and routing on
  tuning data; no numerical release threshold has yet been established.
- Authenticate evaluation receipts and operator approval through the trusted host,
  then use the existing versioned policy registry and review/CAS contracts for
  activation, revocation and rollback. Registration and implementation alone do
  not activate a policy. Complete all-artist backfill, verify public behavior and
  recovery, select public evidence excerpts and rehearse staged rollout.

## Context and current evidence

The [research record](../../../research/2026-10-17-full-normalization/README.md) owns the census, schema evidence and follow-up audits. Its production snapshot at `2026-10-17T16:32:22Z` contains 292,476 sources, 5,063,161 tracks, 215,560 shows, 216,304 imported venue rows, 16,605 song rows and 38,475 provider setlists. Of the tracks, 5,010,402 are not flagged orphaned. Among those, 116,932 lack a remaining source and matching set, while 4,893,470 have matching catalog playback relationships and media references. Neither the flag nor those links prove remote audio availability. Preserve historical file evidence while restricting playback results to current valid relationships.

Only 53 artists have a repertoire dictionary. Another 4,309 artists have tracks not flagged orphaned but no dictionary. A generic artist must work without a curated fan database. Provider song IDs and specialist catalogs remain valuable evidence where available.

Source text is substantial: 229,568 sources have nonempty lineage; 267,878 have lineage or source text; 246,849 have taper text; 227,655 have transferrer text. These are populated fields, not normalized people or established capture families.

The replica reports PostgreSQL 17.10, recovery/read-only mode and migrations 1–12. The [performance and larger-import foundation](../2026-10-17-show-performances/README.md), including migrations 16–17, is locally verified; it is not evidence of production deployment. Resolve that release dependency explicitly before backfill.

## Required model and ownership

Keep imported facts as evidence. PostgreSQL owns canonical identities, accepted relationships, original supporting inputs and review history. Temporal coordinates bounded, restartable work; Redis is not durable ownership.

An artist's repertoire entry and a global composition are separate identities. Link them through evidence. Distinguish composers, lyricists, arrangers and original performers; equal titles or a cover credit do not establish all these roles. Preserve provisional title-derived entries and their basis without presenting them as verified works.

People have global identities; recording credits carry roles and original wording. Namesakes, unresolved handles and teams must remain distinguishable. Taper, transferrer, musician and matrix-builder credits are different roles. Collection membership does not establish a taping credit.

A physical venue is distinct from an imported name or city coordinate. Retain historical names, dates and supported location evidence. A `Show` represents a performance engagement under the foundation contract; a song occurrence represents a passage within it. Reprises, repeated songs, gaps and unknown passages need independent representation. File contents can span songs or sets within the recording's performance without invented offsets.

Retain original source and lineage prose as evidence for named recording credits. A named taper or transferrer does not establish a shared capture family. Equipment and chains remain original supporting text; the release does not need a normalized ancestry graph, matrix components or audio processing. An uncertain same-day recording stays playable under its default/current show until a supported reassignment. Each recording belongs to one performance; multiple sets do not create additional performances.

## Processing approach

Use one shared evidence and decision system, with domain-specific extractors and matchers. Direct input invalidation uses the durable resource generation plus each subject's published-input watermark; publication compares that read set atomically. This avoids eager per-input fan-out while preserving pending work and stale-result fencing. Catalog binding returns artist and global source-identifier revisions, plus a mapping-registry revision for Local; catalog mutations advance them transactionally. V2 recording refresh carries the original catalog read set, and work admission checks the exact consumed input revision UUIDs. Fresh clocks cannot justify stale facts. Each contributor owns its complete input and dependency sets, and the work service configures their union atomically. Shared canonical-identity edits use dependency revision checks. Configuration changes require an explicit generation compare-and-swap, and work dependencies must be acyclic. Processing follows retained input, structured extraction, candidate retrieval, matching, acceptance or bulk review, then public read projection. Do not require an artist to have a curated repertoire dictionary. Artist context helps interpret titles and credits; people, venues and compositions resolve globally.

`NormalizationCandidateService` retrieves accepted names, aliases and qualified provider IDs through indexed queries. Repertoire is artist-scoped; people, venues and compositions resolve globally through redirects. Identifier hits survive the name limit. A truncation flag prevents treating an incomplete name pool as exhaustive. Equal-score cutoff ties can vary, and output limits do not guarantee bounded database work or catalog throughput. Multi-candidate decisions and measured recall remain required.

Use the four typed [candidate-facts methods](candidate-facts.md) for matching. Each reads selection, accepted assertions, owner observations, catalog clocks and candidate generations in one primary read-only repeatable-read transaction. The older retrieval-only method and public display DTOs do not supply that contract. Current storage does not establish provider-handle classification, geographic locality keys, exclusive identifier authority or a complete composition-author list. Those facts remain unknown.

People facts include exact artist-context totals from accepted credits, including recordings that are not playable. Supporting examples have separate bounds, so hundreds of credits do not become an incomplete artist history merely because only a few witnesses are returned. Venue facts retain every supported dated assertion within explicit limits. The pair matcher preserves alternatives when dates are unknown and distinguishes proven concurrent conflicts from uncertainty. It cannot assemble one address from separate assertions.

Composition facts keep person and catalog artist credit targets distinct, with all supported roles. Only resolved person credits in writer, composer or lyricist roles contribute to person-author comparison. An artist UUID cannot stand in for a person UUID. Individual credits do not establish a complete author list.

[Owner observations](owner-observations.md) fence factual context separately from causal dependencies. A recording can depend on its person identity while person matching observes that recording's credits; these reads create no dependency cycle. Admission and publication check original complete owner stamps. Durable pending checks use desired/editorial revisions and candidate generations, so unchanged completions settle instead of repeatedly scheduling each other. Self-observation retains its original audit stamp while the configured primary ticket fences publication.

[Candidate-universe generations](candidate-universes.md) also detect a new rival, alias or credit contributor, including after an empty result. The facts readers capture these generations with their assertions. Source arrival, removal or artist reassignment can change person context without changing the person's identity row. The initial generations cover these changes conservatively; measure contention before refining their granularity. A stable generation does not make a truncated pool exhaustive.

Declare write scopes before locking retained resources or work owners. Taking
an epoch lock for the first time inside a domain callback reverses publication's
lock order. When publication changes a universe it consumed, record its
post-write generation as the published watermark under the same exclusive lock;
retain the original consumed generation in the attempt's audit read set. Person
redirects also change composition-author facts. Source arrival, removal or artist
reassignment can change a credited person's artist context without changing any
person row, including when the source was missing during the original read.

The [unchanged-publication contract](unchanged-publication.md) now checks fresh
attempts as well as exact work-UUID replay. Equal matching facts retain their
current revisions; credits and venue assignments append each full citation
once. New facts and new support still invalidate their candidate generations.
Tests verify two consumers settling after the accepted catalog stops changing.

Reviewed metadata commands cover person aliases/external IDs and venue names/locations/external IDs, plus display-identity replacement. They preserve basis, assertion revisions and history. Acceptance or replacement cites an input resource bound to the original owner; reviewed retractions cannot be undone by automatic refresh. Corrections after a merge also invalidate the survivor's owner. The [contracts](../../../design/2026-10-17-normalization-contracts.md) specify the command fields and concurrency behavior.

Normalized music routes now expose repertoire, compositions, setlist editions, occurrences, file contents and supported show matches. File contents link to occurrences within the recording's current show. Several songs or sets can share a file without assigning that recording to another performance. Reads preserve existing playback UUIDs. All normalized reads remain disabled by default.

Deterministic parsers handle explicit identifiers and recognizable syntax. Indexed candidate retrieval uses names, aliases and qualified identifiers to avoid all-pairs comparison. Typed facts supply context for matching. Run the [model comparison and fallback-routing experiments](evaluation-strategy.md): train small identity matchers and a credit-extraction model, compare them with rules and Luna on the same inputs, then choose deployment policies from measured results. Evidence of benefit determines deployment, not whether to try training. Similarity scores and a model's stated confidence are not calibrated probabilities. Contradictory relationships must prevent unsafe transitive merges.

Use this Codex session for development analysis and labeling. Use selective GPT-6 Luna API extraction for difficult prose in runtime jobs, with structured proposals and exact supporting text. A name extraction does not establish a person's identity. Cache by the exact relevant input, model, prompt and response-schema version; changing one invalidates reuse. Validate returned evidence spans and references. Limit retries, concurrency and spend per job, retain usage receipts and preserve failed inputs for retry. Reuse durably cached successful responses after downstream publication failures. A crash before that cache commit leaves an uncertain provider outcome; reserve its possible cost and apply a bounded retry policy rather than claiming exactly-once billing.

The [matched-field development comparison](../../../research/2026-10-17-full-normalization/training/credits-development-v1/matched-experiment-v1.json) retains the same 100 original fields across current rules, two trained small models and cached Luna v2 output. Its $5 job cap and 200-attempt limit remain unchanged; the complete attempt ledger accounts for $0.0146575. Key-free replay and model fitting add no API requests. Keep prompt revisions, input masks, failures, output hashes and executable provenance separate. Score named tapers and transferrers separately from other roles and uncertain labels. Development comparisons do not establish release accuracy or activate a policy. Measure useful extractions, errors, tokens and cost before sizing a larger run; available account credit is not a per-job budget. A model access failure preserves local results and creates retryable unresolved work; it cannot remove accepted data.

Automatic matching is the release outcome. Bulk review handles ambiguity and conflicting identity merges; it must not become the routine path for otherwise validated matches. Extract tapers and transferrers from structured fields and description/lineage prose, then resolve people globally. Full ancestry and audio work are outside this release.

## Plan of work

1. **Finish targeted analysis and label real examples.** Use the existing full census to select recordings, venue rows, titles and provider setlists across catalog sizes and providers. Include GD, Phish, Jerry, Aadam and unrelated artists. Preserve exact inputs, original row/provider identities, capture time and query hashes. Keep related evidence together in development/evaluation partitions. Label extraction separately from identity and relation decisions. An unresolved answer is a valid label. Development labels created by Codex require independent review before they establish acceptance accuracy; they are not a blind production benchmark.
2. **Implement retained inputs and the shared decision model.** Use the domain contracts to add versioned evidence, stable canonical identities, proposed and accepted relationships, correction history and merge redirects in PostgreSQL. Preserve structured provider information before lossy conversion. Refresh only missing metadata through bounded adapters. Define catalog ownership, change detection and publication transactions before enabling writes. Keep imported values and all existing playback references.
3. **Build extraction, retrieval and evaluated matching.** Implement deterministic extraction and indexed candidate retrieval, then domain matchers and selective cached Luna extraction. Follow the [evaluation strategy](evaluation-strategy.md) to compare rules, trained small models and Luna-assisted processing, including unseen identities/artists, later recordings and unfamiliar formats. Evaluate Luna on sampled local successes as well as failures, independently adjudicate results, and derive fallback conditions from measured gains. Store the evidence supporting each proposal. Measure parsing accuracy, candidate recall and accepted-match precision separately. Tune only on development cases. Set automatic acceptance thresholds per task from independent evaluation, with ambiguous or conflicting matches entering bulk review. Expanded representative comparison is the next completed milestone; use the [evaluation milestone and coverage ledger](evaluation-milestone.md) to execute it while scale and regression checks continue. Offline experiments do not depend on accepted publication.
4. **Implement the domain services and readers.** Resolve physical venues and historical names globally; build artist repertoire and ordered occurrences with many-to-many file contents; link repertoire to cross-artist compositions and role-specific work credits; resolve global people with recording roles, including tapers and transferrers. Extend ShowPerformanceService to create/select a supported setlist-only engagement without a source; retain unassigned editions until engagement identity is supported. These services share evidence and review ownership. Add the public queries specified in the contracts without replacing legacy v3 identifiers or withholding unresolved playable recordings.
5. **Backfill representative cohorts, then every artist.** Verify GD, Phish, Jerry, Aadam and unrelated cohorts through running APIs. Include sparse artists, unknown dates, same-day performances, repeated songs, compound files and conflicting credits. Estimate full Luna usage from this run. Process the complete artist registry with resumable Temporal jobs, bounded concurrency, PostgreSQL checkpoints and progress/error receipts. New artists and changed inputs follow the same pipeline. A completed run records an outcome for every input; it does not require an invented answer for every fact.
6. **Release and rehearse recovery.** Compare normalized and legacy reads, investigate disagreements and exercise corrections before enabling readers gradually. Verify migration dependencies, backup/restore and rollback on a restored catalog. Read rollback preserves canonical identities, evidence and reviewed decisions. Drain incompatible Temporal histories or version the workflows. Production writes and deployment require their own rollout step.

## Concrete steps and acceptance

The [evaluation format guide](../../../research/2026-10-17-full-normalization/evaluation/format.md) gives executable integrity and failure-path checks for the first corpus. Run its `validate.py` command and `python3 -B -m unittest discover -s docs/research/2026-10-17-full-normalization/evaluation -p 'test_*.py'` from the repository root. Expected results are a verified version-1 corpus receipt and passing capture/validator tests; its 15 ancestry cases are retained historical challenges outside the revised release scope; these do not measure normalization accuracy.

Run `docs/research/2026-10-17-full-normalization/scripts/run_live_census.sh` when a refreshed read-only census is needed. Store audit commands and receipts beside that research. Use `dotnet build RelistenApi.sln` and targeted `./test-local.sh --filter ...` checks for each implementation increment; run the affected regressions and `dotnet csharpier check .` before commits.

Verify through service contracts and running public APIs: the same composition across artists; role-filtered recordings for one person across artists; namesakes; historical venue names; repeated songs and multi-song files; early/late and partial-date shows; explicit taper and transfer credits; unresolved text; reviewed corrections; changed provider evidence; repeated imports; cancellation and restart. Compare IDs, ordering, counts, relationships and complete playback URLs. Use artist cohorts drawn from the complete census, including sparse and unfamiliar catalogs. GD and Phish are reference cases, not the entire acceptance population.

No audio analysis is required to release credited-person matching. File hashes, recording labels and source assignments do not independently establish who made a recording.

## Recovery and rollout

Apply normalization migrations in order through 54 before deploying the corresponding binary. Migration 37 is required by existing append-style people publication as well as reconciliation. Migration 38 adds composition-author assertions; 39 adds durable people-resolution checkpoints; 40 adds imported venue assignment indexes; 41–42 add accepted set structures and correspondence review; 43 adds MusicBrainz acquisition; 44 retains private extraction-failure diagnostics; 45 adds a durable optional extraction-job attempt limit; 46 adds evaluated people-policy and application/stage receipts; 47 distinguishes retained lookup and search commands; 48 retains accepted contributor context and its lookup indexes; 49 adds composition resolution, evaluated policies and publication receipts; 50 adds the venue-resolution registry, attempts, stages and recovery indexes; 51 adds repertoire resolution and publication stages; 52 adds durable setlist/file-content consumers.

Migration 53 retains source actor alias authority and current alias visibility. Existing assertions receive independent baseline support without changing their IDs, revisions or review history. People readers now use `current_person_aliases` and `people-candidate-facts-v5`; bounded raw witnesses are separate from complete distinct matching names. Deploy migration 53 before these readers. Keep source-actor acceptance inactive until its resolver, trusted review and support publication pass their lifecycle checks. On rollback, retain the authority tables and use readers that honor current support; a raw alias-table reader could expose retired managed assertions.

Migration 54 retains the original read proof for trusted one-off reviews. It
compares only consumed facts and locks observed identities without changing
them. Exact committed replay precedes new fact locks; absent proof preserves
legacy command hashes. All 85 focused and 85 metadata regression checks pass,
including a nonempty 53-to-54 upgrade. Apply 54 even for legacy review callers
using this binary. Retain the column and audit rows on rollback, and stop
callers that require the new proof checks. Source-specific review and accepted
support publication remain separate work.

The acquisition service requires 47 even for lookup-only callers; the contributor-context service requires 48; composition resolution requires 49; venue resolution requires 50. The updated people/composition processors add negative-decision stages in existing JSON checkpoints and advance their processor versions. Terminal historical attempts replay unchanged, while unfinished older attempts stop explicitly. Pause these writers before rolling back to binaries that cannot interpret the new stages. Pause composition resolvers and accepted music writers before rolling back to a binary that cannot authenticate those stages. Keep these tables, columns and indexes on binary rollback. Stop paid callers before running a binary that predates migration 45 enforcement, and pause MusicBrainz callers before rolling back to a binary that cannot read search commands or authenticate timed response capture clocks. Finish timed nonterminal commands with a compatible binary before rollback, or leave acquisition paused; retain both response timing proof and database receipt timestamps. New processor registration starts no scheduled work. The [credit](credit-reconciliation.md), [authorship](composition-authorship.md), [people proposal](people-resolution.md) and [venue](venue-review-context.md) guides define their ownership, bounds and recovery behavior.

Commit evidence, accepted decisions and checkpoint advancement atomically where required. Before publishing an automatic result, compare its consumed input, policy, dependency and editorial revisions with current state. A stale run retains its evidence but cannot replace a later result or correction. Durable resource generations and published dependency watermarks make correction refresh resumable without an eager fan-out job for each input fetch. A partial fetch cannot imply deletion. Failed extraction preserves prior accepted playback and records unresolved work. Resume safely after losing execution history. Backfill and correction jobs need bounded resource use, progress receipts and cancellation.

The [migration-50 API rehearsal](../../../research/2026-10-17-full-normalization/scale/evidence/local-normalization-api-rehearsal-v2.json) verifies both separate local catalogs through 1,762 v3 responses: 701 recordings in the Jerry/Aadam copy and 413 in the Gov’t Mule copy. All seven legacy relation fingerprints remain unchanged across migration and the API walk. Accepted normalization state and active people, composition and venue policies remain empty. The collection checks include 299 Aadam recordings across 252 artists and 348 Jerry recordings across six projects. Jerry's partial-date and same-day performance routes retain their playback references; the Mule copy contains no such cohorts. These copies share seed rows, so their counts must not be summed as globally distinct recordings. The original imported databases remain at migrations 17 and 15. Preserve them; use the separate copies for correctness and recovery. The earlier [restore](../../../research/2026-10-17-full-normalization/scale/evidence/local-normalization-rehearsal-v1.json) and [migration-49](../../../research/2026-10-17-full-normalization/scale/evidence/local-normalization-api-rehearsal-v1.json) receipts remain unchanged. Media HTTP availability, matching accuracy, binary rollback and full-catalog shadow/load verification remain separate checks.

Rehearse migrations, backup/restore and API compatibility on a representative restored catalog. Stage matching workers and readers, drain incompatible histories, and document rollback for each migration. Read rollback must preserve identities, evidence and reviewed decisions; it cannot rely on dropping the normalization schema. This plan authorizes no production write or deployment.

## Surprises and discoveries

The release requires automatic matching at catalog scale. The expanded capture contains 32,593 recordings, 34,861 active file titles, 17,073 venues, 1,696 repertoire entries and 432 setlist accounts. It covers every eligible artist for those relations and retains an exact workspace copy outside temporary directories. Those raw inputs still need labels and domain-specific evaluation. The 102-case seed is a contract/regression challenge set; it cannot authorize automatic policies. The [scale audit](../../../research/2026-10-17-full-normalization/scale/README.md) expands profiling and raw sampling across the artist registry. Thousands of independently labeled decisions per domain, rule/provider-specific precision/recall and a full-catalog shadow run remain release work.

Alec narrowed recording provenance to taper and transferrer identities. Full ancestry and audio-family work are outside this release; the earlier broader requirements are superseded. A database credit field can contain equipment or unrelated text, so field names alone cannot supply person labels. Legacy unique show/song memberships cannot recover occurrence order or repeats; the evaluation must retain that unknown rather than sort rows into a fictional setlist.

Resolution must use original extraction receipts and candidate snapshots. Derive an extraction owner observation from its saved ticket and published attempt UUID; reading fresh owner metadata cannot validate an older batch. Pair comparisons alone do not establish uniqueness or authorize creating an identity. The resolver must evaluate complete candidate sets and retain its policy decision.

Accepted people applications now connect evaluated whole-pool decisions to the existing identity and credit writers, with durable stage recovery and no active runtime policy. Accepted credit replacement tracks each contributor's support and protects reviewed decisions and independent append publications. Complete composition-author assertions keep canonical people distinct from catalog artists; writer-based links require consistent, evidenced author sets. People-resolution proposals preserve complete candidate comparisons without granting automatic authority. Reviewed venue creation and imported assignment context preserve prior corrections even when name retrieval omits their targets. Accepted set structures, explicit correspondence review, recording-independent performances and atomic edition selection are implemented. Automatic correspondence and removal policies still need evaluation before activation.

Fresh review identified stale publication, candidate-dependent cache reuse and uncertain paid-request outcomes as required contracts. Publication now compares consumed revisions, the cache hashes the complete request, and billing recovery guarantees apply to durably cached successes.

The earlier 30-table proposal excludes normalized people and defers composition reconciliation. It cannot be implemented unchanged for this scope. The colleague's catalogue terminology and audio thresholds also require adaptation rather than adoption. Populated text and non-orphan flags measure available evidence, not normalized truth or proven playability.

## Decision log

- 2026-10-18, Alec: expanded representative matching evaluation is the next completed milestone. Preserve completed training and Luna results, compare expanded real evidence and continue scale/regression verification in parallel. Offline experiments do not depend on accepted publication. Complete every required domain, then full-catalog shadow processing, backfill and rollout preparation; production mutations and deployment still require approval.

- 2026-10-18: repeated taper, transferrer and description fields can supply identical matching inputs through distinct source-local references. Retain those references as separate views, with no field priority, contact union or inferred global identity. Compare all eligible views, group full-pair overlaps before labels and count each final relationship unit once. Fit complete bags with identical numerical features; retain partial, ambiguous and differing-feature bags as diagnostics. Keep actual rule disagreement separate from numerical agreement.

- 2026-10-18: [People candidate capacity](../../../research/2026-10-17-full-normalization/scale/people-candidate-capacity.md) must scale with distinct matching facts rather than every supporting account. The raw 5,000-context/4 MiB limits can permanently block prolific contributors. Use bounded evidence witnesses, preserve all owner freshness stamps, version people facts separately and measure sequential-read cost before rollout. Read-only production counts establish metadata volume, not accepted identities.

- 2026-10-17: external search cannot prove a complete global candidate universe, and existing work-target ID checks do not establish a local claim’s correspondence. Preserve explicit search coverage while allowing independently evaluated corroborated positive matches. Add an authenticated processor-issued correspondence receipt before general catalog-to-work publication; do not defer cross-artist compositions or accept search rank as identity.

- 2026-10-17, Alec: explicitly train and compare small models rather than waiting for proof of benefit before trying them. Evaluate generalization and Luna's incremental value on the same independently checked inputs, including local successes; use the results to select deployment and fallback policies.
- 2026-10-17, Alec: automatic matching is the release outcome, not a demonstration or routine manual-review process. The 102 seed cases are insufficient validation for hundreds of thousands of shows. Expand full-population profiles and stratified evaluation before enabling rules.
- 2026-10-17, Alec: cut full lineage from this release; identifying tapers and transferrers is sufficient recording provenance. Retain raw lineage prose but do not build ancestry/audio machinery for this release.

- 2026-10-17, Alec: use local parsers and lightweight matching, with selective paid Luna fallback for difficult inputs; use Codex reasoning during development. Automatically accept validated matches and review ambiguity.
- 2026-10-17, Alec: normalization covers every artist and includes cross-artist compositions. Recording provenance is now limited to credited taper/transferrer identities for this release.
- 2026-10-17: separate feature scope from execution dependencies and unknown facts. Complete the audit before fixing schema/API details.
- 2026-10-17: preserve stable playback references, evidence and reviewed corrections; use the locally verified performance contract as a deployment dependency.

## Outcomes and retrospective

The implementation now retains inputs, fences publication and review, stores canonical domain identities and exposes local people, venue and music reads. It supports reviewed identity-metadata corrections, retrieves candidates from accepted names and identifiers, extracts and durably checkpoints credit/title proposals, compares typed identity candidates and caches bounded Luna requests with durable usage receipts. The blind annotation pack expands the retained evidence available for labeling. The 102 development labels remain provisional; neither those labels nor successful extraction counts establish catalog-scale matching accuracy.

Provider capture covers all seven configured importers and Archive intake/discovery. Typed decoders preserve cited assertions, repeated passages, set boundaries and file identities. Legacy recording seeds retain stored UUIDs and incomplete context; local credit extraction maps its quotes back to original inputs. The local API suite and focused service checks verify these contracts; see [Progress](#progress) for current results. The solution builds with existing package advisory warnings. Fresh correctness/readability reviews and repository formatting pass. A [synthetic index probe](../../../research/2026-10-17-full-normalization/scale/evidence/candidate-index-probe.json) identified and removed a sort that scanned every equal-score candidate; it does not measure production capacity. These checks verify service and public contracts, not automatic-matching accuracy.

Automatic matching remains the release requirement. The [catalog-binding contract](catalog-binding.md) covers legacy, Archive, Phish.in and joint Local association, durable invalidation and complete owner input contributions. V2 refresh retains changed catalog wording with its original clocks. The [recording processor and page command](recording-processing.md) save extraction proposals with durable recovery; [candidate generations](candidate-universes.md) now fence competing accepted identities and relationship context.

[Recording lookup enrollment](lookup-enrollment.md) preserves positive and negative outcomes independently from extraction. The [shared processor](provider-processing.md) consumes complete legacy/Archive/Phish/Local evidence sets and retains file-credit scope; [Local processing](local-processing.md) keeps root notes with their directory. [Setlist processing](setlist-processing.md) retains complete accounts and independent associations under separate extraction owners.

The [repertoire resolver](repertoire-resolution.md) connects retained dictionary, title and setlist claims to accepted artist-scoped songs, including visible provisional entries for artists without dictionaries. The [setlist and file-content consumers](repertoire-consumers.md) now publish those decisions, with real recording-to-file results verified through v3. Next, expand independently checked matching evidence and compare automatic matchers. Per-source music subscriptions preserve freshness. People processing now passes public recovery and contributor-only withdrawal with an explicit membership budget and exact admitted headers; many-claim recovery now passes with exact evidence shared within each checkpoint under the unchanged 32 MiB limit. Catalog-wide database cost remains to be measured. MusicBrainz timing, venue publication and negative-decision enrollment now pass their contract checks. The catalog-to-work correspondence processor is implemented; its production matching policy and evaluated setlist correspondence and removal decisions remain required. Train and compare the local models and Luna routing against independently adjudicated labels, backfill every artist, and rehearse rollout and recovery. No production write or deployment has occurred. The [people brief](../../../design/2026-10-17-people-and-recording-credits.md) and [earlier schema](../../../design/2026-10-16-catalog-normalization-schema.md) are design inputs; this plan supersedes their narrower scope and deferrals.
