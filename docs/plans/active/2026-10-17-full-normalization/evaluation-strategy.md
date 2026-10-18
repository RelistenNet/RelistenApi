# Compare models and learn when Luna helps

Automatic matching across venues, artist repertoire, compositions and people is the release outcome. Recording provenance covers credited tapers and transferrers. This strategy determines which local methods to deploy and which inputs benefit from Luna. It extends the [implementation plan](README.md) and [domain contracts](../../../design/2026-10-17-normalization-contracts.md).

Usable full-catalog shadow output is the next milestone, under the [delivery plan](shadow-delivery.md) and [standing goal](goal.md). Compute proposed normalization over the complete captured catalog while independent audits and domain improvements run in parallel. Expanded labeling packets and production publication are not prerequisites. Preserve existing evidence and production approval requirements.

Audit real outputs to choose methods and acceptance policies. Scale model jobs using observed throughput, concurrency limits and authorized cost. The completed training experiments satisfy the instruction to try small models; further fitting is optional when it does not address a demonstrated delivery gap. The [coverage ledger](evaluation-milestone.md#coverage-ledger) guides broad audits rather than serial work admission.

## Learn which overall approach works

Follow the [experimental policy](goal.md#experimental-development-policy). Each experiment should answer a consequential question: can this method complete a feature, cover a missing cohort, separate a recurring ambiguity or remove a practical bottleneck? Compare complete outputs before polishing individual stages. Finish the simplest usable version and give Alec access while inspection and targeted corrections continue.

When the existing features cannot distinguish correct and incorrect identities, try contextual reasoning or a different representation. Do not restrict exploration to thresholds on the same inadequate feature vector. Use a capable model on enough varied cases to learn whether the approach works, within the existing authorized job controls. Local rules, trained models and direct model-assisted resolution are options, not a mandatory sequence.

A plausible inferred proposal can be inspected with its context, alternatives and known limitations before calibration or release adjudication. Keep it distinct from a directly observed fact and from an accepted production identity. Independent audits measure quality and guide revision; they are not approval gates for every experimental output. If an approach is weak, revise or replace it rather than investing in small gains indefinitely.

## Train and compare

Build on the completed experiments and compare improved methods on broader independent evidence. Choose production methods after measuring their benefit. The [coverage ledger](evaluation-milestone.md#coverage-ledger) records each domain's completed evidence, gaps and next actions.

For identity matching, build on the completed rules, logistic-regression and boosted-tree experiments. Compare further alternatives when they can change a domain implementation or routing decision. Train them to compare evidence-bearing record pairs or rank candidates, rather than classify a record into a fixed list of entity IDs. Use features such as name/alias agreement, locality, artist context, role/writer evidence and provider-qualified identifiers. Keep absent evidence distinct from contradictory evidence. Do not use catalog UUIDs as learned identity labels or treat similarity as a probability without calibration.

For credit extraction, reuse the completed local-parser, trained-tagger and Luna comparisons. Keep the better existing methods and investigate common failures in the full shadow output. Test supported names and taper/transferrer roles separately from global person resolution. The model architecture and training library are local implementation choices; training a language model from scratch is unnecessary for these experiments.

Use the same retained inputs and task definitions for each comparison. Also compare complete pipelines: local processing, local processing with small models, and each approach with selective Luna extraction. Hold candidate indexes and acceptance criteria fixed when isolating an extractor's contribution. Measure candidate retrieval separately so a missing candidate is not blamed on the matcher.

New entities enter the current candidate catalog without retraining a fixed class list. An artist's new cover can create a repertoire entry and link to an existing composition when evidence supports it. Test the alternative outcomes too: new identity, different identity and unresolved evidence. Retrain for demonstrated errors or changed input distributions, rather than automatically for every new song.

## Test generalization

Use the [all-artist sampling frame](../../../research/2026-10-17-full-normalization/scale/README.md) to expand labeled decisions across providers, catalog sizes, languages and metadata formats. The 102 seed cases are regression fixtures; raw sample counts are not labeled accuracy evidence.

Maintain separate training, tuning and untouched test partitions. Keep duplicate uploads, recording families and known aliases together. Run distinct evaluation suites for unseen identities, held-out artists, later recordings and unfamiliar formats. One split need not satisfy every holdout constraint; report each suite separately. Temporal tests use candidate records and aliases available at their cutoff, excluding knowledge acquired later.

Include positive matches, plausible hard negatives, new-identity cases and insufficient evidence. Add cross-artist cases without letting GD or Phish dominate. Record labeling evidence, adjudication and unresolved disagreements. Independently check test labels; neither the evaluated model nor Luna can certify its own answer. If a test family informs a rule or prompt change, move it into development and replace it before the next untouched evaluation.

## Measure Luna's incremental value

Reuse local processing as a baseline where useful. Test Luna or another suitable available model on a stratified comparison including local failures, ambiguity and apparent successes. A local-first runtime route is a choice to measure, not a restriction on exploring a different overall method. Independently judge sampled agreements as well as disagreements; agreement can repeat the same error.

For each task/cohort, count local errors Luna corrects, new errors it introduces, unchanged results and unsupported answers. Measure accepted-match precision, automatic coverage at the required precision, candidate recall, extraction name/role accuracy, review volume, latency and cost. Include denominators and uncertainty. Report performance on new identities separately from familiar ones.

Luna can interpret difficult prose or evidence missed by a local method. It cannot establish a missing writer credit or distinguish namesakes without supporting facts. Preserve that distinction in labels and acceptance checks.

## Select and monitor routing

Derive task-specific fallback conditions from tuning results. Candidate conditions include unparsed credit clauses, unfamiliar structures and competing candidates requiring contextual interpretation. Novelty is a diagnostic feature, not an automatic requirement to call Luna. A new artist with an explicit supported identifier can stay local.

Evaluate the resulting routing policy on untouched data. Retain its version, task/cohort applicability, acceptance thresholds, input/model versions and evaluation receipt. Deploy the measured method that meets precision and coverage requirements; a trained model may win in one domain and rules may win in another. No method publishes unsupported links or overwrites reviewed decisions.

After launch, audit a stratified sample of local successes, fallback results and unresolved cases. Track accuracy and coverage by provider and format. Use adjudicated failures to expand regression data and revise routing or training. Apply changed policies through the existing revision-fenced publication and rollback contracts.
