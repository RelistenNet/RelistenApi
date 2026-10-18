# Compare people identity rules and trained models

This experiment compares the actual C# `IdentityCandidateMatcher.ComparePeople`
output with logistic regression and gradient-boosted trees on the frozen
`people-development-v1` bundle. It trains pair scorers and preserves each complete
candidate question. It does not create people, assign credits, choose publication
thresholds or activate a policy.

Run the actual comparison only from the main task after the C# exporter has
produced an authenticated baseline. The author tests use invented rows; they do
not establish results on the real people data.

## What the current data can answer

The bundle contains 168 development questions: 79 actor-kind, 46 within-recording
co-reference and 43 cross-recording identity questions. Its 158 identity candidate
edges contain 59 proposed same-actor, 22 proposed different-actor and 77 unknown
decisions. These are correlated source referents, not 158 independent identities.
All labels remain development drafts with adjudication pending.

The cross-recording *question pools* include candidates from the same recording:
13 proposed same-actor and 61 unknown edges. Filtering by distinct original
recording evidence IDs leaves 11 proposed same-actor and 59 unknown edges, with
zero different-person negatives. All eleven positive edges concern one Aadam
Jacobs project owner. Report these populations separately. A trained model's fit
to these labels cannot establish broad cross-artist identity accuracy.

Three cross-recording edges describe undivided credit expressions or unresolved
references. They remain in pools and split grouping, with explicit unsupported
projection status. They enter neither training nor loss and receive no trained
score. Their original labels remain unchanged.

## One feature contract

Only the following five actual runtime values enter a model, in this order:

1. `name_token_similarity`
2. `exact_name`
3. `artist_overlap`
4. `contact_agreement`
5. `profile_agreement`

The exporter computes these from the real C# comparison: token similarity and
agreement for `name`, `artist_context`, `actor_contact` and `actor_profile`.
Python checks the exported vector against that comparison; it does not implement
another name matcher. IDs, split hypotheses, draft rationale, primary-source
answers, role labels and actor-kind judgments never enter the feature matrix.

The current projection uses source mention wording and its catalog artist only.
Person kind, contact/profile facts, authoritative identifiers and answer-derived
aliases remain unknown. The final two features are therefore absent agreements,
not independently extracted identity evidence. A shared artist is a ranking
feature; different artists do not establish different people.

## Input exchange and provenance

`baseline.jsonl` has exactly one row for each declared identity candidate edge:

```text
taskId, taskKind, focusMentionId, candidateMentionId,
focusEvidenceId, candidateEvidenceId, sameRecording,
declaredCandidateCount, projectionSupported, projectionIssue,
features[5], comparison
```

The actual comparison has no active policy or canonical candidate UUID. Mention
IDs are inventory coordinates, not accepted person IDs. Python independently
joins each row to the source bundle and rejects an omitted candidate, an extra
edge, changed evidence ownership, a changed feature vector or policy authority.

The C# `manifest.json` names `people-pair-baseline-v1`, projection version
`people-mentions-name-artist-five-v1`, actor feature version, the exact ordered
feature names, source bundle hash, each input file's bytes/hash, output hash and
actual matcher/runner assembly artifacts. Python rehashes those executable paths;
hash strings without matching bytes are insufficient.

The existing bundle validator owns source/reservation/span validation. The script
imports its pinned entrypoint and calls `validate_bundle`; it does not recreate
reservation eligibility. Its `build_split_components` receives **all 168 tasks**
before any actor-kind or unknown edge is excluded from fitting. This retains
group bridges through questions that supply no binary identity target.

Small inputs, source/validator code, source bundle files, executable artifacts
and environment manifests are copied byte for byte under SHA-named
`retained-inputs/` paths before fitting. Files larger than 32 MiB remain explicitly
marked external frozen-capture references, with byte length/hash and original
location. Those large captures must remain available; the experiment directory
does not claim to contain the entire catalog snapshot. All original inputs and
retained output files are checked again before the final report appears.

## Fitting and diagnostics

Both methods use the same five unscaled numeric values. Logistic regression uses
L2 regularization, `C=1`, an intercept, L-BFGS and a 1,000-iteration limit. A failed
optimizer is a failed run. Boosting uses 20 depth-one trees, learning rate 0.05
and minimum leaf size two. The random seed is 173. No parameter search or
acceptance-threshold tuning occurs.

