# Full-catalog normalization analysis

The target is the complete normalization layer for Grateful Dead, Phish, Taper's Section and all current and future artists. People, venues, artist repertoires, cross-artist compositions, ordered occurrences and recording-file contents are required domains. Recording provenance in this release identifies tapers and transferrers; full ancestry and audio-family reconstruction are outside scope. The [execution plan](../../plans/active/2026-10-17-full-normalization/README.md) owns that delivery. This analysis establishes the real inputs and constraints; it does not claim that a census has resolved their identities.

The [real-data evaluation corpus](evaluation/README.md) adds 132 evidence records and 102 labeled cases across 25 artists. It separates extraction, identity matches and relationship claims, including unsupported decisions. The [domain contracts](../../design/2026-10-17-normalization-contracts.md) define the proposed implementation and additive APIs. Neither artifact claims measured normalization accuracy.

## What was queried

The census reads the production replica directly in one repeatable-read, read-only transaction. It profiles every artist and every row in the named catalog relations. It does not select a few artists or extrapolate global counts from a sample. The [SQL](sql/catalog-census.sql) and [summary generator](scripts/summarize_census.py) retain the input snapshot, executed-query hash, exact totals and independently reconciled artist profiles.

The current results are in [census-summary.json](evidence/census-summary.json), [artist-profiles.csv](evidence/artist-profiles.csv) and [catalog-schema.json](evidence/catalog-schema.json). The raw JSONL receipt remains outside Git under `/tmp/relisten-full-normalization-20261017/`; its SHA-256 identifies it. All saved profiles contain counts and public artist identifiers, rather than recording descriptions, credit text or credentials.

The October 17 census contains **4,379 artists, 292,476 sources, 5,063,161 track rows, 215,560 shows, 216,304 venue rows, 16,605 repertoire rows and 38,475 provider setlists**. Only 53 artists have repertoire rows; 4,309 artists have files not flagged orphaned but no repertoire dictionary. The resolver cannot require a curated song catalog.

Credit and lineage evidence is already substantial: 246,849 sources have taper text, 227,655 have transferrer text, 229,568 have lineage text and 267,878 have lineage or source-chain text. Nonempty fields can contain multiple people, unknowns or unrelated narrative. These counts do not measure extracted identities or establish shared capture ancestry.

## Playback eligibility is a separate relationship check

The catalog marks 5,010,402 tracks as not orphaned. Of those, 116,932 have no remaining source or matching source-set row. Another 4,893,470 have matching artist/source/set/show references and a media URL. The latter is a catalog-link check, not an HTTP availability or full-audio test. Unmatched historical file rows must not become live normalization search results solely because their orphan flag is false.

This is a data-quality finding, not authorization to delete or rewrite these rows. Retain historical evidence and distinguish it from currently linked playback. The summary reports these conditions separately, and source/track UUIDs remain unchanged.

The live replica is on migrations 1–12. The locally tested performance and intake foundation includes migrations 16–17. The analysis must not assume those newer relations are deployed; the full implementation must account for that release dependency.

## What the next analysis must establish

**Within every artist**, inventory actual title forms, repertoire collisions, ordered provider evidence, repeated passages, compound files, partial recordings and recording credits. Preserve artist-scoped song identities. Distinguish a repertoire entry, a performance occurrence and its recording files.

**Across artists**, build candidate indexes for physical venues, people and compositions from the whole catalog. Retain the context that can distinguish rooms, relocations, namesakes, roles and different works with equal titles. Compare plausible candidates rather than every possible pair. Accepted identities are shared; an artist's derived view references them instead of creating a private copy of each person or venue.

**For recording credits**, examine structured taper/transferrer fields and explicit role statements in descriptions and lineage prose. Extract roles before resolving people globally. Retain equipment and processing text as evidence; full ancestry/audio modeling is outside this release.

**For compositions**, reconcile repertoire entries with supported work and credit information. Composer, lyricist, arranger and original performer remain different credits. Preserve arrangements and excerpts without pretending they are exact work equivalences. Shared titles retrieve candidates; they do not select the work.

The [composition readiness audit](composition-readiness.md) identifies actual development writer evidence, the absent qualified-work inputs and the next supported acquisition step. The [bounded MusicBrainz pilot](composition-enrichment-pilot.md) retains external work bridges and author relationships without accepting catalog matches.

