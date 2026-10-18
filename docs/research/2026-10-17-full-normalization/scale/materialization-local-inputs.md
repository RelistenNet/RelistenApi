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

## Completed recording-title decisions

The next [recording-title receipt](evidence/recording-repertoire-local-v1.json)
records completed repertoire accounts for all 25 saved recording extractions.
The resolver makes 284 claim decisions: 251 visibly `title_derived` provisional
song identities and 33 unresolved decisions with `unsupported_claim_kind`.
All accounts complete and replay through 294 actual worker invocations.
No inferred matching policy is active, and this run merges no identities by title
similarity.

The new database now contains 460 accepted repertoire entries. All 209 original
entries, their imported assignments, external identities and original revision
rows retain their exact fingerprints. All 12 legacy relation fingerprints and the
original rehearsal database also remain unchanged. There are still no accepted
file contents or setlists. The completed accounts supply real inputs for consumer
publication; this run does not yet verify their new song-to-file results over HTTP.

## Expected file-content coverage

A separate read-only [coverage check](evidence/recording-content-expectations-v1.json)
freezes the expected first publication before running the consumer. The complete
284 claims cover 281 files: 251 song claims, eight nonmusic claims and 25 unknown
claims. Each claim appears exactly once in the accepted or unresolved partition.

The expected result is 257 contents across 256 files: 250 song contents and seven
nonmusic contents. Twenty-five files remain unresolved. One otherwise-supported
song claim and one nonmusic claim share files with unknown contents, so the whole
file image stays unresolved. This preserves the complete-image contract while
leaving existing playback available. These are expectations from retained claims
and repertoire decisions, not observations of consumer publication or evidence of
matching accuracy. The command rehearsal must compare its actual first image
against these exact file, position, kind and song coordinates before checking
retries and v3 reads.
