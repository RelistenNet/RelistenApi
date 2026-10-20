# Artist repertoire resolution

Status: implemented locally with migration 51. All 44 core contracts and 22 worker-command cases pass against PostgreSQL and actual worker processes; the solution builds. The full local API suite passes 2,696 tests with no failures and 24 existing tour-contract skips. No production matching policy is activated. This contract covers every artist, including artists with no imported dictionary. It does not change legacy song, show, recording or playback UUIDs.

The resolver turns exact dictionary members, recording-title mentions and setlist passages into artist-scoped repertoire decisions. It can create visible `title_derived` provisional entries without claiming that they identify global compositions. The [setlist and file-content consumers](repertoire-consumers.md) define how subsequent processors consume its completed decisions.

## Existing contracts to reuse

- `NormalizationImportedCatalogService.RefreshArtistRepertoireAsync` captures a complete stored dictionary, including its original artist clock. `NormalizationCatalogBindingService.BindRefreshedRepertoireAsync` verifies that exact projection. A known empty dictionary is valid; missing wording remains unknown.
- `NormalizationRecordingProposalService.GetAttemptBatchAsync` and `NormalizationSetlistProposalService.GetAttemptBatchAsync` expose immutable extraction batches for audit, including unfinished/stale attempts. The resolver must separately verify compatible processing version, terminal published result and Work publication receipt before using one as an authoritative source. Saved preparation and Work tickets retain the original associations and dependencies.
- `NormalizationCandidateService.FindRepertoireFactsAsync` returns bounded artist-scoped alternatives, accepted aliases/IDs, redirects, work links and owner observations. The context reader reuses its internal selection/fact readers inside the source context's primary repeatable-read transaction; separate public reads cannot establish one atomic context.
- `NormalizedMusicService` owns repertoire identities, imported mappings and song external IDs under `(songs, artist_repertoire, artistUUID)`. `SaveRepertoire` accepts `provider_assertion`, `reviewed` and `title_derived`. Names and aliases are nonunique. Existing revision CAS, reviewed protection, redirects and semantic no-op behavior remain authoritative.
- Work already supports original input revisions, causal dependencies, catalog clocks, source-identifier clocks, mapping registry, imported-venue clocks, candidate universes, owner observations and source-UUID clocks. It also supports exact contributor membership and durable pending watermarks. Reuse those families without a new shared protocol.

See [normalization contracts](../../../design/2026-10-17-normalization-contracts.md), [candidate facts](candidate-facts.md), [owner observations](owner-observations.md) and [setlist structure](setlist-structure.md).

## Public processor contract

`NormalizationRepertoireResolutionProcessor` exposes these entrypoints. Each resolve/retry call advances a durable phase; callers resume the same attempt while its result is pending.

```csharp
Task<RepertoireResolutionAttempt> ResolveAsync(
    Guid attemptUuid,
    RepertoireResolutionRequest request,
    CancellationToken ct = default);

Task<RepertoireResolutionAttempt> RetryAsync(
    Guid newAttemptUuid,
    Guid registrationUuid,
    CancellationToken ct = default);

Task<RepertoireResolutionAttempt?> GetAsync(
    Guid attemptUuid, CancellationToken ct = default);

Task<RepertoireResolutionRegistration?> GetRegistrationAsync(
    Guid registrationUuid, CancellationToken ct = default);

Task<RepertoireResolutionRecoveryPage> GetRecoveryAttemptsAsync(
    int limit = 100, Guid? afterUuid = null,
    CancellationToken ct = default);
```

`RepertoireResolutionRequest` contains a typed source reference and optional `ExpectedArtistUuid` constraint. The constraint rejects a conflicting association; it never supplies a missing association. The source is one of four discriminated records:

| Source | Exact request fields | Claims selected by the processor |
| --- | --- | --- |
| Imported dictionary | `FetchUuid` of the existing repertoire capture | Every stored song observation in that immutable complete dictionary |
| Recording titles | `PublishedProcessingAttemptUuid` | Every title segment from every saved input group and positively bound file in that published batch |
| Setlist passages | `PublishedProcessingAttemptUuid` | Every account observation and passage in that published batch, including duplicate accounts |
| Provider dictionary | `Input` reference and qualified `AccountIdentity` | Every decoded repertoire observation belonging to that supported account in the retained catalog/page |

No request accepts titles, aliases, target UUIDs, match reasons, policy versions, facts or clocks supplied by the caller. Source-reference validation runs before registration/admission. Unknown immutable references fail; supported but unresolved associations produce a durable unresolved decision.

