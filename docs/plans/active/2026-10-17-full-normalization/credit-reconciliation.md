# Replace one contributor's automatic credit claims

When a provider corrects a taper from Alice to Bob, accepting Bob must not leave
Alice indefinitely accepted on that provider's authority. Other providers may
still support Alice, and a reviewed decision has its own authority. Reconcile
support separately before deciding which automatic credits can be retired.

## Explicit accepted-write contract

`NormalizedPeopleService.ReconcileRecordingCreditsAsync(workUuid, request, ct)`
accepts a `RecordingCreditReconciliation` naming the recording, published
extraction attempt and contributor replacements. This writes under the existing
`(people, recording, sourceUuid)` owner. It grants no identity-matching policy
authority; the resolution host must establish supported person assignments first.

Each `RecordingCreditContributorReplacement` names its original contributor,
complete scopes and resolved credits. `RecordingCreditCompleteScope` is a role
plus an optional checked track UUID. A null track UUID means recording-wide
evidence; it cannot stand in for an unbound file.

Require the current compatible recording-extraction contract. Older published
versions may omit file credits and cannot establish complete coverage. The
publication ticket must consume that extraction's original receipt and complete
provider, catalog, identifier, UUID and mapping read set. Selecting fresh clocks
cannot validate an older extraction.

Declare only scopes whose extraction and resolution succeeded completely. Every
extracted mention in a declared scope must have its corresponding accepted or
pending credit. Original wording, role, checked scope and citations must agree.
Every citation must belong to a matching extracted mention. Another field or
directory in the same input revision cannot supply additional support.

Set `OriginalText` to the extracted mention's name. The full supporting quote is
also allowed when that exact citation names only one mention in the same role
and scope. A structured field such as `Alice Recorder; Bob Transfer` can produce
two mentions with the same full-field quote. Keep their names separate in the
credit rows and retain the complete original quote in each citation. One shared
quote cannot make Alice's credit account for Bob's mention.

Pending alternatives remain distinguishable from accepted assignments. One mention
cannot establish two different accepted people. A credit can retain several
matching citations, such as repeated evidence for the same name and scope.

## Support and retirement

Migration 37, `AddRecordingCreditContributorSupport`, retains citation-level
support episodes, the work that introduced or retired each episode, and whether
reconciliation introduced the credit. Replacing one contributor retires only its
support in the explicitly complete scopes.

Retire an automatic credit only when reconciliation introduced it, no live
contributor supports it, and it has no independent append-publication support.
Keep its history and evidence. A shared credit survives while another contributor
supports it. Preexisting append-style credits retain their baseline authority.

`PeoplePublication` remains the append-style interface. It can add independent
support but cannot overwrite a credit still supported by another managed claim.
This preserves callers that have no complete replacement contract.

Reviewed credits remain protected. When a later input revision changes the name
in the same reviewed provider field, retain the new claim as pending. An unchanged
input can contain several credited names, even when the extractor cites their
shared field. Reviewing Alice does not hold Bob merely because both names occur
there. A different identity proposed for the same wording and overlapping citation
still needs review. The writer records support against its actual final credit,
including a coalesced credit or a pending claim.

## Incomplete evidence and recovery

Missing inputs, failed extraction and unbound file scope cannot authorize
withdrawal. Omit unsafe scopes and retain their prior support. The current
completeness contract also rejects decoder issues rather than assuming their
missing content is unrelated. Future finer scope classification needs an explicit
tested contract; it cannot silently turn partial extraction into deletion proof.

The service snapshots request collections before asynchronous work. Validate
complete scopes, update support, write credits and retire unsupported automatic
claims in the same publication transaction. A stale ticket invokes no callback;
rollback changes neither support nor accepted credits. Reusing the work UUID
returns its existing publication receipt.

## Reviewed context and verification

`ReadCreditContextAsync(sourceUuid, ct)` returns current credit revisions,
historical reviewed exclusions with exact citations, person redirects and
original owner observations from one primary repeatable-read snapshot. Its people
epoch also fences an absent recording owner and later review. Keep these stamps
with [resolution proposals](people-resolution.md).

Context reads allow 10,000 claim revisions, 50,000 citations and bounded redirect
closure. Overflow fails without dropping an exclusion. The context is evidence
for a later decision, not an accepted matching result.

Verify corrected names, shared contributors, last-support retirement, append
baselines, unchanged runs, reviewed fields, distinct spans, invalid same-input
citations, file withdrawal, unbound scope, older extraction versions, stale input,
rollback and replay. Use actual published extractions and inspect accepted credit
reads and retained support. Apply migration 37 before deploying this binary:
append publication also uses the new support tables. Retain support episodes,
credits and history on rollback. Pause all accepted people publishers before
rolling back to a binary that does not record contributor and append support.
Do not resume that older append writer against managed credits; it cannot
preserve the new support contract. Read rollback can disable normalized routes
independently.
