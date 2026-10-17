using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

/// <summary>Delivery state, independent of whether downstream processing published an extraction.</summary>
public enum NormalizationRecordingLookupDispatchDisposition
{
    /// <summary>A terminal downstream receipt was acknowledged; it may report Stale or Unresolved.</summary>
    Completed,

    /// <summary>No provider consumer is available; the handoff remains pending.</summary>
    AwaitingProviderConsumer,
}

/// <summary>
/// Completed carries the terminal processing receipt, not accepted normalization.
/// AwaitingProviderConsumer carries no receipt and does not acknowledge delivery.
/// </summary>
public sealed record NormalizationRecordingLookupDispatchResult(
    Guid HandoffUuid,
    NormalizationRecordingLookupDispatchDisposition Disposition,
    NormalizationRecordingProcessingResult? ProcessingResult = null
);

/// <summary>
/// Delivers committed lookup handoffs at least once using their saved processing UUIDs.
/// A failure leaves delivery pending; the extraction attempt supplies crash recovery.
/// </summary>
public sealed class NormalizationRecordingLookupDispatcher(
    NormalizationRecordingLookupStore store,
    NormalizationRecordingProposalService proposals,
    NormalizationRecordingProcessor processor
)
{
    /// <summary>
    /// Dispatches a committed handoff. Retry the same handoff UUID after cancellation or
    /// failure to resume its saved processing attempt; a completed delivery returns its
    /// terminal receipt with Replayed=true. Inspect ProcessingResult.Disposition separately
    /// from delivery completion. Retained provider handoffs remain pending for their consumer.
    /// </summary>
    public async Task<NormalizationRecordingLookupDispatchResult> DispatchAsync(
        Guid handoffUuid,
        CancellationToken cancellationToken = default
    )
    {
        var handoff =
            await store.GetHandoffAsync(handoffUuid, cancellationToken)
            ?? throw new ArgumentException(
                "A committed lookup handoff is required.",
                nameof(handoffUuid)
            );
        if (handoff.CompletedResult != null)
        {
            return Completed(handoff, handoff.CompletedResult with { Replayed = true });
        }
        if (handoff.Mode == NormalizationRecordingLookupMode.RetainedProvider)
        {
            // Provider evidence cannot pass the legacy processor's preparation contract.
            // Keep the durable handoff pending for its eventual provider consumer.
            return new(
                handoffUuid,
                NormalizationRecordingLookupDispatchDisposition.AwaitingProviderConsumer
            );
        }

        await PrepareLegacyAttemptAsync(handoff, cancellationToken);
        var result = await processor.ProcessAsync(
            handoff.ProcessingAttemptUuid,
            handoff.SourceUuid,
            cancellationToken
        );

        // The processing attempt commits first. If delivery stops before this acknowledgement,
        // retrying its stable UUID returns the terminal receipt without another refresh.
        await store.AcknowledgeHandoffAsync(
            handoffUuid,
            result with
            {
                Replayed = false,
            },
            cancellationToken
        );
        return Completed(handoff, result);
    }

    private async Task PrepareLegacyAttemptAsync(
        NormalizationRecordingLookupHandoff handoff,
        CancellationToken cancellationToken
    )
    {
        await proposals.GetOrCreateAttemptAsync(
            handoff.ProcessingAttemptUuid,
            handoff.SourceUuid,
            handoff.ProcessingVersion!,
            cancellationToken
        );
        var prepared = await proposals.SavePreparationAsync(
            handoff.ProcessingAttemptUuid,
            handoff.LegacyPreparation!,
            cancellationToken
        );
        if (
            !JsonElement.DeepEquals(
                JsonSerializer.SerializeToElement(prepared.Preparation),
                JsonSerializer.SerializeToElement(handoff.LegacyPreparation)
            )
        )
        {
            throw new NormalizationConflictException(
                "The processing attempt already has another preparation."
            );
        }
    }

    private static NormalizationRecordingLookupDispatchResult Completed(
        NormalizationRecordingLookupHandoff handoff,
        NormalizationRecordingProcessingResult result
    ) =>
        new(handoff.HandoffUuid, NormalizationRecordingLookupDispatchDisposition.Completed, result);
}