The provider-dictionary form uses a checked artist-association reader. Today the generic recording binder and the checked legacy dictionary binder do not establish that association for arbitrary decoded repertoire. Support each provider/resource profile explicitly, with the mapping-registry/catalog stamps read alongside the association. An unsupported profile remains explicit `source_profile_unsupported`; do not guess an artist from a title, performer name or integer song ID. The decoded Phish.in songs catalog is the supported provider-dictionary profile. Invalid members remain separate unresolved claims; they do not erase valid neighbors. Duplicate qualified IDs block only the affected claims.

An attempt freezes the exact request and processor version before preparing facts. Reusing its UUID with another request conflicts. Exact replay returns its saved terminal result even after a version change; unfinished incompatible attempts need an explicit unsupported result, without reinterpretation through new decoder defaults.

## Registrations, claims and stable correspondence

A registration identifies a source lineage and expected artist scope; an attempt identifies one exact observation of it. The lineage excludes publication UUIDs, revision UUIDs and mutable names:

- Imported dictionary: qualified `legacy_catalog/artist_repertoire/<artistUUID>` resource.
- Recording titles: checked catalog source UUID and the recording-processing request lineage.
- Setlist passages: setlist-processing registration UUID, which already identifies the qualified account and expected artist scope.
- Provider dictionary: retained qualified account identity and its supported artist-scope binding contract.

Store registration UUIDs once, protected by a unique normalized lineage key. A newly published proposal attempt must discover its predecessor registration through this authenticated lineage. Never establish lineage merely because titles or dates agree. Different expected-artist scopes remain distinct registrations; their decisions cannot impersonate each other.

Each exact claim retains source revision/publication coordinates, input-group coordinates, original observation origin, extraction version, original literal wording and citations. Recording claims include track UUID, proposal UUID and segment coordinates. Setlist claims include original account-observation and passage coordinates. Provider dictionary claims retain qualified song IDs and explicit alias assertions. The complete original source account remains accessible through its immutable receipt.

Persist claim-to-repertoire decisions separately from the repertoire row: decision UUID, registration/attempt, exact claim coordinate, optional repertoire UUID, disposition, reason, authority, evidence and any superseded association. That association is the replay/correspondence record for consumers. It is not an accepted occurrence identity. Duplicate observations remain separate evidence even when an authenticated decision relates them to one repertoire identity.

Allocate new repertoire UUIDs in a saved stage before publishing. Never recompute an accepted UUID from title, segment number or provider display order. Unchanged exact claims reuse their saved association. A qualified provider song ID can justify reuse across source revisions within its verified namespace and artist association. A revised unkeyed claim needs retained or evaluated correspondence; matching text/order alone cannot pretend that correspondence has already been established. Keep the prior association available while interpretation is unresolved. Reviewed reallocation uses existing correction authority and preserves history.

## Resolution order and provisional entries

Resolve each claim against the complete bounded alternatives from its artist, in this order:

1. Validate an existing source association and resolve its accepted redirects. A reviewed association/exclusion remains authoritative.
2. Check explicit qualified provider song identity and explicit alias relationships under the supported provider authority contract.
3. Apply an active independently evaluated repertoire rule to the whole candidate pool, including competing alternatives and contradictions.
4. For a clear title mention without a supported identity correspondence, allocate a provisional entry with `basis=title_derived`. Record that this is a provisional allocation, rather than a validated match to an existing candidate.
5. Preserve missing wording, noise, unresolved compound interpretation or incomplete artist association as typed unresolved decisions.

The provisional allocation in step 4 is the approved default and does not require a newly learned matching policy. It must remain public through the existing repertoire reads and basis filters. Dictionary membership can support a provider-asserted repertoire entry while its external provider identity remains unknown. A legacy `upstream_identifier` or slug is retained catalog text until a provider-specific authority contract establishes its meaning.

Equal titles can identify candidate alternatives, including several songs within one artist. They cannot by themselves merge those existing identities. The same title in two artists always has separate repertoire scope. A title-derived entry retains its UUID if later renamed or linked to a composition; upgrading its supported assertions is a revision, not replacement of its identity. Repeated independent claims require actual correspondence before consolidation; do not make a unique `(artist, normalized_title)` constraint that silently resolves collisions.

A qualified ID match is authoritative only for the named resource type and provider namespace, with a checked artist association. If the same qualified ID supports several accepted targets, or its source contradicts the artist association, preserve the conflict and publish no automatic reassignment. A title discrepancy is retained as evidence to interpret; it does not automatically mean either an alias or a different work. Distinct IDs in one provider also need the provider's exclusivity contract before being treated as contradictions.

