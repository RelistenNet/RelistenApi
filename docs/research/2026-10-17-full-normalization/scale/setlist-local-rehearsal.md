# A real Jerry setlist from source to v3

A retained Grateful Dead page for July 2, 1995 now passes decoding, setlist
processing, repertoire resolution, publication, exact replay and an unchanged
retry in a separate local catalog. The accepted edition contains three sets and
18 occurrences. All 1,699 HTTP requests succeeded, including comparisons of
1,409 legacy responses and 256 existing file-content responses.

The [rehearsal receipt](evidence/setlist-local-v1.json) authenticates the source,
commands, decision exports, executable snapshots, database checks and HTTP bodies.
This verifies real-source publication and public reads. It does not establish
automatic song-identity accuracy.

## Source and accepted data

The source is a 77,369-byte Jerry metadata mirror page, acquired after checking
its provider-qualified account against the local imported setlist. A separate
source-only review froze three blocks, 18 passages, 11 song links, eight transition
markers and 14 Show Facts lines before decoder execution. The full-body check
then verified 70 citation occurrences across 38 distinct spans, complete proposal
equality and the original capture and processing coordinates.

The real page exposed missing extraction of adjacent set headings, the displayed
date and scoped Show Facts. Decoder version 6 now retains these forms with exact
evidence. A separate reader fix exposes Show Facts as a supported account-note
kind, using decoded display text while preserving original markup in evidence.
The ordered list says “Brokedown Palace”; a separate note says “Broken Arrow.”
Both remain available.

Repertoire resolution created 18 visibly provisional, title-derived song entries.
No inferred matching policy was selected. The setlist consumer published three
sets and 18 occurrences. Exact completed replay and one fresh unchanged retry
preserved all 21 target UUIDs; current accepted entity revisions remain 1.
The check joins original proposal UUIDs to public source claims, repertoire
decisions and materializer targets. It does not recreate private ID algorithms
or use title equality to infer those assignments.

The edition has no supported existing performance assignment. It remains
unassigned and unselected under the artist. This run creates no show and moves
no recording. Existing files keep their current playback references.

## Public behavior and preservation

The API walk used three-item pages for sets, occurrences and annotations. It
verified all set labels and links, the 18 occurrences in source order, exact song
targets, eight transition markers, 22 annotation groups and all 14 Show Facts
lines with their original evidence. It also read all 18 song details.

All 12 legacy relation fingerprints match the pre-run copy. Original rows in
four accepted-music relations remain identical subsets after the legitimate
additions. All 257 previously accepted file-content rows remain wholly unchanged.
GET requests leave accepted and legacy rows unchanged. The owned API and Redis
processes were stopped after verification.

The database `relisten_normalization_setlist_20261018` is a new copy of the
[recording rehearsal](materialization-local-inputs.md), at migration 52.
The original recording rehearsal stays intact. Two initial mirror requests
supplied the retained source; processing and API verification made no further
provider requests, model calls, media requests or production writes.

## Reproduce and continue

The checked-in Jerry HTML excerpt and annotation tests reproduce the decoder
and public-reader regressions. Both new tests failed before their fixes.
Afterward, 166 decoder/compatibility checks, 203 processor/consumer/operator
checks and 14 annotation/real-decoder checks passed. The final full-suite rerun
remains required; the earlier [consumer contract record](evidence/repertoire-consumer-contracts-v1.json)
retains its original assertion mismatch and corrected targeted pass.

The ignored directory
`local-dev/normalization-data/2026-10-18-real-setlist-rehearsal-v1/`
retains the full source, frozen expectations, original requests and receipts,
command outputs, public source/decision exports and HTTP bodies. Its `tools-v1/`
bundle retains the capture, export and worker-driver sources and execution
artifacts. Inspect the checked-in receipt first. Replaying the real run requires
that local bundle; resume its saved UUIDs and original input rather than fetching
new bytes or allocating replacement attempts.

The [implementation plan](../../../plans/active/2026-10-17-full-normalization/README.md)
continues with independently adjudicated identity evaluation, automatic matching
and Luna routing, then all-artist backfill and rollout verification. This source
is development and rehearsal material, not an untouched identity benchmark.
