# Resolve repertoire claims to global compositions

This contract adds a durable, bounded MusicBrainz composition resolver. It preserves a local repertoire claim, every queried alternative and the evidence supporting a proposed correspondence. Only a saved processor stage authenticated by the music writer can publish an evaluated match. No production rule is activated by this increment.

## Progress

- [x] 2026-10-17: Trace existing music writers, candidate snapshots, Work freshness and evaluated people-publication precedent.
- [x] 2026-10-17: Agree on processor-issued proof, original-source freshness and staged composition creation.
- [x] Implement typed attempts, checkpoints, policy registration and source context.
- [x] Implement bounded search/hydration and authenticated music publication.
- [x] Independently review correctness and readability; address authenticated publication, contradictory recordings, peer ownership, reviewed corrections and source freshness findings.
- [ ] Root runs formatting, build and public PostgreSQL/fake-HTTP contracts.

## Existing boundaries

`NormalizedMusicService.Compositions.cs` accepts qualified work identity or complete canonical-person writer evidence. The qualified-ID branch checks the target owns the ID; it does not authenticate how the local claim acquired it. Existing trusted writers and their serialized requests remain compatible. The resolver cannot pass a search winner through that shortcut.

`NormalizationCandidateService` reads accepted assertions and owner clocks from one primary repeatable-read snapshot. `NormalizationWorkService` checks original inputs, catalog clocks, candidate universes and owner observations before atomic domain writes. Observations do not create causal graph edges. MusicBrainz search and fixed lookup commands already share durable attempts, exact response bytes, a provider gate and automatic bounded recovery.

## Interfaces and phases

`NormalizationCompositionResolutionProcessor.ResolveRepertoireAsync(attemptUuid, claim, ct)` receives a `CatalogWorkClaimReference` containing the repertoire UUID and exact accepted music revision UUID. `GetAsync` reads recovery state. `RetryAsync` uses a new attempt UUID and explicitly reads a new local claim revision. Callers cannot provide candidates, a winner, a policy or an authority flag.

Preparation reads the current repertoire claim, exact cited source title, catalog artist context and global composition alternatives in one primary snapshot. An artist's MusicBrainz ID remains an external artist ID, never a canonical-person UUID or an author claim. This entrypoint consumes an accepted assertion, not an extraction proposal. Its raw citations are historical provenance: their age does not make a legitimately current accepted assertion stale. The current accepted row/revision, owner observation and artist catalog clock establish authority. An extraction-only claim form is not accepted by this API; adding one requires authentication of its repertoire association and its complete original proposal/source readset, rather than attaching a fresh head to old extraction output.

The processor saves deterministic search variants and command UUIDs before acquisition. Each invocation advances at most one existing MusicBrainz acquisition command. Pending returns its next eligible time without sleeping. The plan retains at most three variants and four pages per variant. It records omitted variants, malformed pages, moving counts, duplicate hits and exhausted bounds. `Complete` means no more pages remain in this bounded plan, including when failures or bounds stopped acquisition. `ObservedEnd` separately means every query window reached its reported count without count drift. `ExternalUniverseComplete` is always false; neither field establishes global absence.

Hydration uses the fixed work, recording and artist lookup profiles through `NormalizationMusicBrainzInputService`. At most twenty distinct lookup targets are admitted per attempt. Every originating hit remains in the checkpoint; deduplicating a GET does not delete duplicate source claims. Skipped or unavailable hydration remains explicit. An external recording-to-work relationship never alone establishes a catalog recording or repertoire link.

Evaluation retains every alternative, role, direction, unknown and contradiction. A registered artifact and independently approved evaluation receipt describe the exact supported processor, decoder, facts and query versions. No production rule or threshold is supplied here. A fixture-only corroboration rule demonstrates the publication protocol in disposable test databases: source title, catalog performer identity, hydrated recording performer and explicit forward recording-to-work relationship must agree. Its existence makes no claim about real-world accuracy. Search rank, title or missing rivals alone cannot authorize a positive result. Unknown external author completeness remains unknown.

An immutable `EvaluatedCatalogWorkCorrespondenceReceipt` is part of the attempt, not a caller-authored proof store. It binds the exact accepted claim revision and its historical source citations, retained pages/lookups, all compared alternatives, exact target, original readset, policy artifact/evaluation and rule decision. The public music publication route loads the saved stage and authenticates its exact Work UUID/ticket and active policy inside the transaction. It derives the accepted relationship itself.

