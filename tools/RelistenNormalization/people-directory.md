# Browse tapers and transferrers across artists

`people_directory.py` turns a completed shadow run's credit results into a searchable local directory. Open a name, filter by artist, choose a recording by its captured date and performance label, inspect the quoted credit and open its playable files. Each recording retains its source UUID. Multiple roles or mentions on one source count as one recording.

The directory is an experimental projection. It groups names with the same retrieval key used by `NormalizedPeopleService.NameKey`: Unicode NFKC normalization, lowercase and collapsed whitespace. Punctuation and accents remain. A name group can contain namesakes. Explicit aliases in retained actor facts make the originating group searchable and link related groups; they do not merge those groups. The directory never creates or changes accepted people.

The directory scans all 292,523 source results from `2026-10-18-shadow-complete-core-ranges-v1`. Its baseline uses `shadow-people-v1`. A selected 2,000-source overlay now adds retained Luna credit interpretations through the current C# credit adapter. The combined directory contains 253,722 recordings with credits across 4,284 artists, including 2,296 model credit mentions. Luna supplied credits on 151 recordings where the local parser supplied none. These are coverage results, not identity accuracy measurements. The page lists known limitations, including truncated names, missed prose credits and equipment mistaken for people.

## Credit extraction policy

The shared recording byline grammar separates coordinated actions, names and later actions before assigning roles. For example, “Recorded, Mastered and Posted by Pat Myers AKA DATPAT” names Pat Myers, retains DATPAT as an explicit alias and records the supported roles. “Posted” remains an `Other` role with its original wording. A later “Transfer by Keo” starts a new credit. An unsupported action such as “engineered by” also starts its own credit and keeps `Other`; it cannot inherit the preceding taper role. These are source claims, not accepted identities.

Attached complete contacts retain their original spelling and a separate lookup key. Literal uppercase `AT` and `DOT` separators can describe a complete address. When more than one mailbox/domain split is possible, the literal stays unresolved without a guessed lookup key. An incomplete attachment such as “(vwmule at gmail)” stays unresolved without adding a domain suffix. A separate address does not become another taper or an attributed contact. Literal handles, including “Taper Chris” and “Jay in KC,” and ambiguous parentheses remain intact.

Credit grammar v5 and source-alias grammar v4 apply only when new outputs are extracted. The actor fact format remains v1. Existing run chunks, directory rows and the frozen contextual comparison cohort keep their original producer versions and evidence. A later refresh must regenerate affected credit and alias projections together, then prepare new context questions against those occurrences. Keep the previous generation for inspection; its occurrence IDs and comparison witnesses do not silently acquire the new interpretation. Historical v2 and v3 alias reviews use their original byline and attachment grammars.

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

## Refresh source credits and aliases into a new generation

Use `refresh-sources` after a complete source-only extraction with the revised grammar. Produce that extraction with the [source-observation command](source-observations.md). It shares actor, credit and alias extraction with full matching and explicitly records that identity comparison was not run. Include retained model-credit results; a parser-only result would remove model credits absent from its complete proposal.

Seal all context attachments before starting. The command takes a consistent SQLite backup of the previous directory and writes a separate database:

```sh
python3 tools/RelistenNormalization/people_directory.py refresh-sources \
  --run <completed-source-refresh-run> \
  --source-database <sealed-previous-directory.sqlite3> \
  --database <new-directory.sqlite3> --context-sealed
```

The new run must cover exactly the previous source population, with paired people/credit outcomes and unchanged artist ownership and six original text fields. Replay uses each source's latest outcome; a latest failure prevents publication. The command preserves homes, native observations and the old generation. `sourceRun` still owns captured homes; `sourceRefreshRun` identifies the new extraction. Each changed credit keeps its own original run locator.

Only a unique, fully unchanged credit witness keeps its old row ID and evidence locator. Comparison includes the raw fields, name, role, literal role wording, citations, actor facts and model basis. Changed, split or removed credits invalidate their contextual relationships. New generations retain a credit-ID high-water mark so later additions cannot reuse retired IDs. Old questions retain their original witnesses; they cannot be reattached by name or row number.

