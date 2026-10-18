# Complete artist, recording, and show metadata frame

This fixed capture exports every current artist, recording (`sources`), and show, including artists with no recordings. It also resolves each supplied prior track coordinate independently to its current direct and set-based parents. The initial frame is a denominator for later reservations, not a new identity dataset or evidence of independent evaluation. Full track, venue, and music metadata sections remain subsequent work.

The October 18 capture and independent audit passed. The [evidence receipt](evidence/full-catalog-metadata-frame-v1.json) records 4,379 artists, 292,522 recordings, 215,594 shows and all 69,728 requested coordinate results. Twenty-one artists have no recordings. The capture contains metadata only; no normalization identity or untouched evaluation status follows from it.

## What leaves the server

Rows contain catalog IDs, UUIDs, parent pointers, stored flags, numeric metadata, timestamps, and text profiles. Each text profile contains only nullness, Unicode character count, UTF-8 byte count, and MD5. No artist name, source prose, contact, upstream identifier, URL, or display-date text is exported. Null and empty text remain distinguishable.

Computing a text profile is content-derived metadata processing. It is not semantic parsing, and it is not zero access to content on the server. MD5 is a metadata fingerprint, not an identity, audio-duplicate assertion, or replacement for a historical Python family hash. Metadata profiles may still be sensitive and belong with the private evidence artifacts.

The recording provider, retained input revision/generation, knowledge timestamp, verified audio format, and event-date precision are explicitly unknown. `sources.flac_type` is retained as a stored integer, not interpreted as verified audio format. `shows.date` is retained as `catalog_sort_date`; its precision is not inferred. Artist upstream configuration does not establish a recording provider.

Prior exposed or reserved coordinates retain their original namespaces. A `local-catalog` numeric ID is never queried as a production ID. Native track IDs and UUIDs are queried separately, so disagreement remains visible. Both track → source and track → set → source are retained; a current parent hit does not authenticate an old historical binding.

## Authenticate the lookup seed plan

The capture accepts only a closed metadata-only JSON document with version `full-metadata-track-lookup-seeds-v1`, `seeds`, and `authorities`. Each seed has exactly `catalog_namespace`, `track_id`, and `track_uuid`; at least one coordinate is required. Supported namespaces are `catalog-2026-10-17` and `local-catalog`. Production native IDs and UUIDs are independent coordinates, not interchangeable keys.

An empty seed list is valid only with explicit authority metadata. The plan permits at most 100,000 distinct seed tuples and 1,000 authority entries. Each authority records a relative metadata path, byte length, and SHA-256. The operator supplies the plan SHA-256 from outside the capture. The capture authenticates those exact plan bytes and retains the authority references; it does **not** reopen their files or prove that the operator included every prior reservation. Reservation preparation must separately establish that coverage before later plaintext or model access.

Example shape (synthetic coordinates):

```json
{
  "version": "full-metadata-track-lookup-seeds-v1",
  "seeds": [{"catalog_namespace": "catalog-2026-10-17", "track_id": 42, "track_uuid": null}],
  "authorities": [{"path": "reservation/receipt.json", "bytes": 123, "sha256": "0000000000000000000000000000000000000000000000000000000000000000"}]
}
```

The zero hash is illustrative and must be replaced by the actual authority hash. The capture never reads adjudication targets, labels, or held-out prose to construct this plan.

## Run and retain one snapshot

The command uses the same credential transport as the existing scale capture: it reads the `relisten-db-app` secret through `kubectl`, puts the password only in the child environment, and connects directly to `relisten-db-ro.tail09dbf.ts.net:5432/app` with SSL and transaction read-only enforcement. There is no endpoint, arbitrary SQL, or credential CLI override. Inherited `PG*` settings are removed so `PGHOSTADDR` or a service cannot silently redirect it.

From the repository root, after the new files have been integrated:

```sh
python3 docs/research/2026-10-17-full-normalization/scale/scripts/capture_metadata_frame.py \
  /absolute/new-output-directory \
  --seeds /absolute/metadata-seeds.json \
  --seeds-sha256 EXTERNALLY_PINNED_SHA256
```

The output directory must not exist. The fixed SQL runs all sections and bounded seed chunks in **one repeatable-read, read-only transaction**. The header captures its snapshot, replica status, replay position, server encoding, and transaction timestamp. The validator requires UTF-8 on both ends and a replica by default. Counts and catalog-column metadata come from that same transaction; root's earlier census does not substitute for them.

