# Materialize repertoire decisions into setlists and file contents

Status: the completed-account reader and both durable consumers are implemented. All 115 focused consumer/operator checks pass. The [real recording rehearsal](../../../research/2026-10-17-full-normalization/scale/materialization-local-inputs.md) publishes 257 contents, verifies their v3 song/file results and preserves 1,409 legacy responses. The final full local suite passes with 2,838 tests, 24 existing skips and zero failures. The [real Jerry setlist rehearsal](../../../research/2026-10-17-full-normalization/scale/setlist-local-rehearsal.md) also passes source-to-v3 verification for three sets and 18 occurrences, including exact replay and unchanged retry. The full-suite result includes its decoder and Show Facts reader fixes. No production correspondence policy or acquisition is enabled by this increment.

The consumers turn completed repertoire decisions into accepted setlist structures and file-content assertions. They preserve existing song, show, source and track UUIDs. New set, occurrence and content UUIDs are allocated once in saved preparation. A set belongs to an edition; a recording belongs to one performance. Neither title order nor a set boundary creates another performance.

This guide is for engineers implementing the consumers, reviewers checking publication authority, and operators resuming retained attempts. It builds on [repertoire resolution](repertoire-resolution.md), [setlist structures](setlist-structure.md), [correspondence review](setlist-correspondence.md), [selection](setlist-selection.md) and the [normalization contracts](../../../design/2026-10-17-normalization-contracts.md).

## Reuse the existing contracts

`NormalizationRecordingProposalService.GetAttemptBatchAsync` and `NormalizationSetlistProposalService.GetAttemptBatchAsync` return immutable extraction batches, including stale or unfinished batches. Consumers must separately authenticate the exact compatible, published extraction and its original Work receipt. The complete recording batch includes every input group and checked file binding. The setlist batch includes every account observation, empty set, passage, annotation, footnote, tape flag and performer assertion.

`NormalizationRepertoireResolutionStore.GetDecisionsAsync` returns durable correspondence rows from accepted stages before the account is complete. A consumer may use only the exact completed account selected below. Partial stages remain useful to the repertoire resolver's own recovery; they do not supply a complete account to these consumers.

`NormalizedMusicService.PublishSetlistStructureAsync` already enforces complete images, original source reproduction, stable set/passage correspondence and removal authority. `MusicPublication.TrackContents` already enforces recording ownership, revision CAS, reviewed protection, exclusions, live file scope and compatible occurrence alignment. Both writers run inside Work publication transactions. Existing normalized reads expose sets, annotations, occurrences, contents and song/composition show lookup. No new playback references or replacement public read routes are needed.

## Requests and outcomes

`NormalizationSetlistMaterializer` and `NormalizationRecordingContentMaterializer` expose these public operations:

```csharp
Task<SetlistMaterializationAttempt> ProcessAsync(
    Guid attemptUuid, SetlistMaterializationRequest request,
    CancellationToken ct = default);
Task<RecordingContentMaterializationAttempt> ProcessAsync(
    Guid attemptUuid, RecordingContentMaterializationRequest request,
    CancellationToken ct = default);
```

Each request contains only `Guid RepertoireResolutionAttemptUuid`. Its completed resolver request determines the exact extraction attempt and source lineage. Setlist requests require `SetlistRepertoireResolutionSource`; recording requests require `RecordingRepertoireResolutionSource`. Imported/provider dictionaries alone identify no performance or file sequence and cannot drive either consumer. No request accepts a winner, image, policy, source clock or claim-to-target mapping.

Each service also exposes `RetryAsync(newAttemptUuid, registrationUuid, ct)`, `GetAsync(attemptUuid, ct)`, `GetDecisionsAsync(attemptUuid, limit, cursor, ct)`, `GetPendingRegistrationsAsync(limit, cursor, ct)` and `GetRecoveryAttemptsAsync(limit, cursor, ct)`. Get/recovery return compact summaries before loading large preparations. Decisions expose bounded correspondence and reason codes; raw account objects remain internal.

