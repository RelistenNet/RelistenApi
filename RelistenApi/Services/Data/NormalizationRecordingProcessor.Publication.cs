using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationRecordingProcessor
{
    private async Task<NormalizationRecordingProcessingResult> ProcessPreparedAsync(
        NormalizationRecordingAttempt attempt,
        RecordingInputSet inputSet,
        CancellationToken ct
    )
    {
        var preparation = attempt.Preparation!;
        if (preparation.ExpectedContributions == null)
        {
            return (await AttentionAsync(attempt, "unsupported_preparation", ct)).Result!;
        }
        var inputs = NormalizationRecordingReadSet.Inputs(preparation);
        var contributions = inputs
            .Select(input => new NormalizationPreparedInputContribution(
                new(
                    input.Contributor,
                    input.ExpectedContributionRevision,
                    input.Binding.Inputs.Select(reference => reference.Resource).ToArray(),
                    []
                ),
                input.Binding.Inputs,
                input.Binding.CatalogInputs,
                input.Binding.SourceIdentities,
                input.Binding.MappingRegistry,
                SourceUuids: input.Binding.SourceUuids
            ))
            .ToArray();
        NormalizationWorkTicket ticket;
        try
        {
            // Replay admission before revalidation. Every incoming contributor retains its
            // own CAS and facts; atomic admission must not install a partially prepared union.
            ticket = await work.BeginContributedBatchAsync(
                attempt.AttemptUuid,
                Owner(attempt.SourceUuid),
                attempt.ProcessingVersion,
                contributions,
                preparation.ExpectedConfigurationGeneration,
                ct,
                expectedContributionRevisions: preparation.ExpectedContributions
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
        if (!NormalizationRecordingReadSet.IsComplete(ticket, preparation))
        {
            return (await AttentionAsync(attempt, "unsupported_owner_inputs", ct)).Result!;
        }
        var retained = await ReadInputsAsync(
            inputs.SelectMany(input => input.Binding.Inputs).Distinct().ToArray(),
            inputSet,
            ct
        );
        var roots = inputSet.Roots;
        try
        {
            foreach (var input in inputs)
            {
                await RevalidateAsync(input, retained, roots, ct);
            }
        }
        catch (NormalizationConflictException)
        {
            return await FinishChangedPreparationAsync(attempt, ct);
        }
        var accounts = inputs
            .GroupBy(input => input.Binding.InputRevisionUuid)
            .Select(group => group.First())
            .ToArray();
        var extracted = new NormalizationRecordingInputExtraction[accounts.Length];
        for (var index = 0; index < accounts.Length; index++)
        {
            var binding = accounts[index].Binding;
            var input = retained[binding.InputRevisionUuid];
            var decoded = decoder.Decode(input, ct);
            var extraction =
                binding.Resource.Provider == "local"
                    ? ExtractLocal(input, decoded, binding, retained, roots, ct)
                    : extractor.Extract(input, decoded, binding, ct);
            extracted[index] = new(input.Uuid, extraction);
        }
        var batch = new NormalizationRecordingProposalBatch(
            attempt.AttemptUuid,
            attempt.SourceUuid,
            preparation.Binding.Recording.Value!.ArtistUuid,
            attempt.ProcessingVersion,
            preparation,
            ticket,
            extracted[0].Extraction,
            extracted.Length == 1 ? null : extracted.Skip(1).ToArray()
        );
        var checkpoint = await proposals.SaveExtractionAsync(batch, ct);
        if (checkpoint.Result != null)
        {
            return checkpoint.Result with { Replayed = true };
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

    private async Task RevalidateAsync(
        NormalizationRecordingPreparedInput preparation,
        IReadOnlyDictionary<Guid, NormalizationInputRevision> retained,
        Dictionary<Guid, ProviderDecodeResult> roots,
        CancellationToken ct
    )
    {
        var input = retained[preparation.Binding.InputRevisionUuid];
        var decoded = decoder.Decode(input, ct);
        if (decoded.Recordings.Count != 1)
        {
            throw new ArgumentException("A prepared input must contain one recording account.");
        }
        NormalizationRecordingBindingSnapshot current;
        if (input.Resource.Provider == "local")
        {
            var root = retained[preparation.Binding.SupportingInputs!.Single().InputRevisionUuid];
            var decodedRoot = DecodeRoot(root, roots, ct);
            current = await bindings.BindLocalDirectoryWithRootAsync(
                root,
                input,
                decodedRoot,
                preparation.Binding.Recording.Value!.ArtistUuid,
                ct
            );
        }
        else if (preparation.FetchUuid is { } fetchUuid)
        {
            var capture =
                await evidence.GetCaptureAsync(fetchUuid, ct)
                ?? throw new InvalidOperationException("Prepared recording evidence is missing.");
            current = await bindings.BindRefreshedRecordingAsync(
                new(capture, preparation.ProducerCatalogInputs!),
                decoded.Recordings[0],
                cancellationToken: ct
            );
        }
        else
        {
            current = await bindings.BindRecordingAsync(
                decoded.Recordings[0],
                preparation.Binding.Recording.Value!.ArtistUuid,
                ct
            );
        }
        if (
            !JsonElement.DeepEquals(
                JsonSerializer.SerializeToElement(current),
                JsonSerializer.SerializeToElement(preparation.Binding)
            )
        )
        {
            throw new NormalizationConflictException(
                "Saved recording associations do not match their checked evidence."
            );
        }
    }
}
