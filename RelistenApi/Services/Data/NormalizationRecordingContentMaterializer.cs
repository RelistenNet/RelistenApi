using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

/// <summary>
/// Freezes complete recording file images before admission. Replaying an attempt never refreshes
/// its source receipt or allocations; retry first records lineage, then selects one completed account.
/// </summary>
public sealed partial class NormalizationRecordingContentMaterializer(
    DbService db,
    NormalizationRepertoireResolutionStore repertoire,
    NormalizationRecordingContentMaterializationStore store,
    RepertoireConsumerPolicyService policies,
    NormalizationWorkService work,
    NormalizedMusicService music
)
{
    public const string Version =
        "recording-content-materialization-v1/"
        + NormalizationRecordingProcessor.Version
        + "/"
        + RepertoireConsumerCorrespondenceEvaluator.SourceFactsVersion
        + "/"
        + RepertoireConsumerCorrespondenceEvaluator.Version;
    public const string LimitProfile =
        "recording-image-10000-correspondences-20000-16MiB-preparation-128MiB-v1";
    public const int MaximumContents = 10000;
    public const int MaximumCorrespondences = 20000;
    public const int MaximumImageBytes = 16 * 1024 * 1024;
    public const int MaximumPreparationBytes = 128 * 1024 * 1024;

    public async Task<RecordingContentMaterializationAttempt> ProcessAsync(
        Guid attemptUuid,
        RecordingContentMaterializationRequest request,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        if (attemptUuid == Guid.Empty || request.RepertoireResolutionAttemptUuid == Guid.Empty)
        {
            throw new ArgumentException(
                "Nonempty attempt and completed repertoire coordinates are required."
            );
        }
        var existing = await store.GetProcessingAsync(attemptUuid, ct);
        if (existing != null)
        {
            if (existing.RetrySelection != null || existing.Request != request)
            {
                throw new NormalizationConflictException(
                    "The attempt belongs to another pinned repertoire account."
                );
            }
            return await AdvanceAsync(existing, ct);
        }

        var sourceUuid = await ReadSourceDescriptorAsync(
            request.RepertoireResolutionAttemptUuid,
            ct
        );
        var attempt = await store.GetOrCreateAsync(attemptUuid, request, Version, sourceUuid, ct);
        return await AdvanceAsync(attempt, ct);
    }

    public async Task<RecordingContentMaterializationAttempt> RetryAsync(
        Guid attemptUuid,
        Guid registrationUuid,
        CancellationToken ct = default
    )
    {
        if (attemptUuid == Guid.Empty)
        {
            throw new ArgumentException("Retry requires a nonempty attempt UUID.");
        }
        var existing = await store.GetProcessingAsync(attemptUuid, ct);
        if (existing != null)
        {
            if (existing.RetrySelection?.RegistrationUuid != registrationUuid)
            {
                throw new NormalizationConflictException(
                    "The retry UUID belongs to another lineage."
                );
            }
            return await AdvanceAsync(existing, ct);
        }
        var previous =
            await store.GetLatestAttemptAsync(registrationUuid, ct)
            ?? throw new ArgumentException("Unknown recording materialization registration.");
        if (attemptUuid == previous.AttemptUuid)
        {
            throw new ArgumentException("Retry requires a new attempt UUID.");
        }
        var attempt = await store.GetOrCreateRetryAsync(
            attemptUuid,
            registrationUuid,
            previous.AttemptUuid,
            Version,
            ct
        );
        return await AdvanceAsync(attempt, ct);
    }

    public Task<RecordingContentMaterializationAttempt?> GetAsync(
        Guid attemptUuid,
        CancellationToken ct = default
    ) => store.GetAsync(attemptUuid, ct);

    public Task<RecordingContentMaterializationRegistration?> GetRegistrationAsync(
        Guid uuid,
        CancellationToken ct = default
    ) => store.GetRegistrationAsync(uuid, ct);

    public Task<RecordingContentMaterializationDecisionPage> GetDecisionsAsync(
        Guid attemptUuid,
        int limit = 100,
        Guid? afterDecisionUuid = null,
        CancellationToken ct = default
    ) => store.GetDecisionsAsync(attemptUuid, limit, afterDecisionUuid, ct);

    public Task<RecordingContentMaterializationRegistrationPage> GetPendingRegistrationsAsync(
        int limit = 100,
        Guid? afterRegistrationUuid = null,
        CancellationToken ct = default
    ) => store.GetPendingRegistrationsAsync(limit, afterRegistrationUuid, ct);

    public Task<RecordingContentMaterializationRecoveryPage> GetRecoveryAttemptsAsync(
        int limit = 100,
        Guid? afterUuid = null,
        CancellationToken ct = default
    ) => store.GetRecoveryAttemptsAsync(limit, afterUuid, ct);

    private async Task<RecordingContentMaterializationAttempt> AdvanceAsync(
        RecordingContentMaterializationAttempt attempt,
        CancellationToken ct
    )
    {
        if (attempt.Result != null)
        {
            return attempt with { Result = attempt.Result with { Replayed = true } };
        }
        if (attempt.ProcessingVersion != Version)
        {
            return await StaleAsync(attempt, "processor_version_changed", ct);
        }
        try
        {
            if (attempt.Request == null && attempt.SelectedRepertoireAttemptUuid == null)
            {
                return await SelectRetryAccountAsync(attempt, ct);
            }
            if (attempt.Preparation == null)
            {
                var selected =
                    attempt.SelectedRepertoireAttemptUuid
                    ?? attempt.Request!.RepertoireResolutionAttemptUuid;
                if (!await HasSupportedSourceShapeAsync(selected, ct))
                {
                    return await store.CompleteAsync(
                        attempt,
                        new(
                            attempt.AttemptUuid,
                            MusicMaterializationDisposition.Stale,
                            "source_shape_unsupported",
                            RetryRequirement: new(
                                selected,
                                Version,
                                LimitProfile,
                                "source_shape_unsupported"
                            )
                        ),
                        ct
                    );
                }
                return await SavePreparationAsync(
                    attempt,
                    await ReadContextAsync(selected, ct),
                    ct
                );
            }
            if (attempt.Ticket == null)
            {
                var ticket = await AdmitAsync(attempt, ct);
                try
                {
                    NormalizationRecordingContentMaterializationStore.RequireTicket(
                        attempt,
                        ticket
                    );
                    return await store.SaveTicketAsync(attempt, ticket, ct);
                }
                catch (NormalizationConflictException)
                {
                    return await store.SaveFailedAdmissionAsync(
                        attempt,
                        ticket,
                        "admission_read_set_changed",
                        ct
                    );
                }
            }
            if (attempt.AdmissionFailureReason != null)
            {
                return await StaleAsync(attempt, attempt.AdmissionFailureReason, ct);
            }
            var receipt = await music.PublishRecordingContentMaterializationAsync(
                attempt.Ticket.WorkUuid,
                attempt.AttemptUuid,
                ct
            );
            if (receipt.Status == NormalizationPublicationStatus.Stale)
            {
                return await StaleAsync(attempt, "source_observation_changed", ct);
            }
            return (await store.GetProcessingAsync(attempt.AttemptUuid, ct))!;
        }
        catch (Exception exception)
            when (exception
                    is MusicMaterializationCapacityException
                        or NormalizationInputContributionsCapacityException
                        or CompletedRepertoireConsumptionCapacityException
            )
        {
            var selected =
                attempt.SelectedRepertoireAttemptUuid
                ?? attempt.Request?.RepertoireResolutionAttemptUuid;
            return await store.CompleteAsync(
                attempt,
                new(
                    attempt.AttemptUuid,
                    MusicMaterializationDisposition.Stale,
                    "materialization_capacity_exceeded",
                    RetryRequirement: new(
                        selected,
                        Version,
                        LimitProfile,
                        "materialization_capacity_exceeded",
                        exception is MusicMaterializationCapacityException musicCapacity
                                ? musicCapacity.Owner
                            : exception
                                is NormalizationInputContributionsCapacityException inputCapacity
                                ? inputCapacity.Owner
                            : null,
                        exception is MusicMaterializationCapacityException musicMembership
                                ? musicMembership.Membership
                            : exception
                                is NormalizationInputContributionsCapacityException inputMembership
                                ? inputMembership.Membership
                            : null
                    )
                ),
                ct
            );
        }
        catch (CompletedRepertoireConsumptionPendingException)
        {
            return Pending(attempt, "awaiting_repertoire_resolution");
        }
        catch (NormalizationConflictException)
        {
            return await StaleAsync(attempt, "source_observation_changed", ct);
        }
    }

    private async Task<RecordingContentMaterializationAttempt> SelectRetryAccountAsync(
        RecordingContentMaterializationAttempt attempt,
        CancellationToken ct
    )
    {
        var previous = (
            await store.GetProcessingAsync(attempt.RetrySelection!.PreviousAttemptUuid, ct)
        )!;
        var lineage = previous;
        for (
            var depth = 0;
            lineage.SelectedRepertoireAttemptUuid == null && lineage.Request == null;
            depth++
        )
        {
            if (depth >= 1000 || lineage.RetrySelection == null)
            {
                throw new NormalizationConflictException(
                    "The bounded retry lineage has no original repertoire coordinates."
                );
            }
            lineage = (
                await store.GetProcessingAsync(lineage.RetrySelection.PreviousAttemptUuid, ct)
            )!;
        }
        var oldUuid =
            lineage.SelectedRepertoireAttemptUuid
            ?? lineage.Request!.RepertoireResolutionAttemptUuid;
        var old = await repertoire.GetAsync(oldUuid, ct);
        if (old == null)
        {
            return Pending(attempt, "awaiting_repertoire_resolution");
        }
        var registration = await repertoire.GetRegistrationAsync(
            old.Registration.RegistrationUuid,
            ct
        );
        if (registration?.CurrentAttemptUuid is not { } current)
        {
            return Pending(attempt, "awaiting_repertoire_resolution");
        }
        try
        {
            var consumption = await ReadSelectionAsync(current, ct);
            if (consumption.Recording?.SourceUuid != attempt.Registration.SourceUuid)
            {
                throw new NormalizationConflictException(
                    "A retry cannot change its recording source."
                );
            }
            return await store.SelectRepertoireAttemptAsync(attempt, current, ct);
        }
        catch (CompletedRepertoireConsumptionCapacityException)
        {
            // Pin the exact rejected account before its compact permanent failure is recorded.
            // Metadata selection grants no domain authority; the next phase retains only bounds.
            return await store.SelectRepertoireAttemptAsync(attempt, current, ct);
        }
        catch (NormalizationConflictException)
        {
            return Pending(attempt, "awaiting_repertoire_resolution");
        }
    }

    private async Task<RecordingContentMaterializationAttempt> SavePreparationAsync(
        RecordingContentMaterializationAttempt attempt,
        RecordingContentMaterializationContext context,
        CancellationToken ct
    )
    {
        // Reject the complete proposed row count before allocating image UUIDs or expanding
        // source-to-segment projections. Negative claims remain separately bounded decisions.
        var bound = context
            .Consumption.Decisions.Where(decision => decision.RepertoireSongUuid != null)
            .Select(decision => decision.ClaimUuid)
            .ToHashSet();
        var prospectiveRows = context
            .Consumption.Source.Claims.Where(claim =>
                claim.TrackUuid != null
                && (
                    claim.Kind is "jam" or "speech" or "nonmusic"
                    || claim.Kind == "song" && bound.Contains(claim.ClaimUuid)
                )
            )
            .Select(claim =>
                (
                    claim.TrackUuid,
                    Position: claim.CorrespondenceKey[
                        (
                            claim.CorrespondenceKey.LastIndexOf(
                                ":segment:",
                                StringComparison.Ordinal
                            ) + 9
                        )..
                    ]
                )
            )
            .Distinct()
            .Count();
        if (prospectiveRows > MaximumContents || context.Consumption.Source.Claims.Count > 20000)
        {
            throw new MusicMaterializationCapacityException(
                "The complete proposed file image exceeds the row capacity profile."
            );
        }
        var preparation = BuildPreparation(context);
        if (
            preparation.Decisions.Count > MaximumCorrespondences
            || preparation.Image!.Correspondence.Count > MaximumCorrespondences
            || preparation.Image.Contents.Count > MaximumContents
            || JsonSerializer.SerializeToUtf8Bytes(preparation.Image).Length > MaximumImageBytes
            || JsonSerializer.SerializeToUtf8Bytes(preparation).Length > MaximumPreparationBytes
        )
        {
            return await store.CompleteAsync(
                attempt,
                new(
                    attempt.AttemptUuid,
                    MusicMaterializationDisposition.Stale,
                    "materialization_capacity_exceeded",
                    RetryRequirement: new(
                        context.Consumption.Attempt.AttemptUuid,
                        Version,
                        LimitProfile,
                        "materialization_capacity_exceeded",
                        OwnerObservation(context.OwnerContext.Membership.Owner)
                    )
                ),
                ct
            );
        }
        return await store.SavePreparationAsync(attempt, preparation, ct);
    }

    private Task<RecordingContentMaterializationAttempt> StaleAsync(
        RecordingContentMaterializationAttempt attempt,
        string reason,
        CancellationToken ct
    ) =>
        store.CompleteAsync(
            attempt,
            new(attempt.AttemptUuid, MusicMaterializationDisposition.Stale, reason),
            ct
        );

    private static RecordingContentMaterializationAttempt Pending(
        RecordingContentMaterializationAttempt attempt,
        string reason
    ) =>
        attempt with
        {
            Result = new(attempt.AttemptUuid, MusicMaterializationDisposition.Pending, reason),
        };

    internal static NormalizationSubjectKey Owner(Guid source) => new("songs", "recording", source);

    internal static NormalizationResourceKey Contribution(Guid source) =>
        new("relisten", "recording_content_materializer", source.ToString("D"));
}
