using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationSetlistProcessor
{
    private async Task<NormalizationSetlistProcessingAttempt> PublishPreparedAsync(
        NormalizationSetlistProcessingAttempt attempt,
        CancellationToken ct
    )
    {
        var preparation = attempt.Preparation!;
        NormalizationWorkTicket ticket;
        try
        {
            ticket = await work.BeginContributedBatchAsync(
                attempt.AttemptUuid,
                Owner(attempt.Registration.RegistrationUuid),
                attempt.ProcessingVersion,
                NormalizationSetlistReadSet.Contributions(preparation),
                preparation.ExpectedConfigurationGeneration,
                ct,
                preparation.ExpectedContributions
            );
        }
        catch (NormalizationConflictException)
        {
            return await store.CompleteAsync(
                attempt,
                NormalizationSetlistProcessingDisposition.Stale,
                "admission_changed",
                ct
            );
        }
        if (!NormalizationSetlistReadSet.IsComplete(ticket, preparation))
        {
            return await AttentionAsync(attempt, "unsupported_owner_inputs", ct);
        }
        var retained = (
            await evidence.GetInputsAsync(
                preparation.Inputs.Select(input => input.Input).ToArray(),
                cancellationToken: ct
            )
        ).ToDictionary(input => input.Uuid);
        try
        {
            await RevalidateAsync(attempt, retained, ct);
        }
        catch (NormalizationConflictException)
        {
            return await FinishChangedPreparationAsync(attempt, ct);
        }
        var extracted = new List<NormalizationSetlistInputExtraction>();
        foreach (var prepared in preparation.Inputs)
        {
            var input = retained[prepared.Input.InputRevisionUuid];
            extracted.Add(extractor.Extract(input, decoder.Decode(input, ct), prepared, ct));
        }
        var batch = new NormalizationSetlistProposalBatch(
            attempt.AttemptUuid,
            attempt.Registration.RegistrationUuid,
            attempt.ProcessingVersion,
            preparation,
            ticket,
            extracted.ToArray()
        );
        var checkpoint = await store.SaveExtractionAsync(batch, ct);
        if (checkpoint.Result != null)
        {
            return Replayed(checkpoint);
        }
        var publication = await work.TryPublishAsync(
            ticket.WorkUuid,
            (connection, transaction, token) =>
                store.WritePublishedAsync(connection, transaction, batch, token),
            ct
        );
        if (publication.Status == NormalizationPublicationStatus.Stale)
        {
            return await store.CompleteAsync(
                attempt,
                NormalizationSetlistProcessingDisposition.Stale,
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

    private async Task RevalidateAsync(
        NormalizationSetlistProcessingAttempt attempt,
        IReadOnlyDictionary<Guid, NormalizationInputRevision> retained,
        CancellationToken ct
    )
    {
        foreach (var prepared in attempt.Preparation!.Inputs)
        {
            var input = retained[prepared.Input.InputRevisionUuid];
            var current = await bindings.BindSetlistAccountGroupAsync(
                input,
                attempt.Request.AccountIdentity,
                attempt.Request.ExpectedArtistUuid,
                ct
            );
            if (!SameAccountBindings(current, prepared.Accounts))
            {
                throw new NormalizationConflictException("The saved setlist associations changed.");
            }
        }
    }

    private static bool SameAccountBindings(
        IReadOnlyList<NormalizationPreparedSetlistAccount> current,
        IReadOnlyList<NormalizationPreparedSetlistAccount> prepared
    ) =>
        current
            .Select(account => JsonSerializer.Serialize(account))
            .Order(StringComparer.Ordinal)
            .SequenceEqual(
                prepared
                    .Select(account => JsonSerializer.Serialize(account))
                    .Order(StringComparer.Ordinal)
            );

    private async Task<NormalizationSetlistProcessingAttempt> FinishChangedPreparationAsync(
        NormalizationSetlistProcessingAttempt attempt,
        CancellationToken ct
    )
    {
        var publication = await work.TryPublishAsync(
            attempt.AttemptUuid,
            (_, _, _) =>
                throw new InvalidOperationException(
                    "The setlist binding changed while its original work fences remained current."
                ),
            ct
        );
        if (publication.Status == NormalizationPublicationStatus.Published && publication.Replayed)
        {
            return Replayed((await store.GetAttemptAsync(attempt.AttemptUuid, ct))!);
        }
        return await store.CompleteAsync(
            attempt,
            NormalizationSetlistProcessingDisposition.Stale,
            "preparation_changed",
            ct
        );
    }
}
