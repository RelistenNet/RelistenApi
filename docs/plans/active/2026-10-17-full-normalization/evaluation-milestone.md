# Compare normalization methods across the catalog

The next completed milestone is an expanded, independently checked comparison on real catalog inputs. Start with credit extraction and cross-artist people matching, where rules, trained models and Luna experiments already run. Expand venue and composition evidence in parallel. Continue the existing scale and regression checks separately; production publication is not a prerequisite for an offline comparison.

This milestone chooses the next implementations and acceptance policies. It is an intermediate step toward automatic normalization across every current and future artist and provider. After representative evaluation, run a full-catalog shadow pass, audit its results, rehearse full backfill on a restored catalog, and prepare rollout. Obtain approval before production writes or deployment. Preserve original metadata, reviewed corrections and stable playback references throughout.

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

## First expanded comparisons

These are bounded acquisition and comparison targets, not counts already achieved or final precision sample sizes. Freeze the exact selected tasks before opening text, labels or predictions where reservation requires it. Report shortfalls and carry the missing cohort into the next acquisition.

- **Credits:** Compare 600 new complete fields, targeting 300 Archive-origin, 150 Local-native and 150 Phish.in-native inputs if verified evidence supports those counts. Keep the old 100-field experiment as a separate compatibility control. Freeze the existing fitted models before the new comparison; improve models using only admitted development/tuning groups. The proposed new Luna job has a $30 ceiling, at most 1,200 admitted attempts, two attempts per field and bounded concurrency. Report provider and artist strata separately; fields from the single-artist Phish.in and Local formats must not dominate catalog conclusions by raw count. The existing credit runner has fixed-cohort assumptions; make the smallest cohort-aware change while preserving the old command. Its byte/output limits and configured rates must be verified in the new job receipt.
- **People:** Start with 200 unanswered relationship questions from the permitted 2,184 original recordings, targeting at least 100 artists when eligible families permit. Reserve 80 ordinary candidate questions, 60 alias/attachment-format questions and 60 evidence-supported contrast questions. These are selection slots, not promised positive/negative labels. Keep known alias misses in a separate regression panel. Expand to at least 400 total fresh questions, adding up to 200 after the first report, with explicit identity-family and provider deficits driving selection.
- **Venues:** Select 600 recording/venue questions across at least 150 artists, with at most four per artist where eligible evidence permits. Keep 400 artist-balanced sampled questions separate from 200 deliberately difficult questions. Finish independent adjudication of the existing 11 resolved drafts, including the five proposed negatives. Acquire dated context and verify provider/geographic strata before claiming their coverage.
- **Compositions and repertoire:** Start with 120 catalog song claims across at least 40 artists and 60 work-family targets, plus 60 artist-scoped repertoire tasks. Include dictionary-free artists. Retain a separate acquisition job capped at 600 HTTP attempts and 256 MiB, and a proposed Luna comparison capped at $25 and 500 admitted attempts. Expand to at least 400 claims and 200 repertoire tasks after the first report. A failed request, missing candidate, unresolved source and supported work absent from the local catalog remain distinct outcomes.

Actual metadata availability may change these allocations. An unfilled provider or identity stratum stays visible and triggers evidence acquisition; it does not become an invented positive, negative or accuracy claim. Report independent units alongside fields, source views and pair counts. A numerical acceptance threshold remains a product decision. Evaluation can establish precision/coverage tradeoffs and uncertainty before that decision; policy activation cannot.

## Work to complete next

1. **Freeze the comparison baseline.** Retain exact experiment code, environment, input and output hashes, task definitions, label versions, grouping, candidate records, model/prompt/schema versions and usage receipts. Pin each existing experiment to its own source and data authority; separate captures do not become one historical snapshot. An unrelated publication change does not invalidate a frozen extraction or matching experiment. Rerun an affected comparison only when its inputs, task, features, retrieval or method changes.
2. **Expand permitted development evidence while reserving evaluation.** Use the already permitted 2,184-source development frame immediately. In parallel, use the complete metadata captures to close known exposure and family reservations for new train, tune and untouched evaluation groups. Unknown exposure stays explicit. Keep duplicate uploads, recording families and known identity/alias families together. Use separate artist, identity, time and format holdout suites; do not require every question to pass the intersection of unrelated splits.
3. **Create answers before opening predictions.** Freeze candidate assertions before query labeling; labels define scoring equivalence and cannot enrich the index. Give independent reviewers exact supporting inputs without model answers or candidate-selection rationale. Separate credited name/role extraction from global person identity. Preserve evidence for matches, different identities, new identities and unknowns; adjudicate disagreements. For venues obtain dated locality/premises evidence. For compositions obtain supported catalog-to-work bridges. Retain writer-role assertions and author-list completeness when available; a supported bridge can establish work correspondence without a writer assertion. If evidence is missing, investigate and record the acquisition result rather than silently dropping the question.
4. **Run matched experiments and improve the failing methods.** Compare actual rules, logistic regression, boosted trees and trained credit extractors with selective Luna on retained tasks. Where an offline export or real matching adapter is missing, implement it against the existing pure public contracts; a fixture-only acceptance adapter is not a production matcher. Export actual retrieval from the frozen candidate catalog or index; score candidate recall separately from conditional matcher errors. For adjudicated targets known to exist in that catalog, absence from the returned candidate pool is a retrieval miss. Retrieving any supported equivalent satisfies identity retrieval; report duplicate-row coverage separately. New-catalog identities, unresolved evidence and unavailable retrieval keep separate outcomes. Include apparent local successes, ambiguity and failures in Luna comparisons. Fit on development data, choose features and routing on tuning data, and report untouched results after choices freeze. Record new errors as well as corrections, and complete pipeline outcomes as well as component scores.
5. **Publish the decision report and expand the next tranche.** Report incorrect links/merges, missed links, unknowns, abstention, automatic coverage, review volume, latency and cost with denominators and uncertainty. Separate representative sampling from deliberately difficult cases. Name the winning method by domain/cohort, the evidence that changes implementation and the next acquisition or experiment for every material gap. An undersized cohort triggers a bounded expansion. Continue through performances, repertoire, ordered occurrences and file contents, then the full-catalog shadow audit.

