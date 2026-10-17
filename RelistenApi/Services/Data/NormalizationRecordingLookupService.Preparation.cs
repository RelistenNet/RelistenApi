using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationRecordingLookupService
{
    private async Task<NormalizationRecordingLookupPreparation> PrepareAsync(
        NormalizationRecordingLookupAttempt attempt,
        CancellationToken ct
    )
    {
        var owner = await work.GetSubjectAsync(Owner(attempt.Registration.LookupUuid), ct);
        return attempt.Request.Mode == NormalizationRecordingLookupMode.LegacyCatalog
            ? await PrepareLegacyAsync(attempt, owner?.DesiredGeneration, ct)
            : await PrepareRetainedAsync(attempt.Request, owner?.DesiredGeneration, ct);
    }

    private async Task<NormalizationRecordingLookupPreparation> PrepareLegacyAsync(
        NormalizationRecordingLookupAttempt attempt,
        long? expectedConfigurationGeneration,
        CancellationToken ct
    )
    {
        var request = attempt.Request;
        var reusable = await TryPreparePublishedLegacyAsync(
            attempt,
            expectedConfigurationGeneration,
            ct
        );
        if (reusable != null)
        {
            return reusable;
        }
        var sourceUuid = Guid.Parse(request.Resource.ProviderKey);
        for (var number = 1; ; number++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var snapshot = await legacy.RefreshRecordingAsync(sourceUuid, ct);
                if (snapshot == null)
                {
                    var probe = await bindings.ProbeRecordingUuidAsync(sourceUuid, ct);
                    if (!probe.Exists)
                    {
                        return new(null, probe, expectedConfigurationGeneration);
                    }
                    // A source arrived between the missing snapshot and the probe. Its
                    // presence is not metadata; restart the complete refresh and binding.
                    if (number == MaximumPreparationAttempts)
                    {
                        throw new NormalizationConflictException(
                            "The source kept changing during lookup preparation."
                        );
                    }
                    continue;
                }
                return await PrepareLegacySnapshotAsync(
                    snapshot,
                    request,
                    expectedConfigurationGeneration,
                    ct
                );
            }
            catch (Exception error)
                when (number < MaximumPreparationAttempts
                    && error is NormalizationStaleInputException or NormalizationConflictException
                )
            {
                // Nothing has been admitted. Retrying the whole preparation preserves the
                // producer's facts-and-clocks pairing instead of refreshing only one fence.
            }
        }
    }

    private async Task<NormalizationRecordingLookupPreparation?> TryPreparePublishedLegacyAsync(
        NormalizationRecordingLookupAttempt attempt,
        long? expectedConfigurationGeneration,
        CancellationToken ct
    )
    {
        var published = await store.GetCurrentAsync(attempt.Registration.LookupUuid, ct);
        var previous = published?.Preparation;
        if (previous?.FetchUuid == null)
        {
            return null;
        }
        var capture =
            await evidence.GetCaptureAsync(previous.FetchUuid.Value, ct)
            ?? throw new InvalidOperationException("Published lookup evidence is missing.");
        var current = await evidence.GetCurrentInputAsync(attempt.Request.Resource, ct);
        if (
            current?.Revision.Uuid != capture.Revision.Uuid
            || capture.Revision.CaptureVersion
                != NormalizationLegacyInputService.RefreshCaptureVersion
        )
        {
            return null;
        }
        try
        {
            // Equal input bytes alone cannot prove the catalog is unchanged. Reuse the
            // original receipt only after checked binding validates its original producer
            // clocks. This avoids refreshing unchanged shared evidence merely to settle
            // one registration, which would make peer registrations pending again.
            return await PrepareLegacySnapshotAsync(
                new(capture, previous.ProducerCatalogInputs!),
                attempt.Request,
                expectedConfigurationGeneration,
                ct
            );
        }
        catch (NormalizationConflictException)
        {
            return null;
        }
    }

    private async Task<NormalizationRecordingLookupPreparation> PrepareLegacySnapshotAsync(
        NormalizationLegacyRecordingSnapshot snapshot,
        NormalizationRecordingLookupRequest request,
        long? expectedConfigurationGeneration,
        CancellationToken ct
    )
    {
        var observation = DecodeRecording(snapshot.Capture.Revision, request.Resource, ct);
        var bound = await bindings.BindRefreshedRecordingAsync(
            snapshot,
            observation,
            request.ExpectedArtistUuid,
            ct
        );
        NormalizationRecordingPreparation? downstream = null;
        if (bound.Recording.Status == NormalizationCatalogBindingStatus.Bound)
        {
            var sourceUuid = bound.Recording.Value!.SourceUuid;
            var extractionOwner = NormalizationRecordingProcessor.Owner(sourceUuid);
            var extractionState = await work.GetSubjectAsync(extractionOwner, ct);
            var contributor = await work.GetInputContributionAsync(
                extractionOwner,
                NormalizationRecordingProcessor.Contributor(sourceUuid),
                ct
            );
            // Evidence reuse never reuses a downstream admission CAS. Earlier deliveries
            // can have advanced that owner's configuration or this contributor's revision.
            downstream = new(
                snapshot.Capture.Fetch.FetchUuid,
                snapshot.CatalogInputs,
                bound,
                extractionState?.DesiredGeneration,
                contributor?.Revision ?? 0
            );
        }
        return Preparation(
            bound,
            expectedConfigurationGeneration,
            snapshot.Capture.Fetch.FetchUuid,
            snapshot.CatalogInputs,
            downstream
        );
    }

    private async Task<NormalizationRecordingLookupPreparation> PrepareRetainedAsync(
        NormalizationRecordingLookupRequest request,
        long? expectedConfigurationGeneration,
        CancellationToken ct
    )
    {
        var bound = await BindRetainedAsync(request, ct);
        return Preparation(bound, expectedConfigurationGeneration);
    }

    private async Task<NormalizationRecordingBindingSnapshot> BindRetainedAsync(
        NormalizationRecordingLookupRequest request,
        CancellationToken ct
    )
    {
        var references = new[] { request.Input! }.Concat(request.SupportingInputs ?? []).ToArray();
        var retained = await evidence.GetInputsAsync(references, cancellationToken: ct);
        var input = retained.Single(value => value.Uuid == request.Input!.InputRevisionUuid);
        if (request.Resource.Provider == "local")
        {
            var root = retained.Single(value =>
                value.Uuid == request.SupportingInputs!.Single().InputRevisionUuid
            );
            // Bound parsing before the binder materializes all root accounts. The original
            // input remains unchanged; this read does not construct a replacement root map.
            var decodedRoot = NormalizationLocalRecordingSelection.DecodeRoot(
                root,
                decoder,
                cancellationToken: ct
            );
            return await bindings.BindLocalDirectoryWithRootAsync(
                root,
                input,
                decodedRoot,
                request.ExpectedArtistUuid,
                ct
            );
        }
        var recording = DecodeRecording(input, request.Resource, ct);
        return await bindings.BindRecordingAsync(recording, request.ExpectedArtistUuid, ct);
    }

    private static NormalizationRecordingLookupPreparation Preparation(
        NormalizationRecordingBindingSnapshot binding,
        long? expectedConfigurationGeneration,
        Guid? fetchUuid = null,
        IReadOnlyList<NormalizationCatalogDependency>? producerClocks = null,
        NormalizationRecordingPreparation? legacyPreparation = null
    )
    {
        var bound = binding.Recording.Status == NormalizationCatalogBindingStatus.Bound;
        return new(
            binding,
            null,
            expectedConfigurationGeneration,
            fetchUuid,
            producerClocks,
            legacyPreparation,
            bound ? Guid.NewGuid() : null,
            legacyPreparation == null ? null : NormalizationRecordingProcessor.Version
        );
    }

    private ProviderRecordingObservation DecodeRecording(
        NormalizationInputRevision input,
        NormalizationResourceKey resource,
        CancellationToken ct
    )
    {
        var decoded = decoder.Decode(input, ct);
        if (decoded.Recordings.Count != 1 || decoded.Recordings[0].Origin.Item != resource)
        {
            throw new NotSupportedException(
                "The retained input must contain exactly one recording for its requested resource."
            );
        }
        return decoded.Recordings[0];
    }
}
