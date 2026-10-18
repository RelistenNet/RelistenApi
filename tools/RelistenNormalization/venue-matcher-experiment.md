# Compare two trained venue matchers on development drafts

Both trained models score the Oakland and Boulder Fox Theater pair as a likely match, although the draft identifies separate premises. This first experiment makes the lexical approach's limit visible: matching names can outweigh different raw city labels. It does not establish matching accuracy or select a production model.

`venue_matcher_experiment.py` fits L2 logistic regression and gradient-boosted trees on 11 resolved development pairs, then scores 23 unresolved pairs without using them as negatives. It writes the fitted coefficients, trees, exact splits, feature provenance and predictions. No normalization policy is activated, and no catalog data changes.

## What enters the models

The [venue label bundle](../../docs/research/2026-10-17-full-normalization/training/venues-development-v1/README.md) contains 34 draft pair judgments from 67 retained rows across 66 artists. Six pairs are proposed matches, five are proposed different venues and 23 lack sufficient evidence. All tasks and both resources belong to the development artist and family partitions. Only two proposed matches have separate blinded review; no proposed different-venue pair has that review. Independent adjudication remains pending.

All 67 rows contain a name and raw location wording. Coordinates are null; historical names and performance dates are absent. Six proposed matches have identical names. Four proposed negatives have different names in one city; the fifth has identical names across two cities. This small, selected set does not represent the full matching problem.

The six features are exact normalized name agreement, name token Jaccard similarity, name character-trigram Dice similarity, presence of both meaningful raw locations, exact normalized raw-location agreement, and raw-location character-trigram Dice similarity. Character trigrams include two boundary markers on each end. The versioned lexical key uses NFC Unicode normalization, lowercase letters/digits/combining marks, and spaces between other character runs. It retains diacritics and does not expand abbreviations or resolve addresses.

Blank names fail before fitting: these six features cannot distinguish an absent name from two disjoint names. Missing raw locations remain allowed. Blank location wording and the literal normalized placeholder `Unknown Location` are missing; two missing labels do not become an agreement. Each exported row also retains separate missingness flags.

Raw location similarity is **not resolved geographic identity**. It never supplies `MatchingVenueLocation.LocalityKey`, country, address, physical venue kind or performance date. At runtime the wording is available from imported claims and candidate name/location facts, including `CandidateVenueLocationFact.OriginalLocality`. This experiment compares individual imported observations; it does not establish handling of complete canonical name/location histories or candidate retrieval recall.

Only those six lexical values enter fitting. UUIDs, artist names, official-source excerpts, label rationale, candidate-selection reasons and split-hypothesis names stay outside the feature matrix. Official corroboration supports draft judgments, not automatic inputs that the catalog rows do not contain.

## Grouping and fitting

The experiment calls the bundle's validator and `build_split_components` on **all 34 tasks before selecting resolved training rows**. Resource/input families, venue hypotheses and the additive Camp Kee-Wanee and 8x10 links produce 28 components. The 11 resolved pairs occupy 10 components; the Brixx/Mystic negative and Mystic/Mystic positive stay together. Unknown tasks can connect resolved tasks and cannot be dropped before splitting. These are known-context development groups, not a guarantee that every undiscovered venue alias is isolated.

The fixed models use seed 173:

- Logistic regression uses `C=1`, `l1_ratio=0`, the L-BFGS solver, an intercept and a 1,000-iteration bound.
- Gradient boosting uses 20 depth-one trees, learning rate 0.05 and a minimum leaf size of two.

No parameters or acceptance thresholds are tuned. Leave-one-resolved-component-out fits use nine or ten resolved pairs. An unknown pair sharing a resolved component uses that component's withheld model. Components containing only unknown pairs use the final model fitted on all 11 resolved pairs, because their component contributes no training target. That final model is stored separately from fold models.

`insufficient_evidence` has target `null`. Unknown pairs enter neither fitting nor loss/error denominators, and receive no binary classification. A score at or above 0.5 is retained as a diagnostic count, not a positive identity assertion. The 0.5 cutoff for resolved draft disagreements is fixed for inspection; it is not a publication threshold. Scores are uncalibrated development outputs.

## Completed development runs

The completed comparison with the actual C# baseline is in the ignored directory:

```text
local-dev/normalization-data/2026-10-17-cross-artist/training-development-venues-v1/logistic-boosted-with-baseline-v1/
```

Both models disagree with one withheld draft target: Fox Theater, Oakland versus Fox Theater, Boulder. Logistic regression scores it 0.86347 and boosting scores it 0.85416. Draft mean log losses are 0.47556 and 0.44401 respectively. These values compare fit to draft labels; they do not establish accuracy or make boosting a winner.

Both models assign scores at or above 0.5 to 14 of the 23 unresolved pairs. Those are not 14 false positives: the pairs have no resolved target. They demonstrate why a lexical score cannot replace missing premises, room, historical-date or competing-candidate evidence.

