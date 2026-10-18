using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

/// <summary>
/// Resolves current accepted repertoire assertions using retained external observations.
/// Each call advances one durable phase. Call again with the same UUID after interruption or
/// NotBefore; a new UUID requests a new attempt. No HTTP runs inside a catalog transaction.
/// </summary>
public sealed partial class NormalizationCompositionResolutionProcessor(
    DbService db,
    NormalizationCandidateService candidates,
    NormalizationMusicBrainzInputService inputs,
    NormalizationEvidenceService evidence,
    NormalizationProviderDecoder decoder,
    CompositionResolutionPolicyService policies,
    NormalizationCompositionResolutionStore store,
    NormalizationWorkService work,
    NormalizedMusicService music
)
{
    public const string Version = CompositionResolutionPolicyEvaluator.ProcessorVersion;

    public async Task<CompositionResolutionAttempt> ResolveRepertoireAsync(
        Guid attemptUuid,
        CatalogWorkClaimReference claim,
        CancellationToken ct = default
    )
    {
        var attempt = await store.GetAsync(attemptUuid, ct);
        if (attempt != null && attempt.Claim != claim)
        {
            throw new NormalizationConflictException(
                "The resolution attempt belongs to another accepted claim."
            );
        }
        attempt ??= await store.GetOrCreateAsync(attemptUuid, claim, Version, null, ct);
        return await AdvanceAsync(attempt, ct);
    }

    public Task<CompositionResolutionAttempt?> GetAsync(
        Guid attemptUuid,
        CancellationToken ct = default
    ) => store.GetAsync(attemptUuid, ct);

    /// <summary>A new attempt explicitly rereads the current accepted claim; previous receipts remain immutable.</summary>
    public async Task<CompositionResolutionAttempt> RetryAsync(
        Guid newAttemptUuid,
        Guid previousAttemptUuid,
        CancellationToken ct = default
    )
    {
        var previous =
            await store.GetAsync(previousAttemptUuid, ct)
            ?? throw new ArgumentException("Unknown composition resolution attempt.");
        var revision = await db.WithWriteConnection(c =>
            c.QuerySingleOrDefaultAsync<Guid?>(
                new CommandDefinition(
                    "SELECT revision_uuid FROM normalization.repertoire_songs WHERE uuid=@uuid AND NOT retracted AND redirect_uuid IS NULL",
                    new { uuid = previous.Claim.RepertoireSongUuid },
                    cancellationToken: ct
                )
            )
        );
        if (revision == null)
        {
            throw new NormalizationConflictException("The repertoire claim is no longer active.");
        }
        var attempt = await store.GetOrCreateAsync(
            newAttemptUuid,
            new(previous.Claim.RepertoireSongUuid, revision.Value),
            Version,
            previousAttemptUuid,
            ct
        );
        return await AdvanceAsync(attempt, ct);
    }

    private async Task<CompositionResolutionAttempt> AdvanceAsync(
        CompositionResolutionAttempt attempt,
        CancellationToken ct
    )
    {
        if (attempt.Result != null)
        {
            return attempt with { Result = attempt.Result with { Replayed = true } };
        }
        if (attempt.ProcessingVersion != Version)
        {
            return await store.CompleteAsync(
                attempt,
                CompositionResolutionDisposition.Unresolved,
                "processing_version_changed",
                ct
            );
        }
        try
        {
            if (attempt.Preparation == null)
            {
                return await PrepareAsync(attempt, ct);
            }
            if (
                attempt.Preparation.Context.Candidates.Version
                    != NormalizationCandidateService.FactsVersion
                || attempt.Preparation.Variants.Version != MusicBrainzCandidateQueryBuilder.Version
            )
            {
                return await store.CompleteAsync(
                    attempt,
                    CompositionResolutionDisposition.Unresolved,
                    "prepared_fact_versions_changed",
                    ct
                );
            }
            // Resume an admitted negative stage before acquisition. Unsupported local inputs
            // have no network phase, but still retain the same interrupted-stage protocol.
            var pending = attempt.Stages.FirstOrDefault(stage => stage.Receipt == null);
            if (pending != null)
            {
                return await AdvanceStageAsync(attempt, pending, ct);
            }
            var acknowledged = attempt.Stages.SingleOrDefault(stage =>
                stage.Kind == CompositionResolutionStageKind.AcknowledgeDecision
            );
            if (acknowledged != null)
            {
                return await store.CompleteAsync(
                    attempt,
                    acknowledged.NegativeDecision!.Disposition,
                    acknowledged.NegativeDecision.Reason,
                    ct
                );
            }
            if (attempt.Preparation.UnsupportedDecision is { } unsupported)
            {
                return await PrepareNegativeStageAsync(
                    attempt,
                    attempt.Preparation.Context,
                    unsupported,
                    ct
                );
            }
            if (!AcquisitionComplete(attempt))
            {
                return await AdvanceAcquisitionAsync(attempt, ct);
            }
            if (attempt.Proposal == null)
            {
                return await EvaluateAsync(attempt, ct);
            }
            if (attempt.Stages.Any(stage => stage.Kind == CompositionResolutionStageKind.Link))
            {
                return await store.CompleteAsync(
                    attempt,
                    CompositionResolutionDisposition.Applied,
                    "evaluated_composition_correspondence",
                    ct
                );
            }
            return await PrepareStageAsync(attempt, ct);
        }
        catch (NormalizationConflictException)
        {
            // Another invocation may have checkpointed since our read. CompleteAsync uses an
            // exact attempt CAS so a losing worker cannot terminalize the winner's progress.
            return await store.CompleteAsync(
                attempt,
                CompositionResolutionDisposition.Stale,
                "composition_read_set_changed",
                ct
            );
        }
    }

    private async Task<CompositionResolutionAttempt> PrepareAsync(
        CompositionResolutionAttempt attempt,
        CancellationToken ct
    )
    {
        var context = await candidates.ReadCompositionResolutionContextAsync(attempt.Claim, ct: ct);
        var policyState = await policies.ReadStateAsync(ct);
        if (context.LocalTitle == null || context.Issues.Count != 0 || !context.Candidates.Complete)
        {
            // Bounded or unsupported evidence is a durable observation, not an absence of
            // work. Its real owner and candidate/policy clocks must wake a later attempt.
            return await store.SavePreparationAsync(
                attempt.AttemptUuid,
                new(
                    context,
                    new(
                        MusicBrainzCandidateQueryBuilder.Version,
                        new(MusicBrainzLookupKind.Work, context.LocalTitle, []),
                        [],
                        []
                    ),
                    policyState.Active,
                    policyState,
                    new(
                        CompositionResolutionDisposition.Unresolved,
                        "unsupported_or_incomplete_local_claim"
                    )
                ),
                ct
            );
        }
        var policy = policyState.Active;
        var seed = new MusicBrainzSearchSeed(MusicBrainzLookupKind.Work, context.LocalTitle, []);
        var variants = MusicBrainzCandidateQueryBuilder.BuildVariants(seed);
        var queries = variants
            .Queries.Concat(
                new[]
                {
                    new MusicBrainzSearchQuery(
                        MusicBrainzLookupKind.Recording,
                        context.LocalTitle.Value
                    ),
                }
            )
            .ToList();
        if (context.Artist.ExternalArtistMbid is { } performer)
        {
            // This catalog field is fenced by the artist clock. It has no invented raw citation
            // and is used only as performer context, never as an author or canonical person ID.
            queries.Add(
                new(
                    MusicBrainzLookupKind.Recording,
                    context.LocalTitle.Value,
                    new(
                        MusicBrainzSearchArtistUse.RecordingPerformer,
                        ExternalArtistMbid: performer
                    )
                )
            );
        }
        return await store.SavePreparationAsync(
            attempt.AttemptUuid,
            new(context, variants with { Queries = queries.ToArray() }, policy, policyState),
            ct
        );
    }

    private async Task<CompositionResolutionAttempt> EvaluateAsync(
        CompositionResolutionAttempt attempt,
        CancellationToken ct
    )
    {
        var facts = await ReadCorrespondenceFactsAsync(attempt, ct);
        var keys = facts
            .Candidates.Select(candidate => new MatchingExternalIdentity(
                "musicbrainz.org",
                "work",
                candidate.ExternalWorkMbid.ToString("D"),
                true
            ))
            .ToArray();
        // The bound is explicit coverage. Preserve the original local snapshot rather than
        // passing a truncated external identifier list as a complete expanded candidate query.
        var context =
            keys.Length > 16
                ? attempt.Preparation!.Context
                : await candidates.ReadCompositionResolutionContextAsync(attempt.Claim, keys, ct);
        RequireSameAcceptedClaim(attempt.Preparation!.Context, context);
        facts = facts with
        {
            LocalCandidatesComplete = context.Candidates.Complete && context.Issues.Count == 0,
            LocalCandidates = context.Candidates.Facts,
            LocalRepertoire = context.Repertoire,
        };
        var policy = attempt.Preparation.Policy;
        var decision =
            policy == null
                ? new CompositionCorrespondenceDecision(
                    CompositionCorrespondenceAction.NeedsReview,
                    null,
                    "",
                    "acceptance_policy_inactive",
                    []
                )
                : new CompositionResolutionPolicyEvaluator().Evaluate(facts, policy.Policy);
        if (keys.Length > 16)
        {
            decision = new(
                CompositionCorrespondenceAction.NeedsReview,
                null,
                "",
                "local_work_identifier_limit",
                []
            );
        }
        var target = decision.ExternalWorkMbid is { } mbid
            ? ExistingComposition(context, mbid)
            : null;
        var existingLinks = context.Repertoire.CompositionLinks;
        var currentLink = existingLinks.SingleOrDefault(link =>
            link.StoredRepertoireSongUuid == attempt.Claim.RepertoireSongUuid
        );
        if (existingLinks.Any(link => link.ResolvedCompositionUuid != target?.Header.Uuid))
        {
            decision = decision with
            {
                Action = CompositionCorrespondenceAction.NeedsReview,
                Reason = "existing_composition_assignment",
            };
        }
        if (
            (context.ReviewedExclusions ?? []).Any(exclusion =>
                ExcludesCorrespondence(
                    context,
                    exclusion.Publication,
                    target?.Header.Uuid,
                    decision.ExternalWorkMbid
                )
            )
        )
        {
            decision = decision with
            {
                Action = CompositionCorrespondenceAction.NeedsReview,
                Reason = "reviewed_music_exclusion",
            };
        }
        var readSet = await ReadExternalReadSetAsync(attempt, context, ct);
        var receipt = new EvaluatedCatalogWorkCorrespondenceReceipt(
            StableResolutionUuid(attempt.AttemptUuid, "correspondence"),
            attempt.AttemptUuid,
            Version,
            attempt.Claim,
            decision.ExternalWorkMbid,
            decision.ExternalWorkMbid.HasValue
                ? target?.Header.Uuid ?? StableResolutionUuid(attempt.AttemptUuid, "composition")
                : null,
            currentLink?.Provenance.AssertionUuid
                ?? StableResolutionUuid(attempt.AttemptUuid, "link"),
            facts,
            decision,
            policy,
            readSet,
            target?.Header.Revision ?? 0,
            currentLink?.Provenance.Revision ?? 0,
            context
        );
        return await store.SaveProposalAsync(attempt.AttemptUuid, receipt, ct);
    }

    private async Task<CompositionResolutionAttempt> PrepareStageAsync(
        CompositionResolutionAttempt attempt,
        CancellationToken ct
    )
    {
        var proposal = attempt.Proposal!;
        if (
            proposal.Decision.Action != CompositionCorrespondenceAction.ProposeAcceptance
            || proposal.Policy == null
        )
        {
            return await PrepareNegativeStageAsync(
                attempt,
                proposal.Context!,
                new(
                    proposal.Policy == null
                        ? CompositionResolutionDisposition.Unresolved
                        : CompositionResolutionDisposition.Review,
                    proposal.Decision.Reason
                ),
                ct
            );
        }
        var created = attempt.Stages.Any(stage =>
            stage.Kind == CompositionResolutionStageKind.CreateComposition
        );
        var create = proposal.ExpectedCompositionRevision == 0 && !created;
        var context = proposal.Context!;
        var readSet = create
            ? await ReadCompositionDecisionReadSetAsync(attempt, context, ct)
            : proposal.ReadSet;
        if (created)
        {
            context = await candidates.ReadCompositionResolutionContextAsync(
                attempt.Claim,
                [
                    new(
                        "musicbrainz.org",
                        "work",
                        proposal.ExternalWorkMbid!.Value.ToString("D"),
                        true
                    ),
                ],
                ct
            );
            RequireSameAcceptedClaim(attempt.Preparation!.Context, context);
            var derivedFacts = proposal.Facts with
            {
                LocalCandidatesComplete = context.Candidates.Complete && context.Issues.Count == 0,
                LocalCandidates = context.Candidates.Facts,
                LocalRepertoire = context.Repertoire,
            };
            if (
                new CompositionResolutionPolicyEvaluator()
                    .Evaluate(derivedFacts, proposal.Policy.Policy)
                    .Action != CompositionCorrespondenceAction.ProposeAcceptance
                || ExistingComposition(context, proposal.ExternalWorkMbid.Value)?.Header.Uuid
                    != proposal.CompositionUuid
                || (context.ReviewedExclusions ?? []).Any(exclusion =>
                    ExcludesCorrespondence(
                        context,
                        exclusion.Publication,
                        proposal.CompositionUuid,
                        proposal.ExternalWorkMbid
                    )
                )
            )
            {
                throw new NormalizationConflictException(
                    "Post-creation candidates no longer support the saved correspondence."
                );
            }
            readSet = await ReadExternalReadSetAsync(attempt, context, ct);
        }
        var publication = create ? CompositionPublication(proposal) : LinkPublication(proposal);
        if (!create)
        {
            var target =
                ExistingComposition(context, proposal.ExternalWorkMbid!.Value)
                ?? throw new NormalizationConflictException(
                    "The composition target is unavailable."
                );
            var observation = context.ReadSet.OwnerObservations.Single(value =>
                value.SubjectUuid == target.Header.OwnerSubjectUuid
            );
            readSet = readSet with
            {
                Dependencies = readSet
                    .Dependencies.Append(
                        new(
                            observation.SubjectUuid,
                            observation.DesiredGeneration,
                            observation.EditorialRevision,
                            observation.PublishedWorkUuid
                        )
                    )
                    .Distinct()
                    .ToArray(),
            };
        }
        var stage = new CompositionResolutionStage(
            StableResolutionUuid(attempt.AttemptUuid, create ? "create-work" : "link-work"),
            create
                ? CompositionResolutionStageKind.CreateComposition
                : CompositionResolutionStageKind.Link,
            publication,
            readSet,
            DerivedContext: context
        );
        return await store.SaveStageAsync(attempt.AttemptUuid, stage, ct);
    }

    private async Task<CompositionResolutionAttempt> PrepareNegativeStageAsync(
        CompositionResolutionAttempt attempt,
        CompositionResolutionContext context,
        CompositionResolutionNegativeDecision decision,
        CancellationToken ct
    )
    {
        if (
            context.Candidates.Issues.Any(issue =>
                issue.Kind
                    is CandidateFactsIssueKind.MissingOwner
                        or CandidateFactsIssueKind.MissingCatalogClock
            )
        )
        {
            // Registering a missing owner or restoring its catalog clock need not change an
            // accepted candidate row. An epoch cannot watch the missing stamp on our behalf.
            return await store.CompleteAsync(
                attempt,
                CompositionResolutionDisposition.Stale,
                "context_freshness_unavailable",
                ct
            );
        }
        if (
            context.OwnerMembership?.Owner == null
            || context.OwnerBindings == null
            || context.Issues.Contains("owner_input_head_missing")
            || attempt.Preparation!.PolicyState == null
        )
        {
            return await store.CompleteAsync(
                attempt,
                CompositionResolutionDisposition.Stale,
                "negative_decision_context_missing",
                ct
            );
        }
        var readSet = await ReadExternalReadSetAsync(attempt, context, ct);
        return await store.SaveStageAsync(
            attempt.AttemptUuid,
            new(
                StableResolutionUuid(attempt.AttemptUuid, "acknowledge-decision"),
                CompositionResolutionStageKind.AcknowledgeDecision,
                new(),
                readSet,
                DerivedContext: context,
                NegativeDecision: decision
            ),
            ct
        );
    }

    private async Task<CompositionResolutionAttempt> AdvanceStageAsync(
        CompositionResolutionAttempt attempt,
        CompositionResolutionStage stage,
        CancellationToken ct
    )
    {
        if (stage.Ticket == null)
        {
            var ticket = await AdmitStageAsync(attempt, stage, ct);
            return await store.AttachTicketAsync(attempt.AttemptUuid, stage.WorkUuid, ticket, ct);
        }
        var result = await music.PublishCompositionResolutionAsync(
            stage.WorkUuid,
            attempt.AttemptUuid,
            ct
        );
        if (result.Status == NormalizationPublicationStatus.Stale)
        {
            return await store.CompleteAsync(
                attempt,
                CompositionResolutionDisposition.Stale,
                "publication_stale",
                ct
            );
        }
        return (await store.GetAsync(attempt.AttemptUuid, ct))!;
    }

    private static CompositionCandidateFacts? ExistingComposition(
        CompositionResolutionContext context,
        Guid mbid
    ) =>
        context.Candidates.Facts.SingleOrDefault(candidate =>
            candidate.ExternalIdentities.Any(identity =>
                identity.Provider == "musicbrainz.org"
                && identity.ResourceType == "work"
                && Guid.TryParse(identity.ProviderKey, out var key)
                && key == mbid
            )
        );

    private static void RequireSameAcceptedClaim(
        CompositionResolutionContext original,
        CompositionResolutionContext current
    )
    {
        var owner = original.Repertoire.Header.OwnerSubjectUuid;
        if (
            original.Claim != current.Claim
            || original.Artist != current.Artist
            || JsonSerializer.Serialize(original.LocalTitle)
                != JsonSerializer.Serialize(current.LocalTitle)
            || original.ReadSet.OwnerObservations.Single(value => value.SubjectUuid == owner)
                != current.ReadSet.OwnerObservations.Single(value => value.SubjectUuid == owner)
            || !original
                .ReadSet.CatalogInputs.Where(value => value.ArtistUuid == original.ArtistUuid)
                .ToHashSet()
                .SetEquals(
                    current.ReadSet.CatalogInputs.Where(value =>
                        value.ArtistUuid == current.ArtistUuid
                    )
                )
        )
        {
            throw new NormalizationConflictException(
                "The original accepted claim or artist context changed during acquisition."
            );
        }
    }

    private static MusicPublication CompositionPublication(
        EvaluatedCatalogWorkCorrespondenceReceipt proposal
    )
    {
        var work = proposal
            .Facts.Candidates.Single(candidate =>
                candidate.ExternalWorkMbid == proposal.ExternalWorkMbid
            )
            .Work;
        var title =
            work.Work.Name
            ?? throw new NormalizationConflictException(
                "The evaluated work has no evidenced title."
            );
        var evidence = new[] { work.Work.Identity.Evidence };
        return new()
        {
            Compositions =
            [
                new CompositionChange
                {
                    Uuid = proposal.CompositionUuid!.Value,
                    DisplayTitle = title.Value,
                    Evidence = [title.Evidence],
                    Basis = "provider_assertion",
                },
            ],
            ExternalIdentities =
            [
                new MusicExternalIdentityChange
                {
                    Uuid = StableResolutionUuid(proposal.AttemptUuid, "work-id"),
                    CompositionUuid = proposal.CompositionUuid,
                    Provider = "musicbrainz.org",
                    ResourceType = "work",
                    ProviderKey = proposal.ExternalWorkMbid!.Value.ToString("D"),
                    Evidence = evidence,
                },
            ],
        };
    }

    private static MusicPublication LinkPublication(
        EvaluatedCatalogWorkCorrespondenceReceipt proposal
    )
    {
        if (proposal.ExpectedLinkRevision > 0)
        {
            return new();
        }
        return new()
        {
            CompositionLinks =
            [
                new RepertoireCompositionChange
                {
                    Uuid = proposal.LinkUuid,
                    RepertoireSongUuid = proposal.Claim.RepertoireSongUuid,
                    CompositionUuid = proposal.CompositionUuid!.Value,
                    ExpectedRevision = proposal.ExpectedLinkRevision,
                    MatchReason = "evaluated_correspondence",
                    EvaluatedCorrespondenceReceiptUuid = proposal.ReceiptUuid,
                    Basis = "validated_match",
                    Evidence = proposal
                        .Decision.Candidates.Single(candidate =>
                            candidate.ExternalWorkMbid == proposal.ExternalWorkMbid
                        )
                        .Evidence,
                },
            ],
        };
    }

    private static Guid StableResolutionUuid(Guid attemptUuid, string phase) =>
        new(
            SHA256
                .HashData(
                    Encoding.UTF8.GetBytes(
                        Version + "\n" + attemptUuid.ToString("D") + "\n" + phase
                    )
                )
                .AsSpan(0, 16)
        );

    private static bool ExcludesCorrespondence(
        CompositionResolutionContext context,
        MusicPublication rejected,
        Guid? target,
        Guid? externalWork
    )
    {
        var songs = context
            .Repertoire.IdentityClosure.Select(identity => identity.Uuid)
            .ToHashSet();
        return rejected.RepertoireSongs.Any(song =>
                songs.Contains(song.Uuid)
                && song.DisplayTitle == context.Repertoire.Header.DisplayText
            )
            || rejected.CompositionLinks.Any(link =>
                songs.Contains(link.RepertoireSongUuid) && link.CompositionUuid == target
            )
            || rejected.Compositions.Any(composition => composition.Uuid == target)
            || rejected.ExternalIdentities.Any(identity =>
                target.HasValue && identity.CompositionUuid == target
                || identity.Provider == "musicbrainz.org"
                    && identity.ResourceType == "work"
                    && Guid.TryParse(identity.ProviderKey, out var key)
                    && key == externalWork
            );
    }
}