The [provider evidence audit](provider-evidence.md) identifies where importers retain or discard the needed information. Missing stored fields require expanded input capture and metadata refresh as part of this program. The current recording-credit and composition outcomes require that acquisition work.

Evaluation must distinguish parsing accuracy, candidate retrieval, accepted identity accuracy, occurrence alignment and role accuracy. Use independently reviewed cases across providers, catalog sizes and ambiguity levels. Keep related recordings and shared upstream setlist families together in evaluation partitions. Full coverage means every artist is processed with an explicit outcome; unsupported facts remain unresolved rather than fabricated.

## Reproduce the census

From the repository root, use a new output filename. The wrapper reads the current database password directly from the existing Kubernetes secret, connects only to the production replica and writes evidence locally:

```sh
bash docs/research/2026-10-17-full-normalization/scripts/run_live_census.sh \
  /tmp/relisten-normalization-next/catalog-census.jsonl

python3 -B docs/research/2026-10-17-full-normalization/scripts/summarize_census.py \
  /tmp/relisten-normalization-next/catalog-census.jsonl \
  /tmp/relisten-normalization-next/report \
  --sql /tmp/relisten-normalization-next/catalog-census.jsonl.sql
```

The wrapper retains the exact SQL used beside the output. Run the summary into a new evidence directory, then compare it with the committed results; preserve both snapshots. A failed or incomplete SQL receipt must not produce a completed report.

The census has a per-statement timeout and advances no database state. A new capture uses a new transaction and can legitimately differ from the previous live snapshot. Provider failures, changed rows and missing evidence must remain visible in the later backfill, rather than being interpreted as deletions.

## Status

The [local restore and migration rehearsal](scale/evidence/local-normalization-rehearsal-v1.json) preserves 701 Jerry/Aadam sources and 413 sources in the Gov’t Mule catalog. Its seven complete relation fingerprints and original migration histories verify copying and migration through 47. The later [migration-49 API rehearsal](scale/evidence/local-normalization-api-rehearsal-v1.json) checked all 701 and 413 recordings through 1,762 local API responses, with unchanged catalog fingerprints and empty accepted normalization state. Stored playback URLs were compared; media bytes were not fetched. The [whole-field actor replay](training/actor-facts-replay-v1/README.md) retains 543 actual extraction outcomes with exact original citations and unresolved annotation disagreements. Neither receipt establishes automatic matching accuracy.

The all-artist census, initial importer-retention audit, domain/API contracts and initial development fixtures are in place. Local implementation includes evidence/work stores, canonical domain services, people/venue/music read routes, indexed candidate retrieval, typed pair comparisons and cached Luna credit extraction. Provider capture retains input before legacy conversion; the recording processor and bounded page command save extraction proposals with recovery. Catalog binding, repertoire resolution and durable setlist/file-content publication are implemented. Reads are disabled by default. Evaluated multi-candidate matching, independent identity adjudication and full backfill remain work in the plan. Automatic matching remains the release outcome; recording provenance covers taper/transferrer identities.

The [real recording rehearsal](scale/materialization-local-inputs.md) publishes
257 file contents and checks 2,165 API responses. The separate [real Jerry setlist
rehearsal](scale/setlist-local-rehearsal.md) verifies three sets, 18 occurrences and
14 Show Facts lines through 1,699 API requests. Both preserve existing catalog
rows and playback references. These are publication and recovery checks, not
independent identity-matching accuracy.

The [scale audit](scale/README.md) profiles every eligible artist and links the blind annotation pack and small live Luna integration receipt. The initial 102 cases remain regression fixtures, and the expanded tasks remain unlabeled. Neither establishes release accuracy. The older [19-artist research](../2026-10-16-catalog-enrichment/README.md) is historical evidence; its statements that Collections or Aadam support were unimplemented are superseded by the [completed import/performance work](../../plans/active/2026-10-17-show-performances/README.md).

The separate [people development bundle](training/people-development-v1/README.md) adds 168 draft questions across 137 artists, with exact evidence and a frozen blind review subset. It supports actor-kind, alias and co-reference development. Most cross-recording candidates lack enough evidence to establish identity, and the positive cross-artist examples cover one Aadam Jacobs project owner. Additional corroborated identity families remain necessary for global matching evaluation.

