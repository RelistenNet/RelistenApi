# Classify discovered venue associations from original metadata

Use this adapter to recover the original annotation partitions for the 122
source UUIDs found by the [venue association discovery query](venue-context.md).
The earlier 2,184-source development index covers only two of those sources.
An entry missing from that index still needs classification against the full
frozen annotation frame.

`scripts/project_annotation_metadata.py` reads the metadata header of every
physical row in that frame. It stops immediately after the colon before
`raw_record`, without decoding or inspecting the value. The original source
prose and other raw values remain undecoded. Whole-file and physical-row hashes
include those bytes so the output can identify the exact original evidence.

## Run the projection

Run from the repository root with a new ignored output directory. The example
below identifies the completed run; choose a new output directory for another run:

```sh
python3 -B docs/research/2026-10-17-full-normalization/scale/scripts/project_annotation_metadata.py \
  --repo-root "$PWD" \
  --preparation local-dev/normalization-data/2026-10-17-cross-artist/training-development-venues-v1/context-preparation-v2 \
  --discovery local-dev/normalization-data/2026-10-17-cross-artist/training-development-venues-v1/association-discovery-capture-v1 \
  --output local-dev/normalization-data/2026-10-17-cross-artist/training-development-venues-v1/annotation-metadata-v1
```

The command performs no database or network work. It authenticates the frozen
preparation, annotation manifest, reservation index and executed discovery query
before classifying any source. It pins the adapter and reused preparation helper
in its receipt, then rechecks all input files immediately before publication.
An existing output directory is an error. A failed write preserves its partial
files and failure receipt; it does not publish a completed receipt or overwrite
an earlier run.

## What the output establishes

`annotation-metadata.jsonl` contains all 18,628 headers: 6,000 sources, 6,000
venues, 4,500 tracks, 1,696 songs and 432 setlists. Each header retains the
original artist UUID, artist and family partitions, known recording family,
transitive duplicate-family group, physical line number and line hash. The
artist name is parsed as metadata and discarded.

`source-classifications.jsonl` contains every discovered source once, with all
its discovered venue associations. It distinguishes reservations, held-out
partitions, absent annotation entries, changed artist/show associations, missing
context, and development entries awaiting original numeric-ID verification.
`receipt.json` records input/output hashes and exact counts. No malformed header
or failed source classification can be omitted to produce a completed run.

The parser accepts only the six original metadata fields in this order:
`artist`, `artist_partition`, `evidence_id`, `family_partition`,
`input_duplicate_family`, and `known_recording_family`. The artist object must
contain ordered `name` and `uuid` strings. Each prefix must end within 16 KiB.
This closed grammar permits only two object levels, stricter than the maximum
nesting bound of eight. A changed envelope requires explicit review rather than
fallback parsing of the whole row. The complete frame is bounded at 64 MiB;
the metadata-only discovery response is bounded at 32 MiB and 100,000 associations.

## Observed result and next check

The [completed projection receipt](evidence/venue-annotation-metadata-v1.json)
authenticates all 18,628 metadata headers and classifies all 122 discovered
recordings. Two recordings retain their original held-out partitions. Another
118 are absent from the 6,000-source annotation selection. The two development
candidates already belong to the 51-source strict allowlist, so this run adds no
eligible source or date context. All 20 optimized synthetic tests pass.

The subsequent [original-group replay](original-group-replay.md) classifies all
122 against the broader captured snapshot without assigning new partitions. It
verifies 29 development tuples, including 27 beyond the strict allowlist. The
remaining recordings stay held out, context-only or absent from the standalone
source selection. Keep the 93 source-free show associations separate.

A development classification preserves the original partition and duplicate
group. It does not authorize a new raw capture. The prefix contains the source
UUID and the known family's original artist/show numeric IDs; it does not contain
the original numeric source ID. A selected-row read must separately
authenticate that ID and the original tuple before any date or wording expansion.

An absent entry means absent from the 6,000-source annotation selection. It does
not establish absence from the broader 32,593-source snapshot or from nested
source context. Source-free show associations remain counted separately and
unclassified. Missing context, reservations and held-out partitions stay explicit.
This command changes no annotation labels, matching baseline or acceptance policy.

Run the synthetic contract tests without opening the real annotation frame:

```sh
python3 -B -O docs/research/2026-10-17-full-normalization/scale/scripts/test_project_annotation_metadata.py
```
