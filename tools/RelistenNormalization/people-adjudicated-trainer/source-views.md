# Train people matching from independent source views

The source-view trainer keeps every independently supported field view and
actual C# comparison. It fits one deduplicated relationship unit when all its
declared bags are complete, its targets are consistent and every numerical
feature vector agrees. It chooses no field priority, combines no contacts and
asserts no cross-field actor identity.

Use this mode to compare rules, logistic regression and boosted classification
on exposed development evidence. Use the [single-view comparison](README.md)
when the existing one-comparison-per-edge contract is required. Both commands
share authenticated input readers and portable five-feature fitting functions;
their candidate and fit-unit contracts remain distinct.

## Inputs and source ownership

The command accepts a hash-pinned `people-view-baseline-v1` export with projection
`people-independent-source-views-v1`. The TOP `manifest.json` must say
`completed` and authenticate the nested comparator and all view artifacts.
`comparator/manifest.json` alone is insufficient: the exporter can finish that
phase before view expansion fails.

The reader checks the source preparation manifest, four source artifacts,
API/tool snapshots and source versions. It opens separate targets only after
views, full pair keys, relationship units and fold components are fixed. The
[source export contract](../people-view-baseline-contract.json) defines the
serialized rows, version fields and bounds.

An eligible view retains one complete original variant's projection and facts.
It cannot enrich that record with another field's contacts or choose an unowned
actor hypothesis. Each selector contributes to at most one eligible view; a
complete endpoint covers every selector. Repeated references can share a view
within one endpoint when their full scoped actor keys agree. Different fields
remain distinct. Name-only views keep exact selector anchors and field hashes,
with no fabricated actor UUID.

Every declared candidate has a relationship bag, including candidates with no
eligible views. Its `edgeId` is a JSON-encoded array
`[taskId,candidateEndpointId]`; decode the array rather than split punctuation.
Each bag must contain every combination of its eligible left and right views.
The reader streams each leaf comparison, rejects duplicate or foreign keys,
checks its five values against the actual C# comparison DTO and verifies counts.
Partial source coverage can retain diagnostic leaves, but truncation cannot
make a bag complete.

The contract permits at most 100,000 views, 1,000,000 leaf comparisons and 128 MiB
per artifact. The trainer also limits each decoded leaf line to 1 MiB. It checks
expansion products and accumulated counts before reading leaves. Overflow or
incomplete coverage fails the run before target joining and report publication.

## Relationship units and targets

A full unordered actor pair includes both view keys. A grounded actor key
contains original evidence ID, field and source-local reference UUID. A name-only
key contains the evidence, field, field hash and exact selector anchor. Neither
naked UUID equality nor sharing one actor establishes duplicate-pair evidence.

Bags join when they share the same full unordered leaf pair. This is transitive:
if A overlaps B and B overlaps C, they form one unit even if A and C do not
overlap directly. All original tasks, source bridges and overlap memberships
enter fold grouping before labels are read. Actor-kind and unknown tasks remain
grouping bridges. This deduplicates relationship evidence; it does not merge
actors or create canonical identities.

Targets stay attached to declared bags. The target reader authenticates original
draft and agent-adjudicated provenance. An unknown bag remains unknown when
another overlapping bag has a resolved target. Leaves never inherit identity
targets: they are unlabeled observations, not extra positive or negative cases.

Each relationship unit has total fitting weight one. It can fit when every bag
is complete, no selector is genuinely ambiguous, resolved targets are consistent
and all leaf vectors have one numerical value. Missing, unattributed, partial,
ambiguous, conflicting and differing numerical views remain visible and
excluded. Unknown-only units do not become negatives.

Numerical agreement does not require rule agreement. Every actual rule DTO stays
in leaf output. If candidacy, disposition, supported rules or feature agreements
differ, the aggregate rule result is withheld. Identical five-feature vectors
can support numerical fitting without selecting a rule winner or identifying
different source views as one actor.

Logistic and boosted fitting reuse the established portable JSON and raw-score
checks. Each held component is absent from its training units; one-class folds
are explicitly unscorable. A separate final model fits all eligible units for
score parity. The report names the source-view projection; portable models use
`people-mentions-source-context-five-v1` feature semantics. No identity, credit,
merge, creation or policy activation follows from this experiment.

