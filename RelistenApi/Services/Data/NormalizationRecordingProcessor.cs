using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

/// <summary>
/// Refreshes one stored recording and publishes local extraction proposals under an extraction
/// owner. It never accepts identities or changes playback. Preparation commits before work
/// admission; recovery reuses its exact receipt, producer clocks and contributor revision.
/// </summary>
public sealed class NormalizationRecordingProcessor(
    NormalizationLegacyInputService legacy,
    NormalizationEvidenceService evidence,
    NormalizationCatalogBindingService bindings,
    NormalizationProviderDecoder decoder,
    NormalizationRecordingExtractor extractor,
    NormalizationWorkService work,
    NormalizationRecordingProposalService proposals
)
{
    public const string Version =
        "catalog-recording-processing-v2/"
        + NormalizationLegacyInputService.RefreshCaptureVersion
        + "/"
        + NormalizationProviderDecoder.Version
        + "/"
        + NormalizationRecordingExtractor.Version;

    /// <summary>
    /// Processes one source using current stored catalog fields. Reuse attemptUuid for recovery
    /// and exact replay; use a new UUID after a terminal missing, unresolved or stale outcome.
    /// Cancellation and unexpected failures leave unfinished attempts resumable. Published means
    /// extraction proposals were saved, not that people or songs were matched. No paid calls occur.
    /// </summary>
    public async Task<NormalizationRecordingProcessingResult> ProcessAsync(
        Guid attemptUuid,
        Guid sourceUuid,
        CancellationToken cancellationToken = default
    )
    {
        var existing = await proposals.GetAttemptAsync(attemptUuid, cancellationToken);
        if (existing != null)
        {
            if (existing.SourceUuid != sourceUuid)
            {
                throw new NormalizationConflictException(
                    "Attempt UUID already belongs to another recording."
                );
            }
            if (existing.Result != null)
            {
                return existing.Result with { Replayed = true };
            }
            if (existing.ProcessingVersion != Version)
            {
                return await FinishUnsupportedVersionAsync(existing, cancellationToken);
            }
        }
        var attempt =
            existing
            ?? await proposals.GetOrCreateAttemptAsync(
                attemptUuid,
                sourceUuid,
                Version,
                cancellationToken
            );
        if (attempt.Result != null)
        {
            return attempt.Result with { Replayed = true };
        }

        attempt = await PrepareAsync(attempt, cancellationToken);
        if (attempt.Result != null)
        {
            return attempt.Result;
        }

        return await ProcessPreparedAsync(attempt, cancellationToken);
    }

    private async Task<NormalizationRecordingProcessingResult> FinishUnsupportedVersionAsync(
        NormalizationRecordingAttempt attempt,
        CancellationToken ct
    )
    {
        // Older preparations did not consume source UUID clocks. Never admit or publish them
        // with a new fence. Drain old workers before changing binaries; a fresh attempt UUID
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

    private async Task<NormalizationRecordingAttempt> PrepareAsync(
        NormalizationRecordingAttempt attempt,
        CancellationToken ct
    )
    {
        if (attempt.Preparation != null)
        {
            return attempt;
        }

        try
        {
            // A crash before preparation commits may repeat this entire refresh. No work has
            // been admitted yet, and an earlier orphan receipt never acquires newer clocks.
            var snapshot = await legacy.RefreshRecordingAsync(attempt.SourceUuid, ct);
            if (snapshot == null)
            {
                return await proposals.CompleteAsync(
                    attempt,
                    NormalizationRecordingProcessingDisposition.Missing,
                    "recording_missing",
                    ct
                );
            }

            var decoded = decoder.Decode(snapshot.Capture.Revision, ct);
            if (decoded.Recordings.Count != 1)
            {
                return await proposals.CompleteAsync(
                    attempt,
                    NormalizationRecordingProcessingDisposition.Unresolved,
                    "recording_assertion_count",
                    ct
                );
            }

            var binding = await bindings.BindRefreshedRecordingAsync(
                snapshot,
                decoded.Recordings[0],
                cancellationToken: ct
            );
            if (binding.Recording.Status != NormalizationCatalogBindingStatus.Bound)
            {
                return await proposals.CompleteAsync(
                    attempt,
                    NormalizationRecordingProcessingDisposition.Unresolved,
                    binding.Recording.Reason,
                    ct
                );
            }

            var owner = Owner(attempt.SourceUuid);
            var subject = await work.GetSubjectAsync(owner, ct);
            var contribution = await work.GetInputContributionAsync(
                owner,
                Contributor(attempt.SourceUuid),
                ct
            );
            var preparation = new NormalizationRecordingPreparation(
                snapshot.Capture.Fetch.FetchUuid,
                snapshot.CatalogInputs,
                binding,
                subject?.DesiredGeneration,
                contribution?.Revision ?? 0
            );

            // Concurrent callers must continue from the winning preparation, including when
            // their own fresh capture differs. The store never replaces a saved preparation.
            return await proposals.SavePreparationAsync(attempt.AttemptUuid, preparation, ct);
        }
        catch (Exception error)
            when (error is NormalizationStaleInputException or NormalizationConflictException)
        {
            // A concurrent caller may have retained the winning preparation while this
            // caller's refresh lost admission. Its discarded snapshot cannot end that work.
            var current = await proposals.GetAttemptAsync(attempt.AttemptUuid, ct);
            if (current!.Preparation != null || current.Result != null)
            {
                return current;
            }

            // No durable facts or admitted work exist yet. Retrying this attempt starts a
            // whole fresh preparation, rather than pairing the failed receipt with new clocks.
            throw;
        }
    }

    private async Task<NormalizationRecordingProcessingResult> ProcessPreparedAsync(
        NormalizationRecordingAttempt attempt,
        CancellationToken ct
    )
    {
        var preparation = attempt.Preparation!;
        NormalizationWorkTicket ticket;
        try
        {
            // Exact admission replay comes first, including recovery after Begin committed
            // but the process stopped. Never replace saved producer clocks or CAS revisions.
            ticket = await work.BeginContributedWorkAsync(
                attempt.AttemptUuid,
                Owner(attempt.SourceUuid),
                attempt.ProcessingVersion,
                new(
                    Contributor(attempt.SourceUuid),
                    preparation.ExpectedContributionRevision,
                    preparation.Binding.Inputs.Select(input => input.Resource).ToArray(),
                    []
                ),
                preparation.ExpectedConfigurationGeneration,
                ct,
                catalogInputs: preparation.Binding.CatalogInputs,
                sourceIdentities: preparation.Binding.SourceIdentities,
                mappingRegistry: preparation.Binding.MappingRegistry,
                expectedInputRevisions: preparation.Binding.Inputs,
                sourceUuids: preparation.Binding.SourceUuids
            );
        }
        catch (NormalizationConflictException)
        {
            return (
                await proposals.CompleteAsync(
                    attempt,
                    NormalizationRecordingProcessingDisposition.Stale,
                    "admission_changed",
                    ct
                )
            ).Result!;
        }

        // This producer handles one legacy recording input. Another contributor's evidence
        // must remain owned and pending until a coordinator can consume its complete read set.
        // Publishing only this producer's subset would incorrectly consume that watermark.
        if (!NormalizationRecordingReadSet.IsComplete(ticket, preparation))
        {
            return (
                await proposals.CompleteAsync(
                    attempt,
                    NormalizationRecordingProcessingDisposition.Unresolved,
                    "unsupported_owner_inputs",
                    ct
                )
            ).Result!;
        }

        var capture =
            await evidence.GetCaptureAsync(preparation.FetchUuid, ct)
            ?? throw new InvalidOperationException("Prepared recording evidence is missing.");
        var decoded = decoder.Decode(capture.Revision, ct);
        try
        {
            // Reproduce the saved associations as well as checking the original producer
            // snapshot. A valid receipt alone cannot authorize caller-supplied binding facts.
            var checkedBinding = await bindings.BindRefreshedRecordingAsync(
                new(capture, preparation.ProducerCatalogInputs),
                decoded.Recordings.Single(),
                cancellationToken: ct
            );
            if (
                !JsonElement.DeepEquals(
                    JsonSerializer.SerializeToElement(checkedBinding),
                    JsonSerializer.SerializeToElement(preparation.Binding)
                )
            )
            {
                throw new NormalizationConflictException(
                    "Saved recording associations do not match their checked evidence."
                );
            }
        }
        catch (NormalizationConflictException)
        {
            return await FinishChangedPreparationAsync(attempt, ct);
        }

        var extraction = extractor.Extract(capture.Revision, decoded, preparation.Binding, ct);
        var batch = new NormalizationRecordingProposalBatch(
            attempt.AttemptUuid,
            attempt.SourceUuid,
            preparation.Binding.Recording.Value!.ArtistUuid,
            attempt.ProcessingVersion,
            preparation,
            ticket,
            extraction
        );
        var extracted = await proposals.SaveExtractionAsync(batch, ct);
        if (extracted.Result != null)
        {
            return extracted.Result with { Replayed = true };
        }

        var publication = await work.TryPublishAsync(
            ticket.WorkUuid,
            (connection, transaction, token) =>
                proposals.WritePublishedAsync(connection, transaction, batch, token),
            ct
        );
        if (publication.Status == NormalizationPublicationStatus.Stale)
        {
            return (
                await proposals.CompleteAsync(
                    attempt,
                    NormalizationRecordingProcessingDisposition.Stale,
                    "publication_changed",
                    ct
                )
            ).Result! with
            {
                Replayed = publication.Replayed,
            };
        }

        var completed = await proposals.GetAttemptAsync(attempt.AttemptUuid, ct);
        return completed!.Result! with { Replayed = publication.Replayed };
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
