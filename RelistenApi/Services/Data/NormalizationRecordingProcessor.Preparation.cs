using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationRecordingProcessor
{
    private async Task<NormalizationRecordingAttempt> PrepareAsync(
        NormalizationRecordingAttempt attempt,
        NormalizationRecordingLookupHandoff? handoff,
        RecordingInputSet inputSet,
        CancellationToken ct
    )
    {
        if (attempt.Preparation != null)
        {
            return attempt;
        }
        try
        {
            var memberships = await work.GetInputContributionsAsync(
                Owner(attempt.SourceUuid),
                cancellationToken: ct
            );
            if (
                memberships.UnmanagedBaseline != null
                || memberships.Contributions.Any(state =>
                    HasFacts(state) && !IsSupported(state, attempt.SourceUuid)
                )
            )
            {
                return await AttentionAsync(attempt, "unsupported_owner_inputs", ct);
            }
            var previous = await proposals.GetAsync(attempt.SourceUuid, ct);
            NormalizationRecordingPreparedInput[] previousInputs =
                previous == null ? [] : NormalizationRecordingReadSet.Inputs(previous.Preparation);
            var primaryKey =
                handoff?.Mode == NormalizationRecordingLookupMode.RetainedProvider
                    ? (await RequireLookupAttemptAsync(handoff, ct)).Request.Resource
                    : Contributor(attempt.SourceUuid);
            var primaryRevision =
                memberships
                    .Contributions.SingleOrDefault(state => state.Contributor == primaryKey)
                    ?.Revision
                ?? 0;
            var primary =
                handoff == null
                    ? await PrepareLegacyInputAsync(
                        attempt.SourceUuid,
                        primaryRevision,
                        previousInputs,
                        ct
                    )
                    : await PrepareHandoffInputAsync(handoff, primaryRevision, ct);
            if (primary == null)
            {
                return await proposals.CompleteAsync(
                    attempt,
                    NormalizationRecordingProcessingDisposition.Missing,
                    "recording_missing",
                    ct
                );
            }
            if (primary.Binding.Recording.Status != NormalizationCatalogBindingStatus.Bound)
            {
                return await AttentionAsync(attempt, primary.Binding.Recording.Reason, ct);
            }
            var peers = await PreparePeerAccountsAsync(
                attempt.SourceUuid,
                primary,
                memberships.Contributions,
                previousInputs,
                inputSet,
                ct
            );
            if (peers == null)
            {
                return await AttentionAsync(attempt, "peer_recording_unresolved", ct);
            }
            var originalLegacy =
                handoff?.Mode == NormalizationRecordingLookupMode.LegacyCatalog
                    ? handoff.LegacyPreparation
                    : null;
            var preparation = CompletePreparation(primary, peers, memberships, originalLegacy);
            return await proposals.SavePreparationAsync(attempt.AttemptUuid, preparation, ct);
        }
        catch (Exception error)
            when (error is NormalizationStaleInputException or NormalizationConflictException)
        {
            // A refresh loser resumes a concurrent saved winner. Its discarded candidate
            // facts cannot terminate another caller's immutable preparation.
            var current = await proposals.GetAttemptAsync(attempt.AttemptUuid, ct);
            if (current!.Preparation != null || current.Result != null)
            {
                return current;
            }
            throw;
        }
    }

    private async Task<NormalizationRecordingPreparedInput[]?> PreparePeerAccountsAsync(
        Guid sourceUuid,
        NormalizationRecordingPreparedInput primary,
        IReadOnlyList<NormalizationInputContributionState> memberships,
        IReadOnlyList<NormalizationRecordingPreparedInput> previous,
        RecordingInputSet inputSet,
        CancellationToken ct
    )
    {
        var peers = memberships
            .Where(state => HasFacts(state) && state.Contributor != primary.Contributor)
            .ToArray();
        var additional = new List<NormalizationRecordingPreparedInput>();
        var legacyState = peers.SingleOrDefault(state =>
            state.Contributor == Contributor(sourceUuid)
        );
        if (legacyState != null)
        {
            var legacyPeer = await PrepareLegacyInputAsync(
                sourceUuid,
                legacyState.Revision,
                previous,
                ct
            );
            if (!SameRecording(primary, legacyPeer))
            {
                return null;
            }
            additional.Add(legacyPeer!);
        }
        var providerStates = peers.Where(state => state != legacyState).ToArray();
        var resources = providerStates.SelectMany(state => state.Resources).Distinct().ToArray();
        IReadOnlyList<NormalizationBindingInputReference> references =
            resources.Length == 0
                ? []
                : await evidence.GetCurrentInputReferencesAsync(resources, ct);
        var completeReferences = primary
            .Binding.Inputs.Concat(additional.SelectMany(input => input.Binding.Inputs))
            .Concat(references)
            .Distinct()
            .ToArray();
        var retained = await ReadInputsAsync(completeReferences, inputSet, ct);
        foreach (var state in providerStates)
        {
            var peer = await PrepareProviderInputAsync(
                state,
                primary.Binding.Recording.Value!.ArtistUuid,
                references,
                retained,
                inputSet.Roots,
                ct
            );
            if (!SameRecording(primary, peer))
            {
                return null;
            }
            additional.Add(peer);
        }
        return additional
            .OrderBy(input => input.Contributor.Provider, StringComparer.Ordinal)
            .ThenBy(input => input.Contributor.ResourceType, StringComparer.Ordinal)
            .ThenBy(input => input.Contributor.ProviderKey, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool SameRecording(
        NormalizationRecordingPreparedInput primary,
        NormalizationRecordingPreparedInput? peer
    ) =>
        peer?.Binding.Recording.Value is { } recording
        && peer.Binding.Recording.Status == NormalizationCatalogBindingStatus.Bound
        && recording.SourceUuid == primary.Binding.Recording.Value!.SourceUuid
        && recording.ArtistUuid == primary.Binding.Recording.Value.ArtistUuid;

    private static NormalizationRecordingPreparation CompletePreparation(
        NormalizationRecordingPreparedInput primary,
        IReadOnlyList<NormalizationRecordingPreparedInput> peers,
        NormalizationInputContributionsSnapshot memberships,
        NormalizationRecordingPreparation? originalLegacy
    ) =>
        new(
            primary.FetchUuid,
            primary.ProducerCatalogInputs ?? [],
            primary.Binding,
            originalLegacy == null
                ? memberships.Owner?.DesiredGeneration
                : originalLegacy.ExpectedConfigurationGeneration,
            primary.ExpectedContributionRevision,
            primary.FetchUuid == null ? primary.Contributor : null,
            peers.Count == 0 ? null : peers,
            memberships
                .Contributions.Select(state => new NormalizationInputContributionRevision(
                    state.Contributor,
                    state.Revision
                ))
                .ToArray()
        );

    private async Task<NormalizationRecordingPreparedInput> PrepareHandoffInputAsync(
        NormalizationRecordingLookupHandoff handoff,
        long currentContributionRevision,
        CancellationToken ct
    )
    {
        var lookup = await RequireLookupAttemptAsync(handoff, ct);
        var prepared = lookup.Preparation!;
        if (handoff.Mode == NormalizationRecordingLookupMode.LegacyCatalog)
        {
            var legacyInput = handoff.LegacyPreparation!;
            return new(
                Contributor(handoff.SourceUuid),
                legacyInput.ExpectedContributionRevision,
                legacyInput.Binding,
                legacyInput.FetchUuid,
                legacyInput.ProducerCatalogInputs
            );
        }
        return new(lookup.Request.Resource, currentContributionRevision, prepared.Binding!);
    }

    private async Task<NormalizationRecordingLookupAttempt> RequireLookupAttemptAsync(
        NormalizationRecordingLookupHandoff handoff,
        CancellationToken ct
    ) =>
        await lookups.GetAttemptAsync(handoff.LookupAttemptUuid, ct)
        ?? throw new InvalidOperationException("Committed lookup attempt is missing.");

    private async Task<NormalizationRecordingPreparedInput?> PrepareLegacyInputAsync(
        Guid sourceUuid,
        long contributionRevision,
        IReadOnlyList<NormalizationRecordingPreparedInput> previous,
        CancellationToken ct
    )
    {
        var key = Contributor(sourceUuid);
        var old = previous.SingleOrDefault(input => input.Contributor == key);
        if (old?.FetchUuid is { } fetchUuid)
        {
            var capture =
                await evidence.GetCaptureAsync(fetchUuid, ct)
                ?? throw new InvalidOperationException("Published extraction evidence is missing.");
            var current = await evidence.GetCurrentInputAsync(capture.Revision.Resource, ct);
            if (
                current?.Revision.Uuid == capture.Revision.Uuid
                && capture.Revision.CaptureVersion
                    == NormalizationLegacyInputService.RefreshCaptureVersion
            )
            {
                try
                {
                    var bound = await BindLegacyInputAsync(
                        new(capture, old.ProducerCatalogInputs!),
                        ct
                    );
                    return new(
                        key,
                        contributionRevision,
                        bound,
                        fetchUuid,
                        old.ProducerCatalogInputs
                    );
                }
                catch (NormalizationConflictException)
                {
                    // The old bytes no longer prove current catalog facts. Refresh the whole
                    // snapshot, including its producer clocks, instead of rebinding old prose.
                }
            }
        }
        var snapshot = await legacy.RefreshRecordingAsync(sourceUuid, ct);
        if (snapshot == null)
        {
            return null;
        }
        return new(
            key,
            contributionRevision,
            await BindLegacyInputAsync(snapshot, ct),
            snapshot.Capture.Fetch.FetchUuid,
            snapshot.CatalogInputs
        );
    }

    private async Task<NormalizationRecordingBindingSnapshot> BindLegacyInputAsync(
        NormalizationLegacyRecordingSnapshot snapshot,
        CancellationToken ct
    )
    {
        var decoded = decoder.Decode(snapshot.Capture.Revision, ct);
        if (decoded.Recordings.Count != 1)
        {
            throw new ArgumentException("A legacy recording snapshot must contain one recording.");
        }
        return await bindings.BindRefreshedRecordingAsync(
            snapshot,
            decoded.Recordings[0],
            cancellationToken: ct
        );
    }

    private async Task<NormalizationRecordingPreparedInput> PrepareProviderInputAsync(
        NormalizationInputContributionState state,
        Guid artistUuid,
        IReadOnlyList<NormalizationBindingInputReference> references,
        IReadOnlyDictionary<Guid, NormalizationInputRevision> retained,
        Dictionary<Guid, ProviderDecodeResult> roots,
        CancellationToken ct
    )
    {
        var reference = references.Single(input => input.Resource == state.Contributor);
        var input = retained[reference.InputRevisionUuid];
        if (input.Resource.Provider == "local")
        {
            var rootReference = references.Single(value =>
                value.Resource
                == NormalizationProviderResources.LocalRootForDirectory(input.Resource)
            );
            var root = retained[rootReference.InputRevisionUuid];
            var decodedRoot = DecodeRoot(root, roots, ct);
            return new(
                state.Contributor,
                state.Revision,
                await bindings.BindLocalDirectoryWithRootAsync(
                    root,
                    input,
                    decodedRoot,
                    artistUuid,
                    ct
                )
            );
        }
        var decoded = decoder.Decode(input, ct);
        if (decoded.Recordings.Count != 1 || decoded.Recordings[0].Origin.Item != input.Resource)
        {
            throw new ArgumentException(
                "A provider input must contain its exact single recording account."
            );
        }
        return new(
            state.Contributor,
            state.Revision,
            await bindings.BindRecordingAsync(decoded.Recordings[0], artistUuid, ct)
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

    private static bool IsSupported(NormalizationInputContributionState state, Guid sourceUuid)
    {
        if (
            state.DependencySubjectUuids.Count != 0
            || (state.ImportedVenueUuids?.Count ?? 0) != 0
            || (state.CandidateUniverses?.Count ?? 0) != 0
            || (state.OwnerObservationSubjectUuids?.Count ?? 0) != 0
        )
        {
            return false;
        }
        if (state.Contributor.Provider == "local")
        {
            NormalizationResourceKey root;
            try
            {
                root = NormalizationProviderResources.LocalRootForDirectory(state.Contributor);
            }
            catch (ArgumentException)
            {
                return false;
            }
            return state.UsesMappingRegistry
                && state.Resources.Count == 2
                && state.Resources.Contains(state.Contributor)
                && state.Resources.Contains(root);
        }
        if (state.Resources.Count != 1 || state.UsesMappingRegistry)
        {
            return false;
        }
        if (state.Contributor == Contributor(sourceUuid))
        {
            return state.Resources[0]
                == new NormalizationResourceKey(
                    "legacy_catalog",
                    "recording",
                    sourceUuid.ToString("D")
                );
        }
        return state.Contributor == state.Resources[0]
            && (state.Contributor.Provider, state.Contributor.ResourceType)
                is
                    ("archive.org", "item_metadata")
                    or
                    ("phish.in", "show");
    }

    private Task<NormalizationRecordingAttempt> AttentionAsync(
        NormalizationRecordingAttempt attempt,
        string reason,
        CancellationToken ct
    ) =>
        proposals.CompleteAsync(
            attempt,
            NormalizationRecordingProcessingDisposition.Unresolved,
            reason,
            ct
        );
}