An attempt has an immutable request/selection header, processor/source versions, optional preparation, Work admission/publication and terminal result. The setlist unbound-to-bound handoff additionally retains its specific watcher-retirement instruction, ticket and receipt described below; ordinary attempts need no general stage model. States are `Pending`, `Completed` and `Stale`. A completed result includes accepted, retained, review and unresolved counts. `Completed` means the complete consumed account was acknowledged; it does not mean every passage, file or alignment resolved. Exact terminal replay returns the first result. Changing a request under the same attempt UUID conflicts.

A registration follows the authenticated extraction lineage, not a mutable name or an input revision. A recording registration follows the catalog source UUID. A setlist registration follows the existing setlist-processing registration and scope. Retry selects that lineage's current published extraction and a completed repertoire resolution for that exact extraction. If the latter is unavailable, retain `Pending` with `awaiting_repertoire_resolution`; do not substitute a partial result or silently start a new resolver attempt. A new resolution/capture must be durably saved before the consumer resumes.

`ProcessAsync` pins its supplied repertoire-attempt UUID permanently. `RetryAsync` first saves an immutable header containing the registration and previous-attempt UUID, with a nullable selected repertoire-attempt checkpoint. While that checkpoint is null, replay may discover a newly completed account and remains recoverable. Compare-and-swap the selected UUID once a compatible account exists; freeze it before preparation. After selection, resume never substitutes another receipt. This waiting mode is created internally by retry and accepts no caller-selected winner or placeholder repertoire UUID.

## Read one authenticated completed account

The consumption partial on `NormalizationRepertoireResolutionStore` exposes this internal reader:

```csharp
Task<CompletedRepertoireConsumption> ReadCompletedConsumptionAsync(
    NpgsqlConnection connection, NpgsqlTransaction transaction,
    Guid repertoireAttemptUuid, CancellationToken ct);
```

The reader passed 18 new PostgreSQL contract cases and the combined 84-case reader/repertoire/worker regression set, with a successful solution build. Its bounds include 100,000 aggregate redirect-path entries and per-root encoded accepted-payload charging before projection. `RequireCompletedConsumptionAsync` reauthenticates the same frozen account inside an existing Work publication transaction; it opens no second transaction and substitutes no clocks. The broader 2,696-test run predates this reader increment.

The caller supplies its primary read-only repeatable-read transaction. The reader performs no admission, registration or refresh. Its typed result contains:

- The immutable resolver request, lineage and exact source claims; every claim has its immutable decision and correspondence key.
- The completed registration/current-attempt identity and the exact successful source-acknowledgment Work receipt.
- The compatible recording music projection or complete setlist extraction batch, with the exact original producer ticket.
- Canonical repertoire targets with current accepted revisions, artist scope and retained assertion provenance. Repertoire redirects are followed without rewriting stored association coordinates.
- The original producer read set, the repertoire acknowledgment's causal publication stamp, and current accepted identity-owner observations needed by the consumer.

Require a completed resolver result, the matching registration current pointer, a successful acknowledgment stage and an actual published Work receipt. Verify full claim coverage and exact joins back to title `ProposalUuid` plus segment coordinates, or setlist `(InputRevisionUuid, AccountIndex, ProposalUuid)`. Claim UUIDs, proposal UUIDs and repertoire UUIDs are not accepted occurrence/content identities. Reject a request whose source kind or known artist scope contradicts the selected consumer. A completed all-negative account may have a null artist and no targets; retain that valid unresolved snapshot for enrollment rather than treating absence as contradictory ownership.

Recording music reads exclude only the optional primary and additional
`SourceActorAliases` properties before byte preflight and deserialization. The
same SQL projection runs during initial preparation, publication reauthentication
and historical correspondence review. Every original input group, binding,
title, unresolved title, ticket and read-set value remains unchanged. The full
immutable batch stays available from the recording proposal service for people
normalization; excluded alias data must never be treated as complete empty
support. This keeps unrelated people evidence out of the music snapshot budget
without increasing its limit.

Read current accepted images, their revisions, scope columns, correspondence history and active authorization heads in the same transaction as this reader. Each consumer then freezes one typed preparation containing its complete publication proposal and all allocated UUIDs. Use the existing retained-byte limits before loading source batches and history. Do not reconstruct a replacement image from public DTOs that intentionally mask unavailable references.

### Keep source freshness separate from accepted authority

