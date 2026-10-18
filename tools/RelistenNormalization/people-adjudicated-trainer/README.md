# Compare the combined people cohort without counting repeated pairs twice

This separate trainer consumes the prepared development package and its actual
C# adjudicated baseline. It retains every declared pair for inspection, but fits
and measures one unit per unordered source actor pair. The original names-only
and source-context commands remain unchanged.

The current real export has
219 declared edges, of which 167 have executable comparisons. These counts
describe that retained export, not independent identity labels or matching
accuracy. Only 12 of the 61 added pairs are currently supported by its strict
source projection; the missing source associations need separate investigation.
The first actual combined fit has 215 unordered pairs, including four repeated
declarations, and 92 eligible labeled units: 66 same-actor and 26 different-actor
judgments. Both models make two diagnostic half-threshold disagreements. These
development results do not establish release accuracy. Public C# score parity
must be checked separately against the final model.

## Source and target responsibilities

`--package-sha256` pins the exact preparation manifest. The trainer authenticates
all five prepared source/target artifacts and every upstream input recorded by
preparation, including the separate review-validation receipts. It does not
resample sources, change labels or rerun provider ingestion.

`--baseline-sha256` pins the actual C# export. Its manifest must name precisely
the preparation manifest and four source files. API and exporter assembly
snapshots are rehashed. Baseline comparisons, endpoint diagnostics, original
groups and source bridges are authenticated against their output hashes.
Five-feature vectors must equal the values in actual C# comparison DTOs. An
unsupported row must have null features and comparison; it cannot become an
all-zero vector.

Source endpoint keys, pair keys and groups are built before the trainer opens
the separate target artifact. The original 229 tasks remain in grouping,
including actor-kind tasks, unknown judgments and unsupported expressions.
Every exported source bridge must exactly match the actor references and task
memberships in the endpoint diagnostics. Shared unused inventory endpoints can
have a valid bridge with no task members; that bridge is retained and performs
no union.

For a projected endpoint, the pair key uses the original evidence ID, source
field and source-local actor reference UUID. A raw reference UUID alone cannot
join different sources or fields. For a name-only or unsupported endpoint, the
key uses its exact original selector coordinates. This fallback identifies a
source mention, not a certified person. It can leave equivalent actors apart
when extraction has not established their relationship; the report keeps that
limit visible.

An unordered pair key makes reversed declarations one pair. All tasks declaring
that pair are joined before targets are read. These duplicate-pair bridges,
original groups and source bridges establish the final development components.
No label, answer reason or model output selects a name or moves a task to a fold.

Target authentication is separate. Original draft targets must match their
unchanged original label rows. Agent-adjudicated targets must match the exact
question, decision and physical row SHA in the frozen adjudication file, and
that file must belong to the named completed review-validation receipt.
Physical row hashes exclude LF or CRLF terminators only; a lone final CR remains
part of the row. Unknown judgments retain JSON null and never become negatives.

## Independent fit units

The trainer records every unordered pair and all its declared rows in
`fit-units.json`. It applies this explicit policy:

- Conflicting resolved judgments exclude the pair from fitting and primary loss.
  Neither newer adjudication nor an original draft silently takes precedence.
- Multiple distinct supported five-feature vectors exclude the pair from fitting
  and primary loss. The trainer does not pick a direction, choose an actor view
  or average vectors. Every row remains available for diagnostic scoring.
- A consistent resolved pair with one supported feature vector contributes one
  training unit, regardless of the number of repeated or reversed declarations.
  At least one resolved row must itself have a supported projection.
- Unknown-only and unsupported pairs remain visible without entering fitting.
  If one row resolves a pair, an older unknown row still displays an unknown
  target. Its target is not rewritten.

This conservative policy makes the first combined comparison auditable. It
can omit useful alternate views; the retained rows and exclusion statuses show
the cost. A future change to use multiple views needs an explicit weighting and
evaluation contract.

Logistic regression and the boosted classifier reuse the established estimator,
portable JSON export and inference checks. The five runtime values retain their
existing semantics. Portable models therefore use the existing
`people-mentions-source-context-five-v1` model feature contract; the report
separately names the actual new input projection,
`people-adjudicated-source-endpoints-v1`, and pins its extractor/projector
versions and assemblies. The older two trainer contracts and source files are
not modified.

