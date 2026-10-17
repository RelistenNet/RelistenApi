using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationRecordingLookupService
{
    private async Task<NormalizationRecordingLookupAttempt> PublishPreparedAsync(
        NormalizationRecordingLookupAttempt attempt,
        CancellationToken ct
    )
    {
        var preparation = attempt.Preparation!;
        var binding = preparation.Binding;
        IReadOnlyList<NormalizationSourceUuidDependency> sourceUuids =
            binding?.SourceUuids
            ?? (
                preparation.AbsenceProbe == null
                    ? []
                    : new[] { preparation.AbsenceProbe.Dependency }
            );
        NormalizationWorkTicket ticket;
        try
        {
            // Exact admission replay precedes revalidation. A crash after admission must
            // resume its original ticket, not replace the preparation with current clocks.
            ticket = await work.BeginWorkAsync(
                attempt.AttemptUuid,
                Owner(attempt.Registration.LookupUuid),
                attempt.ProcessingVersion,
                (binding?.Inputs ?? []).Select(input => input.Resource).ToArray(),
                [],
                preparation.ExpectedConfigurationGeneration,
                ct,
                catalogInputs: binding?.CatalogInputs,
                sourceIdentities: binding?.SourceIdentities,
                mappingRegistry: binding?.MappingRegistry,
                expectedInputRevisions: binding?.Inputs,
                sourceUuids: sourceUuids
            );
        }
        catch (NormalizationConflictException)
        {
            return await store.CompleteStaleAsync(attempt, "admission_changed", ct);
        }
        if (!NormalizationRecordingLookupReadSet.IsComplete(ticket, preparation))
        {
            // Do not publish or acknowledge a contributor union that this lookup did not
            // consume. Its extra owners and inputs remain pending for their coordinator.
            return await store.CompleteStaleAsync(attempt, "unsupported_owner_inputs", ct);
        }
        try
        {
            await RevalidateAsync(attempt, ct);
        }
        catch (NormalizationConflictException)
        {
            return await FinishChangedPreparationAsync(attempt, ct);
        }
        var publication = await work.TryPublishAsync(
            ticket.WorkUuid,
            async (connection, transaction, token) =>
            {
                await store.WritePublishedAsync(connection, transaction, attempt, ticket, token);
            },
            ct
        );
        if (publication.Status == NormalizationPublicationStatus.Stale)
        {
            var stale = await store.CompleteStaleAsync(attempt, "publication_changed", ct);
            return stale with { Result = stale.Result! with { Replayed = publication.Replayed } };
        }
        var completed =
            await store.GetAttemptAsync(attempt.AttemptUuid, ct)
            ?? throw new InvalidOperationException("Published recording lookup is missing.");
        return completed with
        {
            Result = completed.Result! with { Replayed = publication.Replayed },
        };
    }

    private async Task RevalidateAsync(
        NormalizationRecordingLookupAttempt attempt,
        CancellationToken ct
    )
    {
        var preparation = attempt.Preparation!;
        if (preparation.AbsenceProbe is { } absence)
        {
            var checkedProbe = await bindings.ProbeRecordingUuidAsync(
                absence.Dependency.SourceUuid,
                ct
            );
            if (checkedProbe != absence)
            {
                throw new NormalizationConflictException("The saved source absence changed.");
            }
            return;
        }
        NormalizationRecordingBindingSnapshot checkedBinding;
        if (attempt.Request.Mode == NormalizationRecordingLookupMode.LegacyCatalog)
        {
            var capture =
                await evidence.GetCaptureAsync(preparation.FetchUuid!.Value, ct)
                ?? throw new InvalidOperationException("Prepared lookup evidence is missing.");
            var observation = DecodeRecording(capture.Revision, attempt.Request.Resource, ct);
            checkedBinding = await bindings.BindRefreshedRecordingAsync(
                new(capture, preparation.ProducerCatalogInputs!),
                observation,
                attempt.Request.ExpectedArtistUuid,
                ct
            );
        }
        else
        {
            var input = await RequireInputAsync(attempt.Request.Input!, ct);
            var observation = DecodeRecording(input, attempt.Request.Resource, ct);
            checkedBinding = await bindings.BindRecordingAsync(
                observation,
                attempt.Request.ExpectedArtistUuid,
                ct
            );
        }
        if (
            !JsonElement.DeepEquals(
                JsonSerializer.SerializeToElement(checkedBinding),
                JsonSerializer.SerializeToElement(preparation.Binding)
            )
        )
        {
            throw new NormalizationConflictException("The saved lookup associations changed.");
        }
    }

    private async Task<NormalizationRecordingLookupAttempt> FinishChangedPreparationAsync(
        NormalizationRecordingLookupAttempt attempt,
        CancellationToken ct
    )
    {
        // A changed binding must be explained by the original work fences. If they remain
        // current, publishing an outcome would hide an unfenced fact or invalid preparation.
        var publication = await work.TryPublishAsync(
            attempt.AttemptUuid,
            (_, _, _) =>
                throw new InvalidOperationException(
                    "Prepared lookup changed while its original work read set remained current."
                ),
            ct
        );
        if (publication.Status == NormalizationPublicationStatus.Published && publication.Replayed)
        {
            return Replayed((await store.GetAttemptAsync(attempt.AttemptUuid, ct))!);
        }
        var stale = await store.CompleteStaleAsync(attempt, "preparation_changed", ct);
        return stale with { Result = stale.Result! with { Replayed = publication.Replayed } };
    }
}