## Read the denominators separately

| Artifact | Denominator and purpose |
| --- | --- |
| `relationship-units.json` | One overlap unit, with its bags/leaves, exclusions, weight and rule aggregation. These units define primary fitting and loss. |
| `declared-predictions.jsonl` | Every declared edge and its own target. A numerical aggregate exists only for complete bags with identical leaf vectors. |
| `leaf-predictions.jsonl` | Every actual leaf comparison, full keys, rule DTO and held-component score. Targets are null with `no_leaf_identity_target`. |
| `independent-predictions.json` | One primary prediction per eligible unit. “Independent” means deduplicated fit units, not independent historical truth. |
| `folds.json` | All tasks, separate bag/leaf membership and training-unit IDs. |
| `question-pools.json` | Complete declared candidate pools. Leaf expansion does not multiply candidates. |
| `models.json` | Final and fold-specific portable parameters; training IDs name relationship units. |
| `final-leaf-scores.jsonl` | Final-model logits keyed by `(edgeId,leftViewKey,rightViewKey)`, with a stable `observationId`, for public C# parity. |
| `report.json` | Completion marker: task/bag/leaf/pair/unit counts, primary metrics, provenance, environment and limits. |

The nested single-view comparator stays separately authenticated; its projection
and null results are not replaced by bag aggregates. There is no leaf-level
identity accuracy metric. Rule disagreements have no aggregate winner. These
are exposed development judgments, not untouched release labels.

Small inputs have exact retained copies; large captures remain external byte/hash
references. Inputs, copies and output artifacts are rechecked before
`report.partial.json` becomes `report.json`. Partial files without that marker
are not a completed experiment. Use a new ignored directory for each run.

## Run and verify

Run synthetic tests in the pinned matcher environment:

```sh
RELISTEN_REPOSITORY="$PWD" \
  local-dev/normalization-data/venue-matcher-environment-v1/bin/python \
  -m unittest discover -s tools/RelistenNormalization/people-adjudicated-trainer \
  -p 'test_*.py' -v
```

Tests cover transitive/full-pair overlap, one-actor non-overlap, scoped references,
rule disagreement with identical vectors, partial/ambiguous coverage, targets,
candidate pools, TOP completion, bounds and separate prediction denominators.
They also protect single-view duplicate/unknown behavior and exercise actual
synthetic logistic/boosted fitting.

Run a fit with externally verified input and source hashes:

```sh
local-dev/normalization-data/venue-matcher-environment-v1/bin/python \
  tools/RelistenNormalization/people-adjudicated-trainer/people_source_view_experiment.py \
  --root "$PWD" \
  --package "$PREPARED_PACKAGE" --package-sha256 "$PACKAGE_MANIFEST_SHA256" \
  --export "$TOP_VIEW_EXPORT" --export-sha256 "$TOP_VIEW_MANIFEST_SHA256" \
  --combined-adapter tools/RelistenNormalization/people-adjudicated-trainer/people_adjudicated_matcher_experiment.py \
  --combined-adapter-sha256 "$SHARED_ADAPTER_SOURCE_SHA256" \
  --experiment tools/RelistenNormalization/people_identity_matcher_experiment.py \
  --experiment-sha256 "$FIT_FUNCTIONS_SOURCE_SHA256" \
  --install-sha256 "$PINNED_INSTALL_REPORT_SHA256" \
  --lock-sha256 "$PINNED_REQUIREMENTS_LOCK_SHA256" \
  --output "$NEW_PRIVATE_OUTPUT"
```

Retain the executed sources, invocation and stdout/stderr. Independently verify
counts, exclusions, unit weights, complete bag/leaf coverage and receipt hashes.
Verify final logistic logits through the public C# evaluator on each actual leaf
comparison, using a fixture that cannot accept or create an identity. Join full
leaf keys and compare to `final-leaf-scores.jsonl` at absolute/relative tolerance
`1e-12`. Held-component predictions use different models and cannot serve as
the final-model parity reference.

Source views can recover comparisons from separately attributed fields. They do
not repair missing extraction, decide actor co-reference or establish how much
additional training evidence a cohort provides. Measure those outcomes from
the retained run.
