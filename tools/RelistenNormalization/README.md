# Local extraction from retained captures

Run the recording-credit and file-title extractors over a scale capture without starting the API or ingestion worker:

```sh
dotnet run --project tools/RelistenNormalization -- extract local-dev/normalization-data/2026-10-17-cross-artist/snapshot.jsonl /path/to/new-output-directory
```

The runner uses local code only. It does not read an API key, contact providers, connect to a database or accept canonical identities. Recording inputs retain taper, transferrer, description, source, lineage and taper-note strings. Track inputs retain the title and the available source, set and show UUID context. Every extraction keeps the resource UUID, artist UUID, family and exact supporting text.

A completed capture produces `extractions.jsonl` and `manifest.json`. The manifest records the exact input SHA-256, snapshot receipts, extractor versions, per-kind and per-artist counts, credited roles, missing track context and errors. Credit counts include repeated mentions across fields; they are not distinct people. Venue, repertoire and setlist samples are counted as unsupported by this command. Their omission is explicit; this command does not evaluate those domains or prove accuracy.

Item errors retain the input in an `extraction_error` row and allow other records to finish. A completed capture with item errors has state `completed_partial` and a nonzero exit code. A truncated capture, invalid snapshot envelope or cancellation retains `extractions.partial.jsonl` and `report.partial.json`; no completed manifest is published. Retry with a new output directory. Existing directories are never overwritten.

The runner checks the beginning and ending snapshot markers, read-only replica state and artist count. Use the [scale capture validator](../../docs/research/2026-10-17-full-normalization/scale/README.md) to verify population reconciliation and per-artist sample completeness before drawing coverage conclusions. These extraction outputs support inspection and selection of cases for labeling or a later paid fallback run.
