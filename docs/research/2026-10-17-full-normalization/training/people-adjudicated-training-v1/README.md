# Prepare broader people matching evidence

The original 168 questions and 61 separately adjudicated questions now form one
authenticated development package. It preserves 229 tasks, 318 source endpoints,
227 complete source records and 219 declared identity edges. Source selectors
and identity targets are separate files; answers cannot choose the actor used
for a feature comparison.

The [receipt](../../scale/evidence/people-adjudicated-training-inputs-v1.json)
pins the actual package, verification logs and independent audit. The
[preparation tool](../../../../../tools/RelistenNormalization/people-adjudicated-training/README.md)
defines inputs, output schemas and the reproducible command.

## Implemented and verified

All 15 optimized-Python preparation cases pass. The actual preparation revalidates
the original source bundle and both final review receipts. Independent audit
rebuilds all source records, 367 exact selectors, complete candidate pools,
adjudication provenance and provisional grouping. Original labels and packets
remain unchanged.

The separate [source-reference API](../../../../../tools/RelistenNormalization/people-source-reference.md)
selects actors from exact contact/profile spans. It distinguishes attached actors,
competing references, unowned hints and absent extraction. All 17 new cases and
102 existing C# cases pass. These tests verify attribution mechanics; the combined
package has not yet passed through the actual C# feature exporter.

Targets contain 115 same-entity, 26 different-entity and 78 unknown judgments.
They include original drafts and correlated examples, rather than independent
identity samples. The controls add two cross-recording negatives and two
within-recording co-credit distinctions. The new reviews remain agent development
judgments, outside untouched release evaluation.

## Required before fitting

Export actual comparisons using only source records, selectors and declared
candidate pools. Preserve ambiguous and missing references. Then add source-local
actor and alias bridges to all 229 grouping tasks; the current 93 components are
explicitly provisional. Scope each reference by original evidence, field and local
UUID. Equal raw UUIDs across unrelated sources cannot connect identities.

Detect repeated or reversed projected pairs and conflicting targets before
fitting. Keep unknown labels outside binary targets. Report these outcomes and
declared pair scope; the reviewed pairs do not establish exhaustive catalog
candidate coverage. This package has no trained model or automatic-policy authority.

The ignored directory
`local-dev/normalization-data/2026-10-17-cross-artist/training-development-people-adjudicated-v1/`
retains `prepared-v1/`, its specification and exact verification artifacts.
