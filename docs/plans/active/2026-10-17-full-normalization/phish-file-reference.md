# Compare Phish file titles with retained provider song IDs

`phish-file-reference` compares a title-only C# prediction with the song IDs that
Phish.in assigned to that exact file. It uses the existing title extractor and
artist repertoire resolver. The expected IDs enter only the scorer, after the
prediction has been produced.

This is a stronger file-level comparison than the [whole-artist song-presence
benchmark](reference-benchmark.md). It still measures agreement between metadata
accounts. It does not establish what the audio contains, musical order, repeated
occurrences, timing, or accepted song identity. The retained data and dictionary
are development-exposed; the result is not untouched release accuracy.

## Retained inputs

The original capture is
`local-dev/normalization-data/2026-10-18-native-credit-phish-fields-v1/`.
Its completed manifest declares the selection plan and HTTP receipts. The receipts
pin 150 successful original responses under `responses/0001.json` through
`responses/0150.json`: 3,112 files, 3,160 file/song memberships and 45 files with
more than one song ID. These totals were recounted from the original bodies.

The completed offline admission under
`local-dev/normalization-data/2026-10-18-retained-http-admission-v1/` preserves
those bodies, their receipt coordinates, and the captured recording association.
The benchmark verifies its declared input bytes and each selected original body.
It also verifies the original catalog manifest and compressed song shard. It
neither fetches providers nor writes a database or reader.

A case is keyed by `phish.in/show/<show-id>/track/<track-id>`. The recording UUID
comes from the retained exact source/provider association. The enclosing show ID
must agree with the track's `show_id`. Duplicate provider file keys and conflicting
owners stop the run. Dates, titles, positions and media URLs do not select a case's
recording or reference.

For example, original response `responses/0001.json` belongs to provider show
`683` and captured source `cf255c41-68ae-ed5e-ba09-883bb9e98418`. Track `13821` is
`Alumni Blues > Letter to Jimmy Page > Alumni Blues`; its provider memberships are
`29` and `444`. The original artist dictionary maps those IDs to captured song UUIDs
`d0f88a9d-0f04-cc99-f9d3-f5f637fa51ac` and
`421a0990-b50e-d2a1-16dd-b6526862b068`. The repeated Alumni Blues title segment
remains in the prediction; set scoring counts that song once for this file.

## Prediction and scoring

The public `PhishFileTitleInput` contains only the captured artist ID, artist UUID
and literal title. It has no field for a provider song ID, expected membership,
date, file identity or recording notes. `Predict` calls `TrackTitleExtractor` and
`ShadowRepertoireAdapter.ResolveTitleMention`, the same title policy used by the
catalog and native file adapters. The dictionary contains original song rows for
the same artist. It does not contain expected memberships from these show responses.

Only a segment's proposed song is a prediction. Competing dictionary candidates
remain unresolved. Provisional title identities remain distinct from captured
dictionary identities and can therefore contribute extra predictions. Missing or
uninterpretable titles remain in the denominator.

The scorer maps each original expected song ID through the captured dictionary's
`upstream_identifier`, within the captured artist. It makes no title-based reference
match. An expected ID absent from that dictionary remains an explicit unmapped
reference and a missing membership. Both sides are sets per file; provider arrays
do not supply order or repeated-play counts.

## Run

Use absolute paths in a JSON request:

```json
{
  "retainedAdmissionDescriptorPath": "/absolute/2026-10-18-retained-http-admission-v1/descriptor.json",
  "dictionaryShardPath": "/absolute/2026-10-18-shadow-full-raw-v1/0013-songs-1919-34812-attempt-001.jsonl.gz",
  "maximumSeconds": 300,
  "maximumOutputBytes": 67108864
}
```

```sh
dotnet run --project tools/RelistenNormalization -- \
  phish-file-reference /absolute/request.json /absolute/new-output
```

The output directory must be new. `files.jsonl` retains every provider file key,
recording association, original response fingerprint, reference evidence,
title-only prediction input, actual interpretation and score. `manifest.json`
is published last. It records method versions, input fingerprints, file and
membership denominators, exact agreement, unresolved titles, unmapped references,
intersection, missing and extra memberships, and micro precision/recall. A timeout,
input mismatch or output bound leaves partial evidence without a completion
manifest. Neither side is dropped to improve the score.

Six public-contract tests cover compound titles with repeats, ambiguous and
untitled files, exact provider file binding despite equal titles, unmapped song
IDs, changed original bytes, output bounds, existing destination preservation and
conflicting show ownership. A counterfactual changes only the reference song IDs:
serialized prediction inputs and predictions stay identical while scores change.

## Completed development panel

The first completed report is
`local-dev/normalization-data/2026-10-20-phish-file-reference-v1/run-v1/manifest.json`.
It used `track-title-v3/shadow-repertoire-v7` and finished in 0.448 seconds.
All 150 original responses and 3,112 files were admitted. The scorer retained all
3,160 expected memberships, including the 45 files with several song IDs; no
expected provider song ID was missing from the original dictionary.

Exactly 3,060 files agreed on their complete song sets. Across all files, 3,105
memberships agreed, with 3,163 predicted and 3,160 expected: 98.166% precision and
98.259% recall for this panel. The remaining 52 files contribute 55 missing and
58 extra memberships. Every title received a resolved interpretation; that fact
does not adjudicate the disagreements or establish that the audio matches either
metadata account. The original rows retain both accounts for review.

The report contains 13,593,655 bytes of file evidence. Its SHA-256 is
`5407d75d7e272831d232ebac473ad30fa05ca138f72d05352d08971e2dbded6e`.
The initial completed report remains immutable. Subsequent verification runs use
a new output directory.

The final phase-separated implementation was rerun as `run-v2` in the same
private folder after input fingerprint and stream finalization changes. Its
counts are identical to `run-v1`; the final manifest SHA256 is
`517ca3022551a0676a57d931fce3f2c8da7b8450de459a3d04a49e34a116befd`.