The original extraction's input revisions/generations and every supported nonempty producer read-set family remain exact. Both producers have explicit completeness validators: `NormalizationRecordingReadSet.IsComplete` and `NormalizationSetlistReadSet.IsComplete`. The current recording validator rejects causal dependencies, imported venues, candidate universes and owner observations; the setlist validator also rejects source-identifier and source-UUID dependencies. Do not advertise arbitrary nine-family producer support. Preserve every supported family, and reject richer historical shapes until producer and consumer compatibility is extended together. Never copy today's source clocks into an old proposal.

A completed repertoire association is accepted authority with immutable selection evidence. Consume its exact acknowledgment publication plus the current accepted target/artist state. Its historical candidate epochs remain audit provenance. An unrelated global composition change must not permanently prevent materializing an otherwise current accepted song association. The consumer must not require `GetPendingRegistrationsAsync` to report the repertoire account settled, or copy all historical resolver candidate epochs as current producer requirements.

If a newer repertoire acknowledgment generation has been admitted but has not published, the reader raises `CompletedRepertoireConsumptionPendingException`, carrying the exact requested attempt and owner UUIDs. Consumers retain a pending attempt. Do not combine that newer desired generation with the older publication receipt. A changed current pointer, publication or editorial stamp invalidates the saved consumer context. If the consumer itself evaluates new correspondence or alternatives, it additionally consumes that evaluator's original candidate/owner/policy observations.

Keep genuine causal producer/accepted-owner dependencies separate from observational owner stamps. Reuse the existing Work lock order and complete contributor-membership CAS. Never configure causal edges merely because an observational fact reader saw an owner.

## Build and publish accepted setlists

The accepted owner remains `(songs, setlist, editionUuid)`. Resolve the existing `(artist_uuid, provider, resource_type, provider_key)` edition uniqueness before allocating a new target. A checked artist with an unresolved imported-setlist or performance binding can still produce an edition with `ShowUuid=null`. Source-free show creation and catalog assignment remain separate operations; either mutation requires a newly processed proposal before assignment acceptance. Dates alone do not choose early/late performances.

A setlist registration with no artist still needs durable negative enrollment. Use a source-scoped observation owner `(songs, setlist_materialization, processingRegistrationUuid)` until a canonical edition owner can be selected. That acknowledgment creates no accepted edition or invented artist. Save its exact contributor key/revision and owner coordinates.

When the source becomes bound, save the exact canonical owner choice, publish or acknowledge under that owner, then retire only the registration's obsolete observation contribution. Persist a bounded list of typed retirement requirements, each with the obsolete owner/contributor revision, authenticated installing run, adopted canonical owner/publication and its cleanup ticket/receipt history. Discovery uses actual owned contributions, including admitted runs whose publication failed; a prior completed-negative pointer alone is insufficient. A former canonical owner can also retain an obsolete subscription after artist rebinding. Finish only after every requirement retires or is confirmed superseded by a newer owned revision. A crash between canonical publication and cleanup must resume that instruction.

A capacity block after that publication remains a pending cleanup obligation, not a terminal source-stale attempt. Save its `CleanupRetryRequirement`; omit it from recovery while the owner/membership footprint is unchanged, then resume the same attempt after repair without rechecking old source freshness. A new retry child cannot hide unfinished cleanup.

Cleanup reads current peer coordination and authenticates the prior adoption receipt; it does not require the former source or policy observations to remain current merely to release obsolete keys. Source-only acknowledgment and retirement use typed empty `Work.TryPublishAsync` callbacks; the generic music writer does not recognize this observation owner and must not receive it. Preserve other contributors and the prior accepted image. Two scoped registrations converging on the same canonical edition must reuse its UUID and membership; they cannot each allocate a duplicate edition.

The ordinary bound attempt has these phases:

1. Authenticate the completed account and read current edition/structure/correspondence authority in one snapshot.
2. Save the complete `SetlistStructureChange`, all new UUIDs and the original read set before admission.
3. Admit the complete source contribution under the canonical edition owner, preserving peers and unmanaged baseline.
4. Publish the saved image and attempt receipt atomically through the route below. A decision-only attempt acknowledges its read set without changing accepted rows.

