# Process Local recordings with their root metadata

A Local recording has two retained inputs. The root metadata identifies the
directory account and can contain taper notes. The directory metadata contains
files, tags and text files. Process them together so a credit in the root is not
lost and a note for another directory cannot become this recording's credit.

This extends the [complete recording processor](provider-processing.md). It saves
extraction proposals; accepted people, songs and playback relationships remain
the responsibility of their domain services and evaluated resolution policy.

## Input and ownership contract

`NormalizationRecordingLookupService.LookupLocalAsync(attemptUuid, rootReference,
directoryReference, expectedArtistUuid?, ct)` pins two actual retained revisions.
The directory resource identifies the lookup registration. Its key is the
canonical JSON tuple `[root, directory]`; the supporting root must have the same
root identity. An expected artist is part of the registration's scope, not a
substitute for checking all configured mappings.

The request retains the directory in `Input` and the root in `SupportingInputs`.
Archive and Phish requests cannot attach arbitrary supporting inputs. Root and
directory revisions belong to the immutable attempt, rather than the registration
identity. Retrying the same attempt uses that original pair. A new retry attempt
selects current heads and must validate the pair again.

One directory contributor owns both inputs and the complete read set returned by
[joint Local binding](local-binding.md). There is no separate root recording or
root extraction group. Several directories can share a root or bind to one catalog
recording. Each directory keeps its own claims, while the owner ticket covers the
union of all inputs and dependencies.

Use exact current media ownership to associate files and recordings. Dates, title
similarity and Local review links cannot establish that association. Partial or
unknown dates remain evidence and do not prevent processing a supported binding.

## Credit and file evidence

Decode the directory's text and file assertions from its retained bytes. For the
root, select only the directory account whose `Origin.Item` equals the directory
resource. That account must be unique. Extract its taper notes using citations to
the actual root revision and original member location.

The extraction group belongs to the directory and may cite that explicitly bound
root account. It cannot cite another directory's notes, even if the words match.
Root notes are account-wide credits. Credits attached to files retain their
separate `FileOrigin`; missing playback associations do not turn file credits into
recording-wide credits.

Proposal validation must reproduce these allowed fields from immutable inputs.
Checking that quoted text occurs somewhere in the root is insufficient. The
directory identity, root membership, original field and text span must all agree.

## Preparation, changes and recovery

The incoming handoff retains its original pair, binding and mapping/artist clocks.
Prepare supported peers separately, then save complete owner membership before
atomic admission. Revalidate each saved Local pair through joint binding before
extraction. A changed root matters even when directory bytes are unchanged.

Two directory accounts can observe different root revisions or mapping clocks.
Those observations cannot be reconciled by choosing the newer revision. Report
stale work and prepare a new attempt. Concurrent contributors, catalog changes
and changed mappings follow the same complete-input admission and publication
rules as other recordings.

Bound retained bytes before loading payloads, and bound root accounts before
materializing decoded observations. Reuse an immutable root within one preparation
instead of loading it for every directory. Exceeding a limit is an explicit
failure; it cannot produce truncated evidence or a successful partial publication.

`NormalizationEvidenceService.GetInputsAsync` checks exact references and byte
lengths before projecting payloads. Defaults allow 100 unique input revisions,
32 MiB per input and 128 MiB in total. `GetCurrentInputReferencesAsync` selects
current identities without loading payloads, for at most 1,000 unique resources.
`NormalizationLocalRecordingSelection.DecodeRoot` permits 100,000 account entries
by default, counting malformed entries too. These bounds apply to complete reads;
shared roots count once per revision. Contract callers can supply explicit read
limits when a supported workload requires them.

Cancellation and unexpected failures preserve recoverable attempts. Stale or
unsupported work preserves the last published proposals. Handoff acknowledgement
requires the durable processing result and original input proof. It cannot stand
in for extraction or accepted matching.

## Verification and operation

For offline reader work, [retained Local shadow admission](../../../research/2026-10-17-full-normalization/scale/retained-local-shadow-inputs.md)
prepares the original root and directory captures with historical media-backed
bindings. It preserves the joint evidence contract without creating current input
heads or publication dependencies.

Verify lookup, dispatch and the proposal reader together. Include root-only taper
notes, directory text, file scopes, Unicode directory keys, unknown dates,
duplicate accounts, missing or ambiguous mappings, changed roots and directories,
mapping changes, and multiple contributors sharing one root. Reject a forged
cross-directory citation and preserve the published head after stale processing
or transaction rollback.

Local uses the existing recording attempt and handoff tables; it needs no separate
attempt schema. Service registration starts no discovery, import or schedule.
Automatic matching, independently evaluated accuracy and public rollout remain
requirements of the [main plan](README.md).