Fit the final development models on supported resolved edges. Then leave one
complete connected component out at a time. A held component with supported
resolved edges gets a model trained without that component. An unknown-only
component may use the final model because it contributed no training target. A
component whose training side lacks either binary class is explicitly
`unscorable_one_class_training`; it must not fall back to a model fitted on its
held-out answers. Actor-only components remain in the split receipt even though
they have no identity prediction.

Unknown edges retain a null target. They never enter fitting, log loss or binary
disagreement counts. Each question retains all candidate scores, ties,
unscorable candidates and unknown rivals at or above its best scored known
positive. A top-ranked unknown is still unknown. The fixed sigmoid cutoff 0.5
is a development inspection count, not a matching threshold or identity claim.

The rules comparison retains actual C# dispositions and supported-rule counts.
It supplies no policy, so abstentions remain abstentions. Python does not turn
`not_candidate` into a different-person label or synthesize rule predictions.
The models' sigmoid scores are uncalibrated; draft log losses describe fit to
these selected draft answers.

## Portable models and runtime use

`models.json` holds a final model and supported component-held-out models for
each method. Logistic JSON records an intercept and five raw, unscaled
coefficients, with `scoreKind=raw_logit`. Its `runtimeScorerParameters` maps those
values directly to the existing C# scorer names:

```text
intercept, nameSimilarityWeight, exactNameWeight,
artistOverlapWeight, contactAgreementWeight, profileAgreementWeight
```

The file intentionally has no `MatchThreshold`, `MinimumRunnerUpMargin`, creation
capability or acceptance attestation. The C# parity fixture can load the fixed
parameters and compare raw scores without constructing an activated policy.

Boosted JSON stores the initial log odds, learning rate and bounded tree arrays.
The portable scorer follows scikit-learn's float32 input conversion and
inclusive `<=` split comparison. Both JSON scorers use only the Python standard
library. Every fitted final/fold export is JSON-round-tripped and checked against
the fitted estimator's raw score and sigmoid output to tolerance `1e-12` before
it is retained. Invalid dimensions, cycles, unreachable tree nodes, nonfinite
values and unsupported versions fail explicitly.

## Run the experiment

The experiment, its tests and this guide are in `tools/RelistenNormalization/`.
The recorded executed source hash must match the selected copy. No
network, database, API or paid model client is used by this Python command.

Use the existing pinned venue experiment environment. Do not install new
packages. The script verifies its actual virtual-environment path, exact lock
versions, installation receipt, lockfile and Python executable bytes.

```sh
local-dev/normalization-data/venue-matcher-environment-v1/bin/python -B -m unittest discover \
  -s tools/RelistenNormalization \
  -p 'test_people_identity_matcher_experiment.py' -v
```

For the actual data run, replace the baseline directory and its reviewed manifest
hash with the completed C# exporter result. Choose a **new** output directory.

```sh
local-dev/normalization-data/venue-matcher-environment-v1/bin/python \
  tools/RelistenNormalization/people_identity_matcher_experiment.py \
  --root "$PWD" \
  --bundle "$PWD/docs/research/2026-10-17-full-normalization/training/people-development-v1" \
  --baseline /absolute/path/to/completed-csharp-baseline \
  --manifest-sha256 866ba35da197984f6b5e2df5285f84cd62102f67392e514171115c4f5898bd5c \
  --validator-sha256 ef49b3864315f9b7288142c6a9519abe7a528412cd52f2918e57ef4b8b22953a \
  --baseline-manifest-sha256 REVIEWED_BASELINE_MANIFEST_SHA256 \
  --environment-install-sha256 29cc6e24fb2ff2b78e8f5529de7796ac2f5253c378ced588c7c463f0d2e14f1a \
  --environment-lock-sha256 24be7a7b94b94586b2347299dcf62e5d12fd5b2e3e5316ef08c46943ef33b24f \
  --output /absolute/path/to/new-private-people-comparison
```

The output includes exact features, split membership/training edges, portable
models, all predictions, unknown predictions, complete candidate-pool
diagnostics, actual baseline/manifest, environment and retained input hashes.
`report.json` is renamed into place last. A changed input, failed fit or failed
write can leave partial files but no completed report. Preserve the partial
directory, investigate and retry in a new one. Existing output is never
overwritten.

Before using these weights in a real acceptance artifact, expand independently
adjudicated identity families, hard negatives and generalization suites, measure
candidate recall and whole-pool matching behavior, and select operating points
on tuning data. Test the resulting policy on untouched evidence and use the
existing reviewed activation/publication contracts. This development experiment
does not supply that authority.
