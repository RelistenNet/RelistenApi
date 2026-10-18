# Setlist correspondence and removal review

Migration 42 separates reviewed identity correspondence from accepted setlist metadata. `ReviewSetlistCorrespondenceAsync` records an operator's interpretation of exact retained source claims. It changes the edition owner's editorial revision and authorization ledger. It does not replace an accepted image, mark metadata reviewed, select an edition or change playback.

Use this command when a source passage or set should keep an existing accepted UUID, when several observations intentionally share a target, or when an explicit accepted identity should be removed. `ApplySetlistStructureDecisionAsync` remains the separate full-image review command. Existing rows reviewed through that command retain their protection; a correspondence approval does not release them for automatic replacement.

## Prepare an exact decision

The owner is the existing `(songs, setlist, editionUuid)` subject. `SetlistCorrespondenceDecision` contains:

- A stable operation UUID, the subject and edition UUIDs, and the expected editorial revision.
- `ExpectedEditionRevision` and `ExpectedStructureRevision`. The latter is zero only when the edition has no accepted structure yet. The metadata edition must already exist.
- Correspondence approvals and removal approvals, with stable authorization UUIDs and a reason. The authenticated caller supplies `trustedOperator` separately.

A review command is limited to 16 MiB of serialized JSON, 100 approvals in total, 100 source account references and 100 targets per correspondence item. The service preflights the distinct compatible proposal batches once per command, with a combined 32 MiB limit. Overflow rejects the entire command; it never accepts a truncated subset.

A correspondence approval names the exact published proposal attempt, input revision, account index and complete original account. `SetlistClaimReference` identifies a set index or a passage proposal UUID within that account. It never uses a title, song identity or accepted position as occurrence identity. Targets carry their UUIDs and expected revisions; zero declares a new target. Sets use their current structure revision, while occurrences and editions use their own metadata revision.

Evidence must include the source set boundary or passage title and must belong to the named source input. Removal evidence belongs to the supplied complete source accounts. Every matching account observation in the removal's proposal batches must be retained, including conflicting or duplicate observations. The operator explicitly decides removal; missing text is not an automatic absence rule.

The command supports `Authorize`, `Block` and `Revoke`. Each exact source claim or removal target has one current authorization head. `ExpectedPriorAuthorizationUuid` is null for an absent head and otherwise names the head being replaced. A block or revocation remains a head, so deleting approval from the active set cannot restore an older interpretation. Blocking a source blocks automatic mapping of that claim; it is not a generic candidate-pair rejection API.

For first-image acceptance, first publish the ordinary metadata edition, then review correspondence with `ExpectedStructureRevision=0` and the edition's current revision. Allocate target UUIDs before review and use expected revision zero for targets that do not yet exist. The later complete image must still satisfy all set, occurrence, order, evidence and component rules.

## Historical review does not assert freshness

An operator may approve historical retained evidence after the raw resource or catalog has changed. The review validates the immutable published batch, current compatible processor version, exact account and claim, supporting evidence and current target revisions. It stores the proposal's original tickets unchanged. It deliberately takes no raw resource/catalog freshness locks and does not claim that those original observations are current.

This distinction matters on retry. Exact operation replay returns the original review receipt, even after later changes. A different command under the same operation UUID conflicts. A new operation must use current edition, structure, target and authorization-head revisions. It must not substitute current source clocks for an older proposal's clocks. A new authorization requires a batch compatible with the current processor. Blocking or revoking an existing head instead checks its immutable prior source claim and retains its original tickets, so an old decoder version cannot prevent revocation. It does not reinterpret that account with newer defaults. Saved review history remains available.

## Publish the authorized image

After review, admit a new image Work attempt using the edition owner's updated editorial state and `GetSetlistStructureReadSetAsync(image)`. That helper still returns the original proposal publication receipts and raw/catalog/mapping observations. Approval does not refresh them. A changed source head or catalog clock makes acceptance stale, even when the historical review itself succeeded.

Put the active approval UUID on `SetlistSetCorrespondence.AuthorizationUuid` or `SetlistPassageCorrespondence.AuthorizationUuid`. The approval authorizes exactly its source claim and ordered target list. Before first consumption, target revisions must still match the approved snapshots, including absence for a newly allocated UUID. Once the current accepted image carries that approval, later metadata updates use normal entity revision checks without freezing those rows. It cannot be attached to another proposal/account/input revision. A matching stable retained claim or qualified provider identity can still reuse prior accepted correspondence through the existing identity rules. That history carries its original approval provenance, and the originating approval must remain active. Current blocks, revocations and replacements prevent history from resurrecting an overridden mapping.

For removals, add explicit tombstones and matching `SetlistRemovalAuthorization` entries in `RemovalAuthorizations`. Each entry names its target kind, target UUID and active approval UUID. The publication checks the exact approved target revision. An old approval cannot remove a target after reactivation. A retained, already consumed tombstone may carry the same provenance through subsequent images. Withdrawing an entire edition also requires its explicit edition removal approval and all set/occurrence tombstones; clear display selection first.

Correspondence authorization supplies identity authority only. Source wording, literal dates, known order/membership, transitions, complete image coverage, target ownership and entity revision checks still apply. An authorization can name multiple passage targets, but it does not infer a split or authorize invented component text/order. Existing reviewed metadata and whole-claim exclusions remain enforced. Changing an authorization reference is not a new semantic claim that bypasses rejection.

The Work callback commits accepted revisions, correspondence approval references, removal-consumption history and the publication receipt together. Cancellation or a later invalid row rolls back all of them. Review commits its ledger entries, active heads and shared review receipt together; an invalid later approval rolls back the entire command.

## Read history and remaining policy

`GetSetlistAuthorizationsAsync(editionUuid, limit=100, afterUuid=null)` pages immutable events by UUID. The limit must be 1–200. A page is capped at 32 MiB of stored payload and source-ticket JSON, checked before materialization in the same read-only snapshot. An oversized page fails without partial results; request a smaller limit. Each record includes the exact original source tickets, review operation and whether it is the current head. Accepted correspondence rows retain their authorization UUID; full-image reviewed mappings retain their existing music revision/review-operation provenance. Internal typed structure reads expose authorization references without publishing private source accounts through HTTP.

No evaluated automatic correspondence/removal policy is enabled. A future trusted producer must bind its policy version and independent evaluation approval to exact source/target facts. It cannot use the public review command with a caller-selected policy flag. New unkeyed source revisions still need explicit supported correspondence until that evaluated producer exists. Correspondence-only review preserves metadata authority independently. Matching accuracy and automatic removal rules require separate evaluation.

## Deployment and rollback

Apply migration 42 before deploying the accepted music publisher that reads and consumes these authorizations. The migration retains existing correspondence rows with null authorization references; existing full-image review provenance remains in their music revisions and review receipts.

Before rolling back to an older binary, pause accepted music writers. An older structure writer does not enforce authorization heads and could bypass a block or revocation. Retain the authorization ledger, active heads and removal-consumption history during rollback; do not remove them to make an older writer run. Resume accepted publication only with a compatible writer that enforces those records. This contract does not schedule or enable production processing.
