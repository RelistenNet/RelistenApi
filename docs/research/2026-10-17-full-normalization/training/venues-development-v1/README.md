# Draft venue identity labels for development

This bundle contains 34 actual pair judgments from retained Relisten catalog
venue rows: 6 proposed matches, 5 proposed different venues, and 23 unresolved
pairs. The 67 resources cover 66 artists. Every selected task belongs to the
`development` family partition, and both venue resources belong to the
`development` artist and family partitions.

These labels support initial matcher experiments and independent review. They
do not measure accuracy or authorize automatic publication. All original judgments are
Codex drafts. A second Codex reviewer independently labeled a six-pair subset
before opening those drafts; independent adjudication remains pending. Use only
resolved, independently checked labels for a reported accuracy result. Keep
`insufficient_evidence` separate from different-venue examples.

## Read and review the labels

`labels.jsonl` preserves each original task ID, original physical line number and
SHA-256, both resource IDs, and an `answer` containing the decision, evidence,
reason, reviewer status, and adjudicator status. The original candidate reason
is retained for selection analysis; it does not establish the answer.

`evidence.jsonl` contains only the name, location, historical-name field when
present, coordinates, artist, resource identity, and provenance needed for these
judgments. Each string has a SHA-256 and an exact half-open span measured in
Unicode code points: `text[start:end] == quote`. These are spans of the decoded
JSON field, not offsets into the JSONL byte stream.

`primary-sources.jsonl` retains 18 venue-operator sources with URLs, observation
time, and short exact text fragments. Fragments in one source need not be
adjacent on the page. Their hashes cover the retained UTF-8 fragments, not an
unretained full HTTP response. The labels cite the relevant source IDs.

`blind-review.jsonl` preserves the second reviewer's six judgments unchanged.
`review-comparison.json` records the comparison: all six decisions agree,
comprising four unresolved pairs and two proposed matches. The hash-ranked
subset contains no proposed different-venue pair. The second reviewer used
the same retained evidence, so agreement is not an accuracy estimate or
independent verification of historical recording dates and rooms. None of the
34 original label decisions or pending adjudicator statuses changed.

`manifest.json` records input and output file hashes, selection counts, and
limits. Full selected original rows remain in the ignored local supplement:

```
local-dev/normalization-data/2026-10-17-cross-artist/training-development-venues-v1/
```

Further reviewers should receive the selected original tasks, minimal source
fields, and primary-source excerpts without `answer` or candidate reasons, then
record their own judgment before comparing decisions. Review every proposed
match and different-venue decision first. Resolve disagreements explicitly;
neither model agreement nor shared spelling supplies missing evidence.

## What the judgments mean

The target is a physical performance venue. A business name can move to another
building, and one building can contain several rooms. An event, festival, park,
or campsite label can identify a site without identifying the stage. Equal
names and cities are useful candidate features, but they do not settle these
cases.

The proposed matches combine distinctive retained name/location evidence with
a venue operator's premises description. They identify the named venue at that
scope; they do not establish a historical recording's date or room. Proposed
different-venue decisions have corroborated separate premises. The unresolved
pairs retain the reason a physical assignment remains unsupported. For example,
Nublu's operator lists two Avenue C addresses, and Showbox's operator lists two
Seattle premises. Generic catalog labels cannot select between them.

The venue rows contain no performance date. Catalog `created_at` and `updated_at`
values were not used as event dates. Historical assignments still need dated
recording or event evidence. Primary addresses observed today do not by
themselves prove where a historical performance occurred.

## Selection and limits

There are 65 eligible development-only venue pairs in the unchanged annotation
pack. This bundle selects 34 to cover exact names, spelling variants, different
cities, separate venues in one city, missing locations, possible historical
aliases, and event/site or room ambiguity. It is a judgment sample, with no
catalog prevalence claim. All resources come from the catalog snapshot; this
bundle does not establish upstream provider coverage.

Before splitting any derived experiment, form connected groups from all 34
pairs, including unresolved pairs. Use each label's `split_group_ids` and the
additive links in `split-links.jsonl`. Then select resolved pairs for fitting.
The validator's `build_split_components` function performs that grouping.

The additive links keep Camp KeeWanee/Camp Kee-Wanee context together across
Wormtown and StrangeCreek tasks, and 8x10/8x10 Club context together across two
Baltimore tasks. Exact retained names support each link. These links control
only data separation: `identity_assertion` and `matcher_feature` are false.
They assert no shared room, physical premises identity, or event. They change
none of the 34 label decisions and must not become matcher features.

The links reduce the complete bundle from 31 connected components to 28.
Selecting the 11 resolved pairs afterward leaves 10 represented components.
Unresolved pairs can connect tasks and therefore must remain in the grouping
step even when excluded from model fitting. Extend the groups with subsequently
adjudicated venue identities and aliases. A negative pair can also connect two
venue groups; keeping that component together is conservative. Do not promote
these development resources to a scored validation or test set.

An initial schema-sampling command exposed one validation setlist row before
filtering. That row was outside this venue task and was not used for selection
or labels. No held-out venue content was used. This exposure is recorded so a
future setlist evaluation can account for it.

Run the validator from the repository root:

```
python3 docs/research/2026-10-17-full-normalization/training/venues-development-v1/validate_labels.py
```

It checks hashes, partition eligibility, original task/resource correspondence,
exact field spans, source references, class counts, and the saved blind-review
comparison, and split-only link grouping. It cannot establish
venue identity; that requires independent evidence review.

The validator raises `ValueError` for failed checks, including when Python runs
with `-O` or `PYTHONOPTIMIZE=1`. Regression checks use temporary bundle copies;
they leave annotation inputs and labels unchanged:

```
python3 -O -m unittest discover -s docs/research/2026-10-17-full-normalization/training/venues-development-v1 -p 'test_validate_labels.py' -v
```

Those checks cover a valid optimized run, an out-of-range Unicode span that
Python slicing would otherwise clamp, a held-out partition, changed source
bytes, and U+2028/U+2029 separators inside physical JSONL records.

The grouping regressions also verify that uncertain shared context stays in one
component and that the CLI rejects an omitted links file even after its outer
hash receipt is refreshed. Those checks prevent a future matcher experiment
from silently ignoring the additive grouping requirements.
