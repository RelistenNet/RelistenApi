using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

/// <summary>
/// Retains complete setlist accounts as proposals, independently of accepted music editions.
/// Account identity and artist expectation own the run; page resources own its contributions.
/// </summary>
public sealed partial class NormalizationSetlistProcessor(
    NormalizationSetlistProposalService store,
    NormalizationEvidenceService evidence,
    NormalizationCatalogBindingService bindings,
    NormalizationProviderDecoder decoder,
    NormalizationSetlistExtractor extractor,
    NormalizationWorkService work
)
{
    public const string Version =
        "setlist-processing-v1/"
        + NormalizationProviderDecoder.Version
        + "/"
        + NormalizationSetlistExtractor.Version;

    /// <summary>
    /// Processes every observation of the qualified account in the retained input and every
    /// enrolled peer. Exact attempt replay preserves its input, preparation and terminal result.
    /// </summary>
    public async Task<NormalizationSetlistProcessingAttempt> ProcessAsync(
        Guid attemptUuid,
        NormalizationBindingInputReference input,
        NormalizationResourceKey accountIdentity,
        Guid? expectedArtistUuid = null,
        CancellationToken cancellationToken = default
    )
    {
        var request = new NormalizationSetlistProcessingRequest(
            input,
            accountIdentity,
            expectedArtistUuid
        );
        var existing = await store.GetAttemptAsync(attemptUuid, cancellationToken);
        if (existing != null)
        {
            if (!Same(existing.Request, request))
            {
                throw new NormalizationConflictException(
                    "The setlist attempt already belongs to another request."
                );
            }
            return await ResumeAsync(existing, cancellationToken);
        }
        var attempt = await store.GetOrCreateAttemptAsync(
            attemptUuid,
            request,
            Version,
            cancellationToken
        );
        return await ResumeAsync(attempt, cancellationToken);
    }

    /// <summary>
    /// Uses the latest attempt's incoming page resource and selects its current retained head
    /// once. A saved retry resumes that original selection; it never substitutes a later head.
    /// </summary>
    public async Task<NormalizationSetlistProcessingAttempt> RetryAsync(
        Guid newAttemptUuid,
        Guid registrationUuid,
        CancellationToken cancellationToken = default
    )
    {
        var existing = await store.GetAttemptAsync(newAttemptUuid, cancellationToken);
        if (existing != null)
        {
            RequireRegistration(existing, registrationUuid);
            return await ResumeAsync(existing, cancellationToken);
        }
        var previous =
            await store.GetLatestAttemptAsync(registrationUuid, cancellationToken)
            ?? throw new ArgumentException(
                "A registered setlist account is required.",
                nameof(registrationUuid)
            );
        var current = (
            await evidence.GetCurrentInputReferencesAsync(
                [previous.Request.Input.Resource],
                cancellationToken
            )
        ).Single();
        try
        {
            return await ProcessAsync(
                newAttemptUuid,
                current,
                previous.Registration.AccountIdentity,
                previous.Registration.ExpectedArtistUuid,
                cancellationToken
            );
        }
        catch (NormalizationConflictException)
        {
            var winner = await store.GetAttemptAsync(newAttemptUuid, cancellationToken);
            if (winner == null)
            {
                throw;
            }
            RequireRegistration(winner, registrationUuid);
            return await ResumeAsync(winner, cancellationToken);
        }
    }

    /// <summary>Reads the last published proposal batch; its owner may now be pending.</summary>
    public Task<NormalizationSetlistProposalBatch?> GetAsync(
        Guid registrationUuid,
        CancellationToken cancellationToken = default
    ) => store.GetAsync(registrationUuid, cancellationToken);

    private async Task<NormalizationSetlistProcessingAttempt> ResumeAsync(
        NormalizationSetlistProcessingAttempt attempt,
        CancellationToken ct
    )
    {
        if (attempt.Result != null)
        {
            return Replayed(attempt);
        }
        if (attempt.ProcessingVersion != Version)
        {
            while (attempt.Result == null)
            {
                // A concurrent first preparation may win after this read. Terminalize that
                // winner without reinterpreting it under a different processing version.
                attempt = await store.CompleteAsync(
                    attempt,
                    NormalizationSetlistProcessingDisposition.Unresolved,
                    "processing_version_changed",
                    ct
                );
            }
            return attempt;
        }
        if (attempt.Preparation == null)
        {
            attempt = await PrepareAsync(attempt, ct);
            if (attempt.Result != null)
            {
                return attempt;
            }
        }
        return await PublishPreparedAsync(attempt, ct);
    }

    internal static NormalizationSubjectKey Owner(Guid registrationUuid) =>
        new("extractions", "setlist", registrationUuid);

    private static void RequireRegistration(
        NormalizationSetlistProcessingAttempt attempt,
        Guid registrationUuid
    )
    {
        if (attempt.Registration.RegistrationUuid != registrationUuid)
        {
            throw new NormalizationConflictException(
                "The setlist attempt belongs to another registration."
            );
        }
    }

    private static bool Same<T>(T left, T right) =>
        JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(left),
            JsonSerializer.SerializeToElement(right)
        );

    private static NormalizationSetlistProcessingAttempt Replayed(
        NormalizationSetlistProcessingAttempt attempt
    ) => attempt with { Result = attempt.Result! with { Replayed = true } };
}
