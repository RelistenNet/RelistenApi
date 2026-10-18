# Compare people identities across source views

The people development comparison now retains separately attributed source views
instead of requiring every field to identify the same local actor reference. Its
100 eligible relationship units contain 74 same-actor and 26 different-actor
judgments. Both trained models miss two positive judgments and make no
false-positive classifications at the diagnostic one-half cutoff. This is
working extraction, training and runtime integration; broader independent
identity evidence is still required before automatic acceptance.

The [source-view receipt](../../scale/evidence/people-source-views-v1.json)
pins the source export, models, execution records and independent audits. The
[trainer guide](../../../../../tools/RelistenNormalization/people-adjudicated-trainer/source-views.md)
defines the executable contract and commands. No identity policy is activated.

## Inputs and responsibilities

The authenticated development package preserves the original 168 tasks and 61
separately adjudicated questions: 229 tasks, 318 source endpoints, 227 complete
source records and 219 declared identity comparisons. Source selectors identify
exact name, alias, contact or profile spans. Identity answers remain separate and
cannot choose the actor used to construct features. Original packets and labels
remain unchanged.

The [preparation receipt](../../scale/evidence/people-adjudicated-training-inputs-v1.json)
and [preparation tool](../../../../../tools/RelistenNormalization/people-adjudicated-training/README.md)
cover source and review authentication. The
[source-reference API](../../../../../tools/RelistenNormalization/people-source-reference.md)
distinguishes attached actors, competing references, unowned hints and missing
extraction. Names, contacts and profiles are evidence for comparison, not global
identity keys.

Targets contain 115 same-entity, 26 different-entity and 78 unknown judgments.
They include original drafts and correlated examples. The added reviews are
agent development judgments; they are not untouched release evaluation. Only
two negative controls span recordings.

## Retain independent views without multiplying labels

The actual C# export retains 322 source views and 295 view-pair comparisons
across all 219 declarations. Each declaration forms a relationship bag: the
complete set of comparisons between its eligible left and right source views.
There are 175 complete bags, 17 partial bags and 27 bags with no executable
comparison. Partial and ambiguous source associations remain visible.

Actor-reference keys include their original evidence, field and local UUID.
Name-only views retain exact source coordinates. Neither field priority nor a
union of contact values creates a synthetic actor. All 229 tasks, source groups
and scoped bridges enter grouping before targets are read. Bags that share a
full unordered actor pair merge transitively; sharing just one actor does not
merge relationships.

The trainer retains 215 relationship units. It fits only complete bags whose
numerical feature vectors agree, with one total training weight per final unit.
Conflicting targets, differing features and genuine selector ambiguity remain
explicit exclusions. Unknown judgments stay unknown, and individual view pairs
do not inherit identity labels. Actual rule disagreement is retained separately
from numerical agreement; it cannot produce an invented rule winner.

## Results and verification

The 100 eligible units include 27 positive and two negative units spanning
artists. Another 71 units have only unknown judgments; 17 have incomplete bag
coverage, six have selector ambiguity and 21 have only unsupported bags. These
are relationship units, not 100 statistically independent identities.

Logistic mean log loss is 0.11621668562930668; boosted-tree loss is
0.19940404112347906. Both models retain two false negatives and zero false
positives at the diagnostic cutoff. The unresolved alias cases remain Jon
Cunningham/Corn Mo and Andy Moresi/Hot Dog. The cutoff is a comparison aid, not a
publication threshold.

All 47 Python cases, nine new C# export cases and 178 compatibility cases pass.
Independent audits verify source anchors, all Cartesian view pairs, target
provenance, component exclusion and portable scores. The earlier
[single-view comparison](../../scale/evidence/people-adjudicated-comparison-v1.json)
remains available separately: it has 92 eligible units, including 66 positive
and 26 negative judgments. Its seven behavioral artifacts replay byte for byte.
The additional views improve source coverage without adding identity judgments.

## Public runtime and remaining work

The public `PeopleResolutionPolicyEvaluator` scores all 295 final-model view
comparisons. Maximum raw-logit difference is 4.440892098500626e-16, within absolute
and relative tolerance 1e-12. All actions remain `review`; 43 noncandidate
rejections remain intact. The fixture cannot match or create identities.
Independent audit authenticates 126 retained runtime files. This parity result
covers the frozen source-view runtime, separately from held-component evaluation
and later owner-clock-index changes.

The [selection tool](../../../../../tools/RelistenNormalization/people-evaluation-selection/README.md)
passes 19 cases and preserves all 2,184 previously permitted original recordings
across 1,765 artists and 2,172 components. Its eight prior selection artifacts
and original record bytes remain unchanged. This is the development capture
foundation, separate from the fitted cohort. It does not yet prove that all
recordings of one credited actor stay in the same evaluation component. It cannot
calibrate the full catalog.

Broaden independent identity, artist, time and format coverage under the
[evaluation strategy](../../../../plans/active/2026-10-17-full-normalization/evaluation-strategy.md).
Improve source-supported alias extraction and compare selective Luna processing
on local successes as well as failures before selecting acceptance or fallback
routing policies.

The ignored directory
`local-dev/normalization-data/2026-10-17-cross-artist/training-development-people-adjudicated-v1/independent-views-v1/`
retains the authenticated export, trained models, public score checks and valid
independent audits linked by the receipt.
