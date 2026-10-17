using System;
using System.Threading;
using System.Threading.Tasks;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

/// <summary>
/// Publishes evidenced recording extraction over every supported contributor of one source.
/// Preparation freezes the incoming account, peer accounts and their original producer clocks
/// before admission. It changes neither accepted identities nor playback data.
/// </summary>
public sealed partial class NormalizationRecordingProcessor(
    NormalizationLegacyInputService legacy,
    NormalizationEvidenceService evidence,
    NormalizationCatalogBindingService bindings,
    NormalizationProviderDecoder decoder,
    NormalizationRecordingExtractor extractor,
    NormalizationWorkService work,
    NormalizationRecordingProposalService proposals,
    NormalizationRecordingLookupStore lookups
)
{
    public const string Version =
        "catalog-recording-processing-v4/"
        + NormalizationLegacyInputService.RefreshCaptureVersion
        + "/"
        + NormalizationProviderDecoder.Version
        + "/"
        + NormalizationRecordingExtractor.Version;

    /// <summary>
    /// Processes current catalog facts with the complete extraction-owner input set. Reuse an
    /// attempt UUID for recovery and terminal replay; use a new UUID after a terminal outcome.
    /// Published means proposals were saved, not that identities were accepted. No paid calls occur.
    /// </summary>
    public Task<NormalizationRecordingProcessingResult> ProcessAsync(
        Guid attemptUuid,
        Guid sourceUuid,
        CancellationToken cancellationToken = default
    ) => StartAsync(attemptUuid, sourceUuid, null, cancellationToken);

    /// <summary>
    /// Consumes a committed provider handoff's exact input and binding. Peer inputs are prepared
    /// separately; the initiating provider account is never replaced with a legacy refresh.
    /// The first attempt header freezes the handoff and consumer version before preparation.
    /// </summary>
    public Task<NormalizationRecordingProcessingResult> ProcessRetainedAsync(
        Guid attemptUuid,
        Guid handoffUuid,
        CancellationToken cancellationToken = default
    ) =>
        ProcessHandoffAsync(
            attemptUuid,
            handoffUuid,
            NormalizationRecordingLookupMode.RetainedProvider,
            cancellationToken
        );

    /// <summary>
    /// Consumes the legacy handoff's original capture, catalog clocks and downstream admission
    /// CAS. An earlier delivery can make those CAS values stale; this method never replaces them.
    /// </summary>
    public Task<NormalizationRecordingProcessingResult> ProcessLegacyHandoffAsync(
        Guid attemptUuid,
        Guid handoffUuid,
        CancellationToken cancellationToken = default
    ) =>
        ProcessHandoffAsync(
            attemptUuid,
            handoffUuid,
            NormalizationRecordingLookupMode.LegacyCatalog,
            cancellationToken
        );

    private async Task<NormalizationRecordingProcessingResult> ProcessHandoffAsync(
        Guid attemptUuid,
        Guid handoffUuid,
        NormalizationRecordingLookupMode mode,
        CancellationToken ct
    )
    {
        var handoff =
            await lookups.GetHandoffAsync(handoffUuid, ct)
            ?? throw new ArgumentException(
                "A committed lookup handoff is required.",
                nameof(handoffUuid)
            );
        if (handoff.Mode != mode || handoff.ProcessingAttemptUuid != attemptUuid)
        {
            throw new NormalizationConflictException(
                "The handoff does not identify this processing request."
            );
        }
        return await StartAsync(attemptUuid, handoff.SourceUuid, handoff, ct);
    }

    private async Task<NormalizationRecordingProcessingResult> StartAsync(
        Guid attemptUuid,
        Guid sourceUuid,
        NormalizationRecordingLookupHandoff? handoff,
        CancellationToken ct
    )
    {
        var existing = await proposals.GetAttemptAsync(attemptUuid, ct);
        if (existing != null)
        {
            // Legacy deliveries installed single-input preparations before processing v3.
            // Their old uninitiated terminal receipts remain exact historical replays.
            var oldLegacy =
                handoff?.Mode == NormalizationRecordingLookupMode.LegacyCatalog
                && existing.InitiatingHandoffUuid == null
                && existing.ProcessingVersion != Version;
            if (
                existing.SourceUuid != sourceUuid
                || existing.InitiatingHandoffUuid != handoff?.HandoffUuid && !oldLegacy
            )
            {
                throw new NormalizationConflictException(
                    "Attempt UUID already belongs to another recording request."
                );
            }
            if (existing.Result != null)
            {
                return existing.Result with { Replayed = true };
            }
            if (existing.ProcessingVersion != Version)
            {
                return await FinishUnsupportedVersionAsync(existing, ct);
            }
        }
        var requestedVersion =
            handoff?.Mode == NormalizationRecordingLookupMode.LegacyCatalog
                ? handoff.ProcessingVersion!
                : Version;
        var attempt =
            existing
            ?? await proposals.GetOrCreateAttemptAsync(
                attemptUuid,
                sourceUuid,
                requestedVersion,
                ct,
                initiatingHandoffUuid: handoff?.HandoffUuid
            );
        if (attempt.Result != null)
        {
            return attempt.Result with { Replayed = true };
        }
        if (attempt.ProcessingVersion != Version)
        {
            return await FinishUnsupportedVersionAsync(attempt, ct);
        }
        var inputSet = new RecordingInputSet();
        attempt = await PrepareAsync(attempt, handoff, inputSet, ct);
        if (attempt.Result != null)
        {
            return attempt.Result;
        }
        return await ProcessPreparedAsync(attempt, inputSet, ct);
    }

    private async Task<NormalizationRecordingProcessingResult> FinishUnsupportedVersionAsync(
        NormalizationRecordingAttempt attempt,
        CancellationToken ct
    )
    {
        // Older preparations did not consume the complete owner-input contract. Never admit
        // or publish them under the new protocol. Drain old workers before changing binaries;
        // a fresh attempt UUID
        // starts the current protocol, while terminal receipts keep their exact replay behavior.
        while (attempt.Result == null)
        {
            attempt = await proposals.CompleteAsync(
                attempt,
                NormalizationRecordingProcessingDisposition.Unresolved,
                "processing_version_changed",
                ct
            );
            // Preparation is write-once. If its concurrent winner changed after our read,
            // CompleteAsync returns that winner; the next pass terminalizes that exact receipt.
        }
        return attempt.Result with
        {
            Replayed =
                attempt.Result.Disposition != NormalizationRecordingProcessingDisposition.Unresolved
                || attempt.Result.Reason != "processing_version_changed",
        };
    }

    private async Task<NormalizationRecordingProcessingResult> FinishChangedPreparationAsync(
        NormalizationRecordingAttempt attempt,
        CancellationToken ct
    )
    {
        // Stale publication never invokes its callback. Finalize an already admitted stale
        // run as well as the attempt; retry also repairs a crash between those two commits.
        var publication = await work.TryPublishAsync(
            attempt.AttemptUuid,
            (_, _, _) =>
                throw new InvalidOperationException(
                    "Prepared evidence failed validation while its work read set remained current."
                ),
            ct
        );
        if (publication.Status == NormalizationPublicationStatus.Published && publication.Replayed)
        {
            // Another caller can finish this exact attempt before the catalog changes.
            // Its committed receipt wins even when this caller's later bind is now stale.
            var completed = await proposals.GetAttemptAsync(attempt.AttemptUuid, ct);
            return completed!.Result! with { Replayed = true };
        }

        if (publication.Status != NormalizationPublicationStatus.Stale)
        {
            throw new InvalidOperationException("Changed preparation unexpectedly published.");
        }

        return (
            await proposals.CompleteAsync(
                attempt,
                NormalizationRecordingProcessingDisposition.Stale,
                "preparation_changed",
                ct
            )
        ).Result! with
        {
            Replayed = publication.Replayed,
        };
    }

    // Lookup handoffs must capture these exact ownership and contributor contracts.
    internal static NormalizationSubjectKey Owner(Guid sourceUuid) =>
        new("extractions", "recording", sourceUuid);

    internal static NormalizationResourceKey Contributor(Guid sourceUuid) =>
        new("relisten", "recording", sourceUuid.ToString("D"));
}
