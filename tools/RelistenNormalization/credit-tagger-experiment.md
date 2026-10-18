# Train a small credit extractor on development labels

The first trained extractor performs worse than the retained local parser on these development examples. It is useful as an executable baseline and an error inventory. It provides no authority to accept identities or recording credits.

`credit_tagger_experiment.py` trains an averaged structured perceptron: a small sequence model with ordinary JSON weights. It predicts where a credited name starts and ends, separately for each role. Separate BIO heads—beginning, inside and outside a name—allow one name to be both taper and transferrer. The decoder forbids an inside label without a preceding name. It predicts roles, not person IDs.

The experiment requires Python 3.11 or later and uses only its standard library. It reads no API key and makes no network or database calls. It does not change the C# runtime.

## Data and development splits

The frozen draft has 100 original fields from 78 Archive-configured artists: 105 role-bearing assertions in total. The model trains on 85 fully resolved fields containing 91 assertions. Fifteen uncertain fields are excluded from fitting and full scoring. The receipt's explicit exclusion list is also enforced, even if an excluded field otherwise looks complete. Known positives in excluded fields remain visible as positive-recovery diagnostics; they do not supply outside/negative labels or a full precision denominator.

The five development folds contain 20 fields each. Training subsets contain 66–69 fields from 50–53 artists. Connected grouping keeps each artist, duplicate family, known recording family and exact normalized name within one fold. All four overlap audits are zero. Grouping exact names is a leakage precaution, not a claim that namesakes are the same person. Unrecognized aliases can still cross groups.

The labels remain development drafts. A separate Codex agent reviewed 20 original fields without seeing the draft answers; this does not replace independent human adjudication. One uncertainty disagreement remains excluded. These are development diagnostics, not a population sample or an independent accuracy evaluation.

## First run

The experiment used 12 epochs, seed 173 and the fixed feature set `token-window-3-shape-affix-field-v1`. Features include the original token, nearby tokens, word shape, affixes and field name. Tokenization preserves original Unicode codepoint offsets; no HTML or entity normalization occurs before a name is cited.

On the 91 scored assertions, the trained model produced 41 exact name/role matches, 15 extra predictions and 50 missed assertions. The actual retained `recording-credits-v1` parser produced 63 exact literal name/role matches, 8 extra predictions and 28 misses on the same fields. Model name/role and exact span/role counts happened to agree in this run.

The priority roles show the same result:

- Taper: model 37 matches, 8 extra predictions and 17 misses; retained parser 47, 5 and 7.
- Transferrer: model 4 matches, 7 extra predictions and 12 misses; retained parser 13, 2 and 3.

The model recovered none of the scored description or taper-note assertions. The 27 fields with model errors contain 41 missed assertions without any overlapping prediction, 9 missed assertions with an overlapping prediction, 13 predictions with an overlapping wrong boundary, and 2 correct-boundary predictions with the wrong role. These categories describe this run; they do not establish a catalog error rate.

The report also retains per-role and per-field counts and precision/recall diagnostics. Collector, matrix builder and recording engineer have no labeled observations and are marked `not_evaluated`. Only 16 scored transferrer assertions and 9 scored description fields are available. This data does not justify replacing the rules or selecting an automatic acceptance threshold. More independently checked prose, role, negative and alias coverage is needed before comparing stronger models or deciding where an API fallback adds value.

## Reproduce and inspect

Run from the repository root, with a new output directory:

```sh
PYTHONDONTWRITEBYTECODE=1 python3 tools/RelistenNormalization/credit_tagger_experiment.py \
  --labels docs/research/2026-10-17-full-normalization/training/credits-development-v1/labels.jsonl \
  --receipt docs/research/2026-10-17-full-normalization/training/credits-development-v1/receipt.json \
  --fields local-dev/normalization-data/2026-10-17-cross-artist/training-development-credits-v1/fields.jsonl \
  --baseline local-dev/normalization-data/2026-10-17-cross-artist/extraction-v1/extractions.jsonl \
  --baseline-receipt local-dev/normalization-data/2026-10-17-cross-artist/extraction-v1/manifest.json \
  --frozen-labels-sha256 5f2eb545f8d5d66b4750d74e883cc179e673e4775f5df9207fac027069599c12 \
  --output /path/to/new-development-output
```

The completed first run is under the ignored directory `local-dev/normalization-data/2026-10-17-cross-artist/training-development-credits-v1/perceptron-v1/`:

- `weights.json`: the final model fitted on all 85 eligible development fields, with version and input hashes. This is separate from the fold models used to generate diagnostics.
- `splits.json`: exact label membership and connected grouping for each fold.
- `predictions.jsonl`: every development prediction, original quote and span, gold assertion, and retained parser proposal.
- `errors.jsonl`: the 27 complete fields with model errors.
- `report.json`: counts, per-role/per-field diagnostics, training sizes, overlap audits, source hashes and limitations.
- `artifacts.json`: SHA-256 hashes for those five files.

`report.json` is published last. An interrupted fit leaves no completed report. Existing output directories are never overwritten.

