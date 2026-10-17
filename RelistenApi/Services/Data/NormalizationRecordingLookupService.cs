using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

/// <summary>
/// Saves recording-association lookups and the exact evidence that made them positive or
/// unresolved. Publication advances the lookup outcome and creates any downstream handoff
/// together; extraction delivery runs separately after that transaction commits.
/// </summary>
public sealed partial class NormalizationRecordingLookupService(
    NormalizationRecordingLookupStore store,
    NormalizationLegacyInputService legacy,
    NormalizationEvidenceService evidence,
    NormalizationCatalogBindingService bindings,
    NormalizationProviderDecoder decoder,
    NormalizationWorkService work
)
{
    // A lookup freezes both decoded associations and its legacy extraction handoff. The
    // processor fingerprint includes the refresh, decoder and extractor contracts it consumes.
    public const string Version = "recording-lookup-v1/" + NormalizationRecordingProcessor.Version;
    private const int MaximumPreparationAttempts = 3;

    /// <summary>
    /// Looks up an exact legacy source UUID. Missing sources retain an actual absence probe;
    /// existing sources retain a complete refresh. Reuse attemptUuid for recovery and exact
    /// replay. Changed facts require a new attempt UUID, normally through RetryAsync.
    /// </summary>
    public Task<NormalizationRecordingLookupAttempt> LookupLegacyAsync(
        Guid attemptUuid,
        Guid sourceUuid,
        Guid? expectedArtistUuid = null,
        CancellationToken cancellationToken = default
    )
    {
        if (sourceUuid == Guid.Empty)
        {
            throw new ArgumentException("A source UUID is required.", nameof(sourceUuid));
        }
        return StartAsync(
            attemptUuid,
            new(
                NormalizationRecordingLookupMode.LegacyCatalog,
                new("legacy_catalog", "recording", sourceUuid.ToString("D")),
                expectedArtistUuid
            ),
            cancellationToken
        );
    }

    /// <summary>
    /// Looks up one retained Archive item or Phish show using its actual immutable input.
    /// A missing input or unsupported representation is an error, not a negative catalog
    /// lookup. No provider fetch or legacy metadata substitution occurs.
    /// </summary>
    public Task<NormalizationRecordingLookupAttempt> LookupRetainedAsync(
        Guid attemptUuid,
        NormalizationBindingInputReference input,
        Guid? expectedArtistUuid = null,
        CancellationToken cancellationToken = default
    )
    {
        RequireRetainedResource(input.Resource);
        return StartAsync(
            attemptUuid,
            new(
                NormalizationRecordingLookupMode.RetainedProvider,
                input.Resource,
                expectedArtistUuid,
                input
            ),
            cancellationToken
        );
    }

    /// <summary>
    /// Returns the last published lookup attempt, including after it becomes pending. This
    /// historical outcome is not a freshness proof; WorkService owns pending-state inspection.
    /// </summary>
    public Task<NormalizationRecordingLookupAttempt?> GetAsync(
        Guid lookupUuid,
        CancellationToken cancellationToken = default
    ) => store.GetCurrentAsync(lookupUuid, cancellationToken);

    /// <summary>
    /// Starts a new attempt for a registration. Retained requests select the current head once;
    /// an already saved attempt resumes its original input even if the resource changed again.
    /// Legacy requests reuse their last published capture only while its revision is current
    /// and its original catalog clocks validate. Changed facts require a complete refresh.
    /// </summary>
    public async Task<NormalizationRecordingLookupAttempt> RetryAsync(
        Guid newAttemptUuid,
        Guid lookupUuid,
        CancellationToken cancellationToken = default
    )
    {
        var existing = await store.GetAttemptAsync(newAttemptUuid, cancellationToken);
        if (existing != null)
        {
            RequireRegistration(existing, lookupUuid);
            return await ResumeAsync(existing, cancellationToken);
        }
        var registration =
            await store.GetRegistrationAsync(lookupUuid, cancellationToken)
            ?? throw new ArgumentException("The recording lookup is unknown.", nameof(lookupUuid));
        NormalizationBindingInputReference? input = null;
        IReadOnlyList<NormalizationBindingInputReference>? supportingInputs = null;
        if (registration.Mode == NormalizationRecordingLookupMode.RetainedProvider)
        {
            var resources =
                registration.Resource.Provider == "local"
                    ? new[]
                    {
                        registration.Resource,
                        NormalizationProviderResources.LocalRootForDirectory(registration.Resource),
                    }
                    : new[] { registration.Resource };
            var references = await evidence.GetCurrentInputReferencesAsync(
                resources,
                cancellationToken
            );
            input = references.Single(reference => reference.Resource == registration.Resource);
            supportingInputs =
                resources.Length == 1
                    ? null
                    : references
                        .Where(reference => reference.Resource != registration.Resource)
                        .ToArray();
        }
        var request = new NormalizationRecordingLookupRequest(
            registration.Mode,
            registration.Resource,
            registration.ExpectedArtistUuid,
            input,
            supportingInputs
        );
        try
        {
            return await StartAsync(newAttemptUuid, request, cancellationToken);
        }
        catch (NormalizationConflictException)
        {
            // Another caller may have frozen this retry against a different current head.
            // Resume that original request instead of replacing its selected revision.
            var winner = await store.GetAttemptAsync(newAttemptUuid, cancellationToken);
            if (winner == null)
            {
                throw;
            }
            RequireRegistration(winner, lookupUuid);
            return await ResumeAsync(winner, cancellationToken);
        }
    }

    private async Task<NormalizationRecordingLookupAttempt> StartAsync(
        Guid attemptUuid,
        NormalizationRecordingLookupRequest request,
        CancellationToken ct
    )
    {
        var existing = await store.GetAttemptAsync(attemptUuid, ct);
        if (existing != null)
        {
            if (
                !JsonElement.DeepEquals(
                    JsonSerializer.SerializeToElement(existing.Request),
                    JsonSerializer.SerializeToElement(request)
                )
            )
            {
                throw new NormalizationConflictException(
                    "Attempt UUID already belongs to another lookup request."
                );
            }
            return await ResumeAsync(existing, ct);
        }
        var attempt = await store.GetOrCreateAttemptAsync(attemptUuid, request, Version, ct);
        return await ResumeAsync(attempt, ct);
    }

    private async Task<NormalizationRecordingLookupAttempt> ResumeAsync(
        NormalizationRecordingLookupAttempt attempt,
        CancellationToken ct
    )
    {
        if (attempt.Result != null)
        {
            return Replayed(attempt);
        }
        if (attempt.ProcessingVersion != Version)
        {
            throw new NormalizationConflictException(
                "The saved lookup uses another processing version."
            );
        }
        if (attempt.Preparation == null)
        {
            try
            {
                var preparation = await PrepareAsync(attempt, ct);
                attempt = await store.SavePreparationAsync(attempt.AttemptUuid, preparation, ct);
            }
            catch (Exception error)
                when (error is NormalizationStaleInputException or NormalizationConflictException)
            {
                // A concurrent caller can save its winning preparation while this caller's
                // refresh loses admission. Recover that receipt rather than its discarded facts.
                var winner = await store.GetAttemptAsync(attempt.AttemptUuid, ct);
                if (winner?.Preparation == null && winner?.Result == null)
                {
                    throw;
                }
                attempt = winner!;
            }
            if (attempt.Result != null)
            {
                return Replayed(attempt);
            }
        }
        return await PublishPreparedAsync(attempt, ct);
    }

    private static void RequireRetainedResource(NormalizationResourceKey resource)
    {
        if (
            (resource.Provider, resource.ResourceType)
            is not (("archive.org", "item_metadata") or ("phish.in", "show"))
        )
        {
            throw new NotSupportedException(
                "Recording lookups require an Archive item or retained Phish show input."
            );
        }
    }

    private static void RequireRegistration(
        NormalizationRecordingLookupAttempt attempt,
        Guid lookupUuid
    )
    {
        if (attempt.Registration.LookupUuid != lookupUuid)
        {
            throw new NormalizationConflictException(
                "Attempt UUID already belongs to another recording lookup."
            );
        }
    }

    private static NormalizationRecordingLookupAttempt Replayed(
        NormalizationRecordingLookupAttempt attempt
    ) => attempt with { Result = attempt.Result! with { Replayed = true } };

    private static NormalizationSubjectKey Owner(Guid lookupUuid) =>
        new("lookups", "recording", lookupUuid);
}
