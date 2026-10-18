# Browse tapers and transferrers across artists

`people_directory.py` turns a completed shadow run's credit results into a searchable local directory. Open a name, filter by artist, choose a recording by its captured date and performance label, inspect the quoted credit and open its playable files. Each recording retains its source UUID. Multiple roles or mentions on one source count as one recording.

The directory is an experimental projection. It groups names with the same retrieval key used by `NormalizedPeopleService.NameKey`: Unicode NFKC normalization, lowercase and collapsed whitespace. Punctuation and accents remain. A name group can contain namesakes. Explicit aliases in retained actor facts make the originating group searchable and link related groups; they do not merge those groups. The directory never creates or changes accepted people.

The directory scans all 292,523 source results from `2026-10-18-shadow-complete-core-ranges-v1`. Its baseline uses `shadow-people-v1`. A selected 2,000-source overlay now adds retained Luna credit interpretations through the current C# credit adapter. The combined directory contains 253,722 recordings with credits across 4,284 artists, including 2,296 model credit mentions. Luna supplied credits on 151 recordings where the local parser supplied none. These are coverage results, not identity accuracy measurements. The page lists known limitations, including truncated names, missed prose credits and equipment mistaken for people.

## Build and serve

Build into a new file. Existing files are rejected so an active directory cannot be overwritten. The builder reads existing gzip chunks, retains a compact projection and preserves a 4.5 GiB host disk reserve. It makes no provider requests and does not access the catalog database.

```sh
python3 tools/RelistenNormalization/people_directory.py build \
  --run local-dev/normalization-data/2026-10-18-shadow-complete-core-ranges-v1 \
  --database local-dev/normalization-data/2026-10-18-people-directory-v1/directory.sqlite3

python3 tools/RelistenNormalization/people_directory.py serve \
  --database local-dev/normalization-data/2026-10-18-people-directory-v1/directory.sqlite3 \
  --core-url http://alecs-macbook-pro.tail09dbf.ts.net:18765/ \
  --files-url http://alecs-macbook-pro.tail09dbf.ts.net:18767/ \
  --bind 100.73.204.24 --port 18768
```

The source run must be complete. For retained replay chunks, the last source result replaces all earlier credits from that source. Names and alias search terms come from the surviving credits. A failed build leaves its new database file for inspection; choose a new filename for a retry. Serving opens SQLite read-only and does not reread or alter raw shadow output.

The projection includes a `source_homes` table populated from the same run's performance results. It retains each source's captured show UUID, literal date, performance type and label, unresolved details and evidence locator. Partial or unknown dates stay as captured. These values describe the captured catalog assignment; they do not assert that normalization has independently verified that assignment.

An existing v1 projection can add recording homes without rebuilding credits:

```sh
python3 tools/RelistenNormalization/people_directory.py enrich-homes \
  --run local-dev/normalization-data/2026-10-18-shadow-complete-core-ranges-v1 \
  --database local-dev/normalization-data/2026-10-18-people-directory-v1/directory.sqlite3
```

This explicit phase checks the original manifest hash, streams the performance chunks and commits homes and coverage metadata together. The existing directory can keep serving while it runs. Restart its server once afterward to load the new detail response. Fresh builds include the phase. The initial full enrichment read 173 chunks and retained homes for all 292,523 sources; the table and UUID index use 81.4 MB.

## Add retained model credit proposals

The v3 projection can replace credits for an explicit source selection. The C# adapter must first validate model extraction against the captured source fields and produce completed shadow outcomes. Each replacement outcome supplies all local and model credits for that source. The directory copies those typed proposals and their evidence; it does not parse new names or make identity decisions.

```sh
python3 tools/RelistenNormalization/people_directory.py overlay-credits \
  --run local-dev/normalization-data/2026-10-18-luna-credit2000-directory-output-v1 \
  --selection local-dev/normalization-data/2026-10-18-luna-credit2000-directory-input-v1/selected-sources.json \
  --database local-dev/normalization-data/2026-10-18-people-directory-v1/directory.sqlite3
```

