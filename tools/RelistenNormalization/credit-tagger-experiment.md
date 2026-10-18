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
