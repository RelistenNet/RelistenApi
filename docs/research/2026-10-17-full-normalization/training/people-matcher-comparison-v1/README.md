# People matching: first real model comparison

Both trained models miss the same six explicit source-local aliases, including
“John Hermsen” and “down2earthlandscaper.” The current feature projection contains
names and artist context, but does not supply the source statements connecting
these names. Add supported alias and actor context before repeating the comparison.
Changing the model alone would leave this evidence unavailable.

The [comparison receipt](../../scale/evidence/people-matcher-comparison-v1.json)
pins actual C# export, model fitting and public C# scoring. The
[experiment guide](../../../../../tools/RelistenNormalization/people-identity-matcher-experiment.md)
defines commands, input contracts and recovery behavior.

## Inputs and results

Grouping all 168 original development questions before filtering produces 82
connected components. The 89 identity questions contain 158 candidate edges:
59 draft same-entity judgments, 22 draft different-entity judgments and 77 unknowns.
The other 79 questions concern actor kind; they supply no identity targets, but
their grouping links remain in the split.

The actual C# `IdentityCandidateMatcher.ComparePeople` supplies five numeric
features: name-token similarity, exact name, artist overlap, contact agreement
and profile agreement. This projection supplies names and artist context only.
Absent contact, profile, kind and alias evidence remains unavailable. Labels and
review rationales never become features. Three compound or unresolved actor
expressions remain explicitly unsupported.

The C# rules return 115 `needs_evidence` and 43 `not_candidate` outcomes. Logistic
and boosted-tree models fit 81 resolved draft edges, holding out complete
components together. Unknowns remain in candidate pools and reports, outside
fitting and error counts. One-class training folds are explicitly unscorable.

Both models produce six diagnostic false negatives and no false positives against
these drafts. All six failures concern explicit aliases. Mean held-out draft log
loss is 0.225461 for logistic regression and 0.287898 for boosted trees. These
results do not establish production accuracy, a threshold or a creation policy.

Only 11 same-entity edges actually span recordings. All concern one Aadam Jacobs
project family; there are zero resolved different-entity edges spanning recordings
and 59 unknowns. The question category `cross_recording_identity` contains 13 same
judgments and 61 unknowns because its candidate pools also include four
same-recording edges: two same and two unknown. Use actual edge scope when
assessing generalization.

## Verification and retained evidence

All 19 C# exporter/scorer cases and 24 Python experiment cases pass. The trained
final logistic model also runs through the public C#
`PeopleResolutionPolicyEvaluator`: all 155 supported comparisons match Python's
final-model raw scores exactly; three unsupported comparisons are skipped
explicitly. This verifies coefficient order and scoring behavior. Held-out fold
predictions use different fitted models and are not the parity reference.

The parity fixture grants no match or creation authority. Boosted-tree export
uses JSON-only inference for diagnostics; the current C# policy evaluator accepts
linear parameters. Choose runtime changes after comparing better-supported inputs.

Exact inputs, source copies, executed assemblies, splits, models, predictions and
logs remain under ignored
`local-dev/normalization-data/2026-10-17-cross-artist/training-development-people-v1/`.
The compact receipt exposes hashes and counts without copying raw contact fields
into tracked research. Original draft labels remain unchanged.

## Next implementation

Extract alias, contact and profile relationships from exact source passages, with
explicit actor attachment. Co-mentions do not establish an alias. A contact after
several names does not by itself belong to each person. Keep uncertain attachment
unresolved and retain supporting text.

Add the separate [adjudicated development questions](../people-identity-adjudication-v1/README.md)
through a versioned source/target adapter. Features must derive from source,
independently of answer files. Repeat rules, logistic and boosted comparisons over
complete candidate pools. Evaluate retrieval separately so omitted candidates stay
visible. Broader families, supported hard negatives and untouched generalization
suites remain necessary before calibration or activation; follow the
[evaluation strategy](../../../../plans/active/2026-10-17-full-normalization/evaluation-strategy.md).
