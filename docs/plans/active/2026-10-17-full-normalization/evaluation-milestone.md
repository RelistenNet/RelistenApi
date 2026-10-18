# Compare normalization methods across the catalog

The next completed milestone is the [usable full-catalog shadow delivery](shadow-delivery.md), as amended by Alec on October 18. This document retains experiment results and the audit coverage ledger. Earlier cohort sizes are historical experiment allocations, not prerequisites or limits on the first shadow run.

Run extraction, candidate retrieval and proposed normalization across the complete captured population now. Add missing domain adapters in parallel and audit resulting outputs by provider and failure class. Retain original evidence, unknowns and errors. Production publication is separate; production writes and deployment still require approval. The [standing goal amendment](goal.md) defines the intended outcome across turns.

## Use these results to improve the working approach

The [experimental development policy](goal.md#experimental-development-policy) governs this ledger. Use its results to finish usable features and choose the overall method. Historical packet sizes, incomplete adjudication and remaining small errors do not block local feature access. Preserve source truth, show inferred relationships as proposals and surface known issues. Explore more capable contextual approaches where the current rules/features are inadequate. Group related fixes, replay affected outputs and review at integration points; do not expand tests or provenance machinery solely to validate another tiny accuracy delta.

## What the completed experiments taught us

- **Credits:** On the same 83 available fields and 67 draft taper/transferrer assertions, rules recover 60 assertions with four extras; the perceptron recovers 40 with ten extras; logistic regression recovers 36 with 16 extras; Luna recovers 64 with three extras. Luna corrects three whole fields and introduces no newly wrong field on that denominator. This favors rules as the current local baseline and supports expanding Luna comparisons. It does not justify deploying the current taggers. The original job retains 200 admitted attempts and $0.0146575 of configured accounting. [Matched experiment](../../../research/2026-10-17-full-normalization/training/credits-development-v1/matched-experiment-v1.json).
- **People:** Retaining independently attributed source views makes explicit aliases usable. The first name/artist experiment misses six aliases; adding supported source context reduces the errors to two on the same 81 resolved drafts. The subsequent source-view comparison retains 100 eligible relationship units, including 27 positive and two negative cross-artist units. Logistic regression and boosted trees each miss two positives and classify no negative as positive at the diagnostic cutoff. The 295 source-view comparisons are correlated representations of those judgments. Next expand independent identity evidence and hard negatives, rather than multiplying views. [People comparison](../../../research/2026-10-17-full-normalization/training/people-adjudicated-training-v1/README.md).
- **Venues:** Both trained models classify the same-name Oakland/Boulder Fox Theater draft as a likely match. Names and raw location similarity need stronger premises, locality and historical context. The initial 34 drafts had only 11 resolved pairs in ten groups. Independent adjudication now supplies 24 supported answers; the current report below preserves the resulting errors and unresolved cases. [Venue experiment](../../../../tools/RelistenNormalization/venue-matcher-experiment.md).
- **Compositions:** The existing strict-intersection selection supplies two questions, no supported catalog-to-work labels and no trained composition comparison. The MusicBrainz pilot retains two external recording-to-work bridges; their corresponding Relisten recording identities remain unsupported. Acquire those catalog bridges and writer-role evidence, then compare real candidate retrieval and matching. [Composition inventory](../../../research/2026-10-17-full-normalization/scale/composition-development-selection.md) and [acquisition pilot](../../../research/2026-10-17-full-normalization/composition-enrichment-pilot.md).

These results remain development evidence. Their common limitation is the breadth and independence of identity labels. The work below closes that gap without discarding completed experiments.

## Current execution

The [expanded comparison report](../../../research/2026-10-17-full-normalization/scale/matching-progress-20261018.md)
records the completed credit, people and venue results. The people comparison now
covers 200 relationship questions from 156 artists: 86 have supported answers and
114 remain insufficient. Both frozen trained models get 85 of the 86 supported
answers right. The concentration of answers in a few identity groups and missing
same-name negatives prevent a catalog-wide accuracy claim.

The venue comparison has 24 supported answers and ten unknowns; both trained
models make two false matches and miss three supported matches. A separate
retrieval experiment searches all 216,331 legacy venue records. Both methods find
23 of 32 known directed counterpart records within their first 100 results. This
measures counterpart-row retrieval, not physical-venue recall. Its 600 new queries
across 600 artists still need identity labels.

The [600-field credit comparison](../../../research/2026-10-17-full-normalization/training/credits-native600-v1/README.md)
is complete. It covers 302 artists with verified native Archive, Local and Phish.in
inputs. On the 343 shared, completely labeled fields, Luna recovers 226 of 242
name-and-role assertions with nine extras; rules recover 178 with 12 extras. The
small models fitted on the earlier 84 fields perform worse than rules. Most Luna
gains come from Local and Phish.in, which represent only two artists. The job
accounts for 402 usable outputs, 124 whole-field contact exclusions, 69 unusable
results and five empty fields, at $0.0518212 configured token cost. Only 457 fields
have complete labels. Preserve unavailable outputs and incomplete labels in
coverage; these results do not establish global person identity or release accuracy.

The next people comparison has executed all 162 questions from 95 artists, with
151 cross-artist questions. Source-reviewed development judgments label 47
relationships same and six different; 109 remain unresolved. Both unchanged models produce 43
true positives, three false positives, four false negatives and three true
negatives at the diagnostic one-half cutoff. Every declared source comparison
exports successfully through the public C# projector, but 140 of 240 endpoints
use its name-only fallback. The three false matches share the same five-feature
vector as 32 supported matches and 49 unknowns. A threshold cannot separate those
opposite labels. Improve source interpretation and discriminating evidence before
retraining or selecting acceptance thresholds; comparison completeness does not
establish complete identity evidence. The four missed matches need qualified
handle or explicit alias evidence. Independent result review is complete.
The six negatives concern performers or ensembles; confirmed same-full-name
taper/transferrer collisions remain a gap. This tranche falls 38 short of the
proposed 200-question expansion. Keep its results separate from the earlier 200.

The source-view v2 comparison now uses the same 162 questions, frozen labels and
model parameters after adding exact role-free byline aliases. Both models improve
to 44 true positives, three false positives, three false negatives and three true
negatives. One known relationship is repaired; six errors remain. All 109 unknown
judgments remain unknown. This is a development-seen parser improvement, with no
fitting or threshold selection. Independent arithmetic and source review are
complete. The [expanded comparison receipts](../../../research/2026-10-17-full-normalization/training/expanded-matching-v1.json)
pin the inputs, actual exports and separate reports.

The committed v3 credit extractor now requires actor-specific prose attribution,
supports named media transfers and credited handles, and preserves exact quotes,
whole-response refusal and versioned caches. A compiled replay of all 458 distinct
v2 responses keeps 383 of 389 previously usable responses and accepts 11 previously
refused responses. Six earlier responses still fail the stricter grammar. This
counterfactual does not measure the changed prompt. The full 600-field paired v3
Luna run completed with 413 usable outputs, 58 unusable results, 124 unchanged
contact exclusions and five empty fields. The new job used 458 admitted attempts
and $0.0555205 configured token accounting, below its $30 ceiling. On 328 complete
fields available in both runs, v3 recovers 209 supported credits, adds five false
credits and misses nine; v2 recovers 207, adds six and misses 11. Three fields
improve and one gains an error. Fifteen complete fields become available and
fifteen become unavailable, so preserve those changes separately. Independent
result review is complete; the independently reproduced counts support these
development conclusions and choose no fallback policy.

The 600 venue queries now have an additive read-only catalog capture:
1,954 dated association rows cover 594 queries. All first 60 review queries have
associated rows, but catalog dates do not establish exact event dates or physical
venue identity. Two independent source readings of the first 60 queries are
complete. Adjudication retains 45 premises/history cases, one event-label-only
case and 14 insufficient cases. These are scoped facts; candidate mapping and
identity-equivalent retrieval evaluation remain required. The answers preserve
all four source/show venue-ID conflicts and unknown catalog date precision.
Grouping exact venue name/location observations improves raw counterpart retrieval
over a fixed 100-row budget, but gives no advantage over an equally sized expanded
row budget on the 32 exposed directions. Keep this result as an experiment; it
does not justify production grouping complexity.

Native catalog capture now covers all 6,838 selected venue candidates, with their
original index fields unchanged. It records counts for 20,453 associated show,
source and setlist rows; the dated rows themselves still need capture. The next
capture uses 45 bounded partitions within the original 14 logical batches. This
keeps the existing output limits after checking that one original batch could
not fit its 2 MiB guard. Source prose is materialized only after selecting explicit
source IDs. This preparation establishes neither venue identity nor event dates.

The composition selection retains 120 claims from 40 artists. MusicBrainz
acquisition stopped at its 600-attempt bound: 416 of 420 searches completed and
28 recording responses were captured. Four searches failed after retries and
14 recording lookups remain undispatched. Seventeen literal recording-to-work
assertions are retained. Search windows and the external universe remain
incomplete. Independently adjudicated catalog-first readings preserve the claims
without assigning specific works or writers. Next join that frozen source account
to external evidence and primary credits; acquisition alone establishes no
catalog-to-work identity. Any further acquisition requires its own bounded window.

Repertoire evaluation now covers 200 file-title cases from 115 artists and 117
source families. All are Archive-associated; 100 cases have empty captured artist
dictionaries, and only 15 artists supply nonempty dictionaries. Independent
adjudication retains 168 literal segments and 73 supported local title-derived
links. The actual public comparison exports all 200 tasks and 35,953 pairs. It
admits a candidate signal for 72 of those 73 segments, covering 71 of 72 distinct
case/record pairs. The remaining 1,069 signals are unadjudicated, not false
positives. This whole-title pair contract does not measure content classification,
extracted component spans, final ambiguity handling or accepted-match precision.
Measure those through the existing title extractor and resolver decision
contracts next, preserving unknowns and complete candidate dictionaries.

The [retained broad API regression](../../../research/2026-10-17-full-normalization/scale/evidence/full-suite-current-v1.json)
passes on its pinned earlier revision. The decoder-v7 change passes 624 distinct
affected checks, including genuine v6 and original-schema v1 history restores; its
broad regression passes 3,699 checks, with zero failures and 32 actual TRX entries
not executed. That capsule predates the credit-v3 and native-people reader changes,
which have separate focused verification. The [v7 receipt](../../../research/2026-10-17-full-normalization/scale/evidence/musicbrainz-v7-verification-v1.json)
records the exact scope. The [1,001-source scale case](../../../research/2026-10-17-full-normalization/scale/evidence/source-owner-thousand-one-terminal-v1.json)
reached its one-hour cancellation limit during initial publication, before its
preservation assertions. Publication cost remains separate required work before
backfill; it does not suspend offline matching evaluation.

## Historical comparison allocations

The allocations below describe the earlier evaluation phase. Keep its inputs and results reproducible, but do not finish or repeat these packets before delivering shadow output. Current batch sizes follow throughput, memory, upstream limits and authorized cost. Select independent audit samples from the full run; preserve source/family separation when estimating accuracy.

- **Credits:** Compare 600 new complete fields, targeting 300 Archive-origin, 150 Local-native and 150 Phish.in-native inputs if verified evidence supports those counts. Keep the old 100-field experiment as a separate compatibility control. Freeze the existing fitted models before the new comparison; improve models using only admitted development/tuning groups. The proposed new Luna job has a $30 ceiling, at most 1,200 admitted attempts, two attempts per field and bounded concurrency. Report provider and artist strata separately; fields from the single-artist Phish.in and Local formats must not dominate catalog conclusions by raw count. The existing credit runner has fixed-cohort assumptions; make the smallest cohort-aware change while preserving the old command. Its byte/output limits and configured rates must be verified in the new job receipt.
- **People:** Start with 200 unanswered relationship questions from the permitted 2,184 original recordings, targeting at least 100 artists when eligible families permit. Reserve 80 ordinary candidate questions, 60 alias/attachment-format questions and 60 evidence-supported contrast questions. These are selection slots, not promised positive/negative labels. Keep known alias misses in a separate regression panel. Expand to at least 400 total fresh questions, adding up to 200 after the first report, with explicit identity-family and provider deficits driving selection.
- **Venues:** Select 600 recording/venue questions across at least 150 artists, with at most four per artist where eligible evidence permits. Keep 400 artist-balanced sampled questions separate from 200 deliberately difficult questions. Finish independent adjudication of the existing 11 resolved drafts, including the five proposed negatives. Acquire dated context and verify provider/geographic strata before claiming their coverage.
- **Compositions and repertoire:** Start with 120 catalog song claims across at least 40 artists and 60 work-family targets, plus 60 artist-scoped repertoire tasks. Include dictionary-free artists. Retain a separate acquisition job capped at 600 HTTP attempts and 256 MiB, and a proposed Luna comparison capped at $25 and 500 admitted attempts. Expand to at least 400 claims and 200 repertoire tasks after the first report. A failed request, missing candidate, unresolved source and supported work absent from the local catalog remain distinct outcomes.

Actual metadata availability may change these allocations. An unfilled provider or identity stratum stays visible and triggers evidence acquisition; it does not become an invented positive, negative or accuracy claim. Report independent units alongside fields, source views and pair counts. A numerical acceptance threshold remains a product decision. Evaluation can establish precision/coverage tradeoffs and uncertainty before that decision; policy activation cannot.

## Work to complete next

Execute the [shadow delivery plan](shadow-delivery.md). Start or resume full-population processing with the current working methods while domain agents fill implementation gaps. Preserve completed model and Luna results; add only the audit/export support needed to inspect actual outputs.

Select audit samples across providers, artist sizes, formats, identity families, time and geography. Include proposed matches, negative decisions, new entities, unresolved inputs and failures. Independently adjudicate sampled answers; preserve genuinely untouched selections and record prior exposure. Audit labels must not enter prompts or candidate indexes. Existing development cases remain useful regression controls. Do not pre-label the entire input population or wait for every audit disagreement before processing independent partitions.

Measure extraction, retrieval and final decision errors separately, then evaluate complete pipelines. Fix common failure classes and replay affected partitions. Compare trained models only where the comparison can improve implementation or routing; no requirement remains to train every model type for every domain. Reuse existing caches and observed format-specific wins. Each model job has a retained spend/attempt/concurrency manifest under the existing authorization; an exhausted pilot cap is not a project-wide ceiling.

## Coverage ledger

Update each entry with the completed experiment receipt, independent decision denominator, remaining gap and next action. The complete captures cover 4,379 artists, 292,522 recordings and 5,064,076 files; these are inventories for selection and shadow processing. They do not supply identity answers.

| Domain | Completed evidence or capability | Gap to close next |
| --- | --- | --- |
| Credited tapers and transferrers | Completed native 600-field comparison across 302 artists; rules, two fixed earlier-fit extractors and Luna scored on shared complete labels. | Run current credit extraction across the retained population; audit format-specific gains, unusable responses, contact exclusions and new errors. Measure role extraction separately from identity and fix common failures. |
| Global people and aliases | Source-view rules/model comparison; public linear-scoring parity; supported alias/context features. | Produce proposed full-pool identity decisions; audit independent families, cross-artist hard negatives, ambiguous aliases and unseen identities. Expose creation/kind/co-reference outcomes and improve discriminating evidence. |
| Physical venues, locality and history | Independent answers for 34 questions, saved-model comparison and full-index candidate retrieval; reviewed storage and resolver lifecycle. | Run proposed venue resolution across the complete captured population; audit physical-identity retrieval, historical aliases, dated premises, rooms and relocations. Reuse the available 600-query packet without requiring it to be fully labeled first. |
| Artist repertoire | Qualified identifiers, dictionaries and visibly provisional title-derived identities; real-source publication through v3. | Run title-to-repertoire decisions across captured files and dictionaries; audit equal titles, repeats, snippets, dictionary-free artists and changed dictionaries, then fix common decision errors. |
| Cross-artist compositions | Retained external work/author data and correspondence/publication contracts. | Produce supported proposed claim-to-work links across the retained population; audit covers, same titles, arrangements, medleys and conflicting writer credits. Acquire missing work evidence and compare alternative methods only where they address observed failures. |
| Performances | Local Phish/Jerry/Aadam import and v3 proofs include stable UUIDs, a reviewed early/late split and playable unresolved assignments. | Run proposed performance assignment across providers and audit date uncertainty, venue changes and separate performances. Sets within one concert stay within that performance; uncertain same-day recordings remain playable under the default show. |
| Ordered setlists and occurrences | A real Jerry account publishes three sets and 18 occurrences through v3; repeated/source order and review contracts are implemented. | Produce supported ordered structures and audit provider order, repeats, annotations, conflicting editions, revised unkeyed accounts and removals. Unordered legacy memberships cannot supply missing order. |
| Recording-file contents | Real-source rehearsal publishes 257 contents across 256 files with stable playback; supported segments and unresolved reasons are retained. | Produce file-content relationships across captured formats; audit compound titles, nonmusic, partial files and many-to-many song/file links; retain unknown times and alignment when evidence supplies neither. |

The original credit comparison covered 78 Archive-configured artists without per-recording provider ownership. The new 600-field capture verifies native ownership for Archive, Local and Phish.in; its Local and Phish.in fields still represent only one artist each. Venue drafts cover 66 artists without verified provider or geographic strata. Track those limitations beside each domain result:

- **Providers:** Archive/native/intake/collections, Phish.in, Local, Jerry mirror, Phish.net, Setlist.fm and Phantasy Tour where the domain applies. Record the actual input provider and representation; maintain a registry outcome for every configured provider. New provider formats enter the same capture, evaluation and acceptance contracts. An artist's configured mapping is not a recording's provenance. A missing applicable format requires capture and evaluation; a format with no relevant credit evidence remains an explicit no-evidence outcome.
- **Artist size and influence:** Report artists with 1–10, 11–100, 101–1,000 and more than 1,000 recordings separately where available. Use artist caps in discovery and sampling weights for population estimates. Show identity-family counts so one prolific taper, GD or Phish cannot dominate a conclusion.
- **Geography and history:** Include supported localities, countries, languages, diacritics, historical names, changed premises and unknown locations. Acquire exact allowed context before assigning these strata; hashes and artist names do not establish geography.
- **Formats and difficult cases:** Plain fields, HTML/entities, prose, lists, handles, equipment-only text, namesakes, ambiguous contacts, reordered/repeated sets, compound files, partial dates and early/late billing. Retain unsupported and failed-acquisition cases in the denominator.

## Catalog-wide processing is active work

Start the complete shadow pass before finishing this ledger. Every eligible retained input receives a proposed result, unresolved reason or failure. Reconcile input/outcome counts and audit stratified outputs while processing continues. A domain represented only by candidates, placeholders or adapter errors remains unfinished. Integrate actual normalized relationships into the reader and fix systemic failures before calling the milestone usable.

Qualify production policies and complete publication capacity, migration and recovery checks before production backfill. Those checks do not block independent local shadow computation. Follow [delivery acceptance](shadow-delivery.md#validation-and-acceptance) for completion evidence.
