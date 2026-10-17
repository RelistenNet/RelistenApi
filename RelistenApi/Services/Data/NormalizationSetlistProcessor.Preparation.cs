using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationSetlistProcessor
{
    private async Task<NormalizationSetlistProcessingAttempt> PrepareAsync(
        NormalizationSetlistProcessingAttempt attempt,
        CancellationToken ct
    )
    {
        var membership = await work.GetInputContributionsAsync(
            Owner(attempt.Registration.RegistrationUuid),
            cancellationToken: ct
        );
        if (
            membership.UnmanagedBaseline != null
            || membership.Contributions.Any(state => HasFacts(state) && !IsSupported(state))
        )
        {
            return await AttentionAsync(attempt, "unsupported_owner_inputs", ct);
        }
        var primaryState = membership.Contributions.SingleOrDefault(state =>
            state.Contributor == attempt.Request.Input.Resource
        );
        var peers = membership
            .Contributions.Where(state =>
                HasFacts(state) && state.Contributor != attempt.Request.Input.Resource
            )
            .ToArray();
        IReadOnlyList<NormalizationBindingInputReference> peerReferences;
        try
        {
            peerReferences = await evidence.GetCurrentInputReferencesAsync(
                peers.Select(state => state.Resources.Single()).ToArray(),
                ct
            );
        }
        catch (ArgumentException)
        {
            return await AttentionAsync(attempt, "peer_input_missing", ct);
        }
        var retained = (
            await evidence.GetInputsAsync(
                [attempt.Request.Input, .. peerReferences],
                cancellationToken: ct
            )
        ).ToDictionary(input => input.Resource);
        NormalizationSetlistPreparedInput? primary;
        try
        {
            primary = await PrepareInputAsync(
                retained[attempt.Request.Input.Resource],
                primaryState?.Revision ?? 0,
                attempt.Request,
                ct
            );
        }
        catch (NotSupportedException)
        {
            return await AttentionAsync(attempt, "unsupported_input", ct);
        }
        if (primary == null)
        {
            return await AttentionAsync(attempt, "account_not_in_input", ct);
        }
        var inputs = new List<NormalizationSetlistPreparedInput> { primary };
        foreach (var peer in peers)
        {
            NormalizationSetlistPreparedInput? prepared;
            try
            {
                prepared = await PrepareInputAsync(
                    retained[peer.Contributor],
                    peer.Revision,
                    attempt.Request,
                    ct
                );
            }
            catch (NotSupportedException)
            {
                return await AttentionAsync(attempt, "unsupported_peer_input", ct);
            }
            if (prepared == null)
            {
                // A moving page cannot establish that its former member was deleted. Keep
                // its contribution and prior publication pending for explicit reconciliation.
                return await AttentionAsync(attempt, "peer_account_not_in_input", ct);
            }
            inputs.Add(prepared);
        }
        var preparation = new NormalizationSetlistPreparation(
            membership.Owner?.DesiredGeneration,
            membership
                .Contributions.Select(state => new NormalizationInputContributionRevision(
                    state.Contributor,
                    state.Revision
                ))
                .ToArray(),
            inputs.ToArray()
        );
        try
        {
            // Check each input's original observations before saving. Batch admission also
            // compares shared clocks across inputs; a newer clock cannot reconcile old facts.
            NormalizationSetlistReadSet.Contributions(preparation);
        }
        catch (NormalizationConflictException)
        {
            return await store.CompleteAsync(
                attempt,
                NormalizationSetlistProcessingDisposition.Stale,
                "incompatible_preparation",
                ct
            );
        }
        return await store.SavePreparationAsync(attempt.AttemptUuid, preparation, ct);
    }

    private async Task<NormalizationSetlistPreparedInput?> PrepareInputAsync(
        NormalizationInputRevision input,
        long contributionRevision,
        NormalizationSetlistProcessingRequest request,
        CancellationToken ct
    )
    {
        var prepared = await bindings.BindSetlistAccountGroupAsync(
            input,
            request.AccountIdentity,
            request.ExpectedArtistUuid,
            ct
        );
        return prepared.Count == 0
            ? null
            : new(input.Resource, contributionRevision, new(input.Uuid, input.Resource), prepared);
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

    private static bool IsSupported(NormalizationInputContributionState state) =>
        state.Resources.Count == 1
        && state.Contributor == state.Resources[0]
        && IsSupportedResource(state.Contributor)
        && state.DependencySubjectUuids.Count == 0
        && state.SourceIdentifiers.Count == 0
        && (state.SourceUuids?.Count ?? 0) == 0
        && (state.ImportedVenueUuids?.Count ?? 0) == 0
        && (state.CandidateUniverses?.Count ?? 0) == 0
        && (state.OwnerObservationSubjectUuids?.Count ?? 0) == 0;

    internal static bool IsSupportedResource(NormalizationResourceKey resource) =>
        (resource.Provider, resource.ResourceType)
            is
                ("jerrygarcia.com", "mirror_show_page")
                or
                ("setlist.fm", "setlist-page")
                or
                ("phantasytour.com", "show-list")
                or
                ("phantasytour.com", "show")
                or
                ("phish.net", "shows")
                or
                ("phish.net", "reviews")
                or
                ("phish.net", "ratings");

    private Task<NormalizationSetlistProcessingAttempt> AttentionAsync(
        NormalizationSetlistProcessingAttempt attempt,
        string reason,
        CancellationToken ct
    ) =>
        store.CompleteAsync(
            attempt,
            NormalizationSetlistProcessingDisposition.Unresolved,
            reason,
            ct
        );
}
