# Development labels for people and credit actors

This bundle adds 168 source-grounded draft questions about **who a credit refers to**. The earlier [100 credit-field labels](../credits-development-v1/) test whether an extractor finds a name and role. These questions test a different part of normalization: whether a name refers to a person or a group, whether two mentions in one recording refer to the same actor, and whether mentions across recordings can be linked.

The labels do not create canonical people, merge credits, or enable an automatic acceptance policy. Every original judgment remains a development draft with adjudication pending. Unknown actor kind and insufficient identity evidence are explicit answers; exclude them from binary person/nonperson and same/different scoring.

## What is included

The 168 questions use 144 recordings from 137 artists. They refer to 246 inventoried mentions and 311 exact source citations.

- **79 actor-kind questions:** 34 person, 6 group, and 39 unknown. Musician and video-crew rosters supply human-role context. Member lists and a legal organization suffix support group labels. A bare name in a taper field remains unknown actor kind.
- **46 within-recording co-reference questions:** 20 coordinated actor lists, 20 joint recording/transfer clauses, and 6 explicit alias statements. Co-reference means that two mentions refer to the same actor. Every other inventoried name in the recording is included as a candidate.
- **43 cross-recording questions:** 45 original equal-name collision tasks become 43 complete candidate questions after duplicate focal mentions are combined. Forty questions include a candidate from another artist.

Across the two identity question types, the drafts contain 59 same, 22 different, and 77 insufficient candidate decisions. These are correlated edges within questions, not 158 independent identities. Eleven same edges connect **one Aadam Jacobs project owner** across 12 recordings. That is evidence for one identity, and cannot establish broad cross-artist matching accuracy.

The draft evidence categories and counts are recorded in [manifest.json](manifest.json). Explicit alias statements and joint role clauses support same-actor judgments. Coordinated actor lists support source-local distinctness. Equal names, different spellings, artist overlap, and Archive search links do not establish identity. No verified provider-qualified person identifier was found in these selected sources.

A person label from a roster classifies the expressed actor. It does not prove that the actor taped the recording, attended a particular date, or matches an existing global person. A group label does not allocate a canonical group record. Compound credits remain whole `credit_expression` mentions unless the source provides a supported decomposition.

## Selection and reserved evidence

The retained [annotation source pack](../../scale/annotation/) contains 6,000 source records. Requiring both artist and recording-family partitions to be development leaves 2,185. A separate reservation pass excludes an evaluation-seed source and its complete retained family, leaving **2,184 eligible sources from 1,765 artists**. The 144 selected records are a curated subset of that population. The full retained production snapshot has 32,593 source rows; this bundle makes no claim to exhaustively extract their people.

[reservation-receipt.json](reservation-receipt.json) pins the ignored reservation index, source hashes, counts, and exposure note. The index projects seed case IDs, split, evidence references, and opaque group hashes. Seed answers, rationale, and expected values were not opened. Known exposed references `track:33689437` and `track:7018353` are tracks. Metadata maps track references to retained parent sources when present; local-source namespaces are kept separate from production IDs.

Before explicit reservations were known, initial computational predicates may have processed raw text from `source:3717752`, which met both development partitions. Its source and complete retained family are excluded here. That family cannot support future claims of untouched evaluation. The existing 102-case seed remains a contract/regression resource; launch accuracy requires new or properly excluded evaluation families.

The validator loads reservation eligibility before it uses selected source text. It selects eligible physical line numbers before decoding original annotation rows, then checks metadata and line hashes against the frozen file. Whole-file hashes use bytes without decoding excluded records. No excluded original field is needed to annotate or validate this bundle.

## Candidate completeness and leakage

[candidate-pool.json](candidate-pool.json) freezes the complete **inventoried mention list**. This inventory includes the selected roster, credit, alias, and collision contexts. It is not an exhaustive list of every name in every eligible source.

The question contracts are deterministic:

1. A within-recording question includes every other inventoried mention of kind `name` in that recording. An unresolved reference or undivided compound credit cannot silently become an individual-person candidate.
2. A cross-recording question includes every other inventory mention with an equal folded name, or an equal final Unicode word token of at least four code points. Folding applies NFKC, case folding, and whitespace collapse. A same-recording mention can also be returned by this rule.
3. Every returned candidate receives exactly one same/different/insufficient judgment. The original collision pair must remain among the candidates. Similarity selects questions; it does not answer them.