The selection contains a `sourceUuids` array. Every selected source must already exist in the directory and have an outcome. Unexpected sources or changed credit ownership abort the phase. It replaces only those sources' credits, then rebuilds derived names, groups and counts from surviving credits in the same transaction. Existing show/date homes remain intact. Count snapshots are committed with their data so HTTP requests read small metadata instead of scanning the credit table. SQLite connections close after each request or writer phase.

Credits keep their actual occurrence key and `interpretation_basis`. `retained_luna_extraction` appears as “Luna proposal” on recording cards; local credit proposals remain separately visible. Repeated local/model mentions of one source do not increase recording counts. The initial overlay reused 2,000 retained model results and made no new API calls. Its source output and selection hashes appear in `/api/stats`.

Overlay credit evidence links open `/api/credit` on this directory. That endpoint reads the exact retained source outcome at its stored run, chunk and physical line. Model person keys therefore do not point to the older core reader, where those keys do not exist. The endpoint retains the model's source-field evidence and extraction provenance without claiming that a proposed person identity has been accepted.

## HTTP contract

`GET /` serves the phone-friendly page. The following endpoints return JSON:

- `GET /api/stats` returns scanned sources, recordings and artists with extracted credits, group and credit counts, source run/method information and known issues.
- `GET /api/people?q=<name>&offset=0&limit=30` searches literal normalized name and explicit alias substrings. Empty search lists groups by recording count. It returns `groups`, `total`, `offset`, `limit` and `basis: inferred_name_group`.
- `GET /api/person?id=<draft-name-id>&artist=<optional-uuid>&offset=0&limit=30` returns a group's spellings, explicit aliases, roles, artist counts, related groups and paged recordings. Recording pages sort by source UUID. Artist filtering changes the recording page and its `total`; group-wide counts remain available.
- `GET /api/credit?id=<credit-id>` returns an indexed credit and its exact retained source outcome. Unknown IDs return 404. Credit IDs identify rows in the current local projection; the retained `credit_key` identifies the extraction occurrence.

Limits are clamped to 1–100 and offsets to zero or greater. Invalid numeric parameters return HTTP 400; unknown groups return 404. Group IDs are stable SHA-256 prefixes of the name key, scoped as `draft-name:`. They are retrieval identifiers, never catalog person UUIDs.

Each recording includes `capturedHome` with `showUuid`, `literalDate`, `performanceType`, `performanceLabel`, `outcome`, `unresolved`, `basis`, `chunk` and `line`. A missing home is `null`; no date is invented. It also includes `playableFilesUrl` using the existing file reader contract `?domain=file_contents&entityKey=source:<sourceUuid>`. Each credit retains the original name, role, source-local person key, exact quoted evidence and code point span, explicit alias facts and originating chunk/physical line. `sourceEvidenceUrl` opens the original core credit or the overlay's retained outcome, according to where that credit exists. The directory does not reassign a file, source, performance or artist. A source without playable files can still have a valid recording credit; the file reader shows its actual results.

The SQLite tables separate name groups, searchable spellings, artists and source credit mentions. Evidence is stored once per extracted credit, without copying whole descriptions or raw payloads. Recording and artist counts use distinct source and artist UUIDs. A recording count describes audio sources, not performances.

## Verify

```sh
python3 -m unittest discover -s tools/RelistenNormalization -p test_people_directory.py
```

The tests cover cross-artist grouping, duplicate roles, preserved quotes and playback ownership, partial date and label preservation, alias search without transitive merging, replay replacement, selected-source model overlays, artist filters, pagination and HTTP errors. They also pass generated URLs through the actual file reader to verify source selection. Inspect representative real names and compare a source's quoted evidence and file links when building a new projection. This directory is ready for local exploration without making a claim about production identity accuracy.
