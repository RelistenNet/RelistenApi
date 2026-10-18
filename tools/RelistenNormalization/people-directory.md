# Browse tapers and transferrers across artists

`people_directory.py` turns a completed shadow run's credit results into a searchable local directory. Open a name, filter by artist, choose a recording by its captured date and performance label, inspect the quoted credit and open its playable files. Each recording retains its source UUID. Multiple roles or mentions on one source count as one recording.

The directory is an experimental projection. It groups names with the same retrieval key used by `NormalizedPeopleService.NameKey`: Unicode NFKC normalization, lowercase and collapsed whitespace. Punctuation and accents remain. A name group can contain namesakes. Explicit aliases in retained actor facts make the originating group searchable and link related groups; they do not merge those groups. The directory never creates or changes accepted people.

The initial dataset uses all 292,523 source results from `2026-10-18-shadow-complete-core-ranges-v1`. That run uses `shadow-people-v1`; its local credit extraction predates the newer identity matcher and model replay. The page lists known limitations, including truncated names, missed prose credits and equipment mistaken for people. Showing those results makes recurring failures inspectable across artists before the next extraction and identity experiment.

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
  --bind 0.0.0.0 --port 18768
```

The source run must be complete. For retained replay chunks, the last source result replaces all earlier credits from that source. Names and alias search terms come from the surviving credits. A failed build leaves its new database file for inspection; choose a new filename for a retry. Serving opens SQLite read-only and does not reread or alter raw shadow output.

The v2 projection includes a `source_homes` table populated from the same run's performance results. It retains each source's captured show UUID, literal date, performance type and label, unresolved details and evidence locator. Partial or unknown dates stay as captured. These values describe the captured catalog assignment; they do not assert that normalization has independently verified that assignment.

An existing v1 projection can add recording homes without rebuilding credits:

```sh
python3 tools/RelistenNormalization/people_directory.py enrich-homes \
  --run local-dev/normalization-data/2026-10-18-shadow-complete-core-ranges-v1 \
  --database local-dev/normalization-data/2026-10-18-people-directory-v1/directory.sqlite3
```

This explicit phase checks the original manifest hash, streams the performance chunks and commits homes and coverage metadata together. The existing directory can keep serving while it runs. Restart its server once afterward to load the new detail response. Fresh builds include the phase. The initial full enrichment read 173 chunks and retained homes for all 292,523 sources; the table and UUID index use 81.4 MB.

## HTTP contract

`GET /` serves the phone-friendly page. The following endpoints return JSON:

- `GET /api/stats` returns scanned sources, recordings and artists with extracted credits, group and credit counts, source run/method information and known issues.
- `GET /api/people?q=<name>&offset=0&limit=30` searches literal normalized name and explicit alias substrings. Empty search lists groups by recording count. It returns `groups`, `total`, `offset`, `limit` and `basis: inferred_name_group`.
- `GET /api/person?id=<draft-name-id>&artist=<optional-uuid>&offset=0&limit=30` returns a group's spellings, explicit aliases, roles, artist counts, related groups and paged recordings. Recording pages sort by source UUID. Artist filtering changes the recording page and its `total`; group-wide counts remain available.

Limits are clamped to 1–100 and offsets to zero or greater. Invalid numeric parameters return HTTP 400; unknown groups return 404. Group IDs are stable SHA-256 prefixes of the name key, scoped as `draft-name:`. They are retrieval identifiers, never catalog person UUIDs.

Each recording includes `capturedHome` with `showUuid`, `literalDate`, `performanceType`, `performanceLabel`, `outcome`, `unresolved`, `basis`, `chunk` and `line`. A missing home is `null`; no date is invented. It also includes `playableFilesUrl` using the existing file reader contract `?domain=file_contents&entityKey=source:<sourceUuid>`. Each credit retains the original name, role, source-local person key, exact quoted evidence and code point span, explicit alias facts and originating chunk/physical line. `sourceEvidenceUrl` opens that source-local person credit in the core reader. The directory does not reassign a file, source, performance or artist. A source without playable files can still have a valid recording credit; the file reader shows its actual results.

The SQLite tables separate name groups, searchable spellings, artists and source credit mentions. Evidence is stored once per extracted credit, without copying whole descriptions or raw payloads. Recording and artist counts use distinct source and artist UUIDs. A recording count describes audio sources, not performances.

## Verify

```sh
python3 -m unittest discover -s tools/RelistenNormalization -p test_people_directory.py
```

The tests cover cross-artist grouping, duplicate roles, preserved quotes and playback ownership, partial date and label preservation, alias search without transitive merging, replay replacement, artist filters, pagination and HTTP errors. Inspect representative real names and compare a source's quoted evidence and file links when building a new projection. This directory is ready for local exploration without making a claim about production identity accuracy.
