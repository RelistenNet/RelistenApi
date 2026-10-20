# Compare whole-artist song presence against imported setlists

`tools/RelistenNormalization/artist_setlist_benchmark.py` compares the existing
C# title interpretations and consolidation results with separately retained
Phish and Grateful Dead song/show memberships. It opens current SQLite images
read-only, reads original immutable witnesses, and writes a new private report.
It does not run another matcher or modify a reader.

The captured population contains 1,893 Phish shows and 2,081 playable Grateful
Dead shows. Phish has 37,876 active files; another 52,534 stored orphaned rows are
excluded. GD has 382,324 active files. The reference has 1,893 Phish accounts with
38,146 unique song memberships and 2,315 GD accounts with 38,907 memberships.
These counts come from the retained complete metadata roster and original
song/setlist shards, not a new provider fetch.

## What is compared

Each original file's captured source/show UUID establishes its home. Only an
actual proposed `titleInterpretation.segments[].song` participates; candidate-only
alternatives are not predictions. That original key is mapped through the
completed C# consolidation's proposed targets. Multiple proposed targets remain
multiple song-presence predictions. No Python title, name or date rule is added.

Expected memberships come from the original unordered setlist capture. Their
projection must reproduce the same song UUID set. The benchmark uses the existing
C# setlist association only when it selects one nonconflicting captured show
through an existing provider key or a unique exact artist/date. Its original basis
and witness remain in every result. Calendar alignment is a proposed comparison
alignment, not accepted event identity. Ties, missing alignments, conflicting
references, empty references and missing predictions remain explicit outcomes.

The candidate dictionary is shared with the matcher; the expected per-show
membership set is supplied only to the scorer. This is a development-exposed
comparison, with no training, thresholds or prompt changes. It cannot establish
untouched release accuracy. It measures song-presence agreement, not per-file
truth, order, repeats, audio alignment or composition identity. Missing songs can
reflect incomplete recordings; extra songs can reflect omitted reference entries,
spoken introductions, a provisional title or a genuine matching error.

## Run

Create a JSON request with these existing paths:

```json
{
  "metadata": "/absolute/full-domain-capture-v1/validation.sqlite",
  "normalization": "/absolute/music-consolidation-projection-v1/normalization.sqlite3",
  "rawSetlists": "/absolute/shadow-full-raw-v1/0014-setlists-5654-80297-attempt-001.jsonl.gz",
  "referenceRun": "/absolute/shadow-full-files-serving-v1",
  "fileRun": "/absolute/shadow-full-files-serving-v1",
  "maximumSeconds": 600,
  "maximumOutputBytes": 67108864
}
```

```sh
python3 tools/RelistenNormalization/artist_setlist_benchmark.py \
  --request /absolute/request.json --output /absolute/new-output
```

`manifest.json` is published last and contains denominators, compared-show counts,
intersection/missing/extra counts, micro song-presence precision/recall, coverage,
input fingerprints and selected immutable chunk pointers. `shows.jsonl` retains
every captured show. `errors.jsonl` contains stable artist/show or setlist keys,
expected and predicted keys, original reference/alignment coordinates, and file
witnesses contributing extra predictions. These are a review queue, not adjudicated
false-positive or false-negative labels. An existing output directory is refused;
failure preserves partial evidence without a completion manifest.

The retained native Phish tranche contains 150 show responses, 3,112 files and
3,160 provider song-ID memberships, including 45 files with several song IDs.
The separate [per-file panel](phish-file-reference.md) now withholds those IDs
from its title-only prediction input and supplies them only to the scorer. It
compares exact provider file identities and retains the original reference
evidence. The whole-artist report below remains a song-presence comparison; it
does not imply that all 1,893 original provider responses are retained.

## Completed comparison

The retained run is
`local-dev/normalization-data/2026-10-20-reference-benchmark-v1/run-v4/manifest.json`
(SHA256 `accfbfd09654e2ef038702a66feecbd2cf7b4c040d2a0f46f76ff36124ec5bdf`).
It completed in 35.09 seconds and found predictions for all 420,200 active files.
All 4,208 reference accounts were present in the projection. The 3,863 aligned
accounts use the existing C# `unique_exact_artist_date` proposal, covering 3,834
shows; 345 accounts remain unaligned. No accepted performance binding is claimed.

- **Phish:** All 1,893 shows are compared. The intersection is 37,055 presences,
  against 37,821 predicted and 38,146 reference presences: 97.975% precision and
  97.140% recall on this comparison. Exactly 1,352 shows agree completely; the
  queue retains 1,091 missing and 766 extra presences. Five title segments remain
  unresolved.
- **Grateful Dead:** 1,897 of 2,081 shows are compared. The intersection is 24,452,
  against 48,292 predicted and 38,038 reference presences: 50.634% precision and
  64.283% recall. Exactly two shows agree completely. Sixteen have empty
  references, 140 have no aligned reference and 28 have conflicting accounts.
  The queue retains 13,586 missing and 23,840 extra presences, including 22,710
  provisional predicted identities; 110,995 title segments remain unresolved.

This exposes a broad GD normalization gap. Examples include `Saw Mill` versus
reference `Sawmill`, several `West L.A. Fadeaway` spellings, and file-like
`gd86-03-20 16 Wharf Rat` titles that remain separate identities. These are
review leads with original evidence, not independently adjudicated alias labels.
The show-level union also hides disagreement between recordings of the same show;
per-show captured file/source counts accompany the comparison.

The complete report stores 22,793,479 bytes of show rows and 47,519,932 bytes of
review rows. The initial 64 MiB output guard stopped near the end without a
completion manifest. Its partial files remain alongside the earlier format
failures. The completed run uses the separate `request-v2.json` with a 128 MiB
allowance; it does not truncate the queue or change predictions to fit the bound.

Ten public CLI contracts cover multisong/missing/extra outcomes, candidate and
review exclusion, repeated-membership presence, complete show coverage, ownership
refusal, packed reader UUIDs, native proposal-array separation, output bounds and
existing-directory preservation. A counterfactual reference-label change leaves
file predictions unchanged while changing scores and the error queue. Fresh source correctness and separate
readability reviews are complete. No provider, model, production write or .NET
execution was needed for this comparison.

## Full-population title-rule iteration

The updated shared title rules processed all 971,127 descriptors from the original
complete bundle. The completed output is
`local-dev/normalization-data/2026-10-20-music-consolidation-v2/manifest.json`
(SHA256 `1897fd3d41e4e9277dfd6fe0d200466ecf4f2305b85bde94297331b1eda8dd52`).
Its 911 MiB read projection retains every original descriptor. No reviewed song
correspondences were supplied to this run.

`2026-10-20-reference-benchmark-v1/run-v5/manifest.json` scores that projection
against the same original reference and recording population. Phish totals are
unchanged. GD extra predicted memberships fell from 23,840 to 21,601, while
missing memberships fell from 13,586 to 13,567. Precision is 53.115% and recall
64.333%. More title variants now reach dictionary candidates, including ambiguous
duplicate entries; this iteration does not solve the dictionary identity problem.

The GD dictionary has 557 entries but 501 distinct literal titles. Examples are
three Drums entries, two Space entries and three Playing in the Band entries.
These are distinct imported IDs. Expert song correspondences preserve those IDs
and each play while giving matching a shared artist-song target. Any score using
reviewed identity equivalences must be reported separately from raw-ID scores.

The separate [Phish file comparison](phish-file-reference.md) now supplies a
title-only, exact provider-track comparison for all 3,112 retained native files.
Its disagreements include abbreviations such as HYHU and TMWSIY. They are
concrete alias-development cases with original provider song references.