Each streamed JSON line is validated against the closed schema **before** it is written, including unknown-property rejection. A malformed plaintext sentinel is excluded even from partial output and SQLite. On-disk indexes enforce uniqueness, complete requested-coordinate coverage, parent consistency, and same-snapshot counts without keeping the whole roster in memory. Dangling or contradictory stored pointers remain explicit diagnostics rather than disappearing through inner joins.

The query is limited to 15 minutes per statement and 30 minutes overall. Seed input is at most 16 MiB, each output line at most 64 KiB, and the frame at most 5 million rows and 2 GiB. Capacity overflow fails the whole attempt; it never truncates a complete frame. SQL stderr is discarded because PostgreSQL errors can echo values. Failure receipts contain only a bounded reason code and process exit status.

A footer is insufficient for success because it is emitted before `COMMIT`. Completion requires valid footer and counts, successful `psql` process exit after commit, immutable input/source rechecks, and a final `manifest.json`. Publication refuses existing destinations. The final manifest link is the last publication operation; its harmless `manifest.partial.json` alias is retained so post-publication cleanup cannot relabel success as failure. `manifest.json` is the sole completion marker; an interrupted directory, `frame.partial.jsonl`, or even a published frame without that marker is incomplete.

Success retains `request.json`, exact `lookup-seeds.json`, template and executed SQL, `frame.jsonl`, `validation.sqlite`, and a manifest with all artifact hashes, counts, join diagnostics, seed outcomes, runtime/source pins, and qualifications. Failure retains the already validated prefix and a safe `failure.json` when possible. Retry uses a **new directory and a new complete snapshot**; never append to or stitch partial captures. No credentials or SQL stderr are retained.

## Verify before production capture

Run the focused contracts first:

```sh
python3 -m unittest discover \
  -s docs/research/2026-10-17-full-normalization/scale/scripts \
  -p 'test_capture_metadata_frame.py' -v
RELISTEN_METADATA_FRAME_POSTGRES_TESTS=1 python3 -m unittest discover \
  -s docs/research/2026-10-17-full-normalization/scale/scripts \
  -p 'test_capture_metadata_frame_postgres.py' -v
```

The optional PostgreSQL test creates and removes only its UUID-named disposable database on the existing local server at `127.0.0.1:15432`. It executes the real fixed SQL against synthetic plaintext sentinels, Unicode, empty/null fields, a zero-source artist, and independent native/UUID lookups. It does not expose a production local-endpoint mode or weaken replica validation in the real command. The synthetic suite covers exact output/hash behavior, invalid seeds before transport, rejected plaintext before partial writes, COMMIT/process failure, inconsistent or incomplete streams, input mutation, and new-directory recovery.

Have root execute [the fixed plan queries](sql/full-catalog-metadata-frame-explain.sql) with its approved read-only connection before the actual capture. These are `EXPLAIN (FORMAT JSON, COSTS TRUE)` only, with no `ANALYZE`; they return estimates, not source rows. The actual capture SQL is [full-catalog-metadata-frame.sql](sql/full-catalog-metadata-frame.sql).

The executed snapshot contains 582,272 JSONL rows and 673,728,934 bytes, below the 2 GiB refusal bound. It includes all 512,495 artist/recording/show rows, 46 schema rows, three snapshot/count/footer rows and 69,728 coordinate results. The independent audit reconciles every row and lookup, including the local coordinate that must remain unqueried. Native-ID and UUID parents agree for every paired request; current agreement does not authenticate historical bindings.

The four production plan estimates use the expected complete roster scans and unique ID/UUID indexes for targeted track lookups. No full track scan was required. Source text length affects server hashing cost rather than proportional output size. These results establish this capture's completeness and size, not production normalization throughput or query latency.

## Reservation and exposure limits

The existing 32,593-recording sample and the 2,184 permitted development recordings are development evidence, not new release gold. Preserve exact known exposure/reservation receipts and old Python duplicate-family definitions; new SQL profiles cannot replace them. Equal old/current artist counts do not prove identical UUID rosters. Missing exposure history remains unknown.

This capture alone neither assigns partitions nor proves artist, duplicate-family, time, or format independence. A subsequent authenticated reservation preparation must reconcile original-coordinate exposure, current parent lookups, missing bindings, and unknown families **before any new plaintext inspection, semantic extraction, training, or model access**. Recording/provider ownership and format qualifications must remain unknown until supported by separate evidence. No production policy is activated by capturing the frame.
