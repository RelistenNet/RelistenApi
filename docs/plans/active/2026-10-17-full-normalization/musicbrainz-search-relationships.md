# Preserve recording links in MusicBrainz work search

MusicBrainz work search can embed a recording relationship without a `target-type`
property. The decoder now validates that recording target and preserves its raw
relationship citation. It does not treat the recording as an artist or a writer.
An explicitly declared artist target still requires a valid embedded artist.
Malformed recording targets retain their own cited diagnostics.

The live development response contained six work hits and 1,125 relationships:
nine artist targets and 1,116 recording targets. Decoder v6 incorrectly marked
all recording targets as missing artist relationships. A fresh v7 decode of the
same retained response preserves all six hits and all relationships, with no
decode issues. The original response and v6 interpretation remain unchanged.
Entity hydration and complete external candidate acquisition remain separate work.

## Version and correction behavior

The decoder version is now `provider-observations-v7`. Recording and resolver
fingerprints include that version, so an old pending resolution is refused with
`processing_version_changed`. Terminal history still replays its original values.
New positive processing needs a compatible extraction from the current producer;
an old extraction cannot gain current authority by receiving a new attempt UUID.
Original show, source and accepted identity UUIDs remain unchanged.

Historical rejection and retraction must remain possible. Their input reader
permits the exact preceding v6 recording producer when every other producer
component matches. This allowance applies only to negative review, during both
preparation and application. It still authenticates the published extraction,
original ticket, every retained input and file, selected Local root, complete
field text and exact reproduced alias scopes. The v7 change affects MusicBrainz
search, so those recording-source decoders and alias grammars have not changed.
Other historical producers receive no new allowance.

A saved negative preparation keeps its original evidence and its separately
observed resource heads. A changed head invalidates that preparation; a fresh
negative preparation can correct the historical claim while observing today's
head. Positive Accept/Reassign decisions, ordinary resolution and correspondence
continue to require the current producer. No schema or public API change is needed.
These changes do not rewrite original evidence or accepted decisions. The checks
below do not exercise a binary downgrade. Incompatible positive work needs a fresh
compatible extraction.

## Verification

The [verification receipt](../../../research/2026-10-17-full-normalization/scale/evidence/musicbrainz-v7-verification-v1.json)
records 619 passing affected checks and five additional passing historical cases.
The historical cases use genuine public state produced by the old binary, then
restored into separate owned local databases without changing UUIDs or version
strings. Four cover new and saved Reject/Retract decisions, positive refusal,
changed-head refusal, exact replay and unchanged accepted facts. The fifth checks
older checkpoint history, terminal replay and pending-version refusal.

An initial test failure exposed a fixture assertion that required a historical
resolver version to equal the current version. The test now verifies the recorded
version against its original attempts and stored tickets. Fixture bytes, hashes
and original-request checks remain intact. The current broad suite runs separately;
the targeted results do not claim that it has finished.
