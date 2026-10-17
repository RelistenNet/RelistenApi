# Accepted setlist structures

The setlist processor publishes retained proposals. `NormalizedMusicService` accepts a complete edition image only after the caller supplies canonical set and occurrence UUIDs and their source correspondence.

Migration 41 adds the current structure and set projections and an immutable correspondence ledger. The original proposal batch, source evidence and accepted revision history remain available after later corrections.

## Build one complete image

`SetlistStructureChange` contains:

- `Edition`: the existing `SetlistEditionChange`, including artist, nullable show, literal date and qualified account identity.
- `Sets`: stable `SetlistStructureSet` identities with nullable position, label, encore number, optional qualified provider identity, evidence and retraction state. Empty sets and repeated labels remain separate identities.
- `Occurrences`: the complete active occurrence image plus explicit tombstones for removed occurrences. Each occurrence retains its independent revision check. `SetUuid=null` means unknown membership.
- `Sources`: exact `(ProposalAttemptUuid, InputRevisionUuid, AccountIndex, Account)` references. The account is the complete `NormalizationSetlistAccountProposal` from that published batch. It retains tape status, footnotes, annotations, performer claims, empty sets and provider wording.
- `SetCorrespondence`, `PassageCorrespondence` and `UnresolvedPassages`: an explicit outcome for every source set and passage.

A complete structure can contain at most 100 source accounts, 10,000 sets and 10,000 occurrences. Its serialized size must not exceed 16 MiB; the structure-bearing publication is limited to 32 MiB. Consumed proposal batches together are limited to 32 MiB. Oversized work fails as a whole rather than dropping source claims.

The structure UUID and edition UUID are distinct stable UUIDs. Allocate new set and occurrence UUIDs before submission and retain the same image when recovering an unfinished attempt. Labels, titles, song identities, positions and proposal UUIDs do not become accepted occurrence UUIDs.

Each source set must map to a set in the image. Each source passage must map to one or more occurrence UUIDs or have an explicit unresolved reason. A source passage can remain unresolved without allocating a canonical occurrence. The complete source account remains available in either case. Every account observation in each consumed proposal batch remains a separate source assertion, including duplicate or conflicting observations.

Unordered editions leave accepted set and root occurrence positions null. Source ordinals remain in the retained account. Ordered acceptance must be supported by source order; it cannot fill missing positions. Components retain their own component position. An active component needs an active root parent, and known parent/child set memberships cannot disagree.

## Consume the original proposal receipt

`GetSetlistStructureReadSetAsync(image)` returns the saved proposal publication receipts, original raw resource generations, exact input revision references, catalog clocks and mapping-registry observation. It reads those saved facts in one read-only repeatable-read transaction and does not substitute current clocks.

Use the returned resource references, dependency owner UUIDs and binding clocks with `NormalizationWorkService.BeginWorkAsync` under `(songs, setlist, editionUuid)`. Pass `InputRevisions` as `expectedInputRevisions`. Preserve the image and the returned receipts through admission and publication.

`PublishSetlistStructureAsync(workUuid, image)` checks that the admitted ticket contains each original proposal publication stamp and its complete original raw/catalog read set. A same-byte fetch that advances a resource generation cannot freshen an old proposal. Generic admission can capture a newer causal dependency; the typed writer rejects that receipt mismatch before writing accepted data. Refresh/reprocess the proposal and build a new decision from its complete receipt instead.

Sources must come from published batches produced by the current `NormalizationSetlistProcessor.Version`. Unsupported historical versions or richer read-set families require reprocessing. They are not deserialized into newer defaults and accepted. Historical accepted images remain readable.

The generic `PublishAsync` and `ApplyDecisionAsync` routes also enforce the structure contract. They synchronously copy the entire structure-bearing publication before their first await, so caller mutation during lock waits cannot change the decision or its audit payload. Publications without a structure retain their existing serialization behavior.

## Correspondence authorization

Automatic publication can allocate a new target when its source claim/provider identity has no accepted target. Once a claim or qualified identity has an accepted target, the latest correspondence decision controls reuse. A fresh UUID cannot replace it through a tombstone. Conflicting targets in that latest decision require review; an older, superseded mapping is not authorization. Reusing an existing target requires either accepted correspondence for that exact retained claim or a matching qualified provider occurrence/set identity. A provider song identity cannot identify a particular performance occurrence. Sharing a new target between several source observations currently requires review. Splitting one source passage into several accepted occurrences also requires review.

