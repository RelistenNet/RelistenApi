# Music discovery from retained normalization results

`music_discovery.py` serves an artist repertoire, composition, setlist and playable-recording browser. It reads a small discovery catalogue and follows exact entity keys into the existing shadow readers. It does not normalize titles, create accepted identities, change playback references or build another file index.

The catalogue includes each distinct indexed repertoire key with its observed title and basis. Captured artist dictionaries and title-derived proposals remain distinguishable. Equal titles retain separate artist-scoped keys. A file result can contain several segments or several candidate songs; opening the exact file shows those alternatives and preserves its source scope and authenticated performance home, when one exists. Source title segments do not establish audio timings.

Native provider files contribute the existing repertoire interpretations in each child’s title mentions, including all candidate songs. The projection reuses those keys and bases; it does not match titles again or turn a keyless unknown into a song. Each child retains its immutable native key, recording/file origin, filename and exact title citations. Multiple files with the same title remain distinct children. The child ordinal is an array locator, not musical order, timing or an occurrence identity.

A composition appears only when a retained composition mention contains an actual supported work link. A qualified MusicBrainz work key can join repertoire songs across artists and scopes. Search candidates and competing work keys never create those links. A mention with several alternative repertoire keys does not choose one for the composition association.

Ordered setlists render source sets, passages, labels, markers and notes. Empty sets remain visible. Unordered legacy memberships remain unordered; no occurrence UUID, position or set boundary is inferred. Account notes remain separate from performed passages. Original result JSON is available for inspecting annotations and citations. If an account supplies an exact catalogue show UUID, its files are browsable by that show key; a date alone never creates a recording link or passage-to-file alignment.

## Build and serve

Run from the repository root. Each `--source` supplies a scope, existing run directory and private reader origin. Reuse the `catalogue` scope for core and full-file production-catalogue results. Use `local` for local collections; a coincident local UUID never merges those identities. A native run containing production providers and separate local collection coordinates must use its own explicit scope, such as `native`. Equal matched repertoire keys are reused inside that scope without joining it to `catalogue` or `local`. The UI labels all three namespaces separately.

```sh
python3 tools/RelistenNormalization/music_discovery.py build \
  --database local-dev/normalization-data/music-discovery/catalogue.sqlite3 \
  --source catalogue /absolute/core-run http://host:18765 \
  --source catalogue /absolute/full-file-serving-run http://host:18767 \
  --source local /absolute/local-collections-run http://host:18766 \
  --source native /absolute/native-provider-run http://host:18771

python3 tools/RelistenNormalization/music_discovery.py serve \
  --database local-dev/normalization-data/music-discovery/catalogue.sqlite3 \
  --reader catalogue /absolute/full-file-serving-run http://host:18767 \
  --host 0.0.0.0 --port 18769
```

Keep this server on a private interface. Result bodies can contain retained source prose. The browser proxies only configured readers; it accepts source IDs and bounded coordinates, not arbitrary destination URLs. Native child audio uses only the existing reader’s authenticated `catalogPlayback` result. A retained HTTP(S) provider locator is a separately labeled external link, never an audio control or proof of a catalogue home. Native parent bodies are available for provenance but their pooled playback URLs are not rendered as child playback.

An optional serve-time `--reader` adds a relationship-only reader before its titles have been projected. The UI labels that missing title-discovery coverage explicitly. Once the catalogue includes the same scoped run, it uses that projected source instead.

The builder opens each existing reader SQLite cache read-only. It accepts the compatible compact v2 or v3 schema without upgrading the shared cache. It selects current logical witnesses, then reads their immutable gzip coordinates once per selected chunk. SQLite stores titles, scoped identities, supported work associations and witness locators. It stores no file memberships, audio, full evidence bodies or copied raw catalogue.

A build uses a private replacement file with a **256 MiB default cap** and a **384 MiB explicit ceiling**, plus a **4.5 GiB default free-space floor**. Increase the cap only after measuring the needed title population and available space. The earlier full-title attempt paused at its 4.7 GiB floor. The next prepared build uses a **5.5 GiB floor and 384 MiB cap**; it has not started and waits for complete full-file indexing. The builder reuses processed staging pages before adding the next chunk’s final descriptors. Capacity or immutable-witness failures remove only that replacement. The last completed catalogue remains usable. Rebuild after more source chunks have been indexed to discover additional titles. Browser source lookups carry the catalogue generation; a rebuild that changes source IDs requires a page reload rather than opening another run's record by accident.

## Coverage and source responsibility

The discovery snapshot covers indexed current records in each source at its own read transaction. The full producer population and the reader's indexed population are different quantities. A completed producer can have an incomplete reader index. An empty search means no entry in this discovery snapshot, not proof that a song, recording or work is absent.

Several source runs can cover the same artist. Later configured runs replace display witnesses for an identical scoped repertoire key, while supported work associations retain the independently observed source contributions. The projection does not reconcile cross-run withdrawals. Within each reader, replaced logical records and removed entity memberships are excluded. The browser labels source readers separately when displaying file relationships; counts are per reader and should not be added as unique performance counts.

The catalogue is a snapshot. File searches use each reader's current exact-key membership, and opening a result invokes that reader's immutable-body checks. A temporarily unavailable reader leaves artist, song and composition discovery available. Large original bodies over the browser proxy's 8 MiB limit must be inspected in the source reader.

## HTTP contract

