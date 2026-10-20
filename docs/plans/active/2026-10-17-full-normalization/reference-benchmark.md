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

## Compare original IDs and resolved identities separately

Version 2 keeps the existing top-level comparison and review queue. Their
`expected` set contains imported reference song keys; `predicted` contains current
post-consolidation targets. This comparison can report an extra and a missing
membership when two imported IDs now share one artist-song. Retained version 1
runs remain the historical baseline and must not be overwritten.

Each show row and artist summary also contains two explicit comparisons:

- `rawIdentity` compares original proposed file-segment song keys with original
  reference keys, before consolidation. Unresolved segments and candidate-only
  alternatives do not become predictions. The artist summary reports
  `files_with_original_song_prediction` and
  `files_without_original_song_prediction`; files without any retained prediction
  remain in `files_without_prediction`. The raw comparison can therefore expose
  incomplete original extraction even when current consolidation has improved.
- `resolvedIdentity` compares **current consolidated predictions** with reference
  memberships after mapping both through the same completed normalization image.
  Only one-target, same-artist `identity_alias` correspondences whose original and
  target dispositions are `proposed` can resolve an imported key. Compound
  members, review targets, competing targets and foreign artist targets do not
  establish identity. The scorer adds no title rule and does not use reference
  memberships to change predictions.

All three comparisons stay at the original show UUID. Separate early and late
performances remain separate. Song-presence sets already discard repeated plays;
this benchmark cannot measure repeated occurrences or ordered setlists.

Each comparison reports its own status and denominators: compared shows,
expected and predicted memberships, intersection, missing and extra memberships,
and exact-presence agreements. Precision uses predicted memberships; recall uses
expected memberships. Counts include only shows with a nonempty, nonconflicting
reference and a retained file interpretation. A zero denominator produces a null
rate. Resolved reference accounts may agree after their original song IDs map to
one identity; that show can be compared in `resolvedIdentity` while its original
status remains `conflicting_reference`. Always include each comparison's show
count when interpreting a rate.

`shows.jsonl` retains original reference accounts, post-consolidation predictions,
raw predictions, resolved membership sets and missing/extra song details.
`identityResolutions` records the imported-key-to-target pairs used for that show.
The normalization image fingerprint pins the source of those pairs. Existing
`errors.jsonl` inclusion and `filesForReview` logic still follow the original
top-level mismatch, so resolved agreement does not erase the original review
lead. Reviewers can inspect the nested comparisons to distinguish an imported-ID
disagreement from an unresolved song-presence disagreement.

Resolved agreement measures the effect of current normalization policy. It does
not independently validate a proposed merge, prove a reference complete, or
establish untouched release accuracy. Development corrections and exposed
Phish/GD references stay outside untouched evaluation.

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

## Completed full-catalog comparison

The current run is
`local-dev/normalization-data/2026-10-19-automatic-music-v1/reference-benchmark/manifest.json`.
It uses the complete 971,127-descriptor music rebuild with the shared artist/title
default. All 420,200 active Phish/GD files have a current consolidated prediction.
The original-ID, consolidated-target and resolved-identity comparisons remain
separate in the output. These are exposed development comparisons.

- **Phish:** All 1,893 shows are compared. The resolved intersection is 37,055
  presences, against 37,821 predicted and 38,146 reference presences: 97.975%
  precision and 97.140% recall. Exactly 1,352 shows agree completely; 1,091
  reference presences are missing and 766 predictions are extra. This result is
  unchanged by the duplicate-identity default.
- **Grateful Dead:** 1,897 of 2,081 shows are compared. The resolved intersection
  is 26,980 presences, against 48,617 predicted and 37,395 reference presences:
  55.495% precision and 72.149% recall. The resolved comparison retains 10,415
  missing and 21,637 extra presences. Sixteen shows have empty references, 140
  lack an aligned reference and 28 have conflicting accounts. Two shows agree
  completely. Song normalization remains substantially unfinished for GD.

GD's original reference IDs contain 38,038 memberships in the compared shows;
the resolved view has 37,395 because duplicate song IDs no longer count as
different songs in one show. The original-ID baseline remains in `rawIdentity`.
Do not interpret the smaller resolved denominator as additional independent
proof of correct merges. Source titles, original reference accounts and each
original-to-target mapping remain inspectable.

The same run's historical top-level comparison uses consolidated predictions
against unchanged original reference IDs. GD has 25,923 intersections, 12,115
missing and 22,694 extra presences in that view. The untouched prior output is
`2026-10-20-reference-benchmark-v1/run-v5/manifest.json`; its corresponding GD
counts are 24,471 intersections, 13,567 missing and 21,601 extra. This preserves
the earlier baseline while showing that matching changes can both recover
references and add predictions that still need examination.

The broad remaining GD gap includes title-derived accounts, provider spelling
variants and file-like titles. The current prediction union contains 20,457
provisional memberships. A show-level union also hides disagreements between
recordings of one show. Use the retained file witnesses and review UI to inspect
recurring failures; another merge of routine duplicate IDs alone will not close
this gap.

The output contains 54,912,233 bytes of show rows and 73,165,419 bytes of review
rows. It uses a 512 MiB maximum output allowance without truncating the queue.
Public CLI contracts cover independent score denominators, identity-equivalent
reference conflicts, ownership, compounds, reviews, repeated performances,
original prediction preservation and failure boundaries. Counterfactual
reference changes affect scoring without changing predictions. No model calls
or production writes were needed for this comparison.

The separate [Phish file comparison](phish-file-reference.md) supplies a title-only,
exact provider-track comparison for all 3,112 retained native files. Abbreviations
such as HYHU and TMWSIY remain useful alias-development cases with original
provider song references.