`ProviderFileObservation.SongIdentities` must be read from the exact saved checked file binding. `NormalizationRecordingTitleProposal` does not copy those IDs, and the ID array does not establish passage order or pair each ID with a title segment. A multi-song file therefore cannot zip IDs to extracted segments by position. `OccurrenceIdentity` remains distinct from every song identity.

Alias, medley and reprise facts retain exact evidence. Explicit provider `alias_for` can support a directed alias relation after both endpoints are checked. Punctuation, a parenthetical phrase or removal of “reprise” does not supply equivalent authority. `TrackTitleExtractor` currently retains reprise wording, partial markers and exact Unicode spans; its segment order is title order. A resolver may accept provisional song mentions while leaving compound identity, occurrence correspondence and audio coverage unresolved.

## Matching policy and authority

The current pair matcher can compare qualified song IDs and title-plus-composition evidence. Its pair result does not decide among competing candidates or authenticate publication. The repertoire-specific registry is implemented separately from the pair matcher. It supports separately enabled `QualifiedEntityIdentifier` and `ArtistScopedNormalizedTitle` capabilities. Title-plus-composition matching is refused because these source claims do not yet supply an authenticated composition identity; adding that evidence contract remains required before enabling that rule.

`RepertoireResolutionPolicyService` follows the existing evaluated-artifact/attestation and Work-owned activation pattern. Its stable owner is `(songs, repertoire_acceptance_policy, f75341ca-6c15-48cb-b547-d96056b5726c)`. Typed registration, activation and revocation require an authenticated operator host. The service bootstraps the absence observation before the first preparation. The registry returns the active artifact plus its complete original owner observation; no request can supply that authority. Compare saved serialized artifact content across database reads rather than relying on record equality for nested collections.

The pure evaluator receives the frozen source claims, complete candidate facts, accepted source correspondence and reviewed exclusions. Its saved decision names the exact candidates considered, supported rule, provider-authority contract/version where relevant, contradictions and evaluated artifact/attestation hashes for inferred matches. The fixed default that allocates a new visibly provisional identity is fingerprinted in the processor version, separate from semantic match approval. Existing exact source correspondence may be retained without inventing a new inferred match.

Independent evaluation and trusted activation are required before enabling inferred alias/fuzzy/semantic identity rules. Registering a fixture artifact cannot authorize a real database. The implementation must prove the authenticated positive route with fixture-only policies and leave production artifacts inactive until evaluation. This is required work toward automatic matching; it does not change the objective to permanent unresolved proposals.

## Automatic artist-scoped title reuse

The runtime title rule uses `ArtistSongTitleResolver`, the same policy as the full-catalog batch. It joins supported normalized names within one artist and chooses one representative for the entire connected alias group. Case, punctuation and canonical Unicode differences do not create another song. Accents, word boundaries and named variants remain meaningful. Different known composition sets block reuse; missing work evidence does not establish a contradiction.

An active evaluated artifact must explicitly enable `ArtistScopedNormalizedTitle`. Enabling only qualified provider IDs does not enable title reuse. This implementation adds no real evaluation approval or production activation. Until that gate is met, the existing provisional and provider-dictionary allocation behavior remains available.

Each page reads a compact, complete artist title graph in the same primary repeatable-read transaction as candidate facts. The repertoire candidate-universe observation fences changes to the graph. Inherited names follow existing redirects. The graph uses the existing 50,000-song account bound and a 16 MiB text budget; exceeding either bound or finding an invalid redirect makes automatic context incomplete. It cannot silently omit a rival. Only the relevant complete components enter the bounded detailed fact reader.

Two new dictionary members with a shared title allocate one new repertoire UUID in their saved page. One representative claim owns creation; the other claims retain separate evidence and assignments to that saved identity. Publication replays the same page policy, verifies the creation identity is new, and rejects an allocation reused by unrelated groups. Later imports reuse the accepted song through the same rule. Title reuse adds source correspondence without granting incoming metadata permission to rename reviewed data.

Every claim grouped into a new song contributes its supported aliases and citations. Initial title-based reuse also adds supported names to an existing unreviewed song without replacing its display title or basis. A later independent import can use those aliases to find the same song. Once that provider correspondence exists, ordinary dictionary refresh can update unreviewed metadata under its existing authority. Repeated imports retain reviewed titles and aliases when source spelling differs only under the evaluated title rule. A source that changes its reviewed retained correspondence to an unsupported title still requires review.