The [cross-artist identity discovery](scale/evidence/people-identity-discovery-v2.json) expands that development evidence to 38 family hypotheses across 207 recordings and 172 artists. The [actor-facts development bundle](training/actor-facts-development-v1/README.md) adds 543 whole-field annotations and a separately frozen 43-field answer-blind review. Disagreements and partial coverage remain recorded; adjudication is pending. Exact contact/profile attachment and person/project distinctions remain separate from identity truth. The [actor-evidence guide](../../plans/active/2026-10-17-full-normalization/people-actor-evidence.md) describes implemented extraction and the accepted contextual matching still required.

The [cross-artist credited-actor review](training/people-identity-adjudication-v1/README.md)
adds separate agent adjudication of 55 questions across 82 recordings and 66
artists: 54 same-actor links across ten hypotheses and one unresolved question.
Six separate controls yield two same-actor and four different-actor answers; only
two negatives span recordings. Original source-only packets and labels remain
unchanged. Actual validation checks 1,044 exact citations, and all 60 packet,
control and review-validator tests pass. These are development judgments.

The [first real people model comparison](training/people-matcher-comparison-v1/README.md)
compares rules, logistic regression and boosted trees on the original candidate
pools. Both models miss six explicit aliases absent from their name/artist
features. The final logistic model matches public C# scores exactly on all 155
supported comparisons. The [source-context follow-on](training/people-source-context-comparison-v1/README.md)
now adds four explicit alias relationships and reduces both models' errors from
six to two on the same 81 resolved drafts. It passes 102 C# and 34 Python checks,
with exact final C# scoring and unchanged original name-only output. Contact and
profile agreements remain absent in this cohort; the separately adjudicated
source-reference questions supply the next development inputs.

The [people source-view comparison](training/people-adjudicated-training-v1/README.md)
retains all 219 declared relationships, 322 independently attributed views and
295 view-pair comparisons. Grouping yields 215 relationship units, with 100
eligible labeled units: 74 positive and 26 negative judgments. Both models
retain two false negatives and zero false positives at the diagnostic cutoff.
Public final-model parity checks all 295 view pairs within 1e-12, with review-only
actions and 43 preserved noncandidate rejections. Independent audits verify
source anchors, target provenance, grouping and scores. The
[source-view receipt](scale/evidence/people-source-views-v1.json) pins the evidence;
the earlier 92-unit single-view comparison remains available separately. Only
27 positive and two negative eligible units span artists. Broader independent
identity evidence and source-supported aliases remain necessary before
catalog-wide calibration. The separate
[development selection tool](../../../tools/RelistenNormalization/people-evaluation-selection/README.md)
now passes 19 cases and retains all 2,184 permitted original recordings across
1,765 artists and 2,172 components without changing their bytes. This prior-exposed
development frame remains separate from the fitted cohort and from catalog-wide
calibration.

The [people capacity fixture](scale/people-candidate-capacity.md) retains complete
distinct matching facts and all 6,789 owner observations across 6,788 recording
owners. Two accounts are service-published sentinels; 6,786 are mechanical
relational copies, not identity labels. Owner-only invalidation and genuine
sentinel retraction pass. A linear owner-clock index passes 25 public cases
without SQL or lock-order changes. The default 10,000-binding metadata limit
still refuses the fixture's 13,584 stored bindings. A larger explicit read
succeeds, but full people-pipeline recovery, many-claim checkpoint capacity and
backfill throughput remain unverified.

The [local MusicBrainz search receipt](scale/evidence/musicbrainz-search-local-v1.json) verifies retained timeout outcomes and explicit retry across four invocations. No successful page was received. Successful live search verification and evaluated catalog-to-work matching remain open; the [correspondence processor](../../plans/active/2026-10-17-full-normalization/composition-resolution.md) is implemented. Unavailable results are not an empty candidate set.

The [composition development inventory](scale/composition-development-selection.md)
finds that only two of the 74 cross-artist composition questions meet both
existing development partitions. Their four repertoire rows have no writer or
global work fields. Broader correspondence evidence is required before training
and evaluating composition matching.