The rules can miss identities whose names have no lexical connection. The explicit alias questions exercise some of those relationships, but a deployment evaluation needs broader candidate retrieval and verified identity families.

Each task carries grouping keys for every participating recording, retained recording family, and folded-name hypothesis. The annotation field named `input_duplicate_family` is a transitive union of known recording families and exact-input duplicates; `family_group` preserves that ID. The validator rejects any known recording family that spans more than one group. `build_split_components()` joins all tasks through these keys **before** any development experiment divides them. Unknown questions participate. The current 168 tasks form 82 connected components. Grouping keys prevent shared context from crossing experiment splits; they are neither identity assertions nor matcher features. The entire bundle remains development data regardless of any internal experiment split.

## Files and provenance

- [source-index.jsonl](source-index.jsonl) identifies each selected original source, artist, development partitions, family, and physical annotation-line hash.
- [mentions.jsonl](mentions.jsonl) contains exact source substrings, their actor-expression kind, and context citation IDs.
- [citations.jsonl](citations.jsonl) contains field hashes and exact Unicode code-point spans. The quote is preserved without trimming, spelling changes, or HTML rewriting.
- [labels.jsonl](labels.jsonl) contains the frozen draft judgments, evidence strength, rationale, candidate contract, grouping, and reviewer/adjudicator state.
- [primary-sources.jsonl](primary-sources.jsonl) retains two short excerpts from the Aadam Jacobs project's own account, URL, observation time, and limits. Seventeen words are quoted from that source. It supports the project owner's individual context; it does not authenticate every tape or supply a provider person ID.
- [manifest.json](manifest.json) pins tracked data, ignored original rows, reservation metadata, selection counts, and the review packet.

Original full records, unredacted fields, reservation metadata, and the review packet remain under ignored `local-dev/normalization-data/2026-10-17-cross-artist/training-development-people-v1/`. Full source fields may contain public contact details. Only the exact excerpts needed for these judgments belong in Git.

All original labels were read and drafted by one Codex reviewer without paid API processing. A separate Codex reviewer received 30 questions with original source fields, complete candidate contexts, exact citations, and primary excerpts. The packet contains no draft answers, draft rationale, or model outputs. The reviewer froze the initial answers before seeing the drafts. All 11 actor-kind answers and all 39 candidate decisions across 19 identity questions agreed with the drafts. [independent-review.jsonl](independent-review.jsonl), [review-comparison.jsonl](review-comparison.jsonl), and [review-receipt.json](review-receipt.json) preserve those answers, comparisons, and original packet/output hashes. No original draft was changed. This selected subset contains correlated edges and cannot estimate launch accuracy. All 168 tasks still have adjudication pending; the review was not independent human adjudication. Future adjudication must be recorded separately from the frozen drafts.

## Validate and use

From the repository root:

```sh
python3 -O docs/research/2026-10-17-full-normalization/training/people-development-v1/validate_bundle.py
python3 -O -m unittest discover -s docs/research/2026-10-17-full-normalization/training/people-development-v1 -p 'test_*.py' -v
```

[verification-receipt.json](verification-receipt.json) pins this bundle and its validator after the focused checks and separate source review. The validator needs the ignored retained files pinned in the manifest. Missing originals fail validation; they cannot be reconstructed from the small excerpts. `--bundle` and `--root` let tests validate an isolated copy without changing retained evidence. JSONL is read by physical file lines so Unicode separators inside quoted strings remain intact.

The checks authenticate evidence hashes, reservation and development eligibility, source ownership, exact spans, duplicate IDs, candidate completeness, original collision tasks, split groups, draft status, reported counts, and the independent review against its frozen first answers. They cannot prove the semantic truth of a judgment. The tests corrupt spans, original source text, partitions, reservations, duplicate mentions, and candidate sets after updating outer receipts. They also verify Unicode offsets, grouping through an unresolved task, preservation of first-review answers, and selection before decoding under optimized Python.

Use the bundle to develop actor-kind handling, co-reference, alias rules, and candidate retrieval. Keep uncertain cases visible. Broader independently adjudicated identity families, unseen artists, and held-out recording formats remain necessary before measuring launch accuracy or accepting links automatically.
