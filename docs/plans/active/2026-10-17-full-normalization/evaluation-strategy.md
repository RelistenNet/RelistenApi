# Compare models and learn when Luna helps

Automatic matching across venues, artist repertoire, compositions and people is the release outcome. Recording provenance covers credited tapers and transferrers. This strategy determines which local methods to deploy and which inputs benefit from Luna. It extends the [implementation plan](README.md) and [domain contracts](../../../design/2026-10-17-normalization-contracts.md).

Expanded real-data comparison is the next completed milestone. Preserve the completed credit, people and venue training and Luna experiments, then expand their independently checked evidence under the [coverage and execution plan](evaluation-milestone.md). Continue scale and regression verification in parallel. Offline extraction, retrieval and matching experiments use frozen evidence and require no accepted publication. Interrupt evaluation only for a demonstrated defect that prevents trustworthy experiments or risks accepted data; name that dependency and repair it while independent work continues. Production writes and deployment still require approval. New bounded Luna jobs use the existing spending authorization and retained run controls.

## Train and compare

Build on the completed experiments and compare improved methods on broader independent evidence. Choose production methods after measuring their benefit. The [coverage ledger](evaluation-milestone.md#coverage-ledger) records each domain's completed evidence, gaps and next actions.

For identity matching, compare explainable rules, a logistic-regression matcher and a boosted-tree matcher. Train them to compare evidence-bearing record pairs or rank candidates, rather than classify a record into a fixed list of entity IDs. Use features such as name/alias agreement, locality, artist context, role/writer evidence and provider-qualified identifiers. Keep absent evidence distinct from contradictory evidence. Do not use catalog UUIDs as learned identity labels or treat similarity as a probability without calibration.

For credit extraction, compare the local parsers, a small trained name/role tagger and Luna. Test supported names and taper/transferrer roles separately from global person resolution. The model architecture and training library are local implementation choices; training a language model from scratch is unnecessary for these experiments.

Use the same retained inputs and task definitions for each comparison. Also compare complete pipelines: local processing, local processing with small models, and each approach with selective Luna extraction. Hold candidate indexes and acceptance criteria fixed when isolating an extractor's contribution. Measure candidate retrieval separately so a missing candidate is not blamed on the matcher.

New entities enter the current candidate catalog without retraining a fixed class list. An artist's new cover can create a repertoire entry and link to an existing composition when evidence supports it. Test the alternative outcomes too: new identity, different identity and unresolved evidence. Retrain for demonstrated errors or changed input distributions, rather than automatically for every new song.

## Test generalization

Use the [all-artist sampling frame](../../../research/2026-10-17-full-normalization/scale/README.md) to expand labeled decisions across providers, catalog sizes, languages and metadata formats. The 102 seed cases are regression fixtures; raw sample counts are not labeled accuracy evidence.

Maintain separate training, tuning and untouched test partitions. Keep duplicate uploads, recording families and known aliases together. Run distinct evaluation suites for unseen identities, held-out artists, later recordings and unfamiliar formats. One split need not satisfy every holdout constraint; report each suite separately. Temporal tests use candidate records and aliases available at their cutoff, excluding knowledge acquired later.

Include positive matches, plausible hard negatives, new-identity cases and insufficient evidence. Add cross-artist cases without letting GD or Phish dominate. Record labeling evidence, adjudication and unresolved disagreements. Independently check test labels; neither the evaluated model nor Luna can certify its own answer. If a test family informs a rule or prompt change, move it into development and replace it before the next untouched evaluation.

## Measure Luna's incremental value

Run local processing first. Send a stratified comparison sample to Luna that includes local failures, ambiguity and apparent successes. Independently judge sampled agreements as well as disagreements; agreement can repeat the same error.

For each task/cohort, count local errors Luna corrects, new errors it introduces, unchanged results and unsupported answers. Measure accepted-match precision, automatic coverage at the required precision, candidate recall, extraction name/role accuracy, review volume, latency and cost. Include denominators and uncertainty. Report performance on new identities separately from familiar ones.

Luna can interpret difficult prose or evidence missed by a local method. It cannot establish a missing writer credit or distinguish namesakes without supporting facts. Preserve that distinction in labels and acceptance checks.

## Select and monitor routing

Derive task-specific fallback conditions from tuning results. Candidate conditions include unparsed credit clauses, unfamiliar structures and competing candidates requiring contextual interpretation. Novelty is a diagnostic feature, not an automatic requirement to call Luna. A new artist with an explicit supported identifier can stay local.

Evaluate the resulting routing policy on untouched data. Retain its version, task/cohort applicability, acceptance thresholds, input/model versions and evaluation receipt. Deploy the measured method that meets precision and coverage requirements; a trained model may win in one domain and rules may win in another. No method publishes unsupported links or overwrites reviewed decisions.

After launch, audit a stratified sample of local successes, fallback results and unresolved cases. Track accuracy and coverage by provider and format. Use adjudicated failures to expand regression data and revise routing or training. Apply changed policies through the existing revision-fenced publication and rollback contracts.