Reviewed target metadata stays exact: automatic title reuse does not append source aliases to it. A saved reusable expert alias can make a new source spelling resolve to that target without editing its reviewed metadata. Source-only aliases that have no such reviewed rule still need a separate projection before independent later imports can use them.

Before acknowledging the complete source, the processor rechecks negative decisions against current facts for their original full claim page. Source aliases can connect rivals through another claim on the page; checking a negative claim alone would lose that conflict. Saved decisions remain the publication proof. This final check creates no accepted identity or relationship.

This change does not merge already accepted duplicate repertoire identities. An incoming claim that encounters several existing identities in one title group goes to review with `existing_same_title_identities_require_merge`, or `qualified_work_conflict` when their work evidence differs. Existing IDs and accepted relationships stay intact. The separate consolidation step must move relationships explicitly and preserve old lookup routes through redirects. Automatic importer enrollment also remains a separate integration step.

The processor and matcher versions change with this behavior. Completed old attempts still replay their original result. An unfinished old attempt follows the existing incompatible-attempt cleanup, preserving accepted writes and retiring admitted contributions; an explicit retry creates an attempt under the current version. No in-place reinterpretation of saved old decisions is allowed.

## Durable expert song knowledge

Repertoire resolution uses `ExpertKnowledgeResolver`, shared with the reader and batch. Explicit accepted correspondence takes priority over automatic title matching. Conflicting positive rules require review; a rejection excludes its named accepted candidate while leaving other evaluated matches usable. An unmapped or ambiguous reviewed target requires review. A rule never creates a canonical song merely because its frozen key resembles a UUID.

Runtime observations use namespace `repertoire`, the source claim's exact `CorrespondenceKey`, its exact title as `Literal`, and its admitted artist and provider. A reusable artist-scoped literal rule therefore applies to later independent source claims. An account correction remains exact to its source correspondence key. Frozen-reader account keys retain their original capture scope and do not become runtime account keys automatically. This adapter supplies no locality or date context; rules that require those fields do not apply until the source can supply authenticated values.

`ExpertKnowledgeTarget` retains its frozen namespace and stable key. Its optional `ImportedSongUuid` carries an authenticated original imported-song reference separately from that key. Only song targets can have this attachment, and a target can carry either `CanonicalUuid` or `ImportedSongUuid`. The host derives an imported attachment from the immutable reader target and includes it in the prepared evidence; it must not trust a client-supplied UUID. Null imported attachments are omitted from JSON so existing snapshots keep their content hashes.

The ledger owns reviewed references, revisions and attribution. It can retain an imported reference without owning catalog rows. The runtime catalog establishes accepted identity: an explicit canonical UUID must resolve within the source artist, while an imported attachment must exist under that artist and have a current accepted `imported_song_assignments` correspondence. Existing redirects are followed under the ordinary candidate facts and owner checks. No accepted mapping means review, even if the frozen reference was approved. Typed `imported_song` targets also accept a legacy UUID as their stable key. Typed `provider_song` targets use a JSON array `[provider, resource_type, provider_key]` and require an accepted `music_external_identities` correspondence. Other frozen keys stay opaque.

Each page reads its relevant song-rule snapshot in the same primary repeatable-read transaction as candidate facts. It retains that snapshot once, plus each decision's applied rule UUIDs and revisions. Rule owners join the existing read set. Save, revision and withdrawal also advance the artist's repertoire universe, so an old prepared page cannot replace its observations with current clocks and publish an obsolete answer. A public retry reads current knowledge. Completed attempts replay their original result and preserve their original rule evidence.

Reviewed correspondence changes assignments without renaming reviewed repertoire metadata. Replacing an automatic external correspondence retracts its old assertion and creates a replacement; external assertion ownership remains immutable. Imported and canonical song UUIDs stay unchanged. A withdrawn rule stops authorizing new matches and invalidates an uncommitted prepared page. A completed expert-derived assignment currently survives through retained source correspondence. Re-evaluating those materialized effects after withdrawal remains required work: recover the exact prior expert authority, preserve independently supported relationships and playback, and verify the refreshed canonical v3 result. Existing source history and canonical songs must remain available.

The runtime contract requires the expert registry and catalog to share the primary database so these checks remain atomic. The private review rehearsal currently uses the separate schema-only `relisten_normalization_knowledge` database. Its reader updates do not prove canonical v3 integration. A local rehearsal that preserves the existing ledger and demonstrates save → runtime reprocessing → v3 results is still required before that integration is complete. The resolver adds no implicit cross-database connection or polling fallback.

