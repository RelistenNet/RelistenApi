using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

/// <summary>
/// Produces complete candidate-set decisions from a published recording extraction. This
/// stage publishes proposals only; evaluated policy activation and accepted writes are separate.
/// </summary>
public sealed partial class NormalizationPeopleResolutionProcessor(
    NormalizedPeopleService people,
    NormalizationRecordingProposalService extractions,
    NormalizationCandidateService candidates,
    NormalizationPeopleResolutionDecisionService decisions,
    NormalizationPeopleResolutionStore store,
    NormalizationWorkService work
)
{
    public const string Version =
        "people-resolution-proposals-v1/"
        + NormalizationCandidateService.PeopleFactsVersion
        + "/"
        + NormalizationPeopleResolutionDecisionService.Version
        + "/"
        + NormalizedPeopleService.CreditContextVersion;

    /// <summary>
    /// Resolves every credit proposal in the exact published extraction. Reuse an attempt UUID
    /// for recovery; use a new UUID after a terminal outcome or changed extraction. No policy or
    /// candidate list supplied by a caller can authorize accepted writes through this service.
    /// </summary>
    public async Task<NormalizationPeopleResolutionAttempt> ResolveRecordingAsync(
        Guid attemptUuid,
        Guid extractionAttemptUuid,
        CancellationToken cancellationToken = default
    )
    {
        var attempt = await store.GetAttemptAsync(attemptUuid, cancellationToken);
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
            cancellationToken
        );
        if (attempt.Result != null)
        {
            return Replayed(attempt);
        }
        if (attempt.ProcessingVersion != Version)
        {
            return await store.CompleteAsync(
                attempt,
                NormalizationPeopleResolutionDisposition.Unresolved,
                "processing_version_changed",
                cancellationToken
            );
        }
        if (attempt.Preparation == null)
        {
            attempt = await PrepareAsync(attempt, cancellationToken);
            if (attempt.Result != null)
            {
                return attempt;
            }
        }
        return await PublishPreparedAsync(attempt, cancellationToken);
    }

    internal static NormalizationSubjectKey Owner(Guid sourceUuid) =>
        new("people", "recording_resolution", sourceUuid);

    private static NormalizationPeopleResolutionAttempt Replayed(
        NormalizationPeopleResolutionAttempt attempt
    ) => attempt with { Result = attempt.Result! with { Replayed = true } };

    private async Task<NormalizationPeopleResolutionAttempt> PrepareAsync(
        NormalizationPeopleResolutionAttempt attempt,
        CancellationToken ct
    )
    {
        var extraction = (
            await extractions.GetAttemptBatchAsync(attempt.ExtractionAttemptUuid, ct)
        )!;
        var original = NormalizationRecordingReadSet.Inputs(extraction.Preparation);
        var membership = await work.GetInputContributionsAsync(
            Owner(attempt.SourceUuid),
            cancellationToken: ct
        );
        var contributors = original.Select(input => input.Contributor).ToHashSet();
        if (
            membership.UnmanagedBaseline != null
            || membership.Contributions.Any(state =>
                HasFacts(state) && !contributors.Contains(state.Contributor)
            )
        )
        {
            return await store.CompleteAsync(
                attempt,
                NormalizationPeopleResolutionDisposition.Unresolved,
                "unsupported_owner_inputs",
                ct
            );
        }
        if (membership.Contributions.Any(HasFacts))
        {
            var configured = await store.GetConfiguredPreparationAsync(
                attempt.SourceUuid,
                membership.Owner!.DesiredGeneration,
                ct
            );
            if (configured == null || !OwnsConfiguredBindings(membership, configured))
            {
                return await store.CompleteAsync(
                    attempt,
                    NormalizationPeopleResolutionDisposition.Unresolved,
                    "unsupported_owner_inputs",
                    ct
                );
            }
        }
        var groups = new[]
        {
            new NormalizationRecordingInputExtraction(
                extraction.Preparation.Binding.InputRevisionUuid,
                extraction.Extraction
            ),
        }
            .Concat(extraction.AdditionalExtractions ?? [])
            .ToDictionary(group => group.InputRevisionUuid);
        if (
            groups.Values.Sum(group => (long)group.Extraction.Credits.Count)
            > NormalizationPeopleResolutionStore.MaximumClaims
        )
        {
            return await store.CompleteAsync(
                attempt,
                NormalizationPeopleResolutionDisposition.Unresolved,
                "credit_limit",
                ct
            );
        }
        var context = await people.ReadCreditContextAsync(attempt.SourceUuid, ct);
        var cache = new Dictionary<
            string,
            NormalizationCandidateFactsSnapshot<PersonCandidateFacts>
        >(StringComparer.Ordinal);
        var prepared = new List<NormalizationPeopleResolutionInput>();
        foreach (var input in original)
        {
            var group = groups[input.Binding.InputRevisionUuid].Extraction;
            var claims = new List<NormalizationPeopleResolutionClaim>();
            foreach (var proposal in group.Credits)
            {
                ct.ThrowIfCancellationRequested();
                NormalizationCandidateFactsSnapshot<PersonCandidateFacts>? facts = null;
                var query = PeopleActorProjection
                    .Project(proposal.Mention, input.Binding.Recording.Value!.ArtistUuid)
                    .Query;
                if (query != null)
                {
                    var key = PeopleActorProjection.QueryFingerprint(query);
                    if (!cache.TryGetValue(key, out facts))
                    {
                        facts = await candidates.FindPeopleFactsAsync(query, cancellationToken: ct);
                        cache.Add(key, facts);
                    }
                }
                claims.Add(decisions.Compare(proposal, input.Binding, facts, context));
            }
            var state = membership.Contributions.SingleOrDefault(state =>
                state.Contributor == input.Contributor
            );
            prepared.Add(
                new(
                    input,
                    state?.Revision ?? 0,
                    claims.ToArray(),
                    group.UnresolvedCredits,
                    group.DecodeIssues
                )
            );
        }
        var observation = new NormalizationSubjectObservation(
            extraction.Ticket.SubjectUuid,
            extraction.Ticket.DesiredGeneration,
            extraction.Ticket.EditorialRevision,
            extraction.AttemptUuid
        );
        var preparation = new NormalizationPeopleResolutionPreparation(
            observation,
            context,
            membership.Owner?.DesiredGeneration,
            membership
                .Contributions.Select(state => new NormalizationInputContributionRevision(
                    state.Contributor,
                    state.Revision
                ))
                .ToArray(),
            prepared.ToArray()
        );
        return await store.SavePreparationAsync(attempt.AttemptUuid, preparation, ct);
    }

    private async Task<NormalizationPeopleResolutionAttempt> PublishPreparedAsync(
        NormalizationPeopleResolutionAttempt attempt,
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
                NormalizationPeopleResolutionReadSet.Contributions(preparation),
                preparation.ExpectedConfigurationGeneration,
                ct,
                preparation.ExpectedContributions
            );
        }
        catch (NormalizationConflictException)
        {
            return await store.CompleteAsync(
                attempt,
                NormalizationPeopleResolutionDisposition.Stale,
                "admission_changed",
                ct
            );
        }
        if (!NormalizationPeopleResolutionReadSet.IsComplete(ticket, preparation))
        {
            return await store.CompleteAsync(
                attempt,
                NormalizationPeopleResolutionDisposition.Unresolved,
                "unsupported_owner_inputs",
                ct
            );
        }
        var batch = new NormalizationPeopleResolutionBatch(
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
        // Proposal publication takes no candidate write scope: its rows do not change the
        // accepted people universe, and it must not invalidate its own candidate snapshot.
        var publication = await work.TryPublishAsync(
            ticket.WorkUuid,
            (c, t, token) => store.WritePublishedAsync(c, t, batch, token),
            ct
        );
        if (publication.Status == NormalizationPublicationStatus.Stale)
        {
            return await store.CompleteAsync(
                attempt,
                NormalizationPeopleResolutionDisposition.Stale,
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

    private static bool OwnsConfiguredBindings(
        NormalizationInputContributionsSnapshot snapshot,
        NormalizationPeopleResolutionPreparation configured
    )
    {
        var known = NormalizationPeopleResolutionReadSet
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
                && (state.ImportedVenueUuids?.Count ?? 0) == 0
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
