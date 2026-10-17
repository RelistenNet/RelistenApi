using System;
using System.Threading;
using System.Threading.Tasks;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

/// <summary>Acknowledged delivery carries its terminal processing receipt, not accepted normalization.</summary>
public sealed record NormalizationRecordingLookupDispatchResult(
    Guid HandoffUuid,
    NormalizationRecordingProcessingResult ProcessingResult
);

/// <summary>
/// Delivers committed lookup handoffs at least once using their saved processing UUIDs.
/// A failure leaves delivery pending; the extraction attempt supplies crash recovery.
/// </summary>
public sealed class NormalizationRecordingLookupDispatcher(
    NormalizationRecordingLookupStore store,
    NormalizationRecordingProcessor processor
)
{
    /// <summary>
    /// Dispatches a committed handoff. Retry the same handoff UUID after cancellation or
    /// failure to resume its saved processing attempt; a completed delivery returns its
    /// terminal receipt with Replayed=true. Inspect ProcessingResult.Disposition separately
    /// from delivery completion. Both legacy and retained provider delivery preserve their
    /// original initiating evidence while processing the complete extraction-owner input set.
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
        var result =
            handoff.Mode == NormalizationRecordingLookupMode.RetainedProvider
                ? await processor.ProcessRetainedAsync(
                    handoff.ProcessingAttemptUuid,
                    handoffUuid,
                    cancellationToken
                )
                : await processor.ProcessLegacyHandoffAsync(
                    handoff.ProcessingAttemptUuid,
                    handoffUuid,
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

    private static NormalizationRecordingLookupDispatchResult Completed(
        NormalizationRecordingLookupHandoff handoff,
        NormalizationRecordingProcessingResult result
    ) => new(handoff.HandoffUuid, result);
}