## Preparation and freshness

First authenticate and checkpoint the complete bounded source, its original read set and a stable claim-page plan. Prepare each bounded decision page in one primary read-only repeatable-read transaction: verify that same original source, checked artist association, claim associations, accepted target/redirect revisions, candidate selection/facts, review exclusions, pinned active-policy-or-absence observation and current contributor memberships. Freeze the page context before admitting its domain stage. Later pages may read current accepted facts after this attempt's own writes; they cannot replace its original source or policy observations. This avoids loading every candidate fact for a 50,000-song dictionary into one preparation.

For published recording/setlist sources, carry every nonempty original ticket family and the exact source publication dependency. An unsupported richer receipt must fail explicitly rather than discard fields. Imported dictionaries recover original producer clocks from the exact stored capture/envelope; callers cannot attach fresh clocks to an old dictionary. Raw provider dictionary associations consume the exact retained input plus the original mapping/catalog observations established during preparation.

Current accepted candidates have different authority from newly extracted source claims. Their current owner/revision establishes accepted-fact authority; immutable old citations remain evidence provenance. Do not require every candidate's historical raw citation to remain the latest resource head. Consume original heads for the new source claim, current candidate universes/owner stamps for accepted matching facts, and all catalog/binding clocks actually used for association.

The repertoire and global-composition epochs already cover repertoire facts with work links. Capture both when that projection is read, including empty candidate results. Owner observations must include original and terminal identities, linked-work owners and reviewed-exclusion owners. Observations do not create causal graph edges; preserve genuine causal source-publication dependencies separately. Missing owners/clocks are explicit incomplete context, never fabricated generation zero for an invariant that requires a registered owner.

Candidate limits mean incomplete selection, not an empty pool. Record a negative decision with the original available freshness observations; do not create or match merely because retrieval omitted alternatives. Any required read-set conflict makes the attempt stale. Never retry admission with substituted current clocks attached to the old preparation.

## Ownership and enrollment at artist scale

Accepted repertoire edits remain under `(songs, artist_repertoire, artistUUID)`. They cannot write global compositions, editions, occurrences or file contents. Imported mappings preserve `ImportedSongUuid`; source-track and show UUIDs are untouched.

Use a separate `(songs, repertoire_observation, registrationUUID)` Work owner for each source registration's decision subscription. This owner can acknowledge the source, policy and candidate context but grants no accepted music authority. It can exist while artist binding is unresolved. A registration groups the complete source account's claims; do not create a contributor for every title segment.

This separation is required by current Work bounds: contributor snapshots allow at most 1,000 contributors and 100,000 bindings. A large artist can have more recordings than that. Do not enroll every recording permanently as an artist-repertoire contributor, truncate its peers, or raise the bound until it becomes an unbounded artist-wide read set.

The artist owner uses one stable repertoire-materializer contributor for the stage currently being written. Replace only that contributor's prior transient read keys. Preserve all other contributors, their current exact stamps, and any explicit unmanaged baseline using the existing complete-membership CAS contract. Independent per-source subscriptions retain older source freshness after those transient artist keys are replaced. Keeping an old accepted row does not require copying its whole historical input set into every later write.

There are three durable phases:

1. Publish a saved bounded accepted-write stage under the artist owner. Domain changes, claim associations and the stage receipt commit in the same transaction.
2. Retire the artist materializer's transient contribution through a saved empty Work stage, preserving every peer and its original observations. Do this through normal contributed admission/publication, never by clearing Work bindings behind a ticket. If another attempt has replaced the contribution, its revision/ownership CAS prevents this attempt from clearing the replacement; record that supersession rather than removing another stage's keys.
3. Acknowledge the complete source decision under its observation owner. Record that source's current attempt and every resulting association/negative reason atomically with its Work watermark.

The phases are deliberately resumable. Never acknowledge the source before its required accepted stages have durable receipts and retirement has a durable outcome (cleared or superseded). Retiring the last transient keys prevents later raw changes from leaving the artist owner permanently pending. After an own accepted write and retirement, reread the resulting candidate pool and accepted-owner stamps before the observation acknowledgment. Preserve the original source clocks.