The default bounds are 3 GiB for the combined new run and staging database, plus a 5.5 GiB host reserve. Chunk checkpoints permit retry with the same sealed inputs and target. After backup completes, a failure leaves the private `.building` database for retry; the old directory stays available. Only a complete, validated generation is renamed to the new target; an existing target is rejected. Refreshed credit and alias evidence reads authenticate retained file hashes.

Source alias observations have separate search counts and a separate group section. They assert neither a recording role nor an accepted identity. `capabilities.sourceActors` in `/api/stats` enables this interface; older servers receive no source-actor requests. `/api/person` adds `sourceObservations`, `sourceTotal`, `sourceOffset`, `sourceLimit` and `sourceArtists`. `sourceArtist` and `sourceOffset` filter and page this section independently. `/api/source-actor?id=…` returns the exact actor, alias assertion, original inputs and producer coordinates. Counts remain separate from catalog credits and native observations.

## Add native provider actors without replacing catalog credits

The v4 reader can add a completed native shadow population to this same directory.
The native wave v2 currently retains 670 paired people/credit outcomes: 801 original
actor observations, including two role-free alias actors, and 867 role mentions.
These source observations are exposed development evidence, not accepted people or
an accuracy result. The existing catalog credit and Luna/context counts stay separate.
The same underlying credit may occur in both captures; their counts are not additive
unique-credit totals.

```sh
python3 tools/RelistenNormalization/people_directory.py overlay-native \
  --run local-dev/normalization-data/2026-10-18-shadow-native-domain-wave-v2 \
  --native-url http://alecs-macbook-pro.tail09dbf.ts.net:18771/ \
  --database local-dev/normalization-data/2026-10-18-people-directory-v1/directory.sqlite3
```

This explicit command replaces only the previous native overlay in one transaction.
It validates completed provider/domain counts, paired headers and producer methods,
then retains original actor keys, credit-to-actor scopes, source citations and chunk
locators in compact `native_*` tables. It does not rebuild the full catalog, copy
provider payloads, alter catalog credits or source homes, or attach native actors to
existing contextual decisions. It checks the 4.5 GiB disk reserve before and during
projection. This bounded projection accepts at most 2,000 native parents and
64 MiB of combined decoded people/credit output, including retained replay rows.
It refuses a larger population before changing the database instead of truncating
facts. These are local projection bounds, not a normalization policy or full-catalog
coverage claim. The current 670-parent input is 44,577,474 decoded bytes. A failed
replacement leaves the prior native population available.

Name and explicit-alias search includes both populations. Equal wording shares a
retrieval group; catalog counts and native actor/role/account counts remain distinct.
A role-free alias actor has no recording role, kind or attendance assertion. Repeated
fields, file assertions and selected Local root accounts retain their exact original
scope. Native account counts distinguish original origin locations and enclosing
inputs, even when the provider resource is the same; they are not performance counts.

Native artist filters use capture-qualified keys. Original artist and optional source
UUIDs remain visible as coordinates **within that native capture**. They are never
joined to production source homes simply because a numeric ID or UUID matches.
This matters for Local captures from a different catalog namespace. Unbound actors
remain searchable and retain their provider recording/file citations.

Group pages show a separate native section with its own artist filter and pagination.
“Inspect original actor evidence” opens `/api/native-actor?id=…`, which checks the
retained chunk hash, parent coordinates, original actor key and recording/file scope
before returning the complete original actor. “Browse this input's files” opens the
native reader's original input result. That broad link does not imply every file in
the input belongs to the selected actor or original recording account. It never
substitutes a production MP3 home. The metadata `/api/stats.nativeDirectory` retains
the native run, manifest/chunk hashes, producer, input namespace and separate counts.

