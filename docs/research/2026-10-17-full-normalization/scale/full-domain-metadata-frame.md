# Complete venue, recording-file and repertoire metadata frame

The `full-domain-metadata-frame-v1` command captures every current venue, recording set, recording file, provider setlist, repertoire song and song-to-setlist association. It also captures complete artist, recording and show coordinate rosters in the same transaction. Those parent rosters let the validator check relationships without borrowing current state from an earlier snapshot.

This frame supplies denominators and current catalog links for venue, song, composition and people normalization. It contains no identity judgments, training targets or semantic extraction. A later reservation procedure must reconcile all known exposure before any new source text, labels or model access. Metadata hashes do not establish identity or independent evaluation.

The existing [artist/recording/show frame](full-metadata-frame.md), its command, seed lookups, verifier and evidence remain version 1. The new command is separate. The old frame can support historical comparisons; it is not same-snapshot parent authority for this capture.

## What the frame preserves

The SQL names exactly nine tables. It never queries listener, user or listening-history tables.

| Catalog table | Exported information |
| --- | --- |
| `artists` | Complete native ID/UUID roster and timestamps, including artists with no recordings. |
| `sources` | Complete recording ID/UUID roster, raw artist/show/venue pointers, joined parent UUIDs and timestamps. |
| `shows` | Complete show ID/UUID roster, raw artist/venue pointers, joined parent UUIDs, stored sort date and timestamps. |
| `venues` | Every native ID/UUID, raw optional artist pointer, joined artist UUID, coordinates, timestamps and text profiles. |
| `source_sets` | Every native ID/UUID, raw source pointer, joined source UUID, stored index/encore flag, timestamps and name profile. |
| `source_tracks` | Every native ID/UUID, independent direct-source and set pointers, optional artist pointer, joined parent UUIDs, stored position/duration/orphan flag, timestamps and text profiles. |
| `setlist_shows` | Every native ID/UUID, raw artist/venue pointers, joined parent UUIDs, stored sort date, tour/era coordinates, timestamps and identifier profile. |
| `setlist_songs` | Every native ID/UUID, raw artist pointer, joined artist UUID, timestamps and name/slug/identifier profiles. |
| `setlist_songs_plays` | Every distinct native song/show pair, both joined UUIDs and exact row multiplicity. |

All rosters start from their own tables and use `LEFT JOIN` for parent coordinates. A null pointer, a pointer to a missing parent and a resolved pointer stay distinguishable. The validator checks each joined UUID against the corresponding native parent in its own complete roster. It reports dangling pointers and disagreements between a track's direct source and its set's source. The stored `is_orphaned` flag is retained separately from these observed relationships.

Native IDs retain signed and zero values; UUIDs retain canonical zero values. The stream uses native IDs as row coordinates. Equal UUIDs on different native rows remain visible as duplicate-UUID diagnostics rather than merging those rows.

The source-set index, name and encore flag describe stored sets. They do not establish distinct performances, dates or start times. Both show tables retain their stored dates with `event_date_precision: null`; a catalog sort date does not prove date precision. Tour and era IDs are retained as native coordinates. Tour, era and year parent tables are outside this frame's reconciliation scope.

The deployed song-to-setlist relation has two foreign keys and a unique pair index. It has no occurrence ID, order, set boundary or timestamp. The export groups identical native pairs and records `multiplicity`; same-snapshot counts retain both physical rows and distinct pairs. This also preserves duplicate rows if a catalog lacking that unique index is inspected with the same column contract. The capture does not invent repeated-song order or performance identity from these associations.

## Text and identity limits

For each exported text field, the database computes exactly four values: `is_null`, Unicode character count, UTF-8 byte count and the MD5 of the stored text encoded as UTF-8. Null and empty strings remain distinct. No name, title, URL, contact, upstream identifier or prose leaves the server as plaintext.

Venue profiles cover `name`, `location`, `upstream_identifier`, `slug` and `past_names`. Track profiles cover `title`, `slug`, `mp3_url`, `mp3_md5`, `flac_md5` and `flac_url`. Setlists and songs profile their identifier/name fields as listed in the fixed SQL. A profile of the stored `mp3_md5` or `flac_md5` text is a hash of that text; it does not verify an audio checksum or audio availability.

Provider ownership, event precision, knowledge clocks and exposure history remain explicitly unavailable. These tables do not establish cross-artist composition identity, author credits, taper identity, source lineage or untouched evaluation status. The frame supports later source selection and reservation reconciliation; it does not activate a normalization policy.

## Capture and retain one snapshot

The command uses the existing fixed credential transport: `kubectl` reads the `relisten-db-app` secret in the `relisten3-k3s` context, and `psql` connects only to `relisten-db-ro.tail09dbf.ts.net:5432/app` with SSL. The password exists only in the child environment. Inherited `PG*` settings are removed, and raw PostgreSQL stderr is discarded because errors can echo source values. There is no arbitrary SQL, endpoint or credential override.

From the repository root:

```sh
python3 docs/research/2026-10-17-full-normalization/scale/scripts/capture_domain_metadata_frame.py \
  capture /absolute/new-output-directory
```

