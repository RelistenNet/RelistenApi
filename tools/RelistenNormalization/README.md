# Local extraction from retained captures

Run the recording-credit and file-title extractors over a scale capture without starting the API or ingestion worker:

```sh
dotnet run --project tools/RelistenNormalization -- extract local-dev/normalization-data/2026-10-17-cross-artist/snapshot.jsonl /path/to/new-output-directory
```

The runner uses local code only. It does not read an API key, contact providers, connect to a database or accept canonical identities. Recording inputs retain taper, transferrer, description, source, lineage and taper-note strings. Track inputs retain the title and the available source, set and show UUID context. Every extraction keeps the resource UUID, artist UUID, family and exact supporting text.

A completed capture produces `extractions.jsonl` and `manifest.json`. The manifest records the exact input SHA-256, snapshot receipts, extractor versions, per-kind and per-artist counts, credited roles, missing track context and errors. Credit counts include repeated mentions across fields; they are not distinct people. Venue, repertoire and setlist samples are counted as unsupported by this command. Their omission is explicit; this command does not evaluate those domains or prove accuracy.

Item errors retain the input in an `extraction_error` row and allow other records to finish. A completed capture with item errors has state `completed_partial` and a nonzero exit code. A truncated capture, invalid snapshot envelope or cancellation retains `extractions.partial.jsonl` and `report.partial.json`; no completed manifest is published. Retry with a new output directory. Existing directories are never overwritten.

The runner checks the beginning and ending snapshot markers, read-only replica state and artist count. Use the [scale capture validator](../../docs/research/2026-10-17-full-normalization/scale/README.md) to verify population reconciliation and per-artist sample completeness before drawing coverage conclusions. These extraction outputs support inspection and selection of cases for labeling or a later paid fallback run.

## Bounded Luna credit extraction

The separate `luna-credits` command uses the real Responses API. It compares retained credit inputs from two cohorts: apparent local taper/transferrer successes, and unresolved or missing local credits. Both receive Luna extraction. Stable resource hashes select up to the requested count in each cohort. This diagnostic selection does not estimate catalog accuracy; independently label and judge the outputs before choosing a fallback policy.

```sh
dotnet run --project tools/RelistenNormalization -- luna-credits \
  local-dev/normalization-data/2026-10-17-cross-artist/extraction-v1/extractions.jsonl \
  /path/to/new-output-directory <job-uuid> 32
```

Supply `OPENAI_API_KEY` in the process environment. The command does not automatically read `.env.local`. Supply `RELISTEN_NORMALIZATION_DATABASE_URL` for a dedicated local `relisten_normalization_*` PostgreSQL database with migration 22 applied. The command accepts only `localhost` or `127.0.0.1`; it does not initialize a database, start the API or change catalog relationships. Keep the key out of arguments, source control, receipts and logs.

Each job has a durable $25 cap. Reuse its UUID and a new output directory after interruption. The cache hashes the complete rendered request, including model, prompt, schema and generation settings. Cached successes need no key or paid retry. An uncertain request retains its possible cost and has bounded retries. `accounted_usd` includes those conservative reservations as well as measured charges; it is not an invoice total. Current small-request Standard pricing is documented in the [Luna model reference](https://developers.openai.com/api/docs/models/gpt-6-luna); the extractor explicitly requests Standard processing and rejects oversized input before admission.

The command validates the complete local extraction receipt before any paid call. It saves `selection.json`, then flushes each result to `results.jsonl`. A completed attempt publishes `manifest.json` with states, cache reuse, input hash and spend. Classified unresolved requests produce `completed_partial`. Cancellation or an unexpected failure leaves an inspectable prefix without a completed manifest. Results remain proposals; this command cannot accept a global person or taper/transferrer match.

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
