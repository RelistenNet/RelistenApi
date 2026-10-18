# Capture the complete raw catalog for normalization

The first integrated shadow run processes all **86,655 retained raw sample rows**, producing 258,987 domain outcomes. The [reader](http://alecs-macbook-pro.tail09dbf.ts.net:18764/) exposes those results and their original evidence. Full raw catalog capture is now running against the read-only replica. It uses the complete metadata roster to acquire recording prose, venue names, song titles and file URLs as compressed shards.

## What is retained

| Evidence | Raw inputs available | Population described | What it does not supply |
| --- | ---: | ---: | --- |
| October 17 catalog capture | 32,593 sources; 34,861 flag-active files; 17,073 venues; 1,696 songs; 432 setlist shows | 4,379 artists; 292,476 sources; 5,063,161 files; 216,304 venues; 16,605 songs; 38,475 setlist shows | Raw values for the rest of those tables |
| October 18 source/show metadata frame | No raw prose | 4,379 artists; 292,522 sources; 215,594 shows | Source credits, descriptions, titles or provider ownership |
| October 18 domain metadata frame | No raw names, titles or URLs | 5,064,076 files; 216,331 venues; 296,221 source sets; 16,605 songs; 38,475 setlist shows; 575,103 song/show pairs | Actual strings, writer assertions or setlist occurrence order |
| Current extraction output | 67,454 source/file interpretations; zero processing errors | All 32,593 source samples and 34,861 file samples | Full-catalog completion or validated identity accuracy |

The 86,655 raw sample rows span all eligible artists in their respective selections. Sampling was capped per artist; artist coverage does not mean recording/file coverage. The extraction command does not process the 17,073 venue, 1,696 repertoire or 432 setlist samples. The integrated shadow adapters process those rows; the [first-run receipt](evidence/shadow-catalog-first-run-v1.json) records their outcomes.

The snapshots are independent. Do not combine their counts into one transaction or assume that an October 17 raw row is unchanged in October 18 metadata. A later raw intake needs its own counts, snapshot stamps, missing/drifted-input accounting and completion receipts.

The complete domain frame includes **157,656 files with neither source nor source-set pointer**, all with valid artist pointers. Keep those files. The 52,759 stored orphan flags describe a different condition; do not use that flag as a substitute for raw parent links or filter those rows out. Their titles can still produce artist-scoped song proposals while their recording/performance home remains unresolved.

## Provider and domain ledger

These are the seven configured ingestion providers. Artist configuration is context, not proof that a provider owns each source. The population capture has 4,377 artists configured for Archive, one each for Phish.in, Phish.net, Jerry, Local and Setlist.fm, and two for Phantasy Tour; mappings overlap.

| Provider | Retained catalog evidence usable now | Native input still needed or separately retained |
| --- | --- | --- |
| `archive.org` | Sample recording credits/lineage, file titles, venues and artist configuration | Complete catalog raw fields remain missing. Local Jerry/Aadam/Gov’t Mule intake survives in local catalogs/dumps; portable native item revisions require a separate export. Taper’s Section and Aadam are Archive collection scopes, not extra providers or performing artists. |
| `phish.in` | Phish recording/file and repertoire samples | Catalog metadata does not recover ordered native song passages. Existing decoder supports `show` / `phishin-show-object-v1` and `catalog` / `raw-http:1`; use retained revisions when available, otherwise acquire them separately. |
| `phish.net` | Phish artist mappings and imported source metadata | Native notes/credit field captures exist, but a notes field is not a whole show/setlist response. Decoder supports shows, reviews and ratings representations; their applicability differs. |
| `setlist.fm` | Imported venue/repertoire rows and unordered legacy setlist membership | Ordered sets require a retained `setlist-page` / `setlist-fm-http-v1` response. Whole-catalog membership rows cannot recreate that order. |
| `phantasytour.com` | Configured artist dictionaries and legacy setlist membership | Use actual `show` / `phantasy-tour-http-v1` responses for order; `show-list` is discovery, not a complete show account. |
| `jerrygarcia.com` | Legacy setlist membership plus one genuine retained mirror show page | `processing-v1/capture.json` and `provider-capture-v1/show.body` under the real-setlist rehearsal provide one original typed revision, with three sets and 18 ordered passages. Wider native page coverage remains separate. |
| `local` | Catalog source/file samples; 150 separately retained native credit fields | Native root and directory revisions must remain paired. The root establishes date association; a directory alone does not. File order is not automatically an authoritative setlist order. |

PanicStream remains a known legacy enum value but is not one of the seven enabled Temporal providers. Do not report it as covered merely because the enum or an old importer class exists. Cross-artist compositions also need explicit writer/work evidence from separately retained provider inputs; five million catalog titles do not create that evidence.

## Reuse the local and typed inputs

The local readiness receipt records 701 sources and 7,826 files in the Jerry/Aadam catalog, and 413 sources and 7,561 files in the Gov’t Mule catalog. The Jerry collection has 348 linked recordings across six projects; Aadam has 299 across 252 artists. These are local catalog counts, not additional production counts. Preserve the local namespace and collection membership separately from the performing artist.

- Local rehearsal catalogs: `relisten_jerry_20261017`, `relisten_govt_mule_20261017`; retained rehearsal dumps and table fingerprints are indexed by [the local rehearsal receipt](evidence/local-normalization-rehearsal-v1.json). Their complete native input revisions are not portable typed-revision exports yet.
- Portable local file/performance catalogs: `local-dev/normalization-data/2026-10-17-normalization-api-rehearsal-v2/jerry-api50/input.json` contains all 701 source coordinate records, 7,826 file title/URL records, 717 sets, 607 shows and 268 artists at that capture. `govt-mule-api50/input.json` contains 413 source coordinates, 7,561 files, 429 sets, 325 shows and six artists. These arrays can supply catalog wrappers and exact ID joins now, without DB access. Source coordinate records omit credits/lineage/prose; do not present their missing fields as a complete empty credit account. Keep each original database namespace and snapshot hash.
- Raw local challenge records: eight sources and eight files in the [evaluation capture](../evaluation/CAPTURE.md), including four Jerry projects and four Aadam artists. These are selected examples, not all 701 sources.
- Genuine ordered Jerry page: `local-dev/normalization-data/2026-10-18-real-setlist-rehearsal-v1/provider-capture-v1/show.body`, 77,369 bytes, SHA-256 `0538513070936b6586e18a6e37be4cca40c9b10e5ede09a09aeb8393ff3f905f`. The original revision metadata is in `processing-v1/capture.json`; the expected artist is in `processing-v1/request.json`. The [setlist receipt](evidence/setlist-local-v1.json) records the capture and order contract.

For the performance adapter, use its `provider_input` wrapper: `row` identifies the real catalog artist and input UUID; `input` is the actual `NormalizationInputRevision`. Map `capture.json.revision` to `input`; do not pass the outer `{revision, fetch}` receipt. Set `row.uuid = revision.uuid` and `row.artist_uuid = request.source.expectedArtistUuid`. Copy the original UUID, resource, SHA, media type, capture version and capture time. Restore its omitted `Payload` property by base64-encoding the exact retained body bytes. Do not mint a replacement revision, rebuild HTML/JSON, or present a decoded proposal as original evidence. This wrapper can be prepared without refetching or mutating the local database.

## Complementary compressed raw intake

The operator [capture_shadow_catalog.py](../../../../tools/RelistenNormalization/capture_shadow_catalog.py) streams `psql` through gzip. It retains compressed shards, compact manifests and failed prefixes without an uncompressed full-catalog copy. Keep a 4 GiB free-space floor and, initially, an aggregate **3 GiB compressed intake ceiling**; the operator must account separately for compressed shadow outputs/index space. These are refusal bounds, not estimates of compression or completion. Stop and preserve a partial shard when a bound is reached. No storage deletion is authorized.

Use native-ID ranges from the already retained complete metadata roster, with expected row counts per range. At intake start, read only current `COUNT(*)`, `MIN(id)` and `MAX(id)` for each raw table; add a planned tail range above the retained maximum. Emit extra rows within old ranges and reconcile missing old IDs explicitly. Recheck counts/maxima at the end: later inserts are pending incremental work, not a silently completed current catalog. Completion means the declared roster/ranges at their retained shard snapshots, not an eternally moving database tip. Start with at most 50,000 file rows per shard and one payload SELECT per transaction. Read with `psql -X -qAt -v ON_ERROR_STOP=1 -v FETCH_COUNT=1000`; the cursor fetch count prevents libpq from buffering the whole result. Each shard uses a read-only repeatable-read snapshot, a 20-minute statement limit and an external 20-minute deadline. Record the current shard snapshot and compare emitted IDs/UUIDs with its planned roster. Different shards are not one historical snapshot. Process them as separate retained inputs, not a concatenated stream claiming one snapshot. Load the already retained artist mapping dictionary as shared reader context. Missing, extra and changed rows must be reported, never silently called complete.

Retain every file, including flagged/unbound files. Do not add `LIMIT`, per-artist rank, `is_orphaned=false`, an inner parent join or a title-nonempty predicate. Reuse the existing row kinds for adapter compatibility; `_sample` in their names is historical syntax, not a claim that a full-range shard is sampled.

Run from the repository root. The initial invocation creates a new job; resume continues its exact request and range plan:

```sh
python3 tools/RelistenNormalization/capture_shadow_catalog.py \
  --output local-dev/normalization-data/2026-10-18-shadow-full-raw-v1

python3 tools/RelistenNormalization/capture_shadow_catalog.py \
  --output local-dev/normalization-data/2026-10-18-shadow-full-raw-v1 --resume
```

The actual job began with one source shard, then resumed across all domains.
Its first 50,000 recordings produced 30,050,322 compressed bytes, with zero
missing rows, extra rows, UUID differences or parent-coordinate differences.
The recording, venue, song and setlist ranges have since completed; file ranges
are running. Read `manifest.json` for current counts, pending ranges and drift
rather than using the metadata roster as proof of raw completion.

`payload_sql` in the operator defines each row projection. Source shards retain
all six credit/prose fields plus show and venue context. File shards retain the
full file row and both direct and source-set parent coordinates, while omitting
duplicated source prose. Setlist shards preserve every stored membership and
explicitly state that occurrence order is unavailable. No provider import runs.
Each completed shard repeats the small artist inventory so it can be consumed
independently. A combined normalization run must count those artists once.

A shard becomes complete only after its payload, footer and PostgreSQL process
succeed, including COMMIT. The receipt retains raw and compressed hashes, the
original read-only snapshot, expected/actual rows and exact reconciliation.
Failed prefixes count toward the same compressed-byte limit. Resume verifies
the original request, range plan, source file and completed artifacts; it never
overwrites a failed attempt. Fifteen offline tests cover streaming, capacity,
failure and recovery behavior. Root also verified the first real shard's gzip,
header/footer, native relationships and counts.

The retained profiles sum to 348,957,780 UTF-8 bytes across source text fields and 1,274,420,309 across domain text fields. These are text lower bounds, not JSON or gzip sizes. The largest observed source description is 32,477 bytes and taper notes 22,153; file titles reach 634 bytes. JSON escaping, row keys and repeated set/show coordinates add overhead. The shadow runner and reader already use compressed outputs. Inline extraction from the new captured shards is the next integration step. A five-million-file completion claim needs all expected shard IDs and explicit outcomes, not just the original 67,454 extraction outputs.

## Evidence pins and verification scope

- Raw sample SHA-256: `1bcfa40bfc666c1222b3ee876250b61a76cd9b9adbbb978b62db1d16952166a4`, 176,666,741 bytes; [coverage](evidence/coverage.json).
- Current extraction manifest: `local-dev/normalization-data/2026-10-18-shadow-catalog-extraction-v1/manifest.json`, SHA-256 `bc2c9533ad42aff8352ec96ec694d95248a4bb4888006ef0a2f628424d7f90fd`.
- Complete source/show metadata manifest: `local-dev/normalization-data/2026-10-18-full-metadata-frame/complete-frame-v1/manifest.json`, SHA-256 `0955975899fd82467295e2483b39f03e86cb455c6cc5f34e6ce1dcd9199bef16`.
- Complete domain metadata manifest: `local-dev/normalization-data/2026-10-18-domain-metadata-frame/full-domain-capture-v1/manifest.json`, SHA-256 `c50a6b3a76c6ed685e568ca997dcf8eaa7fb0018d528fece4364aa9abf800e02`; [frame contract](full-domain-metadata-frame.md).

The metadata roster, raw shards and normalized proposals have separate provenance. Full completion requires every declared range and an outcome for every applicable input/domain. Newly inserted or changed catalog rows remain explicit incremental work; independent shard transactions do not establish one historical snapshot.