Successful artist stages record immutable `OwnEffects` with their receipt: accepted writes capture the two candidate epochs while holding their write locks; retirement captures only the artist owner watermark. Finalization replays those effects in stage order against the original decision-page observations. It preserves every foreign stamp, including negative decisions, and rejects an unexplained epoch/editorial change. This allows the new entry to advance its own subscription without attaching fresh clocks to an older ambiguity. A concurrent rival or editorial change still requires reevaluation or a stale result; do not attribute every intervening change to this attempt. The reread must verify expected accepted effects, target ownership, exclusions and all alternatives. A receipt proves only this attempt's changes.

Completion also reevaluates every negative decision against the final candidate facts in that same snapshot. A later own write can resolve an earlier missing alias target; if its disposition, reason, target or authority changes, the attempt becomes stale and its retry reevaluates the complete source. Own-write epochs alone never justify acknowledging an obsolete negative.

Final acknowledgment holds the attempt header lock and reuses one authenticated source preparation and stage summary within its publication transaction. For each decision page, load that exact page and its paired accepted-write stage through the bounded proof reader, then enforce the existing decision and committed-receipt checks. The retained preparation and stage evidence are immutable. This lets large accounts validate every page without loading the entire source again for each page.

When all required accepted assertions already agree, skip artist admission, publication and retirement entirely. A new source association can be committed under its observation owner after validating the accepted target revisions; it grants no authority to change those targets. Acknowledgment then touches only that source owner. Full artist owner observations can cause a finite recheck after another real artist stage, but semantic no-op rechecks must not swap the shared materializer configuration or publish a new artist receipt. Otherwise two unchanged sources could keep each other pending forever. Do not create artist-to-observation causal dependencies.

If an admitted artist stage fails, its actual ticket and `FailureReason` remain saved; it receives no synthetic music publication receipt. Cleanup retires the installed contribution before the attempt becomes Stale. A same-byte resource recapture can fail original-generation comparison after Work admission, so `FailAdmissionAsync` retains that exact committed ticket for cleanup. A failed cleanup admission can itself be retried using its proven revision chain and current peer context. An incompatible processor version can finish cleanup but cannot reinterpret or publish accepted music.

If a process stops between phases, replay the saved accepted stage, finish its retirement, then finish the source acknowledgment. Do not mint new IDs or repeat metadata writes. For sources with several bounded stages, expose partial durable progress; an incomplete attempt has no complete-source acknowledgment. Consumers continue using the last completed registration result rather than treating a partial decision page as a complete account. No transaction remains open while preparing another page.

## Accepted writer integration

`NormalizedMusicService.PublishRepertoireResolutionAsync(workUuid, resolutionAttemptUuid, stageUuid, ct)` publishes accepted and retirement stages. It accepts identifiers only. Under Work's existing locks it must authenticate the saved attempt/stage, processor version, original source reference, exact ticket/read set, artist owner, expected target revisions, policy state and evaluated decision when matching requires one.

Accepted-write stages contain a frozen `MusicPublication` restricted to repertoire rows, imported-song assignments and repertoire external IDs. Retirement stages carry an empty publication and authenticate only their saved contribution-release instruction. The source acknowledgment uses `Work.TryPublishAsync` with the resolver store's typed callback; it cannot call generic music publication under the observation owner, which is outside accepted music ownership. That callback validates saved decisions/receipts and updates associations/current result atomically without editing accepted music. Reuse `ValidateOwnership`, `ValidateDependencies`, evidence validation and existing `Apply`/`Persist` behavior. The resolver cannot emit composition links; those belong to the existing composition resolver. Candidate write scopes are declared before entering Work using the immutable artist owner: repertoire for that artist and compositions as required by the existing writer policy.

New evidence must cite the consumed source inputs. Carrying forward an accepted assertion's historical evidence requires authenticating its exact current revision in the saved context; it must not be disguised as a freshly consumed raw head. Do not rewrite existing repertoire metadata just to record that another claim resolved to it: store the new source association and its evidence. A genuine alias/metadata update must retain supported prior assertions and pass the existing reviewed-authority guard.

Do not turn the generic trusted `PublishAsync` into an evaluated-resolver authority flag. Preserve its existing callers, serialization and replay. The dedicated route authenticates processor-owned durable decisions; it never trusts caller-supplied matching payloads. Rejection or failure rolls back accepted rows, associations and receipt together. Existing semantic no-op rules keep identical accepted facts from advancing candidate epochs.

## Outcomes, retry and history