Every account observation remains a separate `SetlistStructureSource`. Match each passage to its repertoire decision through the exact proposal coordinate. Bound/provisional decisions can supply a repertoire UUID; review/unresolved decisions retain their reason and cannot invent that binding. A source passage can retain an occurrence with no song binding when its own occurrence correspondence is supported.

Preserve empty/repeated sets, literal dates, unknown positions, set membership and source annotations. Automatic metadata must satisfy the existing exact-source checks. Conflicting accounts must remain distinct claims. Do not merge their sets or choose one order/date by convenience. If the existing single-image representation cannot faithfully express their disagreement, acknowledge a review decision and retain the previous accepted image.

Initial one-to-one allocation, exact retained correspondence and qualified provider **set/occurrence** identity reuse existing writer authority. A provider song ID identifies no occurrence. A revised unkeyed account must not allocate replacement UUIDs for the same unresolved passages. Preserve the prior accepted image until exact, reviewed or evaluated correspondence supports the update.

Use `ReviewSetlistCorrespondenceAsync` for current manual correspondence/removal approvals. Its historical-exact approval cannot freshen publication clocks. Preserve active blocks, revocations, overridden mappings and exact target revisions; old history cannot resurrect an overridden target. Full-image review remains a stronger command that marks metadata reviewed and blocks automatic replacement. Selection stays with `ApplySelectionAsync` under `(songs, show_setlist_selection, showUuid)`; materialization preserves the physical selection flag.

## Build and publish recording contents

The accepted owner remains `(songs, recording, sourceUuid)`. Freeze complete per-file images from all original input groups and positively bound title proposals. Keep unbound files and conflicting assertions as explicit decisions in the attempt. Do not fabricate a catalog track identity for them.

If input groups assert incompatible sequences for one file, or a proposed sequence collides with reviewed/unmanaged rows at `(track, position)`, retain every claim in the attempt and keep the prior accepted file image. Record a review/unresolved reason until compatible assertions or explicit correspondence authorize a replacement. Do not allocate overlapping rows, choose one group implicitly, or append competing orders.

Each content source reference contains the extraction attempt, input revision, proposal UUID, track UUID, retained file origin and exact segment coordinate/evidence. Its correspondence record names the stable accepted content UUID or an unresolved reason. All active content rows managed by this consumer must be retained or explicitly withdrawn; omission cannot delete them. An incomplete account cannot replace the last complete result.

The additive `TrackContentChange.TitleFacts` retains what the title says about partial coverage, transition markers and supported segment qualifiers. These are source assertions, not verified audio coverage or transition timing. Exact absolute input citations support the source field and qualifier spans, including Unicode code-point offsets. Existing publications omit this optional field when absent, preserving their stored hashes and replay. The existing track-content read exposes optional `title_facts` and `title_facts_status`; both HTTP and stored-publication serializers omit absent details. Details come from the current immutable music revision, with a 32 MiB page preflight. Oversized optional details report `size_limit` while the parent contents and playback remain available.

`TrackTitleSegment.Position` determines order among claims in a file title. It provides no audio time, setlist position or performance identity. Preserve exact wording, kind, partial/reprise qualifiers and source evidence. Unsupported partitioning can remain one unknown file-content assertion or an unresolved source decision, according to the saved extraction; do not split it again in the consumer. Set `StartMs`, `EndMs` and unsupported `OccurrenceUuid` to null.

Existing accepted rows without this consumer's correspondence ledger remain unmanaged baseline. Preserve their UUIDs and metadata; adopting them as managed claims requires explicit supported correspondence. A matching title, position or `provider_item_key` alone cannot bypass the writer's adoption rules.

Several files may reference one occurrence. A file may reference several songs or sets within its recording's single performance. `ProviderFileObservation.SongIdentities` has no occurrence identity and its array order is not a segment mapping. Initial repertoire-backed file contents already support song/show lookup without occurrence alignment. Later automatic alignment requires a separately authenticated correspondence receipt against current same-performance setlist occurrences; neither a unique title nor provider song ID proves which repeated occurrence is present. The alignment context also needs a performance-scoped accepted-edition membership epoch: observed edition owners alone cannot detect a newly arriving rival edition. Reuse the candidate-universe locking/read-set mechanism with an explicit performance scope when this evaluator is implemented. Current music candidate epochs deliberately exclude setlist usage and do not supply that membership fence.