The parser baseline is the actual retained extraction output, joined by source UUID and field, with exact original-input equality checks. Its citations cover supporting phrases; the experiment derives a name span only when the phrase contains one unique literal occurrence. It never treats phrase offsets as name offsets. The comparison's primary parser metric uses literal name and role, so ambiguous name anchoring is not silently dropped.

The old extraction manifest records the snapshot hash and parser version but has no output or implementation hash. The experiment therefore records the baseline's current file hash and explicitly marks historical output verification and equivalence to current C# code as unavailable. It records current C# source hashes without claiming that they identify the historical producer. Future manifests with `outputSha256` are checked against that digest.

The frozen label hash is `5f2eb545f8d5d66b4750d74e883cc179e673e4775f5df9207fac027069599c12`; the label receipt hash is `bba2cc9c6d6d1d7dbd42d9034c9f4e86bf97ef218d9a592cc3956b08bdbbe46f`. The experiment source hash for the first run is `100a59e7601d50f8c128d7b2051142f11713617ebc3600b2cf54eb7f6258753d`, and its completed report hash is `f0f7a0be1cd6e848fc2aee693ddb36f4626b72aa24c08f6720086afb3021d6f5`.

Run the contract tests with:

```sh
PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover \
  -s tools/RelistenNormalization -p test_credit_tagger_experiment.py
```

The tests cover Unicode separators and astral codepoints, legal BIO transitions, overlapping roles, unknown and explicit-exclusion masking, grouping, JSON weight round trips, phrase/name anchoring, and interrupted fitting. They verify the experiment's contracts, not extraction accuracy.

## Matched rules, trained models and cached Luna

`matched_credit_experiment.py` extends this experiment with a second trained model
and a common comparison against current rules and retained Luna results. It reads
exactly the same 100 original fields. It starts no provider, API or database work.
The earlier perceptron run and all frozen labels remain unchanged.

The two learners use the existing raw token windows, shapes, affixes and field
features. The perceptron learns sequence weights. `LogisticBioTagger` fits sparse
per-role logistic emissions with legal BIO decoding; it is not a CRF. Its vocabulary
and coefficients are fitted within each training fold. A role absent from a fold
has an explicit constant-outside model. An unfinished optimizer fails the run.
Restored JSON models decode without scikit-learn. Weights and vocabulary can contain
original name tokens, so keep model artifacts in ignored storage.

Group **all 100 fields before filtering** by artist, duplicate-input family, known
recording family and exact normalized names. Then exclude incomplete/unknown fields,
the original receipt exclusion and the additive Josh whole-field sensitivity mask
from both fitting and scoring. The Josh mask also removes its supported transfer
credit; it does not convert the disputed taper role into a negative. Merely masking
old predictions would leave that disputed label in other folds' training data.
Models saved by this command are the actual fold models used for out-of-fold
predictions, not models fitted on the entire scored corpus.

The 543-field actor bundle is a separate attachment diagnostic corpus. Its Context
coverage does not establish exhaustive name-token labels, and its unknown actorless
fields cannot supply negative sequences. It is not implicitly added to this fit.

### Prepare authenticated inputs

First retain a **separate key-free** `luna-credit-fields` export from the current
compiled rules, as described in [the field-comparison guide](luna-credit-fields.md).
That is a separate operator step; this Python command never starts it. Successful
Luna outcomes must have equal successful cache replays. Missing cached outcomes
remain unavailable even when their error objects contain local fallback data.

Retain the code receipt beside that export. Use the existing JSON contract:
`local_extractor_version`, `luna_extractor_version` and `files`, where each file
has `path` and `sha256`. For this study the versions are
`recording-credits-v4/recording-actor-evidence-v1` and `luna-recording-credits-v2`.
Archive these eight files under the receipt's code root before another build can
replace them:

- `RelistenApi/Services/Normalization/RecordingCreditExtractor.cs`
- `RelistenApi/Services/Normalization/RecordingActorEvidenceExtractor.cs`
- `RelistenApi/Services/Normalization/LunaRecordingCreditExtractor.cs`
- `RelistenApi/Services/Normalization/LunaRecordingCreditClient.cs`
- `RelistenApi/Services/Data/NormalizationExtractionCacheService.cs`
- `tools/RelistenNormalization/LunaCreditFieldEvaluationBatch.cs`
- `tools/RelistenNormalization/bin/Debug/net10.0/RelistenApi.dll`
- `tools/RelistenNormalization/bin/Debug/net10.0/RelistenNormalization.dll`

The adapter verifies source/archive fingerprints and the saved export's DLL hashes.
This binds the declared source snapshot and binaries; it does not independently
prove compilation from source. It also authenticates the frozen labels, original
fields, exclusion receipt, reservation index, selection and complete exported
results/audit bytes. Every successful Luna answer must bind to the retained request
for that one field, without added context, and to its cached response. No label
answer is passed to a learner as an input feature.

Use the existing locked environment under
`local-dev/normalization-data/venue-matcher-environment-v1/`; no installation is
needed. If running from another checkout, use the absolute path to that environment.
Replace the comparison/code locations and hash placeholders below with the retained
export and receipt. Hash arguments must come from the reviewed artifact copies.
`--source-root` locates the reservation index pinned by its receipt;
`--code-root` locates the archived source and binaries.