All routes are read-only and return JSON except `/`, which serves the UI.

- `/api/stats` returns catalogue counts, source snapshots and a string `generation` and backend capability `native_file_queries: true`. The UI requires that capability before calling child routes, so an older process serving the new HTML keeps its legacy behavior during cutover. Projected sources include `native_files` (indexed children), `native_file_parents` (indexed provider-input accounts, including zero-file accounts), and `file_query_mode` (`native`, `mixed` or `records`). These counts describe the index at build time; they do not certify complete computation or live indexing. Old caches without child tables remain readable with zero native counts.
- `/api/artists`, `/api/songs`, `/api/works`, `/api/setlists` return paged `items`, `total`, `limit` and `offset`. `q` is a literal case-insensitive substring, never a normalization or identity decision. Songs and setlists can filter `artist` UUID and `scope`; artist search can filter scope.
- `/api/song?scope=…&key=…`, `/api/work?key=…`, `/api/setlist?scope=…&key=…` return the retained identity descriptor and source witness. Song/work details include paged supported associations, with `works_total` / `songs_total`, the corresponding limit and offset. The same `limit` and `offset` parameters page those relationships.
- `/api/related?source=…&generation=…&key=…&domain=file_contents` performs an exact entity-key query in a configured reader. Other allowed domains are `setlists`, `compositions` and `repertoire`. Pagination is per source.
- `/api/record?source=…&generation=…&id=…` returns the original reader’s retained parent body. The UI renders legacy playback here; native input accounts link to individual children instead.
- `/api/files?source=…&generation=…&key=…` queries exact child entity-key memberships through the configured reader. `limit` and `offset` page native files, not parent accounts or performances. A parent containing another matching sibling cannot cause a nonmatching child to appear.
- `/api/file?source=…&generation=…&parentId=…&nativeFileKey=…` opens one exact retained child. It returns the original typed file, mentions and evidence, separately authenticated catalogue playback, and a separately labeled provider locator. A missing child returns 404; the proxy never substitutes a sibling.

A stale generation returns 409 and requires reload. Unknown identities return 404. Reader failures return 503 without discarding the completed catalogue. Search and relationship pages are capped at 100 entries.

## Verification

```sh
cd tools/RelistenNormalization
python3 -m unittest -v test_music_catalogue.py test_music_discovery.py
```

The tests build real small shadow reader caches and exercise scope separation, logical replay and withdrawal, captured versus provisional identities, supported links versus rivals, same-title alternatives, ordered versus unordered setlists, immutable witness refusal, bounded replacement failure, HTTP playback provenance, exact-key native child retrieval and pagination, Unicode citations, ambiguous mentions, logical child removal, zero-file accounts, unavailable reader recovery and stale-generation refusal.

## Current private rehearsal

The native integration is live on port 18769. Its source reader has 4,690 domain outcomes for 670 provider inputs and 34,139 child files. These are observations, not unique songs or performances. The shared catalogue also includes the completed cached-composition replay: 24,242 scoped song keys, 31 supported work proposals, 35 work/song associations and 38,517 setlist accounts. All prior song, artist and setlist memberships survived the atomic replacement. “Crazy Fingers” connects separate Grateful Dead and Dark Star Orchestra repertoire identities through cited work evidence; exact song-key queries reach their existing playable files.

The current database is 31,735,808 bytes. Actual HTTP checks and phone-width inspection verified native child lookup, original evidence, catalog MP3 playback and separate provider links. This native coverage does not satisfy the broader full-reader title projection. Full-file indexing has resumed; one complete title build follows it. Builds retain the normal free-space floor and publish only a complete replacement, including space for the temporary database.

The first completed core/local image contains 4,626 scoped artist entries, 19,407 song keys, five supported work keys and 38,517 setlist accounts in 43,565,056 bytes. Full-reader relationships are available independently of the broader title augmentation. The full reader contains 963,476 repertoire keys and 32 qualified work keys at the measured indexed snapshot. Augmentation paused at its 4.7 GiB free-space floor during staging; no partial replacement was published. Its title coverage remains explicitly unprojected until a complete replacement succeeds after capacity becomes available. These counts describe discovery entries, not independent songs, people or performances.

The live private page is [music discovery](http://alecs-macbook-pro.tail09dbf.ts.net:18769/). Useful retained examples are [Bob Weir’s “Poison Ivy”](http://alecs-macbook-pro.tail09dbf.ts.net:18769/#song?scope=catalogue&key=repertoire%3Ae3a2010ff6a95ec81c426f6bbf49a9c4), which returned 38 indexed files at verification, and the [Grateful Dead July 2, 1995 ordered account](http://alecs-macbook-pro.tail09dbf.ts.net:18769/#setlist?scope=catalogue&key=setlist_account%3Ae93236cf65318ee1207cdf33443b55c2), with three source set labels and 18 passages. The latter preserves ambiguous dictionary alternatives and keeps the separate source notes out of the performed sequence. A [qualified “Crazy Fingers” work](http://alecs-macbook-pro.tail09dbf.ts.net:18769/#work?key=composition%3Amusicbrainz%3A8973193c-be9e-450d-9e26-2cced570c5aa) leads into the independently scoped local repertoire and its recordings.

The largest remaining domain limit is composition coverage: this browser can connect only the supported work correspondences already present in the results. It supplies discovery and playback over retained outcomes; it does not activate an identity policy or turn plausible title/author similarity into a global work link.