A content UUID cannot move to another track. Current catalog source ownership wins when a track moves; a retained stored source is fallback only when the catalog reference is absent. A new accepted attempt for the current source must authenticate any reuse. Do not apply the former source's stale extraction under the new source owner.

For reconciliation, read internal `MusicRecord<TrackContentChange>` values and stored source/artist scope, not `NormalizationReadService.ListTrackContentsAsync`. The public projection deliberately masks stale links. Preserve reviewed rows and exclusions. Identical automatic facts can be acknowledged without another content revision only after scope and original proof are checked; `Persist` intentionally does not provide this optimization for generic usage writes. New supporting claims may be retained in the correspondence ledger without rewriting identical metadata.

`SaveContent` applies a historical-binding withdrawal check comparable to `SaveOccurrence`: an explicitly authorized unchanged tombstone may preserve its former song/occurrence binding after that target was withdrawn or redirected. New or changed musical bindings still require live scope validation. This correction must not permit arbitrary reactivation, reassignment or omission-based deletion.

## Authenticate publication and automatic correspondence

The dedicated routes on `NormalizedMusicService` accept identifiers only:

```csharp
Task<NormalizationPublicationResult> PublishSetlistMaterializationAsync(
    Guid workUuid, Guid attemptUuid, CancellationToken ct = default);
Task<NormalizationPublicationResult> PublishRecordingContentMaterializationAsync(
    Guid workUuid, Guid attemptUuid, CancellationToken ct = default);
```

Inside the existing Work callback, authenticate the saved attempt/preparation/ticket, exact source and completed repertoire decision, owner, expected current revisions and active authority. The setlist route reuses structure validation/application; the recording route permits only its saved `TrackContents` image. Apply accepted rows, append correspondence/history and record the attempt receipt in the same transaction. A terminal replay authenticates its attempt/work identity before Work skips the callback. No route accepts a caller authority flag or caller-built winner image.

The consumers retain typed evaluated correspondence decisions in their immutable preparation before general backfill. A processor-issued proof records the exact old/new source claims, complete target set and expected target revisions, original read set, evaluated policy/artifact hashes, supported rule and contradictions. A creation, reuse, split, withdrawal and file-to-occurrence alignment are distinct operations. For inferred revised correspondence, split, withdrawal or alignment, publication authenticates the immutable saved processor decision and active independently evaluated policy, unless explicit reviewed authority applies. Supported literal first-image allocation and existing exact/qualified provider correspondence retain their current authority; they do not require a new inferred-correspondence policy. Publication cannot obtain authority from a caller-populated receipt UUID alone.

Reuse the setlist authorization ledger's latest-head and consumption semantics when extending it for evaluated decisions. Migration 52 extends the migration 42 ledger with mutually exclusive reviewed-operation and authenticated processor-decision references; a discriminator alone cannot satisfy the existing foreign key. Retain old reviewed rows, their FK and exact original source tickets. Extend the typed correspondence/removal readers and guards to authenticate the new processor reference; never insert a fictional review operation. Keep reviewed authority distinct. Add the equivalent source correspondence/withdrawal authority for recording contents, including a narrow `ReviewRecordingContentCorrespondenceAsync` command for historical-exact operator approval without marking unrelated metadata reviewed. Do not create a second generic proof store when the materializer's immutable preparation and decision records can authenticate lifecycle. The schema must retain active blocks/overrides and prevent a consumed removal from withdrawing a later reactivation.

The evaluated capabilities have distinct evidence requirements:

- A revised unkeyed set or passage between two independent native usage identities can reuse its accepted target when both complete source images and the current target set agree.
- A passage may reuse its target within an actual provider-qualified set when the checked event/account, set identity and unique accepted repertoire song/kind agree across complete before/after account images. Other sets supply competing claims. A repeated song, rival target or active correspondence block refuses the rule. A provider song ID remains a song identity; it never becomes an occurrence identity.
- A recording's single clear file-content claim can continue under the same checked catalog track and actual provider file identity.
- Evaluated setlist withdrawal requires retained native usage anchors around the removed claim. Revoked or blocked anchor correspondence supplies no authority.