Every diagnostic score comes from a model trained without that row's entire
component, including unknown-only components. A fold whose remaining eligible
units lack both classes is explicitly unscorable. There is no fallback to a
model trained on the held component. A final model is also fitted to all
eligible units for portable-score parity, separately from held-component scores.

Primary model counts and loss use independent eligible units. The report also
shows actual C# rule/candidate diagnostics on those units. Rule candidacy is
retrieval behavior, not an accepted identity or a calibrated probability.
Diagnostic logit zero/sigmoid one-half comparisons do not activate publication
thresholds. No policy, canonical identity, merge, credit or creation behavior is
enabled.

## Outputs

| File | What to inspect |
| --- | --- |
| `fit-units.json` | Pair membership, conflicts, unsupported targets, multiple views, provenance counts and fit eligibility. |
| `folds.json` | Every task in each component and the independent units used for its held-component fit. |
| `models.json` | Final and fold-specific portable logistic and boosted parameters. Training IDs name pair units. |
| `declared-predictions.jsonl` | Every declared row, original target status, provenance, actual rule DTO and held-component model scores. |
| `independent-predictions.json` | One held-component prediction per eligible fit unit; the denominator for primary loss. |
| `question-pools.json` | Complete declared pools, including unknown or unscorable rivals. New reviewed singleton pairs remain pair-scoped. |
| `final-declared-scores.jsonl` | Final-model raw logits keyed by task ID and candidate endpoint ID, for public C# scorer parity. These are not held-component metrics. |
| `report.json` | Completion marker, counts, primary metrics, versions, input/output hashes, pinned environment and limits. |

Small inputs have exact retained copies; larger captures stay external with
byte/hash references. Originals, retained copies and output artifacts are
checked before the final report is renamed into place. A failed run may leave
partial files but must not leave `report.json`. Use a new ignored output
directory for every run.

## Root runbook

First run the synthetic tests with the existing pinned matcher environment:

```sh
RELISTEN_REPOSITORY="$PWD" \
  local-dev/normalization-data/venue-matcher-environment-v1/bin/python \
  -m unittest discover -s tools/RelistenNormalization/people-adjudicated-trainer \
  -p 'test_people_adjudicated_matcher_experiment.py' -v
```

Tests cover reversed/duplicate pairs, contradictory and multiple-view units,
source-local UUID scope, unused source bridges, unsupported projections,
unknowns, whole-pool coverage, target-independent grouping, exact row hashes,
retained-copy tampering and actual synthetic fitting across held components.
They use synthetic data; they do not open the real target packet.

Review source-only projection coverage before fitting. Keep unsupported examples
visible, then run the command with externally verified hashes:

```sh
local-dev/normalization-data/venue-matcher-environment-v1/bin/python \
  tools/RelistenNormalization/people-adjudicated-trainer/people_adjudicated_matcher_experiment.py \
  --root "$PWD" \
  --package "$PREPARED_PACKAGE" --package-sha256 "$PACKAGE_MANIFEST_SHA256" \
  --baseline "$CSHARP_BASELINE" --baseline-sha256 "$BASELINE_MANIFEST_SHA256" \
  --experiment tools/RelistenNormalization/people_identity_matcher_experiment.py \
  --experiment-sha256 "$EXPERIMENT_SOURCE_SHA256" \
  --install-sha256 "$PINNED_INSTALL_REPORT_SHA256" \
  --lock-sha256 "$PINNED_REQUIREMENTS_LOCK_SHA256" \
  --output "$NEW_PRIVATE_OUTPUT"
```

For the current retained inputs, `PREPARED_PACKAGE` ends in
`training-development-people-adjudicated-v1/prepared-v1` and the actual baseline
ends in `training-development-people-adjudicated-v1/csharp-baseline-v2`. Use the
retained manifests' verified hashes rather than computing a new trust anchor
from an unreviewed replacement. Retain exact executed source and stdout/stderr.
The command prints aggregate counts and a final report hash only.

Finally run the public C# people policy evaluator on the same supported baseline
comparisons with `models.json`'s final logistic `runtimeScorerParameters`.
Use diagnostic parameters that cannot accept or create identities. Compare raw
scores to `final-declared-scores.jsonl` by `(taskId,candidateEndpointId)` at
absolute and relative tolerance `1e-12`; unsupported rows must be explicit
skips. Pin that verifier source, actual API assembly, model file and baseline.
This checks runtime score parity. It does not establish identity accuracy or
authorize policy activation.
