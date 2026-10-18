# Count recording-text coordinates once

The shared recording-text reader now counts each raw span once when calculating
evidence offsets. It preserves the extracted text, exact quotes, Unicode
code-point coordinates and producer versions. This improves credit and source
alias extraction without changing accepted identities or publication authority.

`RecordingText` in `RecordingCreditExtractor.cs` splits provider text at HTML block
tags and newline boundaries. The previous reader counted both growing prefixes
for every line. Long descriptions therefore repeated an increasing amount of
coordinate work and allocated temporary prefix strings. The replacement carries
a code-point cursor across disjoint raw spans. Boundary endpoints are ASCII, so
they cannot divide a surrogate pair. Blank lines, skipped angle contacts and
invalid-surrogate replacement counting retain their original behavior.

## Verified behavior

The [verification receipt](../../../research/2026-10-17-full-normalization/scale/evidence/recording-text-scan-v1.json)
records 645 distinct passing checks: 36 credit extraction, 206 shared extraction
and credit reconciliation, and 403 source resolution, review and publication
checks. Four explicit large/restored-history cases were not executed in these
runs. Three new public extraction cases check exact citations across mixed
newlines, HTML, entities, emoji, combining characters, angle contacts and invalid
surrogates. The historical credit fixture correction is a separate commit; it
preserves the absence of source-actor output under an older producer.

A diagnostic invokes both public extractors against the old and new assemblies.
Its synthetic inputs contain a 1,000-person roster and 1,001 credit statements.
For each input and extractor, every old and new result remains byte-identical.
Each comparison hashes the complete returned result, including quotes, offsets,
assertion IDs, unresolved output and coverage. There are 112 samples in total.
Two comparisons run in opposite order. Median roster-alias extraction takes 211–214 ms before
the change and about 31 ms afterward. These shared-host observations measure
pure extraction; they do not establish publication or catalog throughput.

## Scale result and remaining work

The preceding 1,000-person lifecycle test failed at its one-hour cancellation
limit. Its last successful observation retained 1,106 committed stages: 1,000
preliminary acceptance stages and 106 complete-field finalizations. This is a
partial observation, not a completed application or the final database count.
The exact binaries, terminal test report, partial proofs and sampled trace are
retained separately from the passing text-reader checks.

Finalization remains required even when it retires no aliases: it records
complete field coverage and clears preliminary observations. Do not skip those
writes to make the scale test pass. The [target batch reader](source-target-batches.md)
now shares current component acquisition within each transaction while retaining
every original identity, complete redirect component and current owner observation.
Preparation and publication remain independent. Repeat the full-size
distinct-person, repeated-claim and 1,001-source lifecycle tests. Full-suite
verification and independent automatic-matching evaluation remain required.