`/api/person` adds `nativeObservations`, `nativeTotal`, `nativeOffset`, `nativeLimit`
and `nativeArtists`. `nativeArtist` selects a capture-qualified artist context;
`nativeOffset` pages native actors independently of the catalog `offset`. Original
catalog/context endpoints and their witness ownership remain unchanged. A later
catalog credit overlay can still replace its selected sources without removing native
observations or importing them into model-context questions.

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

## Compare people with source context

`people_context.py` prepares comparisons between witnessed credits. Names and aliases retrieve candidates. The questions then include the exact credit occurrence, surrounding original prose, competing roles, attributed contacts and profiles, and captured performance date and location. A transfer credit does not imply that the transferrer attended the performance. Missing text in a bounded excerpt does not establish that the source lacks that text.

The contextual population contains 5,000 processed questions using 7,582 source witnesses across 1,837 artists. Of these questions, 4,650 cross artist boundaries. The directory retains 4,677 usable proposals: 2,239 possible same-person links, 216 possible namesake distinctions and 2,222 insufficient-evidence results. Another 323 questions remain rejected or missing. The cohort includes name variants, explicit aliases, overlapping contact hints and potential namesakes. Same-date performances in different captured locations and differing contacts retrieve difficult questions; neither supplies an automatic negative label. These inputs are development data, not an independent accuracy evaluation.

The C# `people-context <batches.jsonl> <new-output-directory> <job-uuid>` command owns Luna requests, exact quotation checks, caching and cost admission. It processes at most 500 batches and 5,000 questions, with four concurrent requests, a $25 job cap and 1,000 maximum attempts. It uses the existing dedicated local normalization cache database and reads `OPENAI_API_KEY` from its environment. It does not alter production identities. Requests use `gpt-6-luna`; reported token cost uses configured rates and is separate from an invoice. Uncertain calls retain their cost reservations.

After a run settles, including a sealed cancelled prefix, attach its validated proposals:

```sh
python3 tools/RelistenNormalization/people_context.py --attach \
  --database local-dev/normalization-data/2026-10-18-people-directory-v1/directory.sqlite3 \
  --output local-dev/normalization-data/2026-10-18-people-context-results-v1
```

The attachment verifies the completed result hash and each witness's credit, group, source and artist coordinates before committing compact relationships. It also checks the credited name, role, occurrence key and exact field/quote/span, so a reused local row ID cannot attach old context to changed evidence. It copies decisions and their supporting quotations. The original results own the full question context and usage receipts; SQLite stores a result locator rather than another copy of those inputs. Attachment does not merge groups, infer transitive relationships, change recording counts or change playback ownership.

`GET /api/relationships?group=<optional-draft-group-id>&offset=0&limit=10` pages comparisons for one group, or all comparisons when the group is omitted. Each result contains the relationship, model rationale, exact supporting quotes and the two group names. `GET /api/relationship?id=<question-id>` returns the original witnessed question and validated decision, with source-evidence and source-owned playback URLs. Unknown question IDs return 404. The “Contextual comparisons” link opens the complete browsing path. Group pages show their own comparisons, including competing evidence within a shared name group.

A proposed same-person relationship is evidence to inspect. A proposed different-people relationship is a possible namesake distinction. Insufficient evidence remains a visible result. None of these states asserts an accepted person identity. Further work should use this directory to identify broad failure classes and measure matching accuracy on independently labeled data.

An immutable snapshot can expose a flushed prefix while the job continues. Its manifest has `state: snapshot`, the original run, exact result hash and original physical-line range. The page reports processed questions against the selected 5,000, so it does not present a partial run as complete catalog coverage.

Exact quotation checks also apply during recovery. One changed quote can invalidate an original batch response even when the other decisions are supported. `PeopleContextRecovery` reads that response from the local cache ledger, checks the original rendered request and rejects duplicate or invented question IDs, then validates each present decision separately. Each absent question remains `missing_decision`. It makes no HTTP calls, changes no spend or cache acceptance, and preserves rejected question IDs and reasons. Recovered decisions appear with that basis on the page. Their original source witnesses and model usage stay attached; recovery does not charge the original cost twice.