The recording consumer does not automatically withdraw a passage from a revised unkeyed medley. Its changed neighbor claims do not yet have accepted after-image correspondence, so they cannot authenticate removal of the middle claim. Such an attempt records review and preserves the complete previous file image. Exact narrow reviewed correspondence and withdrawal can authorize a replacement, including simultaneous moves into vacated positions. A future evaluated removal rule needs a reachable, independently tested proof path; the pure evaluator's recording-anchor helper alone does not establish one. No caller may fabricate the missing maps.

`RepertoireConsumerPolicyService` registers immutable independently attested artifacts and applies explicit activation/revocation under its Work owner. Registration does not activate a policy. Consumers take a causal dependency on the original policy-owner state, including absence, so activation re-pends an unresolved account without inheriting historical repertoire candidate epochs. Fixture artifacts require explicit opt-in and a disposable test database. No real artifact is supplied here. Split and file-to-occurrence alignment are distinct operations; the evaluator does not infer them from title, ordinal or provider song IDs. Unsupported cases retain the accepted image and an explicit review outcome until their own reviewed or evaluated authority exists.

First-image fixtures can prove the supported source-assertion path. Fixture-evaluated policies must also prove revised unkeyed reuse, conflict refusal and authorized removal without invoking full-image review. Real labels, competing-candidate evaluation and trusted policy activation remain required before enabling those inferred operations for automatic backfill. An initial/manual-only implementation does not complete this contract.

## Failure, retry and discovery

Invalid, missing or wrong-kind source coordinates fail before Work admission. A permanent unsupported source shape or capacity overflow records `Stale` with an explicit reason `source_shape_unsupported` or `materialization_capacity_exceeded`, before accepted publication. Retain only the bounded failure metadata needed to identify the exact source/version and limit profile; never acknowledge unavailable original proof or interpret overflow as an empty complete image. The previous accepted image and playback remain available.

An unchanged permanent failure is not an automatic recovery candidate. Retry requires a changed source receipt, a processor/source version that supports the shape, or an explicitly changed capacity profile, as applicable to the recorded reason. A capacity failure caused by accepted/peer state also records that owner and its bounded contributor counts/revision-sum stamp. A capacity repair at admission may leave the owner's desired/editorial/publication stamp unchanged; the membership footprint detects that repair without loading oversized contributions. The service rejects an unchanged retry without admitting another Work run; recovery scans exclude it until that requirement is met. Preserve this distinction through bounded source/version/limit coordinates in the failure receipt rather than loading the rejected payload to rediscover the same failure. Any already-admitted contribution cleanup still follows its saved recovery obligation; a terminal failure must not hide a live watcher.

A transient I/O/database error or cancellation leaves immutable preparation/admission resumable. Resume the same attempt with the same request; do not reacquire provider data or refresh its clocks. A rolled-back publication leaves accepted rows, correspondence, attempt receipt and Work watermark unchanged. Recovery uses the saved UUIDs. Preserve the last complete accepted result after stale, unresolved or review outcomes.

A stale result needs a new attempt. Retry selects current extraction/repertoire receipts through the stable registration, then saves new preparation. Decisions from a partial accepted transaction remain available to the consumer's own identity recovery, while external consumers still use the last complete result. If acknowledgment or watcher retirement remains unfinished, recovery must expose it; a terminal result cannot hide a live obsolete contribution.

Negative acknowledgments retain real source, mapping, accepted-owner and applicable policy observations. Changes to those dependencies must re-pend the registration. Missing required historical clocks produce `Stale` without acknowledging fabricated state. Recovery pages include unfinished and eligible leaf-stale attempts, excluding retried parents and completed history. Pending scans expose settled negative registrations whose dependencies changed. These are bounded discovery APIs, not a scheduler.

Compare semantic content and durable catalog scope before publishing another accepted image. Unchanged reruns must settle without changing a shared artist owner, alternating source receipts or needlessly invalidating downstream consumers. New actual source evidence or an editorial correction can legitimately advance the relevant owner. Preserve contributor membership and original peer/baseline clocks throughout admission and any watcher handoff.

## Models, schema and implementation sequence