The actual C# matcher export has 25 `needs_evidence` comparisons and nine `not_candidate` comparisons, with no automatic matches or active policy. The original pre-baseline fit remains unchanged in the adjacent `logistic-boosted-v1/` directory. The script ingests the actual export through `--baseline`; it never imitates that matcher in Python. The exporter supplies names-only comparisons with kind, geography, parents, dates and identifiers unknown. `needs_evidence` is an abstention, and `nameTokenSimilarity` is a retrieval diagnostic, not a negative label or probability. Baseline input hashes and exact task/evidence coverage must match the frozen bundle. The experiment retains the original baseline JSONL and manifest unchanged when supplied.

## Environment and reproduction

The run used Python 3.14.6 and scikit-learn 1.9.1. That release and its Python requirement were checked against [official PyPI metadata](https://pypi.org/pypi/scikit-learn/1.9.1/json) and the [official installation guide](https://scikit-learn.org/stable/install.html). The macOS arm64 CPython 3.14 wheel SHA-256 is `8893bc6331f60f18d4ac75e12ed356e2dcf6a564bf767918b5b7ca54c8c8be49`.

The isolated environment is under ignored `local-dev/normalization-data/venue-matcher-environment-v1/`. Its `install-report.json`, `upstream-verification.json` and `requirements.lock.txt` retain installation evidence and dependency versions. The run used NumPy 2.5.4, SciPy 1.18.1, joblib 1.6.0, narwhals 2.27.1 and threadpoolctl 3.7.0. These packages are experiment dependencies, not API project dependencies.

To create the environment from the repository root:

```sh
python3 -m venv local-dev/normalization-data/venue-matcher-environment-v1
local-dev/normalization-data/venue-matcher-environment-v1/bin/python -m pip install \
  --only-binary=:all: --index-url https://pypi.org/simple \
  scikit-learn==1.9.1 numpy==2.5.4 scipy==1.18.1 joblib==1.6.0 \
  narwhals==2.27.1 threadpoolctl==3.7.0 \
  --report local-dev/normalization-data/venue-matcher-environment-v1/install-report.json
```

Run with a **new** output directory. The requested pins are verified before validation; training examples are built from captured bytes checked against the manifest. A bundle change during validation is rejected. Input source/field hashes, the validator hash and requested pins remain in the report.

```sh
local-dev/normalization-data/venue-matcher-environment-v1/bin/python \
  tools/RelistenNormalization/venue_matcher_experiment.py \
  --frozen-manifest-sha256 c54b6c16340c23b767cfffbab9d25718782eaeeb92bc551466c2a00bf139bc94 \
  --frozen-labels-sha256 5108d963cecd7447c6f7eb5d2c483a21e31f62a1e7fcdbaea61f1ecb677e6f20 \
  --frozen-split-links-sha256 3141d0fb22b913e806c2d847fbeb3aea5c6d00d341742b13af201ed3f1521028 \
  --install-receipt local-dev/normalization-data/venue-matcher-environment-v1/install-report.json \
  --baseline local-dev/normalization-data/2026-10-17-cross-artist/training-development-venues-v1/csharp-baseline-v1 \
  --output /path/to/new-development-output
```

The baseline argument requires a completed actual exporter result. A mismatched bundle, output hash, task set, evidence identity, projection or policy-bearing comparison is rejected. If the argument is omitted, the report explicitly records the baseline as pending.

The output files are:

- `features.jsonl`: six values, field hashes, original evidence references and explicit missingness per task.
- `splits.json`: complete held-component and resolved-training task membership.
- `models.json` and `final-models.joblib`: fold/final coefficients and trees, plus the reloadable final estimators.
- `predictions.jsonl`, `unknown-predictions.jsonl` and `errors.jsonl`: all scores, a separate unresolved subset, and resolved draft disagreements.
- `environment.json`, `artifacts.json` and `report.json`: versions, artifact hashes, provenance, exact counts, diagnostics and limits. An optional baseline adds unchanged `baseline.jsonl` and `baseline-manifest.json`.

`report.json` is renamed into place last. A failed fit or write can leave partial files but no completed report. Existing directories are never overwritten; investigate the failure and retry in another directory. None of these artifacts permits publication or accepted identity changes.

Run the 12 focused tests with:

```sh
local-dev/normalization-data/venue-matcher-environment-v1/bin/python -m unittest discover \
  -s tools/RelistenNormalization -p 'test_venue_matcher_experiment.py' -v
```

They cover Unicode, missing locations, blank-name exclusion, review-metadata leakage, unknown links and additive split hypotheses, held-out input rejection, frozen-file mutation, unknown training exclusion, saved estimator replay, baseline provenance, unusable folds and failed-write recovery.

The completed comparison records experiment source SHA-256 `255a65557b094e6f5d7e35615a9097756e0e5100eb631ba10d8298bbef8ed80c` and report SHA-256 `a662c6efeb6389682d3fb51978135324de9968a75e516b4200915efda12e4328`. Its unchanged actual C# baseline has output SHA-256 `2882c3bf92366d6f6b1286b72f03d1aa5d9cea461f3cbddc99f947bc22fab502` and manifest SHA-256 `4f76fb98ef63aa973766b6fd8dfea10895e3e67efa53623ceeb8326f5fe7e95b`. Assembly fingerprints remain in the copied baseline manifest and report.