These are the current authorization rules while no correspondence policy is active. Evaluated automatic correspondence across revised unkeyed accounts remains required before backfill; routine updates must eventually retain stable UUIDs without routine full-image review. Title or ordinal equality alone will not authorize reuse. Future evaluated correspondence receipts must extend the explicit authorization contract rather than bypass it.

`ApplySetlistStructureDecisionAsync` is a **full-image review**. It marks the structure, edition metadata and every supplied occurrence reviewed. Later automatic replacement is blocked by the existing reviewed-authority guards, even when a caller wants only to retain a correction and add unrelated provider information. A separate fine-grained correspondence review command and immutable authorization ledger are required next, before automatic matching/backfill. That command must approve exact source/target correspondence without reviewing unrelated metadata.

## Atomic replacement, removals and recovery

The writer validates shape, membership, stable ownership, original source reproduction and correspondence before applying the image. Automatic metadata must preserve the source literal date, set labels, passage wording, order, membership and explicit transition assertions. Only a true boolean or the supported arrow-marker syntax establishes a segue. Empty strings, unrecognized marker text and false booleans leave the transition unknown; their original values remain in the source account. Reviewed corrections retain the original source account alongside the corrected accepted values. The edition's qualified account identity cannot change; another account needs another edition.

Every previously active set and occurrence must appear in the image, either active or explicitly retracted. Retiring an active identity currently requires reviewed removal authority. Allocating fresh UUIDs and tombstoning the old image cannot bypass correspondence authorization. This is a temporary baseline, not the release workflow: evaluated correspondence/removal authority must allow clear automatic corrections supported by complete source evidence and validated policy. An unresolved interpretation retains the prior active identity rather than allocating its replacement. Tombstones refer to previously accepted identities and may retain their unchanged repertoire binding after that song is retracted or merged. New or changed bindings still require a live song in the edition’s artist repertoire. Omission cannot delete a passage. A withdrawn edition requires its set and occurrence tombstones in the same image; clear its display selection before withdrawing or reassigning it.

Edition metadata, the structure revision, current set rows, occurrence revisions and correspondence history commit in the existing Work transaction. Invalid later rows, cancellation or a stale publication leave the previous accepted image intact. Exact work/operation replay returns its saved receipt. A new source image uses a new work UUID and current expected entity revisions. Usage records retain their existing revision behavior; a new accepted attempt is not an exact work replay.

Once an edition has a structure record, legacy edition/occurrence delta publication is rejected. Corrections must preserve the complete-image invariant. Legacy editions without a structure remain supported.

## Read and performance contracts

`NormalizedMusicService.GetSetlistStructureAsync(editionUuid)` returns the accepted typed image for normalizer/operator code. Its full `Sources.Account` objects are not an HTTP response. Existing typed edition reads and the nested edition in structure reads overlay the physical display-selection flag. Selection remains independent of source evidence and correspondence; see the [selection contract](setlist-selection.md).

The public [set-list contract](setlist-public-reads.md) exposes sets through `GET /api/v3/normalization/setlists/{uuid}/sets`. It includes empty sets, nullable position and selected per-set evidence, without publishing the private account objects. Occurrence responses include `set_uuid`. Complete public annotation projection still needs its own explicit read contract; the full typed source details are retained internally.

A nullable `ShowUuid` preserves an unassigned account. Creating a supported setlist-only performance is a separate catalog phase using a stable provider/reviewed engagement key. That mutation advances the artist catalog clock. Reprocess the proposal afterward, even when retained bytes are unchanged, before accepting an assignment. Current automatic acceptance requires the original supported performance binding; an association still unresolved by the imported-setlist binder needs an explicit reviewed target. The structure writer never creates shows or changes catalog assignments inside its Work callback. Date equality alone cannot identify an early/late performance.

Recording playback remains governed by the existing recording/show and file contracts. Setlist selection, unresolved passages and missing occurrence alignment do not withhold playable recordings.

## Deployment and rollback

Apply migration 41 before starting this version of any accepted-music publisher. The shared writer checks for a managed structure even when a caller submits a legacy edition or occurrence delta, so the new table is required for those writes too. Migration 41 retains existing edition, occurrence and playback identities.

Before rolling a publisher back to a binary without these contracts, pause all accepted-music publishers. An older writer can bypass complete-image and independent-selection ownership. Do not resume it against managed editions. Keep the additive tables, indexes and immutable history; rolling back public reads does not require deleting accepted data or resuming an older writer.