A later credit overlay invalidates contextual relationships that cite the replaced source's credit occurrences in the same transaction. It retains unrelated relationships and updates their count snapshot. This prevents an old local credit ID from linking to newly projected evidence. The original contextual run remains available as historical evidence; attaching a new proposal requires its witnesses to match current credits.

Recover a sealed run with the original job UUID. This command does not need an API key:

```sh
dotnet tools/RelistenNormalization/bin/Debug/net10.0/RelistenNormalization.dll \
  people-context-recover <sealed-run-directory> <new-recovery-directory> <original-job-uuid>
```

A cancelled run retains all prepared inputs and the hash of its completed result prefix. Recovery verifies the recorded number of completed batches, without treating unrecorded inputs as failed responses. Its projection preserves the stopped execution state even though recovery itself completed. Recovery retained 718 supported decisions without new calls or spend. Known token cost for the original job was $0.6608652 at configured rates; the ledger retained $0.7115587 including uncertain cost reservations. These values are not invoice totals.

## Pending-only continuation

The retained `continuation.json` lists completed and pending batch IDs against the original input hash, plus attempts whose results were not recorded. The stopped job stays closed. Prepare a new job containing only those pending batches:

```sh
python3 tools/RelistenNormalization/people_context.py \
  --continue-from <sealed-original-run> --output <new-preparation-directory> \
  --job-uuid <new-job-uuid>
shasum -a 256 <new-preparation-directory>/plan.json
dotnet tools/RelistenNormalization/bin/Debug/net10.0/RelistenNormalization.dll \
  people-context-continue <new-preparation-directory>/plan.json <plan-sha256> <new-run-directory>
```

The plan pins the original manifest, results, continuation receipt and exact pending input bytes. Admission authenticates the complete original partition before creating the new job. The new job has a $10 reservation ceiling, 508 maximum attempts, a 30-minute execution limit and a 5.5 GiB free-space floor. The attempt ceiling permits two attempts for each of the 254 pending batches; the shared cache still enforces two attempts per request across both jobs. Completed batches are excluded. The old ledger keeps all charges, including uncertain cancellation reservations; cached valid responses require no new request. Output retains the validated plan bytes and its hash even when execution is cancelled.

Run keyless `people-context-recover` on the **new run** with its new job UUID after execution settles. Attach the new primary output, then its recovery output, using the existing `--attach` command. Keep the original run and its recovery already attached; do not reattach that older summary afterward. Attachment authenticates both sealed jobs, preserves their separate costs and recovery counts, and reports distinct processed question IDs against the original 5,000-question population. The original 142 rejected or missing answers remain visible; processing the pending population does not repair those answers. Every new recovery independently validates each known question and preserves unsupported or absent decisions. These remain proposals for inspection, without identity activation or transitive merging.

The projection records distinct question IDs per job separately from successful decisions. Overlapping snapshots and recovery do not increase processed coverage; a later continuation containing disjoint pending questions does. Only compact IDs are stored, without another copy of question prose.

The completed continuation processed all 254 pending batches. Its primary results supplied 1,440 usable decisions; keyless recovery retained another 919, leaving 181 rejected or missing questions. Combined with the original job, the directory contains 4,677 proposals and 323 rejected or missing answers across all 5,000 questions. The continuation cost $0.6855741 at configured rates; combined known token cost is $1.3464393 and accounted cost is $1.3971328, including unchanged uncertain-call reservations. Original artifacts and catalog/native counts remain unchanged.

In the original job, fifteen responses omitted independent decisions: fourteen returned nine answers for ten questions, and one returned seven. Another response duplicated an ID and remains wholly rejected. Recovery keeps present unique known IDs when their exact witness evidence passes validation and records the omitted ID as `missing_decision`. Requiring every response ID before recovering any result discarded supported independent answers. Duplicate or invented IDs still reject the whole response; recovery never guesses or remaps ownership.
