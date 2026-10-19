# Find a composition, then hear its versions

The music browser can show possible compositions behind an artist’s song. Open a
work, choose another artist’s song group, then follow its performances to the
original audio. The song groups, show UUIDs and playback references stay in the
existing music and file readers.

These suggestions come from literal titles and explicit aliases in the retained
MusicBrainz work pool. They are unconfirmed. A sole result does not establish
identity, and an empty result does not establish that no composition exists.
The retained-title producer does not choose a work. An optional, separately
authenticated model result can suggest one of those supplied works. The browser
keeps the model rationale, provider assertions and literal matches separate.

The original supported composition links remain at `#works`, `/api/works` and
`/api/work`. `#compositions` opens **Possible versions** when the new projection is
configured, and otherwise opens the supported view. Each song group shows its
supported links separately from possible versions and their alternatives.

## Build and configure

Use the completed C# `composition-hypothesis-output-v1` manifest and the same
normalization projection already used by the music browser:

```sh
python3 tools/RelistenNormalization/composition_catalogue.py \
  --normalization <music-normalization>/normalization.sqlite3 \
  --manifest <completed-composition-output>/manifest.json \
  --manifest-sha256 <composition-manifest-sha256> \
  --database <new-composition-projection>/compositions.sqlite3
```

The private build checks the completion manifest, all chunk hashes and counts,
the exact music input/output generation, qualified work identities and referenced
song groups. Exact checkpoint repeats are allowed; changed repeats fail. The
producer’s complete proposed-song population is authoritative. The projection
reconciles that population and its outcomes while retaining only candidate-bearing
song rows, every work ID, searchable literal facts and original evidence pointers.
It does not copy the music database or read file bodies.

The initial sealed population contains 896,216 proposed song targets, 1,024 works
and 120 retained search pages. It has 3,034 candidate-bearing targets and 893,182
targets without a candidate in this retained pool. Later acquisition checkpoints
are outside this image until another complete C# output is published. The first
projection uses 15,642,624 bytes and retains 27,555 candidate relationships,
including competing works with the same title.

The default projection cap is 256 MiB and the free-space floor is 5.5 GiB. Failure
removes the private build and preserves the previous published projection. Keep
the original C# output, including missing-candidate records and search pages.

Add `--compositions <projection>/compositions.sqlite3` to the existing
`music_discovery.py serve` command. Optional `--people-url` and `--venue-url` accept
HTTP(S) origins for common navigation. Without configured origins their links are
hidden. No build command restarts a reader. Rollback removes `--compositions` and
restarts only that owned music process; original routes and evidence remain.

## Read contracts

`/api/stats` advertises `composition_queries`, complete `compositionSuggestions`
coverage and a generation that includes the composition manifest pin. New detail
and evidence requests require that `generation`. An open page receives HTTP 409
after a generation changes and must reload.

| Route | Result |
| --- | --- |
| `/api/composition-works?q=&scope=&artist=` | Qualified works, searchable by title, alias, writer, ISWC or work ID. Works without song candidates remain present. |
| `/api/composition-work?key=&scope=&artist=&generation=` | One work, its source names, artist filters and paged proposed song groups across artists. |
| `/api/song-composition-hypotheses?scope=&artist=&key=&generation=` | All supplied alternatives for one exact proposed song target, or explicit missing retained candidates. |
| `/api/composition-evidence?kind=work&key=&generation=` | The original typed retained work result. Use `kind=song`, `scope` and `artist` for a candidate-bearing song’s complete hypothesis result. |

Lists accept `limit` from 1 to 100 and a nonnegative `offset`. Composition search
accepts at most 256 characters. SQLite reads have a ten-second deadline. The work
page links to the existing `#song-group` coordinates, whose public performance and
file routes continue to locate audio by exact retained memberships and show UUIDs.

Artist and song keys remain scoped. Work keys use
`composition:musicbrainz:<work-MBID>`; the original MusicBrainz UUID remains
`provider_key`.
Writer names are external provider assertions, with their original roles and
support available in source evidence; they are not canonical people identities
or a complete authorship list. Multiple same-title works and every supplied
alternative remain distinct. `acceptedIdentity` and `publicationPermitted` stay
false, and `inference` stays null for this producer version.

The default landing page searches song titles. Other categories show their own
search labels, preserve the query when switching and offer another category when
no result is found. Common navigation uses optional
`stats.navigation = {songs, compositions, venues, tapers}` links; it contains no
assumed local ports.


## Optional model suggestions

A completed selection run covers its approved plan, which may be one pilot song
or a larger subset. It does not imply that every candidate-bearing song was
processed. The browser reports the covered plan and the source population
separately. A song without a saved result remains visibly unprocessed.

Build the separate selection projection without copying or rebuilding the
composition catalogue:

```sh
python3 tools/RelistenNormalization/composition_catalogue.py \
  --composition-database <composition-projection>/compositions.sqlite3 \
  --manifest <completed-selection-output>/manifest.json \
  --manifest-sha256 <selection-manifest-sha256> \
  --database <new-selection-projection>/selections.sqlite3
```

The default selection cap is 16 MiB; the free-space floor remains 5.5 GiB.
The builder checks the completed report, saved approved plan and preflight,
exact scoped input keys, request cache keys, original hypothesis pointers and
selected/alternative work IDs. C# owns request rendering and fact hashing;
Python authenticates those saved coordinates. Every saved row must keep its
prepared input and supplied work IDs. For resumed runs, the last valid row in
the producer’s chunk order supplies the current result and evidence pointer.
Incomplete runs, invalid rows or changed source generations leave the previous
projection intact.

Add `--composition-selections <selection-projection>/selections.sqlite3` to the
existing serve command. Without this option the title-match experience remains
available. Remove only this option and restart the owned music reader to roll
back model display. Its generation includes the selection manifest pin.

`stats.composition_selection_queries` advertises the optional capability, and
`stats.compositionSelections` gives coverage, outcomes and execution states.
Song hypothesis responses and work-page song rows add `modelSelection`, or null
when no saved request covers that song. The fields include `executionState`,
`outcome`, `inferenceOutcome`, `selectedWorkKey`, `alternativeWorkKeys`,
`rationale`, `method`, `model` and the saved request keys. The original typed row
is available at `/api/composition-selection-evidence?scope=&artist=&key=&generation=`.

The UI labels a selected work **Model suggestion · unconfirmed** and links to its
other artists, existing performances and audio. A valid model abstention is
separate from a skipped request or a transport, configuration or budget failure.
A model choice never removes another title match, hides audio or changes a
supported link. The supplied pool may omit the correct composition; even a
confident explanation can be wrong. Every result keeps `acceptedIdentity` and
`publicationPermitted` false.
