using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

/// <summary>
/// Delivers a committed recording into song discovery through the existing domain processors.
/// Each call advances one durable phase. This coordinator supplies no matching policy and owns
/// no accepted music. Import enrollment and scheduling are separate responsibilities.
/// </summary>
public sealed class RecordingMusicDeliveryService(
    RecordingMusicDeliveryStore store,
    NormalizationRecordingLookupStore lookups,
    NormalizationRecordingLookupDispatcher dispatcher,
    NormalizationImportedCatalogService imported,
    NormalizationRepertoireSourceService sources,
    NormalizationRepertoireResolutionProcessor repertoire,
    NormalizationRecordingContentMaterializer contents,
    NormalizationWorkService work
)
{
    public const string Version =
        "recording-music-delivery-v1/"
        + NormalizationRecordingProcessor.Version
        + "/"
        + NormalizationRepertoireResolutionProcessor.Version
        + "/"
        + NormalizationRecordingContentMaterializer.Version;

    public static NormalizationSubjectKey Owner(Guid handoffUuid) =>
        new("extractions", "recording_music_delivery", handoffUuid);

    public Task<RecordingMusicDelivery?> GetAsync(
        Guid handoffUuid,
        CancellationToken ct = default
    ) => store.GetAsync(handoffUuid, ct);

    public Task<RecordingMusicDeliveryPage> GetPendingAsync(
        int limit = 100,
        Guid? afterHandoffUuid = null,
        CancellationToken ct = default
    ) => store.GetPendingAsync(limit, afterHandoffUuid, ct);

    /// <summary>
    /// Advances a committed handoff without refreshing its original evidence. Reuse the handoff
    /// UUID after cancellation or an exception. Pending work remains listed even after extraction
    /// acknowledgement. A stale receipt requires a fresh checked lookup, not a changed replay.
    /// </summary>
    public async Task<RecordingMusicDelivery> ProcessAsync(
        Guid handoffUuid,
        CancellationToken ct = default
    )
    {
        if (handoffUuid == Guid.Empty)
        {
            throw new ArgumentException(
                "A committed recording lookup handoff is required.",
                nameof(handoffUuid)
            );
        }
        ct.ThrowIfCancellationRequested();
        var handoff =
            await lookups.GetHandoffAsync(handoffUuid, ct)
            ?? throw new ArgumentException(
                "Unknown recording lookup handoff.",
                nameof(handoffUuid)
            );
        var lookup = (await lookups.GetAttemptAsync(handoff.LookupAttemptUuid, ct))!;
        var binding = lookup.Preparation!.Binding!;
        var recording = binding.Recording.Value!;
        var delivery = await store.GetOrCreateAsync(
            handoffUuid,
            recording.SourceUuid,
            recording.ArtistUuid,
            Version,
            ct
        );
        if (delivery.Result != null)
        {
            return delivery;
        }
        if (delivery.ProcessingVersion != Version)
        {
            throw new NormalizationConflictException(
                "The unfinished music delivery requires its original processor version. Resume that version or create a new checked lookup."
            );
        }

        switch (delivery.Phase)
        {
            case RecordingMusicDeliveryPhase.Admission:
                return await AdmitAsync(delivery, binding, ct);
            case RecordingMusicDeliveryPhase.Extraction:
                return await ExtractAsync(delivery, ct);
            case RecordingMusicDeliveryPhase.DictionaryCapture:
                return await CaptureDictionaryAsync(delivery, ct);
            case RecordingMusicDeliveryPhase.DictionaryResolution:
                return await ResolveDictionaryAsync(delivery, ct);
            case RecordingMusicDeliveryPhase.RecordingResolution:
                return await ResolveRecordingAsync(delivery, handoff.ProcessingAttemptUuid, ct);
            case RecordingMusicDeliveryPhase.Contents:
                return await MaterializeContentsAsync(delivery, ct);
            case RecordingMusicDeliveryPhase.Completion:
                return await CompleteAsync(delivery, ct);
            default:
                throw new InvalidOperationException(
                    "Music delivery has an unsupported saved phase."
                );
        }
    }

    private async Task<RecordingMusicDelivery> AdmitAsync(
        RecordingMusicDelivery delivery,
        NormalizationRecordingBindingSnapshot binding,
        CancellationToken ct
    )
    {
        // Both the saved children and this recoverable owner precede the dispatcher's
        // extraction acknowledgement. No matching runs inside the catalog transaction.
        try
        {
            await work.BeginWorkAsync(
                delivery.WorkUuid,
                Owner(delivery.HandoffUuid),
                Version,
                binding.Inputs.Select(input => input.Resource).Distinct().ToArray(),
                [],
                cancellationToken: ct,
                expectedInputRevisions: binding.Inputs
            );
        }
        catch (NormalizationConflictException)
        {
            // Admission can reject old source heads before a Work run exists.
            // Preserve that outcome without dispatching or acknowledging extraction.
            return await store.AdvanceAsync(
                delivery,
                RecordingMusicDeliveryPhase.Finished,
                ct,
                result: new(RecordingMusicDeliveryDisposition.Stale, "delivery_admission_changed")
            );
        }

        return await store.AdvanceAsync(delivery, RecordingMusicDeliveryPhase.Extraction, ct);
    }

    private async Task<RecordingMusicDelivery> ExtractAsync(
        RecordingMusicDelivery delivery,
        CancellationToken ct
    )
    {
        var extraction = (
            await dispatcher.DispatchAsync(delivery.HandoffUuid, ct)
        ).ProcessingResult;
        if (extraction.Disposition != NormalizationRecordingProcessingDisposition.Published)
        {
            return await FinishAsync(
                delivery,
                new(
                    extraction.Disposition == NormalizationRecordingProcessingDisposition.Stale
                        ? RecordingMusicDeliveryDisposition.Stale
                        : RecordingMusicDeliveryDisposition.Unresolved,
                    extraction.Reason
                ),
                ct
            );
        }
        return await store.AdvanceAsync(
            delivery,
            RecordingMusicDeliveryPhase.DictionaryCapture,
            ct
        );
    }

    private async Task<RecordingMusicDelivery> CaptureDictionaryAsync(
        RecordingMusicDelivery delivery,
        CancellationToken ct
    )
    {
        try
        {
            return await store.CaptureDictionaryAsync(
                delivery,
                token => SelectDictionaryCaptureAsync(delivery.ArtistUuid, token),
                ct
            );
        }
        catch (NormalizationConflictException)
        {
            return await FinishAsync(
                delivery,
                new(RecordingMusicDeliveryDisposition.Stale, "dictionary_capture_changed"),
                ct
            );
        }
    }

    private async Task<Guid> SelectDictionaryCaptureAsync(Guid artistUuid, CancellationToken ct)
    {
        var fetch = await store.ReadCurrentDictionaryFetchAsync(artistUuid, ct);
        if (fetch != null)
        {
            try
            {
                await sources.ReadAsync(
                    new(new ImportedRepertoireResolutionSource(fetch.Value), artistUuid),
                    ct
                );
            }
            catch (NormalizationConflictException)
            {
                // Reusing equal bytes is insufficient when the original catalog clock changed.
                // A new complete snapshot must come from the existing catalog capture service.
                fetch = null;
            }
        }
        if (fetch == null)
        {
            var snapshot = await imported.RefreshArtistRepertoireAsync(artistUuid, ct);
            if (snapshot == null)
            {
                throw new NormalizationConflictException("The dictionary artist no longer exists.");
            }
            fetch = snapshot.Capture.Fetch.FetchUuid;
        }
        return fetch.Value;
    }

    private async Task<RecordingMusicDelivery> ResolveDictionaryAsync(
        RecordingMusicDelivery delivery,
        CancellationToken ct
    )
    {
        var attempt = await repertoire.ResolveAsync(
            delivery.DictionaryAttemptUuid,
            new(
                new ImportedRepertoireResolutionSource(delivery.DictionaryFetchUuid!.Value),
                delivery.ArtistUuid
            ),
            ct
        );
        if (attempt.Result is null or { Disposition: RepertoireResolutionDisposition.Pending })
        {
            return delivery;
        }
        if (attempt.Result.Disposition == RepertoireResolutionDisposition.Stale)
        {
            return await FinishAsync(
                delivery,
                new(RecordingMusicDeliveryDisposition.Stale, "dictionary_" + attempt.Result.Reason),
                ct
            );
        }
        return await store.AdvanceAsync(
            delivery,
            RecordingMusicDeliveryPhase.RecordingResolution,
            ct
        );
    }

    private async Task<RecordingMusicDelivery> ResolveRecordingAsync(
        RecordingMusicDelivery delivery,
        Guid extractionAttemptUuid,
        CancellationToken ct
    )
    {
        var attempt = await repertoire.ResolveAsync(
            delivery.RepertoireAttemptUuid,
            new(
                new RecordingRepertoireResolutionSource(extractionAttemptUuid),
                delivery.ArtistUuid
            ),
            ct
        );
        if (attempt.Result is null or { Disposition: RepertoireResolutionDisposition.Pending })
        {
            return delivery;
        }
        if (attempt.Result.Disposition == RepertoireResolutionDisposition.Stale)
        {
            return await FinishAsync(
                delivery,
                new(RecordingMusicDeliveryDisposition.Stale, "recording_" + attempt.Result.Reason),
                ct
            );
        }
        return await store.AdvanceAsync(delivery, RecordingMusicDeliveryPhase.Contents, ct);
    }

    private async Task<RecordingMusicDelivery> MaterializeContentsAsync(
        RecordingMusicDelivery delivery,
        CancellationToken ct
    )
    {
        var attempt = await contents.ProcessAsync(
            delivery.ContentAttemptUuid,
            new(delivery.RepertoireAttemptUuid),
            ct
        );
        if (attempt.Result is null or { Disposition: MusicMaterializationDisposition.Pending })
        {
            return delivery;
        }
        if (attempt.Result.Disposition == MusicMaterializationDisposition.Stale)
        {
            return await FinishAsync(
                delivery,
                new(RecordingMusicDeliveryDisposition.Stale, "contents_" + attempt.Result.Reason),
                ct
            );
        }
        return await store.AdvanceAsync(delivery, RecordingMusicDeliveryPhase.Completion, ct);
    }

    private async Task<RecordingMusicDelivery> CompleteAsync(
        RecordingMusicDelivery delivery,
        CancellationToken ct
    )
    {
        var result = (await contents.GetAsync(delivery.ContentAttemptUuid, ct))!.Result!;
        var disposition =
            result.Review > 0 ? RecordingMusicDeliveryDisposition.Review
            : result.Unresolved > 0 ? RecordingMusicDeliveryDisposition.Unresolved
            : RecordingMusicDeliveryDisposition.Completed;
        return await FinishAsync(
            delivery,
            new(
                disposition,
                disposition == RecordingMusicDeliveryDisposition.Completed
                    ? "contents_published"
                    : "contents_require_attention",
                result.Accepted,
                result.Review,
                result.Unresolved
            ),
            ct
        );
    }

    private async Task<RecordingMusicDelivery> FinishAsync(
        RecordingMusicDelivery delivery,
        RecordingMusicDeliveryResult result,
        CancellationToken ct
    )
    {
        // Domain receipts already own publication. This watermark only closes delivery of
        // the handoff's original inputs and can be replayed after a crash before the final row.
        var receipt = await work.TryPublishAsync(
            delivery.WorkUuid,
            (_, _, _) => Task.CompletedTask,
            ct
        );
        if (receipt.Status == NormalizationPublicationStatus.Stale)
        {
            result = new(RecordingMusicDeliveryDisposition.Stale, "delivery_input_changed");
        }
        return await store.AdvanceAsync(
            delivery,
            RecordingMusicDeliveryPhase.Finished,
            ct,
            result: result
        );
    }
}
