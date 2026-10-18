# Matching comparisons: 18 October 2026

The completed comparisons cover native credit extraction, cross-artist people,
and venues. Luna improves credit extraction mainly on Local and Phish.in notes;
people and venue identity evidence still needs broader independent support.
These are exposed development experiments. None establishes release accuracy,
an acceptance threshold, or authority to publish automatic matches.

## Credits: more recovered names, uneven gains and coverage

The [600-field comparison](../training/credits-native600-v1/README.md) retains
verified native inputs from 302 artists. On 343 shared, completely labeled fields,
Luna finds **226/242** name-and-role assertions with **nine extras**; rules find
**178/242** with **12 extras**. Luna makes 41 previously incorrect whole fields
exact and introduces six new field errors. The two small extractors, fitted on
the earlier 84 fields without new fitting here, perform worse than rules.

Local and Phish.in supply 34 of the 41 corrections across only two artists. On
the 250 shared Archive fields, Luna recovers one more assertion and makes two
more false assertions than rules. Routing needs format-specific evidence.

Only 457 fields have complete labels. Across all 600 fields, 402 have usable Luna
outputs, 124 whole fields were excluded from requests because they contain
contact-like text, 69 have unusable responses and five are empty. All remain in
coverage. The job records 453 attempts and **$0.0518212** configured token cost,
with cross-job cached cost separate. This measures extracted credits, not global
person identity. Investigate unusable responses and new errors, broaden supported
prose cases across artists, then compare the changed methods against this frozen
baseline.

## People: complete execution, limited identity truth

The new packet contains **200 relationship questions**, 162 recording contexts
and 156 artists. Independent source adjudication resolves **54 same, 32 different
and 114 insufficient**. Positive judgments include scoped recording handles and
projects; they do not establish natural-person identity, account ownership or a
recording role on another source.

The public C# projection and matcher produce 298 source views and 566 complete
comparisons. Full unordered source-view deduplication leaves **200 relationship
units**, not 566 identity labels. Both unchanged, previously fitted models give
the same result at the diagnostic one-half cutoff:

| Model | True positive | False positive | False negative | True negative |
| --- | ---: | ---: | ---: | ---: |
| Logistic | 53 | 0 | 1 | 32 |
| Boosted tree | 53 | 0 | 1 | 32 |

That is **85/86 determinate questions**, with all **114 unknowns** outside error
counts. The common miss is a source-explicit alternate performing/project credit
that this credit-context projection leaves name-only. Rules retain candidates
for 53/54 resolved positives but support no identity rule; candidacy is not an
accepted link.

The 200 units form **28 connected groups**; only 13 contain determinate answers,
and two supply **60/86** determinate questions. There are **no same-name labeled
negatives**. Both models classify 82 unknown questions diagnostically as same,
without evidence that those are correct. Zero observed false positives therefore
does not establish catalog merge precision. The
[source-view guide](../training/people-adjudicated-training-v1/README.md)
explains the feature and relationship-unit contracts.

## Venues: classification and retrieval are separate results

Independent adjudication of the existing **34 pair questions** gives **16 same,
8 different and 10 insufficient**. Rescoring saved predictions yields
**13 TP, 2 FP, 3 FN and 6 TN** for both models on the 24 determinate pairs:
precision 13/15 and recall 13/16 at the diagnostic cutoff. The 24 pairs occupy 19
frozen grouping components, not 19 independently sampled physical venues.

This is not a uniformly held-out comparison: 11 previously resolved pairs use
held-component scores; 13 newly resolved pairs use saved final-model scores.
No refitting occurred. Unknowns remain unknown, and missing historical dates
cannot be supplied by a current address. See the
[venue experiment](../../../../tools/RelistenNormalization/venue-matcher-experiment.md)
and [review-context contract](../../../plans/active/2026-10-17-full-normalization/venue-review-context.md).

The separate retrieval run indexes **216,331 catalog venue observations**,
including duplicates of premises. Exact normalized names and character TF-IDF
each return the specific known counterpart in **23/32 directions at top 100**.
Those directions come from 16 exposed positive pair questions, with one shared
endpoint. This measures raw counterpart-row retrieval, **not physical-entity
recall**: another observation of the same premises may already be returned.

The **600 new queries from 600 artists remain unlabeled**. Exact names leave
166 empty pools; TF-IDF leaves none, but produces very broad pools and rank-100
ties for 504 queries. More candidates do not establish better identity matching.

A follow-up groups exact raw name/location observations for retrieval only. On
the same 32 exposed directions, TF-IDF returns 28 counterpart rows when allowed
100 groups, compared with 23 at 100 individual rows. But those groups expand
to more rows: a row-only search with the same expanded count also finds 28.
There is no measured benefit at the same expanded row budget. Grouping has not been added to the
production matcher.

An additive read-only catalog capture supplies 1,954 show, source and setlist
association rows for 594 of the 600 queries. All first 60 review queries have
associated rows. These dates and display strings help reviewers investigate
historical names; they do not establish date precision, premises identity or
equality between a source venue and its linked show venue.

## Next experiment and retained evidence

Independently label the new venue queries with premises, room and historical
context, then measure identity-equivalent candidate recall separately from row
coverage and matcher errors.

The next people packet contains 162 questions across 95 artists, including 151
cross-artist questions; two independent source readings are in progress. It is
38 short of the planned next 200. Expand supported identity evidence across
additional groups, especially same-name negatives and alternative aliases.
Compare rules, frozen models and Luna on the same completed contexts, including
apparent successes and unknowns. This packet includes no new people Luna result.
Keep development exposure explicit and reserve untouched evaluation separately,
as required by the [evaluation strategy](../../../plans/active/2026-10-17-full-normalization/evaluation-strategy.md)
and [current milestone](../../../plans/active/2026-10-17-full-normalization/evaluation-milestone.md).

Authenticated ignored evidence under `local-dev/normalization-data/`:

- People: `2026-10-18-people-expansion-comparison-v1/method-report/manifest.json`, SHA256 `c314c206993a2af1e47b81c3223de021b977913bfdb43870628f2535f7d32b6a`.
- Venue rescore: `2026-10-18-venue-adjudicated-rescore-v2/report.json`, SHA256 `60116ee10ba8016f48b645aa70465c5a940541d0f666db449c4c63c7ec3a4faa`.
- Full venue retrieval: `2026-10-18-venue-full-catalog-retrieval-v1/manifest.json`, SHA256 `cff31ed25db944d798f03dc2231d634fbf9bb491790a38a442babea62ad47dc2`.
- Grouped venue retrieval: `2026-10-18-venue-grouped-retrieval-v1/manifest.json`, SHA256 `acc1bf01510ed240fa3daf7e9b323f984689f16a23bce505f9c73a8b68eaf5f5`.
- Dated venue context: `2026-10-18-venue600-dated-context-v1/manifest.json`, SHA256 `944c0bc7246565aecdb58900adde704bdfc3be815d5827c110b9240d6f94e4d4`.
- Next people packet: `2026-10-18-people-native600-expansion-v1/manifest.json`, SHA256 `eb9bd16d0b3f3f3654030d1974c10d4836ec1ff2cedeae3a9ce241ce4c0b0128`.
