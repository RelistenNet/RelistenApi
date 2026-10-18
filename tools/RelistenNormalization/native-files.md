# Native file contents

`ShadowNativeFileContentsAdapter` projects every file returned by the existing
provider decoder. One `provider_input` produces one `file_contents` result with
`proposal.files[]`. Each child retains its original recording and file origins,
provider identities, filename, title, locator, position, set label and text. No
file-count cutoff or song-ranking cutoff removes files from this inventory.

To process native inputs while retaining the catalog context they need:

```sh
dotnet run --project tools/RelistenNormalization -- \
  shadow-native /absolute/context-and-native-inputs.jsonl /absolute/new-output
```

The input can contain catalog artist, song, source and show rows alongside native
provider rows and supporting Local roots. All rows are observed to build the
dictionary and ownership context; only `provider_input` rows produce results.
`RELISTEN_SHADOW_PROVIDER_INPUTS` can name a separate retained provider JSONL file.

The run records `processingScope: provider_inputs_only`. Restart the same command
with `--resume` and the same inputs and method versions. Changing between
`shadow-native` and `shadow-rows` requires a new output directory: the processing
scope is part of the run identity. The public `RunRowsInlineAsync` method exposes
this choice as `providerInputsOnly`, whose default remains `false`.

`fileOrdinal` is the child's zero-based position in that result's `files` array.
It addresses a reader card; it does not establish a song occurrence or musical
order. `entityKey` identifies the exact native file observation, including its
input revision and evidence coordinates. Duplicate filenames remain separate
observations.

## Titles and song links

The adapter uses `TrackTitleExtractor` and
`ShadowRepertoireAdapter.ResolveTitleMention`, the same title policy used for
catalog files and provider setlist passages. Supply the retained artist song
dictionary through `Observe`, or share an observed repertoire adapter. A native
song identifier alone does not establish a catalog song UUID.

`titleInterpretation` retains the complete original title and title-relative
syntax evidence. Each `mentions[]` entry contains the repertoire interpretation
and a second evidence reference in the original provider field. Those references
use Unicode code-point offsets. Title order, segue markers and partial labels
describe the title only; no audio boundaries or performance assignments follow.

Missing or malformed titles leave the original file inspectable. Decoder issues
are retained both on the parent and, where their coordinates belong to a file,
on that child. A title failure cannot erase another file. An unsupported capture
or invalid input hash produces an explicit failed parent result.

## Catalog links and playback

Catalog source and show UUIDs are optional historical links supplied by
`ShadowProviderObservations`. Track and source-set UUIDs require its exact file
anchor. A contradictory explicit provider recording owner prevents inheriting
the enclosing recording's catalog links.

`catalogPlayback` uses `ShadowFilePlayback`. It is populated only from the
shared helper's exact catalog file anchor and captured media URL. Current Local
admission establishes those anchors through the original root/directory pair,
exact media URL and catalog artist/source/set ownership. Archive and Phish native
files remain inspectable without guessed legacy track UUIDs or playback links.
`file.playbackLocator` is the literal provider value; it is not catalog playback.

Each child's `entityKeys` contains its native observation key, supported song
candidate keys and authenticated catalog home keys. The parent contains their
complete union. Reader child cards retain the parent chunk/line coordinates and
`fileOrdinal`; they do not allocate new catalog file identities.

These results are proposals. They do not publish normalization assignments,
change existing source/show/file UUIDs, or supply acceptance authority.