```sh
/path/to/primary-checkout/local-dev/normalization-data/venue-matcher-environment-v1/bin/python -B \
  tools/RelistenNormalization/matched_credit_experiment.py \
  --labels /path/to/primary-checkout/docs/research/2026-10-17-full-normalization/training/credits-development-v1/labels.jsonl \
  --receipt /path/to/primary-checkout/docs/research/2026-10-17-full-normalization/training/credits-development-v1/receipt.json \
  --fields /path/to/primary-checkout/local-dev/normalization-data/2026-10-17-cross-artist/training-development-credits-v1/fields.jsonl \
  --labels-sha256 5f2eb545f8d5d66b4750d74e883cc179e673e4775f5df9207fac027069599c12 \
  --comparison /path/to/new-key-free-export \
  --comparison-sha256 "REPLACE_WITH_MANIFEST_SHA256" \
  --code-receipt /path/to/current-code-receipt.json \
  --code-root /path/to/archived-code \
  --code-sha256 "REPLACE_WITH_CODE_RECEIPT_SHA256" \
  --exclusions /path/to/primary-checkout/docs/research/2026-10-17-full-normalization/training/credits-luna-development-v1/label-sensitivity-v2.json \
  --exclusions-sha256 "REPLACE_WITH_EXCLUSION_RECEIPT_SHA256" \
  --reservation-receipt /path/to/primary-checkout/docs/research/2026-10-17-full-normalization/training/people-development-v1/reservation-receipt.json \
  --source-root /path/to/primary-checkout \
  --output /path/to/new-ignored-output
```

### Read the result

The primary metric is a field-level set of **exact literal gold name versus emitted
display name, plus role**, limited to Taper and Transferrer. No entity decoding,
whitespace folding or identity normalization makes names agree. Boundary diagnostics
use explicit model/actor anchors over original Unicode code points. A supporting
phrase is not searched for a convenient name span. Raw/display differences and
correct raw anchors with differing display names remain separate diagnostics.

`full_resolved_priority` retains the entire resolved denominator and each method's
available field/assertion denominator. `shared_luna_available_priority` compares all
four methods only where Luna has usable output. With the currently retained labels
and final key-free cache coverage, the expected denominators are 84 fields/68
assertions and 83 fields/67 assertions, respectively; the command computes them
from authenticated inputs. It does not force data to fit those totals. Unavailable
Luna results never become empty predictions or hidden successes.
Local unresolved clauses also remain in each field's diagnostics and in aggregate
outcome counts, even when its emitted name/role set is empty.

Other-role and boundary diagnostics remain visible. Luna's all-role score is marked
not evaluated because its schema supports only the two priority roles. Incomplete
and excluded fields retain predictions and known-positive recovery; extra output in
those fields is unscored. Exact-name grouping is a leakage precaution, not verified
identity grouping. These drafts cannot establish unseen-identity, temporal,
provider-format or release accuracy, and they authorize no routing or policy change.

A run writes `splits.json`, `models-fold-N.json`, `predictions.jsonl`, the artifact
hashes and a pending report. It rechecks every input/source snapshot after writing
artifacts, then publishes `report.json` without overwriting an existing file. Failure
or interruption leaves diagnostics without a completed report. Recovery uses a new
output directory. The pending report remains as the publication checkpoint; only
`report.json` marks completion. The original labels and retained exports are read-only.

Run the synthetic behavior checks in the locked environment:

```sh
PYTHONPATH=tools/RelistenNormalization /path/to/locked-environment/bin/python -B -O \
  -m unittest test_credit_tagger_experiment test_credit_logistic_model test_matched_credit_experiment
```

These tests fit synthetic inputs only. They verify masks in fitting, grouping through
an excluded field, fold-local vocabulary, Unicode/HTML spans, unsupported/unavailable
outcomes, source and label leakage, retained-response binding, late input changes
and preservation of existing outputs. They do not estimate real-data accuracy.

### Matched development result

The [retained matched run](../../docs/research/2026-10-17-full-normalization/training/credits-development-v1/matched-experiment-v1.json) completed all five grouped folds. On the 83 jointly available resolved fields, the draft labels contain 67 taper/transferrer assertions. The current rules recover 60 with four extra predictions and seven misses. The perceptron recovers 40 with ten extra predictions and 27 misses; logistic regression recovers 36 with 16 extra predictions and 31 misses. Luna recovers 64 with three extra predictions and three misses.

Luna corrects three whole-field rule errors and introduces no error in a previously correct field on that shared denominator. Both methods have errors in three fields. The full resolved population contains 84 fields and 68 assertions; Luna is unavailable for one of those fields. Sixteen incomplete or excluded fields remain outside fitting and complete scoring.

These results keep the current rules as the local baseline. They do not establish that small models cannot help: the training population is small, contains few transfer credits and remains based on draft labels. Expand independently checked prose and format coverage before selecting weights or fallback routing. The key-free export adds no requests or spend; the existing extraction job remains at 200 admitted attempts and $0.0146575 in configured accounting.
