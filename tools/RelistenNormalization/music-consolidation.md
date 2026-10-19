# Shared songs, distinct performances, original files

Music consolidation gives each artist a usable song page across the title accounts
already present in the music catalogue. The shared C# policy decides which titles
refer to a song and which describe a jam, tease, reprise, practice passage or compound.
Python exports evidence, projects those decisions and serves the browser. It does
not implement another title matcher.

A song page leads to distinct retained show UUIDs, then to its matching files.
Sources without a linked show remain recording accounts. Early and late shows on
the same date stay separate. Dates, sets and file positions never join performances.
The original repertoire keys, source/show/file UUIDs and playback references remain
available. These browsing proposals do not publish accepted production identities.

## Produce the input and correspondence image

The completed input bundle contains every **971,127** descriptor in the current
catalogue: 963,490 catalogue, 2,802 local and 4,835 native accounts. Its **33,446**
dictionary spellings come from genuine catalog song rows and unordered memberships:
16,605 catalogue, 236 local and 16,605 native-context names. Provider interpretations
that previously matched a known song do not become new dictionary assertions.

The export reads the completed catalogue and original retained witnesses without
changing reader caches. Every non-null title has an exact JSON value pointer and
chunk SHA256. Unknown titles keep null pointers. Direct dictionary rows take display
priority over duplicate membership witnesses. Same-title legacy identities survive
as separate rivals.

Provider-only runs do not emit their observed catalog dictionaries. For those runs,
the exporter reads the original ordinary input authenticated by the producer's
`inputIdentity`, snapshot path and input-kind counts. `catalog_context` witnesses
retain the original absolute file, physical line and field pointer; their `recordId`
is zero because they are not reader records. C# derives their known repertoire keys
from the actual legacy UUID using the existing shared identity contract.

```sh
python3 tools/RelistenNormalization/music_discovery.py export-normalization \
  --database local-dev/normalization-data/2026-10-18-music-discovery-v1/catalogue.sqlite3 \
  --output local-dev/normalization-data/2026-10-18-music-consolidation-input-v1 \
  --minimum-free-bytes 5905580032 --maximum-bytes 536870912
```

The current input manifest is pinned by SHA256
`c348a81fbe3195301c57fc4f68cb3ebfe09adcd02ebd15fa8a9be0932e1616a5`.
It records the catalogue image, descriptor/dictionary files, 2,705 witness chunks,
the original native context and exporter source versions. An interrupted export
leaves an incomplete directory without a completion manifest; use a new destination.

Run the shared `music-consolidate` operator against that pinned manifest. Its
completed output has one logical `repertoire_correspondences` result per original
scoped key. Each target retains its relation, disposition, reason, qualifiers and
supporting text. A partly understood compound can contain a proposed known-song
target alongside unresolved material. Ambiguous rival targets remain under review.

## Build and serve the separate normalization projection

The projection stores preferred groups, original-to-target relations and output
chunk/line coordinates in a separate SQLite file. It does not copy the existing
510 MB catalogue or the full file index. Complete typed evidence stays in the C#
output chunks. The full projection uses 956,424,192 bytes (912 MiB).

```sh
python3 tools/RelistenNormalization/music_discovery.py project-normalization \
  --database local-dev/normalization-data/2026-10-18-music-discovery-v1/catalogue.sqlite3 \
  --input-manifest local-dev/normalization-data/2026-10-18-music-consolidation-input-v1/manifest.json \
  --output-manifest <completed-output>/manifest.json \
  --normalization <new-projection>/normalization.sqlite3 \
  --minimum-free-bytes 5905580032
```

Projection checks the exact catalogue/input/output pins, original title witnesses,
artist scopes and complete descriptor coverage before publishing. Exact checkpoint
repeats are allowed. Conflicting repeats, changed preferred display facts and
incomplete output fail without replacing the previous completed projection.

Add `--normalization <new-projection>/normalization.sqlite3` to the existing
`music_discovery.py serve` command. Keep the catalogue's configured readers. Those
readers need the new union-query routes described below; their stats advertise
`musicQueries.unionKeys`. Coordinate owned reader restarts before the new browser
cutover. No export or projection command starts or restarts a reader.

The sidecar is valid for one exact catalogue image. A changed image requires a new
matching projection. The original browser can continue without `--normalization`;
its APIs and source files remain unchanged. Without the normalization capability,
the shared HTML uses the existing raw-song routes.

## Public browsing contracts

The existing `/api/songs`, `/api/song`, relationship and file-detail APIs keep their
contracts. The configured browser adds:

- `/api/normalized-songs`: preferred groups plus original accounts that still need
  review, with complete original-title search and the existing scope/artist filters.
- `/api/song-group?scope=&artist=&key=`: preferred display, paged original relations
  and supported composition links. `artist` is the exact artist UUID.
- `/api/original-song-groups`: proposed targets for an original scoped song key.
  The UI opens a single supported group by default; `raw=1` retains an explicit
  original-account view. Compounds with several songs keep those separate links.
- `/api/correspondence`: the original typed C# result, checked against its retained
  manifest, chunk and logical-result hash.
- `/api/song-performances`: the complete selective source union, paged after
  grouping actual show UUIDs. Unbound accounts keep their source-run namespace.
- `/api/performance-files`: deduplicated files at one exact returned `homeKey`,
  with original reader coordinates and C# relation/qualifier labels.

Only **proposed targets** supply the default group file membership. A known song in
a partly understood file remains usable, with a partial-interpretation label.
Review candidates do not make a file an occurrence of every rival. Jam, tease and
compound labels remain visible on file cards and in the original relations.

Cross-reader file deduplication uses the original catalog file UUID, or the original
native key within its run. Reader `recordId`/`parentId` values are rebuildable cache
addresses; native file keys and original origin/revision evidence retain identity.
The reader still owns original-body and playback validation. Native provider URLs
remain distinct from authenticated catalog MP3 playback.

## Reader union queries and failure behavior

Read-only `POST /api/music/homes` accepts
`{artistUuid, entityKeys, limit, offset}`. `POST /api/music/files` also requires the
exact returned `homeKey`. Both use existing indexed memberships and connection-local
requested-key tables. They do not scan all file bodies or depend on a large SQLite
parameter list. Original typed file homes supply bounded-page display dates;
missing native date facts remain unknown.

Requests allow up to 32,768 exact repertoire keys and 8 MiB of JSON, with at most
100 rows per page and a ten-second SQLite deadline. Oversized or invalid requests
fail explicitly. The discovery service reads the complete selective union before
publishing cross-reader totals; failure or a changing source count returns an error,
not a clipped result. `fileCountUpperBound` on a performance is deliberately an upper
bound across overlapping source views. The file page deduplicates actual file keys.

Unknown native recordings are grouped only by their exact retained
`recordingOrigin` within the original input. The compact native cache adds that
account key atomically and backfills only native parents. It never reindexes the
millions of legacy files into a second table. Missing/failed chunks remain hidden;
replay replaces old memberships through the existing parent transaction.

Focused contracts cover literal export and native-context authentication, namesake
rivals, exact repeats and incomplete output, partial targets versus review rivals,
alias search, overlapping readers, same-date distinct shows, original playback,
unknown native accounts, migration, replay, missing chunks and query failure/recovery.
The phone fixture checks the same search-to-file journey at 390 px. Full-run browser
verification follows a completed C# output and coordinated reader cutover.