The immutable attempt includes the initial preparation and final expanded local candidate snapshot. Target selection happens after hydration, using qualified work keys alongside titles; the processor checks that the original local claim and artist context have not changed. If a supported external work has no canonical composition, the processor checkpoints a stable new composition UUID before writing under its composition owner. The qualified external-ID uniqueness constraint prevents two accepted identities for the same key. A later repertoire-owner stage writes the link. A post-own-write candidate reread is saved as a distinct derived snapshot; it does not replace the original local source clocks. Partial completion is visible and resumable. Repeating an already accepted assignment uses an empty link stage: it acknowledges the current exact link revision without rewriting its evidence, reviewed authority or candidate epochs.

## Freshness, recovery and authority

The original readset includes every used Work dependency family. New page and lookup captures retain their original resource generations, including A→B→A changes. The repertoire owner's complete existing peer/baseline membership and current coordination clocks are captured with facts. Contributed admission preserves those memberships; unrelated keys remain owned by their peers and disappear when those peers are removed. Coordination does not claim to reinterpret their raw source documents. The resolver contributor owns only its actual decision inputs, candidate context, policy and target dependency. Admission and publication reject missing or conflicting stamps rather than dropping them. Candidate epochs fence newly arriving local rivals; owner observations fence editorial-only exclusions and policy changes. External search has no global index-generation clock. An evaluated positive rule may support corroborated correspondence despite that limitation, but cannot claim global absence from returned pages.

Request UUID reuse with changed content conflicts. Exact terminal replay returns the saved outcome before current-version checks. Cancellation or a transient failure retains saved preparation, child commands and completed stages. No HTTP runs in a database transaction. Stage publication, its durable receipt and accepted rows commit together; callback failure rolls them all back. A failed or stale attempt does not delete an earlier accepted link or source extraction. Reviewed corrections retain their authority.

## Operational bounds

Each call advances one phase and invokes at most one acquisition command. Acquisition permits three variants, four pages of fifty hits per variant and twenty distinct hydration targets. Requests use the existing acquisition cache with an explicit seven-day maximum age; this is a retrieval setting, not an accuracy threshold. At most thirty-two distinct response inputs are decoded, eight MiB each and 128 MiB total. Exact accepted-title evidence uses the existing bounded retained-input reader. Local candidate limits remain explicit; more than sixteen hydrated work keys cannot be represented by this candidate query and produces a review outcome. Owner coordination permits at most one hundred contributors and ten thousand bindings. Checkpoints have a 32 MiB serialized limit and at most 128 rows; overflows reject without partial acceptance.

## Validation and acceptance

Root runs focused PostgreSQL/fake-HTTP tests, then relevant compatibility tests and the full suite. Tests must cover a supported fixture positive without source MBID or complete authorship; same-title rivals; wrong claim/evidence/receipt/target; performer versus writer and person versus catalog artist; pending transport and saved-response recovery; cancellation; stale raw/catalog/candidate/editorial/policy state; rollback; exact replay; composition creation races and post-own-write continuation. Existing qualified-ID, complete-writer and reviewed contracts remain supported.

Apply migration 49 before enabling these services. Pause resolver and accepted music writers before rollback to a binary that cannot authenticate these stages. Retain attempts, checkpoints, policy receipts and accepted history. No schedules or production policies start automatically.

## Decisions and discoveries

The resolver uses the existing Work transaction and MusicBrainz command lifecycle. It does not add a scheduler, generic pipeline or second HTTP/response store. A registry artifact is not authority by itself: publication requires an active, independently evaluated registration and the exact saved processor stage.

## Outcomes and remaining work

The implementation builds and passes 256 tests across composition, MusicBrainz, music, Work ownership and contributor compatibility, including all 43 new resolver/rule cases. Independent correctness and readability reviews are clean. The combined checkout also passes 1,808 normalization tests. The next full solution suite remains a separate check. Independently labeled cross-artist composition examples, evaluated positive rules, live successful search-profile verification and policy activation remain required before automatic composition matching or backfill. They are evaluation and rollout work, not a permanent requirement to leave no-MBID claims unresolved.
