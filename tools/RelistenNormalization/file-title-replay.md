# Replay retained file titles

The first full file capture used `track-title-v1`. Its unresolved dictionary
matches remain unresolved when a later pass only remaps existing song keys.
For example, joining the Dead's duplicate “Drums” entries does not give a song
key to an old file interpretation that never selected either entry. File title
replay applies the current shared resolver to those original titles as well.

The exporter selects every current `file_contents` result from each reader in
the full music input manifest. It includes both catalog files and individual
native provider files. Files without titles remain in the ownership map. The
exporter checks original headers, file ownership and exact title coordinates;
it does not interpret titles or choose songs.

Literal `(scope, artist, title)` values are processed once. The export's integer
title IDs join files to these values inside one pinned artifact. They are not
song, file or performance identities. The C# replay uses the same complete
dictionary, descriptor population and optional reviewed snapshot as music
consolidation. It observes every retained file title before resolving any of
them. That makes new file-only targets available to later review decisions and
keeps preferred spellings independent of file order. The shared resolver
supplies stable artist-song keys; `fileReplayVersion` pins the replay method.

## Run the offline pass

Use new directories for all three steps. Keep the current reader running while
building the replacement. None of these commands starts a model client,
imports from a provider or writes to a catalog database.

```sh
python3 tools/RelistenNormalization/file_title_replay.py export \
  /absolute/path/music-input/manifest.json \
  /absolute/path/replay-input MUSIC_INPUT_SHA256

dotnet tools/RelistenNormalization/bin/Debug/net10.0/RelistenNormalization.dll \
  file-title-replay /absolute/path/replay-input/manifest.json \
  /absolute/path/replay-output REPLAY_INPUT_SHA256

python3 tools/RelistenNormalization/file_title_replay.py project \
  /absolute/path/replay-output/manifest.json \
  /absolute/path/replay-projection REPLAY_OUTPUT_SHA256
```

For a reviewed snapshot, append `--knowledge /absolute/path/snapshot.json
SNAPSHOT_FILE_SHA256` to the C# command. Preserve that snapshot with the run.
The output records both its internal content hash and its retained file pin;
it must not be described as reflecting later
decisions until those decisions have been applied.

Each step publishes its completion manifest last. A failed or cancelled step
leaves incomplete artifacts for inspection. Repeating these local calculations
in a new directory makes no paid calls. The previous completed generation
remains usable.

## Read the results

`replay-input/files.sqlite3` owns the compact original-file map:

- `readers` identifies each original reader and source scope.
- `chunks` retains original body paths and hashes.
- `titles` holds exact literal titles and one original witness for each value.
- `files` links a title to every original file, source, show, parent record and
  optional native child ordinal. A missing catalog UUID stays missing. Native
  file keys remain their original provider observation keys.

`replay-projection/replay.sqlite3` contains C# results:

- `interpretations` records each title's original repertoire key, disposition,
  reason and sequence basis.
- `targets` records each ordered target, relation, disposition, exact title
  evidence and qualifiers. Repeated mentions remain separate rows. Only
  `disposition='proposed'` targets supply default song membership.
- `native_homes` preserves the existing provider recording account for native
  files without a catalog show or source. It uses the same account-key function
  as the original reader. A provider filename does not create a catalog UUID.
- `metadata.fileMap` pins the separate file map. Attach that immutable file
  when joining `targets.title_id` to `files.title_id`; do not copy millions of
  original files into another catalog.

The projection also retains full input and output manifest pins and both
knowledge pins. Its source-key checks handle older compact reader indexes whose
header UUID column is empty. The original packed input key still binds each
file UUID to its exact retained coordinate.

File search and the reference comparison must consume this same projection.
Search follows a target to original file coordinates, then reads playback from
the original result. It must not substitute a new show, source or audio URL.
The comparison retains the original raw-ID result and scores replayed results
separately. An improved score is not evidence that a source title or incomplete
setlist reference is correct.

The projection validates every output title against the export's original
title, scope, artist and witness. A missing title result, changed artifact or
cross-artist target prevents completion. Distinct qualified compositions remain
reviewable when the shared resolver finds a contradiction.

Run the existing whole-artist comparison with the same completed receipt used
by discovery:

```sh
python3 tools/RelistenNormalization/artist_setlist_benchmark.py \
  --request /absolute/path/reference-request.json \
  --output /absolute/path/new-comparison \
  --file-title-replay /absolute/path/replay-projection/manifest.json
```

`resolvedIdentity` uses replayed proposed targets for every captured titled
file. A covered title with no proposed target contributes no song. A file
without a captured title keeps its previous prediction as an explicit fallback.
The report counts both paths. `rawIdentity` and the existing top-level results
retain the original comparison, so the change can be assessed against the same
files and references. References pass only through the existing same-artist
identity-alias map; replay predictions do not change the reference answers.

This command evaluates the frozen knowledge snapshot in the replay receipt.
It does not read later review decisions. Save a separate comparison generation
when evaluating subsequent expert feedback, and label that evidence as exposed
development data.

For discovery, add `--file-title-replay` with that receipt to the existing
`music_discovery.py serve` command. The reader rejects a replay from a different
music input, title policy or resolver version. Original playback remains in the
source readers. To roll back replayed membership, restart discovery without the
flag; keep the completed artifacts for comparison and recovery. Neither action
moves or rewrites a recording.

Performance-page playback links pass the optional `fileKey` to `/api/record`.
The reader checks the retained map against the current record ID, file UUID,
artist, chunk and line. Native files also retain their original key and child
ordinal. Before returning playback, the reader verifies the selected original
witness hash. An unrelated append to a source index remains valid. A moved
coordinate or edited witness returns HTTP 409, and the original source readers
remain available.

`/api/replay-song` authenticates evidence for a target that exists only in the
file replay. It can still show that immutable baseline evidence after a review
moves the target's current memberships. Membership pages use the compact file
map; they do not hash every source body on each request.
