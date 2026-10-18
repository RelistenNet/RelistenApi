# Source aliases improve the people matcher

Adding source-supported aliases reduces both trained models' errors from six to
two on the same 81 resolved development comparisons. Four documented alias pairs
now agree through the shared actor projection. The remaining misses are
Jon Cunningham/Corn Mo and Andy Moresi/Hot Dog, whose relationships appear in
musician-roster text outside the current recording-credit grammar.

The [receipt](../../scale/evidence/people-source-context-comparison-v1.json) pins
the final runtime, source snapshots, model outputs and verification. The
[exporter guide](../../../../../tools/RelistenNormalization/people-source-context.md)
defines the new command and attachment contract. The
[original comparison](../people-matcher-comparison-v1/README.md) remains unchanged.

## What changed

The exporter runs `RecordingCreditExtractor` on complete authenticated original
fields, then binds each named mention to an exact actor anchor or explicit alias
reference. Both raw and retained credits use `PeopleActorProjection`. Labels and
review rationales do not supply aliases or other features.

Of 246 original mention records, 188 join actor anchors and four join explicit
aliases. Forty-eight remain name-only with an explicit missing-actor reason; six
unsupported expressions remain visible. These are mention records, not distinct
people. All 158 candidate edges remain: 155 support model scoring and three are
explicitly unsupported. Grouping remains 82 components over all 168 questions.

Neither the 55 separately reviewed questions nor the six controls entered this
fit. This cohort supplies no attached contact or profile facts. Both corresponding
feature columns remain zero, so the measured benefit comes from alias-aware name
agreement. The separately adjudicated questions will test contact/profile-based
actor selection; this run does not evaluate those signals.

## Results and compatibility

Both models retain two false negatives and zero false positives against the
original drafts. Logistic mean held-out draft log loss falls from 0.225461 to
0.137319; boosted-tree loss falls from 0.287898 to 0.214425. The 77 unknown edges
remain outside fitting and losses. These diagnostics establish neither catalog
accuracy nor a publication threshold. True cross-recording positives still cover
one Aadam Jacobs project family.

All 102 focused C# cases and 34 Python cases pass. The trained final logistic model
matches the public C# evaluator exactly on 155 supported edges, with three explicit
skips. That comparison uses the final model, separately from held-component
predictions. Its fixture cannot match or create an identity.

The final runtime also reproduces all 158 original name-only comparisons byte for
byte. Sharing the five-value conversion between exporters leaves `baseline.jsonl` and
`context-diagnostics.jsonl` unchanged. The retained-credit
projection keeps its existing version and behavior. This experiment issues no
policy or evaluation attestation.

## Next work

Add the [separate adjudicated development questions](../people-identity-adjudication-v1/README.md)
through source-only reference selectors and target-only answer tables. A contact
or profile must select zero, one or several actors from its exact original source;
the reviewed identity answer cannot choose a name. Preserve unattached and
ambiguous references. Scope raw actor reference UUIDs by original evidence and
field, because identical raw text can produce the same local UUID in unrelated
recordings.

Combine all grouping links before fitting, resolve duplicate or conflicting
projected pairs explicitly, and retain complete declared candidate pools. Broader
identity families, supported hard negatives and untouched evaluation remain
required by the [evaluation strategy](../../../../plans/active/2026-10-17-full-normalization/evaluation-strategy.md).

Exact outputs remain under ignored
`local-dev/normalization-data/2026-10-17-cross-artist/training-development-people-v1/source-context-comparison-v3/`.
The earlier pre-cleanup run and failed runtime-copy preparation remain separate.
No provider request, model API call or accepted-identity write occurred.
