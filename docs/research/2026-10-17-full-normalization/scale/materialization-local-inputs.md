# Real recording inputs for setlist and file-content publication

A dedicated local copy now has a published extraction page of 25 recordings
across 17 artists. Its 281 file-title proposals include 276 extracted segments
and three files with several segments. Exact terminal replay preserves the saved
selection, attempt UUIDs and outcomes. These are prepared inputs for the
[setlist and file-content consumers](../../../plans/active/2026-10-17-full-normalization/repertoire-consumers.md),
which remain under implementation. No accepted file contents or setlists were
published by this preparation.

The [input receipt](evidence/materialization-local-inputs-v1.json) records the
copy, operator output, read-only inventory and artifact hashes. The source is the
verified migration-51 [repertoire rehearsal](repertoire-local-rehearsal.md).
The new database, `relisten_normalization_materialization_20261018`, retains its
268 artists, 701 recordings, 7,826 files and 209 accepted imported songs.

The copy uses a read-only logical dump and restores into a new local database.
All 12 legacy relation fingerprints and four accepted-music relation fingerprints
match before and after the copy and recording extraction. The original repertoire
rehearsal remains unchanged. Both extraction and replay run the actual
`--normalize-recordings` worker command in Production configuration. All 25 exact
producer Work receipts are published and remain their owners' current publication.
Consumers must still authenticate the original input and dependency read sets
before accepting data. No provider, model, Redis or Temporal connection is needed.

The ignored directory is
`local-dev/normalization-data/2026-10-18-materialization-rehearsal-v1/`.
It retains the copy procedure, dump, baseline fingerprints, saved page UUID and
selectors, exact command outputs and inventory SQL. Reuse the saved page and
processing-attempt coordinates for replay; allocate new attempt UUIDs for later
repertoire and consumer processing. Preserve this input receipt when adding
those separate results. This page establishes real-input preparation and replay,
not inferred matching accuracy, accepted song coverage or a complete backfill.