The output directory must not exist. All counts, exact column metadata, parent rosters, domain rows and footer run in one repeatable-read, read-only transaction. The header retains the snapshot, capture timestamp, replica status, replay position and UTF-8 encodings. The production command requires a replica. It does not accept a lookup seed plan or reopen prior sampling/label artifacts.

Each line passes a closed schema before reaching the partial evidence file or SQLite. Unknown properties, including nested plaintext fields, fail without retaining that line. SQLite holds only the coordinates needed to reconcile native parents and multiplicity; it uses an 8 MiB page cache and disk indexes. It does not load the complete track roster into Python memory.

The refusal limits are 64 KiB per line, 20 million stream rows, 16 GiB of frame bytes and an 8 GiB SQLite main database. The SQL uses 16 MiB `work_mem`, a 45-minute statement limit and a 90-minute process deadline. These are bounds, not measured capture time or disk requirements. Counts that cannot fit the row bound fail before the rosters; any later capacity failure invalidates the whole attempt.

Capture and offline verification also require at least 4 GiB available space on each relevant output filesystem. The command checks before creating the capture directory or verification index, every 1,000 streamed lines, at the end of the stream and before finalization. Verification checks both the retained frame filesystem and the new index filesystem; paths on the same filesystem share one capacity check. An explicitly exhausted inode count also refuses the operation.

The reserve prevents a complete catalog capture from consuming the host's remaining disk without an operator-visible refusal. It is an observed floor, not an exclusive reservation: concurrent processes can use space between checks, and allocation or filesystem-stat errors still fail the attempt. Capture retains its validated prefix without a completed manifest. Failed verification may retain its separate partial index but leaves the original completed capture unchanged. Neither operation deletes evidence, truncates the roster or retries with a smaller sample. If the whole frame cannot fit, choose another storage location and start a new complete capture. Leave room for both SQLite indexes and ordinary filesystem overhead; their refusal maxima do not predict their actual sizes.

Completion requires all columns and row counts, exact play multiplicity, consistent joined tuples, the footer, successful `psql` exit after `COMMIT`, and unchanged source/template bytes. The template is executed verbatim. A completed directory retains `request.json`, the template, executed SQL, `frame.jsonl`, `validation.sqlite` and their hashes in `manifest.json`.

Only the final `manifest.json` link marks completion. Publication refuses existing destinations. The harmless `manifest.partial.json` alias remains after publication so cleanup cannot turn a completed capture into a reported failure. A partial frame, footer or published frame without the final manifest is incomplete. Failure retains only the validated prefix and a bounded reason/exit-code receipt when possible. Retry uses a new directory and a fresh complete snapshot; never append to or stitch captures.

## Verify a completed capture offline

Pin the completed manifest SHA-256 independently, then revalidate into a new SQLite index outside the capture directory:

```sh
python3 docs/research/2026-10-17-full-normalization/scale/scripts/capture_domain_metadata_frame.py \
  verify /absolute/completed-capture \
  --manifest-sha256 EXTERNALLY_PINNED_SHA256 \
  --index /absolute/new-verification.sqlite
```

The verifier authenticates the retained artifact bytes, replays every bounded line through the closed validator and reconciles all counts and current parent tuples. It compares the derived summary with the pinned manifest and rechecks the artifact hashes before returning success. It never starts transport, runs SQL or opens executable paths supplied by the manifest. Its own current sources are hashed separately. The caller should retain the compact verification result and its externally pinned manifest reference; a verification index alone is not a completion receipt.

An offline verification proves consistency with the retained capture. It does not prove that a database row existed historically, establish original exposure or certify identity accuracy.

## Check SQL and behavioral contracts

Before a production capture, inspect estimates from [the fixed EXPLAIN queries](sql/full-domain-metadata-frame-explain.sql). They use `EXPLAIN (FORMAT JSON, COSTS TRUE)` without `ANALYZE`; no source rows are returned. A plan estimate is not a throughput measurement. The executed export is [full-domain-metadata-frame.sql](sql/full-domain-metadata-frame.sql).

Run synthetic capture and verification contracts, then the opt-in real SQL test against an owned local disposable database:

```sh
python3 -m unittest discover \
  -s docs/research/2026-10-17-full-normalization/scale/scripts \
  -p 'test_capture_domain_metadata_frame.py' -v
RELISTEN_METADATA_FRAME_POSTGRES_TESTS=1 python3 -m unittest discover \
  -s docs/research/2026-10-17-full-normalization/scale/scripts \
  -p 'test_capture_domain_metadata_frame_postgres.py' -v
```

The PostgreSQL test creates and removes only its generated `relisten_domain_frame_test_*` database on `127.0.0.1:15432`. Its synthetic catalog deliberately includes missing parents, inconsistent direct/set ownership, duplicate associations, Unicode, null/empty profiles and plaintext sentinels. It executes the actual SQL and validator. The synthetic capture tests cover failed commit, rejected plaintext before partial writes, changed inputs, bounds, tampering, offline reconciliation and retry into a new directory. Storage cases simulate refusal before creation, during streaming and before success, independent frame/index filesystems, exhausted inodes and failed file allocation. Neither test enables provider imports, schedules or production policy.
