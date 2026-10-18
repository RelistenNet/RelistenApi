# Normalization over retained captures

## Browse and reproduce the shadow catalog

The [shadow reader](http://alecs-macbook-pro.tail09dbf.ts.net:18764/) shows the
completed retained-sample run. It contains **258,987 domain outcomes with zero
failures** from 32,593 recordings, 34,861 files, 17,073 venues, 1,696 repertoire
songs and 432 legacy setlists, plus one retained Jerry Garcia provider input.
An input can produce several domain outcomes; these counts are not distinct
normalized identities. Full raw capture of 292,522 recordings and 5,064,076 files
has started and is still running; those full-population outputs are not yet part
of this completed sample run.

The run lives in
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

Open `http://127.0.0.1:18764/` for that local instance. The current hosted instance
uses the Tailscale link above. The reader's rebuildable `.shadow-reader.sqlite3`
stores summaries, filters, entity keys and chunk/line pointers. Detail views read
the original immutable chunk; the index does not copy proposal or evidence bodies.
`/api/stats` reports run coverage and index lag, `/api/records` searches outcomes,
and `/api/record?id=<id>` returns one result with available playback links.

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
tracks the remaining full-population capture, provider coverage and audits.

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
