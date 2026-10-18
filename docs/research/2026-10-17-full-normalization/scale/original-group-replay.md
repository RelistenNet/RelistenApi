# Recover original groups for the full captured selection

Run `scripts/replay_original_groups.py` to recover the original family and
duplicate groups for all 86,655 captured resources, including 32,593 recording
sources. This makes the original split assignments available for sources omitted
from the 6,000-source annotation selection. The tool then classifies all 122
sources in the retained venue-association discovery capture.

The replay uses the original builder's `load_snapshot()` and
`family_assignments()` functions from authenticated source bytes. It does not
rerun annotation selection, generate pairs, extract credits, train a model or
change labels. The builder joined known recording families and exact duplicate
inputs across the complete capture before selecting annotation rows. Replaying
that complete computation preserves those connections through unselected rows.

## Data handling

This command programmatically decodes the full frozen capture, including credit
strings, inside its standalone offline Python process. Those values serve only
the original exact duplicate algorithm. Source prose is not exported, printed,
passed to a model or used for training. This differs from the earlier
[metadata-prefix projection](venue-context-metadata.md), which stopped before
every raw payload.

The process reads the original capture already retained under ignored local
storage. It makes no database, API or provider requests. Its CLI suppresses
underlying parser exceptions so malformed source values cannot appear in a
terminal traceback. The metadata outputs also belong in ignored local storage.

## Run

Run from the repository root in a new process, with a new output directory:

```sh
python3 -B docs/research/2026-10-17-full-normalization/scale/scripts/replay_original_groups.py \
  --repo-root "$PWD" \
  --projection local-dev/normalization-data/2026-10-17-cross-artist/training-development-venues-v1/annotation-metadata-v1 \
  --preparation local-dev/normalization-data/2026-10-17-cross-artist/training-development-venues-v1/context-preparation-v2 \
  --discovery local-dev/normalization-data/2026-10-17-cross-artist/training-development-venues-v1/association-discovery-capture-v1 \
  --output local-dev/normalization-data/2026-10-17-cross-artist/training-development-venues-v1/original-group-replay-v1
```

The [completed replay receipt](evidence/original-group-replay-v1.json) records
86,655 recovered resources and all 18,628 matching annotation headers. Of the
122 discovered recordings, 29 have verified development tuples across 21 artists.
Two already belong to the strict allowlist. The
[expanded context capture](expanded-venue-context.md) now supplies dates for all
29, including the other 27. Twenty-three recordings remain held out, nine occur
only in nested context, and 61 are absent from the original standalone source
selection. No partition or label changed.

All 15 optimized synthetic tests pass. They exercise the original grouping code
with fabricated captures, including a chain of inconsistent track-parent links
and a cycle. Fresh review verified the fixed-point reservation closure before
the real run. The real closure blocks four groups and retains 18 unmatched
original references explicitly; it does not treat those references as absent
from the catalog.

## Checks before publication

The command authenticates the tracked compact metadata-projection receipt, its
private completed receipt and all recorded input/output hashes. The supplied
preparation and discovery must belong to that projection. Their executed query,
read-only replica receipt and allowed venue tuples must still match.

The original annotation manifest pins the builder source and capture. The
capture must contain exactly 176,666,741 bytes and 104,228 physical records.
The original loader checks its replica/completion markers, unique resources,
per-artist sampling caps and relation totals. A bounded reader prevents the
unchanged loader from exceeding the authenticated byte or row counts if the
file changes. The recovered resource counts must be exactly:

- 32,593 sources;
- 34,861 tracks;
- 17,073 venues;
- 1,696 songs;
- 432 setlist shows.

Every one of the 18,628 saved annotation headers must match the recovered
resource, artist UUID, literal family, full component group and both original
partitions. Any discrepancy fails the run. The original Python runtime was not
recorded; the receipt pins the replay runtime and records this limitation.

## Outputs and their limits

`original-resource-metadata.jsonl` contains IDs, UUIDs, original artist/show
references, original group IDs and partitions. It omits names and credit prose.
Standalone source rows contain their original numeric show ID, but not the
original show UUID; that UUID stays unknown.

`reservation-closure.json` preserves the prior exposure note and records additive
component exclusions. Existing reserved references, linked parents, known
families and excluded evidence seed the complete original groups. Every track in
an excluded component also excludes its actual stored parent source,
even when inconsistent artist/show fields kept those records in separate groups.
The tool follows tracks in each newly excluded component until no groups are
added. Each group is visited once; cycles terminate. Explicit reservation seeds
and derived parent exclusions are recorded separately. The frozen reservation
index remains unchanged.

`source-classifications.jsonl` retains all 122 current sources and associations.
An original development tuple is verified only when the original numeric source
ID, source UUID, artist ID/UUID and numeric show ID match the current discovery,
with current show context present. Reserved groups, held-out partitions, changed
tuples and missing context remain explicit. A source found only in nested track
context receives no standalone source group or development assignment. Absence
from the 32,593-source captured selection does not establish catalog-wide absence.

Each metadata output is bounded at 64 MiB. `receipt.json` pins executable sources,
Python version, prerequisites, outputs and counts. All inputs are checked again
before the completed receipt is published atomically. Invalid inputs fail before
output creation. A publication failure retains partial files and a failure
receipt without a completed receipt. Recovery uses another directory; existing
outputs are never overwritten.

These files prepare further evidence work. They do not establish venue identity,
model accuracy, a new matching policy or an accepted catalog change.

Run the synthetic tests separately:

```sh
python3 -B -O docs/research/2026-10-17-full-normalization/scale/scripts/test_replay_original_groups.py
```
