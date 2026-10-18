using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

/// <summary>
/// Resolves role-free source assertions into durable identity proposals. This ID-only boundary
/// owns all original-source, current candidate and authority reads; callers cannot supply facts.
/// Neither publication nor a machine-selected UUID creates accepted identity or credit authority.
/// </summary>
public sealed partial class SourceActorResolutionService(
    NormalizationCandidateService candidates,
    PeopleResolutionPolicyService policies,
    SourceActorResolutionDecisionService decisions,
    SourceActorResolutionStore store,
    NormalizationWorkService work
)
{
    public const string Version =
        "source-actor-resolution-proposals-v1/"
        + NormalizationRecordingProcessor.Version
        + "/"
        + NormalizationCandidateService.PeopleFactsVersion
        + "/"
        + PeopleResolutionPolicyEvaluator.RetrievalVersion
        + "/"
        + PeopleResolutionPolicyEvaluator.ScorerVersion
        + "/"
        + SourceActorReviewCorrespondence.Version
        + "/"
        + SourceActorResolutionDecisionService.Version;

    /// <summary>
    /// Reuse both IDs for cancellation or process recovery. Terminal replay retains its original
    /// proof; changed extraction IDs conflict. No accepted assignments, aliases or credits are written.
    /// </summary>
    public async Task<SourceActorResolutionAttempt> ResolveAsync(
        Guid attemptUuid,
        Guid extractionAttemptUuid,
        CancellationToken ct = default
    )
    {
        var attempt = await store.GetAttemptAsync(attemptUuid, ct);
        if (attempt != null && attempt.ExtractionAttemptUuid != extractionAttemptUuid)
        {
            throw new NormalizationConflictException(
                "Resolution attempt already belongs to another extraction."
            );
        }
        attempt ??= await store.GetOrCreateAttemptAsync(
            attemptUuid,
            extractionAttemptUuid,
            Version,
            ct
        );
        if (attempt.Result != null)
        {
            return Replayed(attempt);
        }

        if (attempt.ProcessingVersion != Version)
        {
            return await store.CompleteAsync(
                attempt,
                SourceActorResolutionDisposition.Unresolved,
                "processing_version_changed",
                ct
            );
        }
        try
        {
            if (attempt.Preparation == null)
            {
                attempt = await PrepareAsync(attempt, ct);
                if (attempt.Result != null)
                {
                    return attempt;
                }
            }
            return await PublishPreparedAsync(attempt, ct);
        }
        catch (NormalizationConflictException)
        {
            return await store.CompleteAsync(
                attempt,
                SourceActorResolutionDisposition.Stale,
                "preparation_changed",
                ct
            );
        }
        catch (SourceActorResolutionCapacityException exception)
        {
            return await store.CompleteAsync(
                attempt,
                SourceActorResolutionDisposition.Unresolved,
                exception.Reason,
                ct
            );
        }
        catch (SourceActorReviewCapacityException exception)
        {
            return await store.CompleteAsync(
                attempt,
                SourceActorResolutionDisposition.Unresolved,
                exception.Reason,
                ct
            );
        }
        catch (NormalizationInputContributionsCapacityException)
        {
            return await store.CompleteAsync(
                attempt,
                SourceActorResolutionDisposition.Unresolved,
                "contribution_capacity_exceeded",
                ct
            );
        }
    }

    internal static NormalizationSubjectKey Owner(Guid sourceUuid) =>
        new("people", "source_actor_resolution", sourceUuid);

    private static SourceActorResolutionAttempt Replayed(SourceActorResolutionAttempt attempt) =>
        attempt with
        {
            Result = attempt.Result! with { Replayed = true },
        };

    private async Task<SourceActorResolutionAttempt> PrepareAsync(
        SourceActorResolutionAttempt attempt,
        CancellationToken ct
    )
    {
        // Registry initialization belongs outside the read-only snapshot. The actual positive
        // or negative policy state is read again inside it through the guarded instance reader.
        await policies.ReadStateAsync(ct);
        var preparation = await store.ReadPreparationSnapshotAsync(
            attempt,
            ReadPreparationAsync,
            ct
        );
        if (preparation == null)
        {
            return await store.CompleteAsync(
                attempt,
                SourceActorResolutionDisposition.Unresolved,
                "unsupported_owner_inputs",
                ct
            );
        }
        return await store.SavePreparationAsync(attempt.AttemptUuid, preparation, ct);
    }

    private async Task<SourceActorResolutionPreparation?> ReadPreparationAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SourceActorResolutionSource source,
        CancellationToken ct
    )
    {
        var membership = await NormalizationWorkService.GetInputContributionsInSnapshotAsync(
            c,
            t,
            Owner(source.Extraction.SourceUuid),
            PeopleResolutionCapacity.ContributionMembership,
            ct
        );
        if (!await CanReplaceInputsAsync(c, t, source, membership, ct))
        {
            return null;
        }

        var policy = await policies.ReadStateInSnapshotAsync(c, t, ct);
        var proposed = await new CandidatePreparation(
            source,
            membership,
            policy,
            candidates,
            decisions,
            c,
            t
        ).BuildAsync(ct);
        var preparation = new SourceActorResolutionPreparation(
            SourceActorResolutionStore.ExtractionObservation(source.Extraction),
            source.Extraction.Ticket,
            policy,
            source.PeopleUniverse,
            source.SourceReviews,
            membership.Owner?.DesiredGeneration,
            membership
                .Contributions.Select(state => new NormalizationInputContributionRevision(
                    state.Contributor,
                    state.Revision
                ))
                .ToArray(),
            proposed.Inputs,
            proposed.Pools
        );
        var contributions = SourceActorResolutionReadSet.Contributions(preparation);
        SourceActorResolutionReadSet.RequireCapacity(preparation, contributions);
        return preparation;
    }

    private async Task<SourceActorResolutionAttempt> PublishPreparedAsync(
        SourceActorResolutionAttempt attempt,
        CancellationToken ct
    )
    {
        var preparation = attempt.Preparation!;
        NormalizationWorkTicket ticket;
        try
        {
            ticket = await work.BeginContributedBatchAsync(
                attempt.AttemptUuid,
                Owner(attempt.SourceUuid),
                attempt.ProcessingVersion,
                SourceActorResolutionReadSet.Contributions(preparation),
                preparation.ExpectedConfigurationGeneration,
                ct,
                preparation.ExpectedContributions
            );
        }
        catch (NormalizationConflictException)
        {
            return await store.CompleteAsync(
                attempt,
                SourceActorResolutionDisposition.Stale,
                "admission_changed",
                ct
            );
        }
        if (!SourceActorResolutionReadSet.IsComplete(ticket, preparation))
        {
            return await store.CompleteAsync(
                attempt,
                SourceActorResolutionDisposition.Stale,
                "admission_changed",
                ct
            );
        }
        var batch = new SourceActorResolutionBatch(
            attempt.AttemptUuid,
            attempt.SourceUuid,
            attempt.ExtractionAttemptUuid,
            attempt.ProcessingVersion,
            preparation,
            ticket
        );
        var saved = await store.SaveProposalsAsync(batch, ct);
        if (saved.Result != null)
        {
            return Replayed(saved);
        }

        try
        {
            var publication = await work.TryPublishAsync(
                ticket.WorkUuid,
                (c, t, token) => store.WritePublishedAsync(c, t, batch, token),
                ct
            );
            if (publication.Status == NormalizationPublicationStatus.Stale)
            {
                return await store.CompleteAsync(
                    attempt,
                    SourceActorResolutionDisposition.Stale,
                    "publication_changed",
                    ct
                );
            }
            var completed = (await store.GetAttemptAsync(attempt.AttemptUuid, ct))!;
            return completed with
            {
                Result = completed.Result! with { Replayed = publication.Replayed },
            };
        }
        catch (NormalizationConflictException)
        {
            return await store.CompleteAsync(
                attempt,
                SourceActorResolutionDisposition.Stale,
                "publication_changed",
                ct
            );
        }
    }

    private async Task<bool> CanReplaceInputsAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SourceActorResolutionSource source,
        NormalizationInputContributionsSnapshot membership,
        CancellationToken ct
    )
    {
        var contributors = source
            .Inputs.Select(input => input.OriginalInput.Contributor)
            .ToHashSet();
        if (
            membership.UnmanagedBaseline != null
            || membership.Contributions.Any(state =>
                HasFacts(state) && !contributors.Contains(state.Contributor)
            )
        )
        {
            return false;
        }
        if (!membership.Contributions.Any(HasFacts))
        {
            return true;
        }
        var configured = await SourceActorResolutionStore.GetConfiguredPreparationAsync(
            c,
            t,
            source.Extraction.SourceUuid,
            membership,
            ct
        );
        return configured != null && OwnsConfiguredBindings(membership, configured);
    }

    private static bool OwnsConfiguredBindings(
        NormalizationInputContributionsSnapshot snapshot,
        SourceActorResolutionPreparation configured
    )
    {
        var known = SourceActorResolutionReadSet
            .Contributions(configured)
            .ToDictionary(value => value.Contribution.Contributor);
        return snapshot
            .Contributions.Where(HasFacts)
            .All(state =>
                known.TryGetValue(state.Contributor, out var expected)
                && state.Resources.ToHashSet().SetEquals(expected.Contribution.Resources)
                && state
                    .DependencySubjectUuids.ToHashSet()
                    .SetEquals(expected.Contribution.DependencySubjectUuids)
                && state
                    .CatalogArtistUuids.ToHashSet()
                    .SetEquals((expected.CatalogInputs ?? []).Select(value => value.ArtistUuid))
                && state
                    .SourceIdentifiers.ToHashSet()
                    .SetEquals(
                        (expected.SourceIdentities ?? []).Select(value => value.UpstreamIdentifier)
                    )
                && state.UsesMappingRegistry == (expected.MappingRegistry != null)
                && (state.SourceUuids ?? [])
                    .ToHashSet()
                    .SetEquals((expected.SourceUuids ?? []).Select(value => value.SourceUuid))
                && (state.OwnerObservationSubjectUuids ?? [])
                    .ToHashSet()
                    .SetEquals(expected.Contribution.OwnerObservationSubjectUuids ?? [])
                && (state.CandidateUniverses ?? [])
                    .ToHashSet()
                    .SetEquals((expected.CandidateUniverses ?? []).Select(value => value.Universe))
                && (state.ImportedVenueUuids ?? [])
                    .ToHashSet()
                    .SetEquals((expected.ImportedVenues ?? []).Select(value => value.VenueUuid))
            );
    }

    private static bool HasFacts(NormalizationInputContributionState state) =>
        state.Resources.Count
            + state.DependencySubjectUuids.Count
            + state.CatalogArtistUuids.Count
            + state.SourceIdentifiers.Count
            + (state.SourceUuids?.Count ?? 0)
            + (state.ImportedVenueUuids?.Count ?? 0)
            + (state.CandidateUniverses?.Count ?? 0)
            + (state.OwnerObservationSubjectUuids?.Count ?? 0)
            > 0
        || state.UsesMappingRegistry;
}
