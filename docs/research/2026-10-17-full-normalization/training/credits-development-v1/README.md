# Draft development labels for recording credits

These **100 complete fields from 78 real recordings and artists** contain 105 draft credit-role assertions, 15 fields with unresolved text, and 21 fields with neither a credit assertion nor unresolved text. They start development work on a trained credit extractor. They do not measure accuracy, establish global person identities, or authorize automatic acceptance.

The fields come from the existing [blind annotation pack](../../scale/annotation/README.md). Every selected task has both `family_partition` and `artist_partition` set to `development`. The original tasks remain unanswered: each draft covers one complete field, not all credit fields in its source record. The [receipt](receipt.json) retains pack hashes, selection counts, the source snapshot hash and the exact draft-label hash. [labels.jsonl](labels.jsonl) retains the existing task and evidence IDs so a reviewer can retrieve the original record.

All 78 artists have an Archive.org mapping in the retained catalog snapshot. This batch therefore does **not** establish coverage of Local, Phish or Jerry provider formats. An artist mapping also does not prove the upstream owner of an individual recording. The evidence resource remains `relisten_catalog_snapshot`. Broader provider coverage needs additional retained development inputs; held-out tasks must stay untouched.

## What is labeled

The selection includes 40 taper fields, 20 transferrer fields, 22 lineage fields, 13 descriptions, four taper-note fields and one source field. It includes personal names, handles, comma-separated credits, HTML, a long Yes description, Aadam Jacobs, equipment chains, unknown contributors and organization/handle ambiguity. Selection favored varied, readable fields. It is not a probability sample or an identity-disjoint benchmark. This batch has no positive examples for `MatrixBuilder`, `RecordingEngineer` or `Collector`; their absence does not validate rejecting those roles.

Each `credit-field-draft-v1` record contains:

- The original task, evidence, source and artist identifiers; both partitions; the known recording and duplicate-input families; and the exact field path.
- A SHA-256 of the canonical evidence record, a SHA-256 of the original UTF-8 field, and its Unicode codepoint length. Canonical evidence JSON uses sorted keys, compact separators and literal Unicode.
- An `answer` with explicit assertions, unresolved spans, `exhaustive` and `unresolved` flags, a draft reviewer name and a null adjudicator. Every assertion retains its exact name span and its structured-field or quoted prose role basis.

All spans use Unicode codepoints, with an exclusive end offset. They refer to the original retained text, including HTML and entities. Decode neither HTML nor whitespace before applying them. The same name span can carry several supported roles. A model must support that relationship rather than force one role per name token.

A populated field is not automatically a person credit. Equipment-only lineage and an empty transferrer field have no positive assertions. `Unknown` remains unresolved. Production names and ambiguous handles remain unresolved where the text does not identify an individual. A supported personal handle can be a credit mention without establishing its global identity. Comma-separated names can be extracted without deciding whether they are aliases of one person.

`Taper` includes explicit recorded/taped credits. Explicit transfer, mastering and upload/seed credits retain those roles. Mixing, tracking and unspecified post-production use `Other` with the exact cue; they are not silently converted into transfer or mastering. Explicit musician rosters use `Musician`. A song title, cover attribution, dedication or equipment brand is not a recording credit.

## How to use the drafts

`exhaustive: true` means the entire selected field was read for the supported recording-credit roles. It does not mean every actor or role is known. `unresolved: true` means one or more spans remain uncertain. Preserve those spans as unknowns and mask them from negative training labels. An unresolved organization is not a labeled non-person. Outside positive and unresolved spans, negative token labels are available only for an exhaustively reviewed field. Do not derive negatives from omitted fields, unanswered tasks or the earlier nonexhaustive seed labels.

The original full fields are in the ignored directory:

```text
local-dev/normalization-data/2026-10-17-cross-artist/training-development-credits-v1/
  fields.jsonl
  blind-review-fields.jsonl
```

Checked-in labels contain minimal supporting excerpts, not full descriptions or contact strings. Use the source snapshot and field hashes to recover and verify each complete input. No provider response bytes are claimed: these are exact retained catalog fields.

A separate Codex agent read 20 selected original fields without seeing the draft answers. Its [saved judgments](blind-review.jsonl) have 51 validated spans. All 20 name-role sets agree with the drafts; this is a review agreement count, not an accuracy estimate. The [comparison](review-comparison.jsonl) retains one unresolved difference: the draft masks the unclear processing word `flaed`, while the reviewer did not mark it unknown. Exclude that entire field from fitting until adjudication, using `receipt.training_excluded_label_ids`.

The reviewer fully read all 20 fields. Its original `exhaustive: false` flags mean uncertainty remained, rather than unread text; those flags are preserved alongside `coverage_complete: true`. Consumers must use that explicit coverage flag when comparing the two label formats. A separate draft-quality pass checked all 100 fields; it was not blinded. Neither review is independent human adjudication.

All 100 labels remain `draft_development`, with null adjudicators. None enters a scored holdout or activates a matching policy. The next experiment can fit a development-only extractor, masking unresolved spans and excluding the disputed field, then compare held-out development families with the existing local extractor. Validation, test and unseen-artist tasks remain reserved for independently adjudicated evaluation.

## Validate the inputs and spans

From the repository root:

```sh
python3 docs/research/2026-10-17-full-normalization/training/credits-development-v1/validate_labels.py \
  local-dev/normalization-data/2026-10-17-cross-artist/annotation-v1
```

The validator reads the original annotation pack and checks its hashes, untouched answers, partitions, source identity, field hashes, the exact ignored training text, saved blind-review hashes, exact name/cue/unknown spans and counts. It creates no answers. A passing check proves that the draft points to the retained text; semantic correctness still needs review.

Five behavioral checks cover literal Unicode line separators, bad spans under optimized Python, held-out partitions with matching hashes, changed capture bytes and stale training text despite an updated file hash:

```sh
python3 -m unittest discover \
  -s docs/research/2026-10-17-full-normalization/training/credits-development-v1 \
  -p 'test_*.py' -v
```