Use separate cohesive setlist and recording classes. Share the completed repertoire reader and existing Work/read-set types; do not add a generic workflow engine.

Add typed request, immutable retry-selection header/checkpoint, registration, preparation, decision, result and compact-page records in `SetlistMaterialization.cs` and `RecordingContentMaterialization.cs`. The setlist preparation embeds the existing `SetlistStructureChange`; bounded retirement requirements and immutable cleanup history release only this registration's obsolete observation or canonical-owner subscriptions. The recording preparation adds `RecordingContentImage` with complete managed content rows, exact source correspondence, unresolved claims and explicit withdrawal references. Keep new fields optional/omitted in existing serialized publications unless the new route needs them; old trusted writer hashes/replays must remain compatible.

Use an additive migration for domain registrations/attempts, immutable preparations/decisions/receipts, source-to-content correspondence and recording authority heads/consumption. Existing setlist structures, correspondence and authorization tables remain authoritative. Extend their authority references and XOR constraints for processor decisions as described above. When adding automatic occurrence alignment, add the performance-scoped edition-membership epoch and its declared write/read scopes in the same increment; do not claim the existing repertoire epochs cover it. Index stable source lineage, canonical edition target uniqueness, latest claim correspondence, unfinished attempts and eligible retries. Retain historical rows and prohibit mutation of original proof; add no deletion cascade to legacy catalog tables.

Operational bounds reuse the source reader's 100 inputs and 128 MiB retained-input budget, setlist writer's 16 MiB image/100 sources/10,000 sets and occurrences, and 1–200 result pages. Recording images permit 10,000 contents, 20,000 claim/correspondence records and 16 MiB; complete preparations permit 128 MiB. The correspondence-read bound matches the write profile: two agreeing sources may support one content, so correspondence count is not bounded by content count. A source-only 5,001-file/two-source fixture checks 10,002 mappings through initial publication and unchanged retry. The completed-account reader separately bounds accepted projection expansion to 256 MiB and 100,000 aggregate redirect-path entries. It decodes each canonical payload once, then charges its encoded size for every requested-root projection before building the returned graph. Bound complete preparation, history and projection bytes before materialization, including after lock waits. Overflow rejects the complete attempt; it never truncates an accepted image.

Implement and verify in this order:

1. **Completed-account reader.** Partial, historical, wrong-source and newer-unpublished receipts fail without writes. A current accepted association remains usable after an unrelated candidate epoch change. Original producer head/editorial changes still stale it.
2. **Setlist first image and durable resume.** Published extraction → repertoire → accepted edition/sets/occurrences works through public services. Empty/repeated sets, unknown order, annotations and unassigned performance remain observable. Cancellation/rollback/replay preserve UUIDs and selection.
3. **Recording complete image.** Published title groups → accepted contents support existing normalized song/show reads. Cover medleys, repeated mentions, missing titles, conflicting input groups and decisions beyond default read pages. Playback IDs and legacy response fingerprints remain unchanged.
4. **Routine updates and withdrawals.** Prove exact/provider correspondence and fixture-evaluated revised unkeyed reuse and native-set removal, refused ambiguous splits/recording-medley removal, active block/revocation and fresh reviewed reactivation. Reviewed metadata is preserved. No title/ordinal-only replacement or omission deletion succeeds.
5. **Enrollment and convergence.** Unbound setlist becomes bound, competing scopes converge, obsolete watchers retire, inactive policy later activates, and two unchanged subscribers settle. Exercise races in supported original producer families and every additional accepted-state family actually consumed, peer membership, source/artist/track/show moves, disappeared/orphaned/empty-URL files and same-day early/late performances.
6. **Operator integration and rollout.** Add explicit bounded commands after the public services pass. Replay frozen per-item source/attempt coordinates; do not refresh on replay. Apply additive schema before enabling writers. Before rolling back to a binary unaware of complete content/correspondence authority, pause those writers and retain all images, ledgers and receipts. Public read rollback must not delete accepted history.

The existing public APIs remain the observation point: setlists/sets/occurrences/annotations, track contents, and song/composition show lookup. Setlist-only performances remain browsable without invented audio. A song's playable result still requires the claimed file's own valid current playback joins. Unresolved alignment and materializer failures never withhold otherwise playable recordings.