Each follow-on Luna job needs a new retained job manifest; the exhausted pilot limit is not a project-wide limit. Pin a justified field count, attempt limit, concurrency, model, prompt/schema, price configuration and spend ceiling before starting. Reuse valid cached responses and retain every admitted attempt, failed input and uncertain provider outcome. Size subsequent jobs from measured cost and unresolved coverage within the existing spending authorization. No new production policy is activated by an experiment.

## Coverage ledger

Update each entry with the completed experiment receipt, independent decision denominator, remaining gap and next action. The complete captures cover 4,379 artists, 292,522 recordings and 5,064,076 files; these are inventories for selection and shadow processing. They do not supply identity answers.

| Domain | Completed evidence or capability | Gap to close next |
| --- | --- | --- |
| Credited tapers and transferrers | Completed native 600-field comparison across 302 artists; rules, two fixed earlier-fit extractors and Luna scored on shared complete labels. | Investigate 69 unusable responses, 124 whole-field contact exclusions and new Luna errors; broaden difficult prose across artists. Measure role extraction separately from people matching and reserve independent evaluation. |
| Global people and aliases | Source-view rules/model comparison; public linear-scoring parity; supported alias/context features. | More independent identity families, cross-artist hard negatives, ambiguous aliases and unseen identities; actual full-pool retrieval and creation/kind/co-reference outcomes. |
| Physical venues, locality and history | Independent answers for 34 questions, saved-model comparison and full-index candidate retrieval; reviewed storage and resolver lifecycle. | Label the 600 new queries; evaluate identity-equivalent retrieval and historical aliases; investigate dated premises, rooms and relocations. A current address cannot determine an old performance's venue. |
| Artist repertoire | Qualified identifiers, dictionaries and visibly provisional title-derived identities; real-source publication through v3. | Independently evaluate title parsing, artist-scoped identity and dictionary-free artists; include equal titles, repeats, snippets and changed provider dictionaries. |
| Cross-artist compositions | Retained external work/author data and correspondence/publication contracts. | Build independently supported local claim-to-work answers and supported writer-role assertions, keeping author-list completeness explicit; compare retrieval, rules, trained models and Luna on covers, same titles, arrangements, medleys and conflicting credits. |
| Performances | Local Phish/Jerry/Aadam import and v3 proofs include stable UUIDs, a reviewed early/late split and playable unresolved assignments. | Evaluate automatic assignment across providers, date uncertainty, venue changes and separate performances. Sets within one concert stay within that performance; uncertain same-day recordings remain playable under the default show. |
| Ordered setlists and occurrences | A real Jerry account publishes three sets and 18 occurrences through v3; repeated/source order and review contracts are implemented. | Independently label provider order, repeats, annotations and conflicting editions; evaluate correspondence, revised unkeyed accounts and removals. Unordered legacy memberships cannot supply missing order. |
| Recording-file contents | Real-source rehearsal publishes 257 contents across 256 files with stable playback; supported segments and unresolved reasons are retained. | Evaluate compound titles, nonmusic, partial files and many-to-many song/file links across formats; retain unknown times and alignment when evidence supplies neither. |

The original credit comparison covered 78 Archive-configured artists without per-recording provider ownership. The new 600-field capture verifies native ownership for Archive, Local and Phish.in; its Local and Phish.in fields still represent only one artist each. Venue drafts cover 66 artists without verified provider or geographic strata. Track those limitations beside each domain result:

- **Providers:** Archive/native/intake/collections, Phish.in, Local, Jerry mirror, Phish.net, Setlist.fm and Phantasy Tour where the domain applies. Record the actual input provider and representation; maintain a registry outcome for every configured provider. New provider formats enter the same capture, evaluation and acceptance contracts. An artist's configured mapping is not a recording's provenance. A missing applicable format requires capture and evaluation; a format with no relevant credit evidence remains an explicit no-evidence outcome.
- **Artist size and influence:** Report artists with 1–10, 11–100, 101–1,000 and more than 1,000 recordings separately where available. Use artist caps in discovery and sampling weights for population estimates. Show identity-family counts so one prolific taper, GD or Phish cannot dominate a conclusion.
- **Geography and history:** Include supported localities, countries, languages, diacritics, historical names, changed premises and unknown locations. Acquire exact allowed context before assigning these strata; hashes and artist names do not establish geography.
- **Formats and difficult cases:** Plain fields, HTML/entities, prose, lists, handles, equipment-only text, namesakes, ambiguous contacts, reordered/repeated sets, compound files, partial dates and early/late billing. Retain unsupported and failed-acquisition cases in the denominator.

## Continue to catalog-wide processing

Use qualified methods for a complete shadow run with one outcome for every eligible input. Audit stratified accepted-looking, unresolved and failed outputs across the ledger, improve the affected methods, and repeat the affected checks. Estimate runtime, resource use and Luna cost from that run. Verify incremental updates, recovery, reviewed corrections and public playback during the restored-catalog backfill rehearsal and rollout preparation. The running scale/regression checks remain required; interrupt evaluation only for a demonstrated defect that prevents trustworthy experiments or risks accepted data. Record that dependency and make the smallest coherent repair while independent evaluations continue.
