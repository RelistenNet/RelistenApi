# Normalization over retained captures

## Browse and reproduce the shadow catalog

The [full recording/venue reader](http://alecs-macbook-pro.tail09dbf.ts.net:18765/)
shows 1,242,539 outcomes from all retained 292,523 recordings, 216,332 venues,
16,605 repertoire songs and 38,475 legacy setlists, plus one ordered provider
input. The [sample reader](http://alecs-macbook-pro.tail09dbf.ts.net:18764/) adds
Luna interpretations for 2,000 recordings to its 258,987 outcomes. Both runs
completed without processing failures. An input can produce several outcomes;
these counts are not distinct normalized identities or matching-accuracy results.
Raw capture is complete for 5,064,097 files. Full file normalization is in progress.

The original sample run lives in
`local-dev/normalization-data/2026-10-18-shadow-catalog-v1/`.
Its `manifest.json` reconciles every expected provider/domain outcome;
`progress.json` and `checkpoint.json` retain progress and restart state.
The execution request in
`local-dev/normalization-data/2026-10-18-shadow-catalog-execution-v1/request.json`
records the executed command, assembly hashes and evidence settings. The checked-in
[first-run receipt](../../docs/research/2026-10-17-full-normalization/scale/evidence/shadow-catalog-first-run-v1.json)
summarizes the actual population and verification.

To reproduce these inputs in a new directory, run from the repository root:

```sh
RELISTEN_SHADOW_PROVIDER_INPUTS="$PWD/local-dev/normalization-data/2026-10-18-shadow-provider-inputs-v1/inputs.jsonl" \
RELISTEN_SHADOW_COMPOSITION_EVIDENCE="$PWD/local-dev/normalization-data/2026-10-18-composition120-primary-adjudication-v1/manifest.json#sha256=c27c3135c51ff3364bf18425e435ce2ae46cbd8e08eb561e5b5c951ee023799f" \
dotnet run --project tools/RelistenNormalization -- shadow \
  local-dev/normalization-data/2026-10-17-cross-artist/snapshot.jsonl \
  local-dev/normalization-data/2026-10-18-shadow-catalog-extraction-v1/extractions.jsonl \
  local-dev/normalization-data/shadow-catalog-replay
```

The snapshot, extraction receipt and optional provider inputs must already exist.
`extract-source-actors`, documented below, produces the recording/file extraction
used by the shadow runner. Without `RELISTEN_SHADOW_COMPOSITION_EVIDENCE`, the
composition adapter retains unresolved outcomes without those cited links.
The shadow command uses local code and retained evidence; it makes no database,
provider or paid-model calls and changes no production identities.

Outputs are immutable per-domain `*.jsonl.gz` chunks. Only completed chunks become
visible; unfinished `*.writing` files remain outside the index. To continue an
interrupted run, repeat the same command and directory with `--resume`. Resume
requires the same snapshot, extraction and provider-input hashes and adapter
version strings. The composition method also includes its evidence manifest hash.
Checkpoints follow flushed chunks. A replay can retain duplicate physical rows;
the latest `(domain, inputKey)` result defines the logical outcome. Changed inputs
or methods require a new run.

Start a local reader over an existing run with:

```sh
python3 tools/RelistenNormalization/shadow_reader.py \
  --run "$PWD/local-dev/normalization-data/2026-10-18-shadow-catalog-v1" \
  --bind 127.0.0.1 --port 18764
```

To reserve capacity for other local work, restart the reader with
`--pause-indexing`. It keeps the existing cache searchable and reports the pause
and pending chunks through `/api/stats`. Restart without that flag to continue
indexing into the same cache. No completed chunks or indexed records are deleted.

Coverage counts are committed with each indexed chunk. Replays replace counts for
the same logical result, and failed or missing chunks remain excluded. The first
restart of an older v3 cache builds these counts once in an atomic migration;
later HTTP requests read the small count table instead of scanning every result.

Open `http://127.0.0.1:18764/` for that local instance. The current hosted instance
uses the Tailscale links above. The reader's rebuildable `.shadow-reader-v3.sqlite3`
stores summaries, filters, entity keys and chunk/line pointers. Detail views read
the original immutable chunk; the index does not copy proposal or evidence bodies.
Version 3 gives file titles priority over recording dates. Earlier cache files
remain intact, so rebuilding an older run requires space for the new cache too.
`/api/stats` reports run coverage and index lag, `/api/records` searches outcomes,
and `/api/record?id=<id>` returns one result with available playback links.

For a reader that contains credits or venues but no file results, add
`--file-reader-url http://127.0.0.1:18767` with the origin of an existing file
reader. Its detail view offers “Find playable files” beside exact `source:`,
`show:` and `file:` UUID keys. Each link opens that reader with
`?domain=file_contents&entityKey=<exact-key>`. The destination comes only from
operator configuration and appears as `fileReaderUrl` in `/api/stats`; it must
be an HTTP(S) origin with no credentials, query, fragment or path beyond `/`.
The option defaults to disabled. Links preserve captured ownership and make no
request until opened. Results depend on the destination reader's indexed files;
the link does not infer audio or assign a recording to another show.

Use the domain, artist, provider and outcome filters, then open a result to inspect
its proposal beside native evidence and alternatives. Follow an entity key to
clear filters and inspect related rows across artists and domains:

- Open [Bowery Ballroom across White Denim and Ted Leo and the Pharmacists](http://alecs-macbook-pro.tail09dbf.ts.net:18764/?domain=venues&entityKey=venue%3A8cd9a5eaf90ebe8add9f89d95c6e47db),
  then inspect either result and its shared `venue:` key.
  Venue metadata is proposed. City labels, coordinates and provider references
  still require physical-premises and history resolution.
- Search a song in `repertoire`, then follow its keys into `file_contents` and
  `performances` to inspect the file, catalog home and available playback URL.
- Search a credited name in `people` or `credits` to inspect source-local names
  and taper/transferrer evidence. Shared keys are proposals, not accepted people.
- Open [Jerry Garcia's ordered setlist](http://alecs-macbook-pro.tail09dbf.ts.net:18764/?domain=setlists&provider=jerrygarcia.com): the retained provider account preserves
  three ordered sets and 18 passages. All 432 legacy setlists remain unresolved
  for ordered occurrences.
- Open the [three supported composition proposals](http://alecs-macbook-pro.tail09dbf.ts.net:18764/?domain=compositions&outcome=proposed)
  and their citations. Composition coverage remains incomplete; equal titles do
  not establish a shared authored work.

`catalog:unattributed` preserves missing importer provenance. `playback:` labels
identify the captured audio host; an artist's configured providers alone do not
identify which importer produced a row. The [shadow delivery plan](../../docs/plans/active/2026-10-17-full-normalization/shadow-delivery.md)
tracks the remaining full-file processing, provider coverage and audits.

### Browse individual native files

Use `?view=files` in the same reader to browse native provider files individually.
Each file keeps its exact native key, physical ordinal and parent provider
result. Search covers every file title and filename, including files late in a
large response. It uses case-insensitive token prefixes: all query words must
match, in any order. The query is limited to 256 characters. Artist, provider,
outcome and exact entity-key filters apply to files; the domain is file contents.
Following an entity key in this view stays within native files.

`GET /api/files` accepts `q`, `artist`, `provider`, `outcome`, `entityKey`,
`parentId`, `limit` and `offset`. Pages contain `items`, `total`, `limit` and
`offset`; limits are clamped to 1–100. Each item includes `parentId`,
`nativeFileKey`, `fileOrdinal`, `label`, filename, artist, provider and outcome.
Order is parent ID descending, then physical file ordinal ascending. An ordinal
identifies the original array member; it does not establish performance order.
`GET /api/file?parentId=…&nativeFileKey=…` returns that original file, parent
metadata and a URL for the complete original parent. An unknown or withdrawn
file returns 404; changed immutable evidence returns 409.
The parent ID addresses this rebuildable reader cache, not a lasting catalog
identity. Retain the native file key and original revision/origin as evidence.
After a cache rebuild, an exact `entityKey=native_file:…` search finds the current
parent address.

Only `catalogPlayback.mp3Url` supplies a catalog audio control. A native
`playbackLocator` that is an absolute HTTP(S) URL appears separately as “Open
provider file”; relative filenames and other schemes remain inspectable facts.
The reader never constructs a URL from a filename, assumes the provider format
is MP3, or loads provider media automatically. Exact catalog home keys can open
the configured full-file reader even when the native file has no catalog match.

The existing v3 cache adds a compact child table, exact entity memberships and
SQLite FTS5 over titles/filenames. Original files and evidence stay in parent
chunks. The first restart backfills only indexed native `provider_input`
file-content parents; legacy catalog `track_sample` rows retain their existing
index. Replay replaces a parent's child pointers, search terms and memberships
in the same transaction. Failed or missing chunks hide their children.
`/api/stats.nativeFileCoverage` reports indexed files and parents separately
from domain-outcome coverage. Neither count establishes accepted identities.

### Compare distinct accepted people

`RELISTEN_SHADOW_PEOPLE_CANDIDATES=<path>#sha256=<digest>` adds an offline
candidate snapshot to a new shadow run. `ShadowPeopleCandidateSnapshots.ExportAsync`
reads distinct queries through `NormalizationCandidateService.FindPeopleFactsAsync`;
the caller saves the returned bytes and their SHA-256. Each query has its own
read-only transaction. `Serialize` accepts already retained typed snapshots.
Both preserve the original limits, issues, owner observations and candidate
generations. The file does not assert current freshness.

The adapter uses each actor's exact projected query. A missing query is unknown;
a complete empty result has no accepted candidates. Neither creates a person.
Comparisons use full accepted alias and actor matching aggregates, even when
supporting prose witnesses were bounded. Each resolved person UUID appears once
in a query's candidate pool. Canonical comparisons remain separate from recording
witness rankings and carry a snapshot locator instead of copying the full facts
into each result. New snapshot bytes change the method identity and require a new
run. Automatic acceptance still requires the evaluated production policy.

## Credit and file-title extraction

Run the recording-credit and file-title extractors over a scale capture without starting the API or ingestion worker:

```sh
dotnet run --project tools/RelistenNormalization -- extract local-dev/normalization-data/2026-10-17-cross-artist/snapshot.jsonl /path/to/new-output-directory
```

The runner uses local code only. It does not read an API key, contact providers, connect to a database or accept canonical identities. Recording inputs retain taper, transferrer, description, source, lineage and taper-note strings. Track inputs retain the title and the available source, set and show UUID context. Every extraction keeps the resource UUID, artist UUID, family and exact supporting text.

A completed capture produces `extractions.jsonl` and `manifest.json`. The manifest records the exact input SHA-256, snapshot receipts, extractor versions, per-kind and per-artist counts, credited roles, missing track context and errors. Credit counts include repeated mentions across fields; they are not distinct people. Venue, repertoire and setlist samples are counted as unsupported by this command. Their omission is explicit; this command does not evaluate those domains or prove accuracy.

Item errors retain the input in an `extraction_error` row and allow other records to finish. A completed capture with item errors has state `completed_partial` and a nonzero exit code. A truncated capture, invalid snapshot envelope or cancellation retains `extractions.partial.jsonl` and `report.partial.json`; no completed manifest is published. Retry with a new output directory. Existing directories are never overwritten.

The runner checks the beginning and ending snapshot markers, read-only replica state and artist count. Use the [scale capture validator](../../docs/research/2026-10-17-full-normalization/scale/README.md) to verify population reconciliation and per-artist sample completeness before drawing coverage conclusions. These extraction outputs support inspection and selection of cases for labeling or a later paid fallback run.

## Source actor alias proposals

Use the explicit `extract-source-actors` mode to add separate role-free alias
assertions beside the existing credit/title output:

```sh
dotnet run --project tools/RelistenNormalization -- extract-source-actors \
  /path/to/retained-snapshot.jsonl /path/to/new-source-actor-output
```

The default `extract` command keeps its existing output contract. This mode uses
local code and the same pure parser as retained recording extraction; it makes
no provider, database or model call. Its additional `SourceActorAliases` retain
original names, alias relationships, conditional headings, unresolved statements
and field coverage. They assign no canonical identity, recording role, actor
kind or attendance. Existing recording-credit proposals remain separate.

Use a new output directory and preserve the source-actor producer versions and
original `Inputs`. Proposal yield is not matching accuracy or publication
authority. The [source actor alias plan](../../docs/plans/active/2026-10-17-full-normalization/source-actor-aliases.md)
records current grammar/cohort verification and the required assignment, support,
review, candidate and withdrawal lifecycle.

## Normalize complete compressed capture shards

Use `shadow-inline` when raw capture shards already contain the source fields and
file titles. It runs the same local extractors directly, avoiding a second large
extraction file:

```sh
dotnet run --project tools/RelistenNormalization -- shadow-inline \
  local-dev/normalization-data/2026-10-18-shadow-full-raw-v1/manifest.json \
  local-dev/normalization-data/shadow-full-catalog
```

A single `.jsonl` or `.jsonl.gz` snapshot is also accepted. Manifest input selects
its completed shards in order and verifies their gzip bytes and receipts before
normalization. Each shard retains its original snapshot and footer. The run
saves its selected inputs in `input-descriptor.json`; `--resume` uses that frozen
selection even when raw capture has since added shards. Start a new output
run to select an expanded capture. Changed selected bytes or method versions
reject resume.

Repeated artist inventories count once. Conflicting artist IDs or UUIDs fail
preparation rather than assigning songs to a shared unknown artist. Per-row
extraction failures remain visible while independent domains continue. Source
and file show relationships use the captured source's actual show ID and UUID;
a mismatched show context remains unresolved. No database or external request
runs during this command.

The [population guide](../../docs/research/2026-10-17-full-normalization/scale/shadow-population.md)
covers capture commands, range completion and parentless files. Full-catalog
processing and matching quality remain separate acceptance requirements.

For an ordinary catalog-row export without replica framing, use
`shadow-rows <catalog-rows.jsonl[.gz]> <output-directory> [--resume]`. This route
uses the same inline extractors and domain adapters, pins the entire input file
and any provider-input file, and records the extractor versions. Changed input
bytes or methods reject resume. The export's own provenance defines its capture
scope; the command creates no database snapshot claim. Keep catalogs with
colliding numeric IDs in separate runs. `shadow-inline` retains its strict
original replica-shard verification.

### Shared geographic identities

Set `RELISTEN_SHADOW_VENUE_GEOGRAPHY` to an offline GeoNames manifest followed by
`#sha256=<manifest-hash>`. The [geographic intake guide](venue-geography/README.md)
covers the public reference download, attribution and pinned files. The adapter
records the reference digest in its method version. Exact aliases, country and
region constraints can propose a shared `geonames:<ID>` locality. Missing towns,
conflicting constraints and multiple candidates remain unresolved. A locality
does not identify a physical venue; premises still need provider or address
evidence. The runner makes no geographic network requests.

### Admit retained Archive and Phish HTTP responses

Use `retained-http-inputs <request.json> <new-output-directory>` to reuse completed
native credit captures without fetching them again. The request names the original
raw `catalogManifestPath` and an `httpCaptureDirectories` array of completed
`native-archive-credit-fields-v1` or `native-phish-credit-fields-v1` directories.
Preparation verifies their manifests, selection plans, HTTP receipts and exact
response bytes, then finds each selected source in the captured catalog.

The output contains `inputs.jsonl` and a completed `descriptor.json`. Set
`RELISTEN_SHADOW_PROVIDER_INPUTS` to that input file for a subsequent shadow run.
Reuse the completed file for exact replay. Preparing another directory creates
new admission UUIDs and timestamps; the original HTTP time remains separate.
This offline admission creates no database history or current-provider receipt.
Cancellation or failure leaves no completed descriptor; use a new destination.

Recording results use the captured source-to-show assignment. A missing or
conflicting assignment remains explicit rather than selecting another recording
by date. Updated provider dates and native file facts remain visible alongside
that assignment. This does not move playback, infer ordered performances from
file positions, or attach native files to guessed legacy file UUIDs. The full
Phish response uses `phishin-show-response-v1` and `/data/...` citations; the
existing `phishin-show-object-v1` contract keeps its original root coordinates.

### Rank global people candidates with a retained model

`RELISTEN_SHADOW_PEOPLE_MODEL` accepts a portable `models.json` path followed by
`#sha256=<file-hash>`. The loader validates the trained five-feature logistic
model, feature order, class order and runtime coefficient mapping. Its digest
becomes part of the adapter version, so a changed model cannot resume an old run.

The adapter retrieves names, explicit aliases and attached accounts across the
indexed recordings, then scores every retrieved compact witness. Detailed prose
is limited to 100 relationships per actor; compact outcomes and set-level
decisions include the complete retrieved pool. Raw scores are ranking diagnostics,
not probabilities or publication thresholds. Equal names alone still require
identity evidence, and these results activate no automatic acceptance policy.
An actual full-index diagnostic produced tied rankings for all 14 selected actor
mentions. Repeated recording witnesses can describe one person; acceptance still
needs distinct-person candidates and independent identity evaluation.

### Acquire composition evidence across the repertoire

`shadow-compositions-acquire <request.json> <output-directory>` reads pinned
catalog snapshots and retained MusicBrainz inputs, then advances bounded durable
MusicBrainz commands. It uses the existing acquisition service, rate admission,
cache and retry receipts. It needs a dedicated local normalization database with
migrations 22 and 44; it does not initialize that database or start the API.
Supply its URL through `RELISTEN_NORMALIZATION_DATABASE_URL`.

The request contains `catalogSnapshots` and `retainedInputs` arrays of
`{path, sha256}` objects, optional `originalCommands` in the same file-reference
format, and `limits`. Paths resolve relative to the request file unless absolute.
Snapshots are ordinary captured JSONL or gzip JSONL. Each retained-input file is
a complete `NormalizationInputRevision`, including its original payload encoded
as base64. The optional command file is an array of
`ShadowCompositionAcquisitionCommand` values. Reuse original command UUIDs and
requests to preserve failed or pending acquisitions.

Limits name the maximum service calls, conservative new-response allowance,
duration and retained payload bytes. Each reserved service call consumes the
full 8 MiB response allowance even when cache reuse sends no request. Retained
payload bytes exclude JSON/base64, checkpoints and disk overhead. These are run
bounds; reaching one does not mean the external candidate universe is complete.

The planner searches recordings with performer constraints and works by title.
It hydrates returned alternatives and explicit recording-to-work relationships.
A repertoire artist is never assumed to be a composer. The output retains typed
inputs, commands, receipts and unresolved issues in `checkpoint.json` and
`input-<UUID>.json` files. Repeating the same request and output resumes its fixed
deadline and bounds. Provider calls and completed responses remain recoverable
in the local database. Acquisition itself accepts no canonical identities.

For first-party cover credits missing from recording notes, use
[Go-Set evidence acquisition](go-set-composition-sources.md). It retains original
pages and performer annotations separately from any proposed catalog attachment.

## Bounded Luna credit extraction

The separate `luna-credits` command uses the real Responses API. It compares retained credit inputs from two cohorts: apparent local taper/transferrer successes, and unresolved or missing local credits. Both receive Luna extraction. Stable resource hashes select up to the requested count in each cohort. This diagnostic selection does not estimate catalog accuracy; independently label and judge the outputs before choosing a fallback policy.

```sh
dotnet run --project tools/RelistenNormalization -- luna-credits \
  local-dev/normalization-data/2026-10-17-cross-artist/extraction-v1/extractions.jsonl \
  /path/to/new-output-directory <job-uuid> 32
```

Supply `OPENAI_API_KEY` in the process environment. The command does not automatically read `.env.local`. Supply `RELISTEN_NORMALIZATION_DATABASE_URL` for a dedicated local `relisten_normalization_*` PostgreSQL database with migrations 22, 44 and 45 applied. The command accepts only `localhost` or `127.0.0.1`; it does not initialize a database, start the API or change catalog relationships. Keep the key out of arguments, source control, receipts and logs.

The source-credit command runs eight independent requests concurrently and shares one request for identical field maps. It writes one result per selected recording, with durable progress and exact selection/results hashes in the completed manifest.

Each job has a durable $25 cap. Reuse its UUID and a new output directory after interruption. The cache hashes the complete rendered request, including model, prompt, schema and generation settings. Cached successes need no key or paid retry. An uncertain request retains its possible cost and has bounded retries. `accounted_usd` includes those conservative reservations as well as measured charges; it is not an invoice total. Current small-request Standard pricing is documented in the [Luna model reference](https://developers.openai.com/api/docs/models/gpt-6-luna); the extractor explicitly requests Standard processing and rejects oversized input before admission.

The command validates the complete local extraction receipt before any paid call. It saves `selection.json`, then flushes each result to `results.jsonl`. A completed attempt publishes `manifest.json` with states, cache reuse, input hash and spend. Classified unresolved requests produce `completed_partial`. Cancellation or an unexpected failure leaves an inspectable prefix without a completed manifest. Results remain proposals; this command cannot accept a global person or taper/transferrer match.

Completed source-credit jobs can enrich a shadow run without more API calls.
Set `RELISTEN_SHADOW_LUNA_CREDITS` to the completed job directory and
`RELISTEN_SHADOW_LUNA_INPUT_SHA256` to the exact original extraction-stream hash.
The adapter verifies the selection and result hashes, source/artist identity,
original fields and quoted spans. It preserves local credits and attached facts,
labels model evidence separately and leaves failed or changed inputs unresolved.
Empty model output does not remove local credits. Model-only people remain
source-scoped; a matching name alone cannot establish a global identity.

## Matched-field Luna comparison

Use [`luna-credit-fields`](luna-credit-fields.md) to compare the current local parser and Luna on the same 100 frozen development fields, with a separate $5 job cap. It retains all outcomes, keyless cache replay and current fallback routing. The offline scorer verifies frozen inputs and reports exact taper/transferrer names and roles, corrections, new errors and unavailable responses. Uncertain fields supply no negative labels. The guide covers preparation, scoring and recovery.

## Actual venue pair baseline

Export the current C# `IdentityCandidateMatcher.CompareVenues` results for a retained venue development bundle:

```sh
dotnet run --project tools/RelistenNormalization -- venue-pair-baseline \
  docs/research/2026-10-17-full-normalization/training/venues-development-v1 \
  /path/to/new-venue-baseline-output
```

This command runs offline. It reads only task/evidence correspondence and retained venue names into the matcher. It supplies no policy and accepts no identity. Raw location labels, coordinates, dates, kind and provider IDs do not become resolved geographic or identity facts. An empty supported-location collection means those facts are unavailable, not that a venue lacks a location.

`baseline.jsonl` preserves each `task_id`, ordered `evidence_ids`, and the actual comparison with features, rules, candidate status and disposition. Label decisions, primary-source rationales and split links are excluded from matching. The linked labels, evidence and split-link files are fingerprinted against their exact manifest entries; split links are never interpreted as matcher evidence.

The completed `manifest.json` records the projection version, original bundle/file hashes, executed API and runner assembly hashes, and output hash/count. Inputs have byte, row and name bounds. A failed or cancelled run publishes no completed manifest; an output prefix can remain as `baseline.partial.jsonl`. Retry in a new directory. These comparisons support development experiments against the real implementation; they do not measure production accuracy or activate automatic matching.

## People identity experiments and review

The [people pair exporter](people-pair-baseline.md) supplies actual C# comparisons
and the existing five runtime features. The [identity experiment](people-identity-matcher-experiment.md)
compares those rules with trained logistic and boosted-tree scorers on complete
development questions. Unknown judgments stay outside fitting and error counts;
all questions participate in split grouping.

Use the separate [source-context exporter](people-source-context.md) to supply
aliases, contacts and profiles attached by the current credit extractor. It
authenticates the same original fields, joins exact source-local actor references
and records missing or ambiguous attachment. The experiment accepts its explicitly
versioned contract alongside the original name-only baseline.

The [source-reference projection](people-source-reference.md) selects actors from
exact contact/profile spans, retaining ambiguous and unattributed hypotheses.
The [adjudicated training preparation](people-adjudicated-training/README.md)
combines the original questions with separately reviewed pairs while keeping
source selectors and identity targets in different files. Preparation does not
produce matcher features or complete the grouping needed for fitting.
The [adjudicated source exporter](people-adjudicated-baseline.md) reads those
source selectors without opening the target file. It exports actual comparisons,
all endpoint diagnostics and source-local grouping bridges for the trainer.
The [independent source-view exporter](people-view-baseline.md) retains separate
field views and every supported comparison between them. Its top manifest marks
completion; the unchanged comparator remains available beside those outputs.
The [combined training comparison](people-adjudicated-trainer/README.md) joins
the separate targets after grouping, counts repeated actor pairs once and
reports conflicting or unsupported examples without turning them into negatives.
Use the [source-view training comparison](people-adjudicated-trainer/source-views.md)
to retain each field comparison and fit complete relationships once. It keeps
partial coverage and unlabeled field observations separate from identity targets.

The separate [cross-artist review packet tool](people-identity-packet/README.md)
prepares 55 unanswered source-backed identity questions and validates complete
contexts. The [control packet tool](people-identity-controls/README.md) adds six
questions about co-credits and cross-recording linkage from four separate source
contexts. The [review validator](people-identity-reviews/README.md) checks frozen
first answers and separate adjudication against either packet format, including
exact citations and links to original answer bytes. These tools use retained
development data only. None accepts an identity or activates a publication policy.

The [development evidence selector](people-evaluation-selection/README.md)
inventories every permitted source, closes known reservations and copies complete
selected originals. It keeps artist coverage and probability panels separate,
retains unknown parent links and records prior exposure. Its current frame is
development data; it cannot establish catalog-wide matching accuracy.

## Retained MusicBrainz acquisition

Use [`musicbrainz-acquire`](musicbrainz-acquire.md) for a bounded work, recording or artist lookup through the public acquisition service. The mode uses a dedicated local database, returns immediately when admission is pending, and exports exact responses, retained input, typed observations and hashes. It does not accept catalog identity links.

## Bounded MusicBrainz candidate search

Use [`musicbrainz-search`](musicbrainz-search.md) to freeze cited literal query variants and stable page commands, acquire at most one search page per invocation, and resume explicitly in a new output directory. Receipts retain partial/failed coverage, all observed hits, and the original response/capture provenance. External coverage always remains incomplete; hydration and correspondence acceptance are separate work.