Each claim has one of `Bound`, `Provisional`, `Review` or `Unresolved`, with a reason code, optional accepted repertoire UUID and exact source evidence. An attempt has `Pending`, `Completed` or `Stale`; `Completed` means every claim has a durable decision and the source watermark was acknowledged. It does not mean every claim resolved. Return counts plus bounded paginated decisions rather than embedding an unbounded dictionary in the response. Public `GetAsync` and recovery summaries omit the source claim array and processed candidate contexts in SQL before materialization. `ClaimsOmitted`/`TotalClaims` and `ContextOmitted` identify those projections. `GetStageAsync(attemptUuid, stageUuid, ct)` explicitly reads one full bounded immutable stage. The store also exposes `GetDecisionsAsync(attemptUuid, limit=100, afterClaimUuid=null, ct)` and `GetPendingRegistrationsAsync(limit=100, afterRegistrationUuid=null, ct)`; the latter joins registered observation owners to Work pending state. Both return at most 200 entries per page.

Same attempt UUID resumes saved preparation/stages. `RetryAsync` creates a new attempt linked to the prior registration/current attempt. It selects current source heads/publications through that lineage, then prepares new facts. For an imported dictionary, a changed catalog projection requires a new valid capture; unchanged retained bytes and fences can reuse the existing capture without incrementing its resource head. Never repeatedly refresh unchanged dictionaries merely to make peer registrations pending.

Inactive policy, ambiguous alternatives, missing artist association and unsupported source interpretation must receive typed no-op acknowledgment when their real freshness observations are available. That enrollment must re-pend on source, candidate, owner-editorial or policy-registry changes, including activation after an observed absent policy. Provisional default allocation remains available independently of inactive semantic matching rules. Preserve reviewed decisions and peer contributions during every acknowledgment.

A registration becoming bound supersedes its old unresolved subscription through the same stable lineage, including when the proposal publication UUID changed. Retire obsolete owned keys rather than keeping a second live watcher. This changes enrollment only; it does not infer deletion of any old accepted song or assignment. Missing required historical owner/catalog clocks produce `Stale` without watermark acknowledgment; expose the repair prerequisite and retain recovery eligibility.

Keep immutable attempts, decisions, original preparation and stage receipts. A new attempt can supersede a decision but cannot rewrite its proof. Missing source members or conflicting partial inputs preserve the last accepted association. Automatic identity merges, source reassignment and withdrawal need explicit existing review or an authenticated evaluated authorization; a dictionary deletion alone cannot erase a song still supported by recordings.

Recovery pages contain unfinished attempts and eligible leaf-stale attempts. Exclude retried parents and completed decision history. Completed negative registrations are discovered through Work pending scans. Apply row/byte bounds before materializing saved payloads, including after waiting for a checkpoint writer. Cursor scans are bounded discovery, not a scheduler or exactly-once delivery claim.

## Subsequent consumers and performance ownership

An accepted-setlist consumer uses the exact published account plus these repertoire decisions to construct `SetlistStructureChange`. It retains empty/repeated sets, unknown ordering, annotations and unresolved passages. It uses existing exact/provider correspondence first; evaluated correspondence/removal requires the separately authenticated authority described in [setlist correspondence review](setlist-correspondence.md). Proposal passage UUIDs never become accepted occurrence UUIDs by assumption. Selection remains its separate owner and command.

A recording-content consumer uses exact bound-file claims and decisions to create `TrackContentChange`, with null occurrence/audio offsets when unsupported. Many files may cover one occurrence, and one file may cover several songs or sets. Every accepted occurrence alignment must agree with the source's current single performance. No early/late cross-performance file homes are introduced. Source moves invalidate current alignment projections while preserving playback and retained evidence.

The existing composition resolver consumes accepted repertoire after these decisions. Neither a title-derived allocation nor a cover performer proves a global composition, canonical writer person or complete authorship.

## Components and verification

Keep the implementation to these cohesive components:

- `RepertoireResolution` models: typed source requests, registrations, exact claims, contexts, decisions, stages, results and bounded pages.
- `NormalizationCandidateService.RepertoireResolutionContext.cs`: atomic source association, candidates, review/exclusion and contributor context.
- `NormalizationRepertoireResolutionStore` plus migration: registrations/attempts, immutable decisions/stages and source correspondence; bounded recovery reads. The same migration adds the typed evaluated-policy registry/active pointer.
- `RepertoireResolutionPolicyService` and a pure repertoire evaluator: evaluated registration/activation/revocation, observed absence and whole-pool decisions; no production artifact is supplied by this increment.
- `NormalizationRepertoireResolutionProcessor`: authenticate source, prepare, decide, publish accepted stages, acknowledge subscription, resume/retry. Split partials by those responsibilities when necessary.
- `NormalizedMusicService.RepertoireResolution.cs`: authenticated stage publication using the existing writer transaction. A checked provider-dictionary artist-binding partial is needed; existing provider decoding is reused.

