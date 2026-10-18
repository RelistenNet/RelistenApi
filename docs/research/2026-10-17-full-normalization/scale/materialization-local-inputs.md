# Real recordings from extraction to v3 file lookup

Twenty-five recordings from 17 artists have passed extraction, repertoire
resolution, file-content publication, exact replay and fresh retries through the
actual worker. The run published 257 contents across 256 files and 16 artists:
250 song contents and seven nonmusic contents. Twenty-five files remain
unresolved. Existing playback remains available for those files.
The inputs include Jerry Garcia Band, Legion of Mary, Reconstruction, Grateful
Dead and artists from the Aadam Jacobs collection.

The [publication and v3 receipt](evidence/recording-contents-local-v1.json) records
225 worker invocations and 2,165 successful HTTP requests. All 1,409 retained
legacy responses match. Normalized reads verify each accepted file image,
250 title-derived song details and their show results against the current
playback ownership exposed by legacy show responses. Fresh retries preserve
content UUIDs and ownership; each of the 257 contents still has one accepted
revision. These checks establish publication and read behavior, not inferred
identity-matching accuracy.

## Inputs and complete file coverage

The [input receipt](evidence/materialization-local-inputs-v1.json) retains the
published extraction page: 281 file-title proposals, 276 extracted segments and
three files with several segments. The [repertoire receipt](evidence/recording-repertoire-local-v1.json)
accounts for 284 claims: 251 provisional song identities with `title_derived`
basis and 33 unresolved decisions with `unsupported_claim_kind`. No inferred
matching policy is active. Titles do not establish cross-artist identity.

A separate read-only [coverage check](evidence/recording-content-expectations-v1.json)
froze every expected file, position, kind and song target before publication.
All 284 claims appear exactly once in its accepted or unresolved partition.
The first published image matches that expectation in full.

The 257 accepted contents cover 256 files. Twenty-five files contain unknown
material. One otherwise-supported song claim and one nonmusic claim share such
files, so their complete images stay unresolved too. The consumer preserves
existing playback while retaining each unresolved reason. Title segment order
supplies no audio times or setlist occurrence alignment; those fields remain
null in the accepted contents and public responses.

## Local copy and preservation

The database `relisten_normalization_materialization_20261018` is a separate
copy of the verified [repertoire rehearsal](repertoire-local-rehearsal.md).
It retains 268 artists, 701 recordings, 7,826 files and 460 accepted repertoire
entries, including all 209 original imported songs. Migration 52 was applied
through the actual worker after saving a dump and baseline fingerprints.

All 12 legacy relation fingerprints and four original accepted-music relation
fingerprints match after publication and retries. The latter comparison excludes
only the new `track_contents` revision kind, then compares complete original
rows, including their UUIDs and payloads. API reads leave accepted and legacy
rows unchanged. No acquisition, model processing, media download or production
write occurred, and no repertoire or correspondence policy was enabled.

The ignored directory
`local-dev/normalization-data/2026-10-18-materialization-rehearsal-v1/`
retains the inputs, migration backup, saved attempt UUIDs, command outputs,
coverage expectations, SQL and HTTP bodies. The receipts authenticate their
exact bytes. Immutable assembly and source snapshots identify the executed
implementation. Resume saved coordinates rather than replacing a failed run
with newly allocated attempts.

## Remaining verification

This copy contains no retained real ordered-setlist source and no accepted
setlist. The [setlist and file-content consumers](../../../plans/active/2026-10-17-full-normalization/repertoire-consumers.md)
have service-level publication and recovery coverage. A real setlist rehearsal
must retain a supported provider account, freeze its order and annotations,
then verify extraction, repertoire decisions, publication and v3 reads.
Legacy song-membership joins cannot reconstruct missing source order.

The [consumer verification record](evidence/repertoire-consumer-contracts-v1.json)
retains the full regression result and corrected capacity-retry rerun. Independent identity evaluation, production policy
activation and the all-artist backfill remain release requirements.