Use paged claims/stages for a complete dictionary of up to the producer's 50,000-song/16 MiB limit. Preflight source inputs through the existing bounded Evidence reader. A stage contains at most 100 claims and 32 MiB of serialized facts. The complete source preparation has a separate 128 MiB bound to retain up to 50,000 typed claims from a 16 MiB dictionary; an individual oversized claim yields an explicit incomplete-context decision without materializing an unbounded payload. Candidate facts retain their existing independent selection/closure/evidence limits. Persist exact input order and the bounded claim-page plan before side effects. Bound public decision pages to 32 MiB and projected recovery/processing summaries to 256 MiB before materializing bodies, including after row-lock waits. Expected-attempt CAS uses the monotone checkpoint revision, so retained full stage history does not accumulate into an accidental 32 MiB attempt limit. Overflow rejects a page without claiming complete-source success. Limits cannot silently omit claims or turn missing candidates into unique matches.

Persist registration identity/current-completed-attempt pointer, immutable attempts/preparation, typed stages/receipts and claim-decision associations in resolver-specific tables. Keep evaluated decision data in the saved attempt/stage rather than adding a second proof store. The claim association's exact source coordinate must be unique within an attempt, and stage publication must enforce one first receipt. Index registrations by lineage and attempts by registration/previous-attempt for pending and recovery reads. Migration 51 creates these tables and the evaluated-policy registry. Store mutation methods are internal to the processor assembly; public source requests cannot manufacture a preparation or winner stage.

Public-contract tests must cover:

| Boundary | Required behavior |
| --- | --- |
| Dictionary and missing wording | Empty dictionary settles; null/empty names remain unknown; complete import preserves every stored song UUID and provider text without invented IDs |
| Stable identity | Exact replay/new unchanged attempts keep UUIDs and epochs; two different unchanged source subscriptions settle without new artist runs; rename/reorder preserves supported correspondence; same title across artists stays separate; same-artist alternatives are retained |
| Qualified IDs | Checked namespace/artist can resolve; duplicate-target and wrong-artist contradictions cannot pick a convenient target; file ID arrays are not zipped to title segments |
| Provisional defaults | Artist without dictionary gets visible `title_derived` entries; no global work inferred; ambiguous noise/compound claims remain explicit |
| Aliases and syntax | Exact alias evidence survives; reprise, partials and medley alternatives are not discarded or consolidated by punctuation alone |
| Freshness | Source/candidate/owner/editorial/policy changes stale old work; new rival after empty retrieval is detected; historical accepted citations do not require refreshed raw heads |
| Peers and scale | Other artist contributors/baseline survive; source subscriptions scale beyond 1,000 recordings without artist-wide membership; retire the final transient contributor; bounds never truncate |
| Negative decisions | Inactive policy settles, activation re-pends; ambiguous/unbound claims remain enrolled; new publication UUID finds/supersedes the same source lineage |
| Recovery and rollback | Stop before/after accepted commit resumes saved UUIDs; no source acknowledgment before all stages; rollback leaves rows/associations/receipts unchanged; recovery pagination excludes terminal negatives/retried parents |
| Downstream compatibility | Repertoire reads show basis/aliases/legacy mappings; file/setlist consumers can retrieve exact claim decisions without assuming occurrence identities or changing playback ownership |

Evaluation must measure same-artist candidate recall and accepted matching precision separately from provisional allocation coverage. Add labeled title-to-repertoire, alias/reprise/medley and same-title-collision examples before enabling semantic rules. Existing challenge drafts and fixture-only policies do not authorize deployment. No new user decision is required for this implementation contract; automatic preferred-edition selection remains a separate policy question.

## Deployment and remaining consumers

Apply migration 51 before deploying the resolver or its operator command. Keep registrations, immutable source preparations, stage receipts, own-effect watermarks and association history during rollback. Pause automatic repertoire writers before reverting to a binary that cannot enforce those records. Existing legacy routes and playback remain usable; rollback must not delete normalized history to make an older writer run.

The direct worker command is documented in [ingestion operations](../../../ingestion/operations.md). It does not acquire provider data, start Temporal, schedule all artists or activate a policy. Durable bulk page orchestration remains separate work. The [file-content and setlist consumers](repertoire-consumers.md) are implemented; their guide records the remaining verification. Production semantic matching still requires independent labels, evaluation and authenticated activation; provisional repertoire creation is already its distinct default behavior.
