using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

/// <summary>
/// Consumes a completed repertoire account without changing source extraction or selection.
/// Preparation, admission, publication and optional observer retirement are durable retry phases.
/// </summary>
public sealed partial class NormalizationSetlistMaterializer(
    DbService db,
    NormalizationRepertoireResolutionStore repertoire,
    RepertoireConsumerPolicyService policies,
    NormalizationSetlistMaterializationStore store,
    NormalizationWorkService work,
    NormalizedMusicService music
)
{
    public const string Version =
        "setlist-materialization-v1/"
        + NormalizationSetlistProcessor.Version
        + "/"
        + RepertoireConsumerCorrespondenceEvaluator.SourceFactsVersion
        + "/"
        + RepertoireConsumerCorrespondenceEvaluator.Version;
    internal const int MaximumImageBytes = 16 * 1024 * 1024;
    internal const string LimitProfile =
        "setlist-image-16mib-100sources-10000sets-10000occurrences-v1";

    public async Task<SetlistMaterializationAttempt> ProcessAsync(
        Guid attemptUuid,
        SetlistMaterializationRequest request,
        CancellationToken ct = default
    )
    {
        if (attemptUuid == Guid.Empty || request.RepertoireResolutionAttemptUuid == Guid.Empty)
        {
            throw new ArgumentException(
                "Materialization requires nonempty attempt and completed repertoire identifiers."
            );
        }
        var existing = await store.GetProcessingAsync(attemptUuid, ct);
        if (existing != null)
        {
            if (existing.Request != request || existing.RetrySelection != null)
            {
                throw new NormalizationConflictException(
                    "The materialization attempt belongs to another request."
                );
            }
            return await AdvanceAsync(existing, ct);
        }
        var processing = await ReadProcessingRegistrationAsync(
            request.RepertoireResolutionAttemptUuid,
            ct
        );
        var attempt = await store.GetOrCreateAsync(attemptUuid, request, Version, processing, ct);
        return await AdvanceAsync(attempt, ct);
    }

    public async Task<SetlistMaterializationAttempt> RetryAsync(
        Guid newAttemptUuid,
        Guid registrationUuid,
        CancellationToken ct = default
    )
    {
        if (newAttemptUuid == Guid.Empty)
        {
            throw new ArgumentException("A retry requires a new attempt UUID.");
        }
        var existing = await store.GetProcessingAsync(newAttemptUuid, ct);
        if (existing != null)
        {
            if (
                existing.Registration.RegistrationUuid != registrationUuid
                || existing.RetrySelection == null
            )
            {
                throw new NormalizationConflictException(
                    "The retry UUID belongs to another materialization request."
                );
            }
            return await AdvanceAsync(existing, ct);
        }
        var previous =
            await store.GetLatestAttemptAsync(registrationUuid, ct)
            ?? throw new ArgumentException("Unknown setlist materialization registration.");
        if (
            previous.Result == null
            && previous.Receipt?.Status == NormalizationPublicationStatus.Published
            && previous.Preparation?.RequiresObservationRetirement == true
        )
        {
            throw new NormalizationConflictException(
                "Resume the admitted subscription cleanup before creating a fresh source retry."
            );
        }
        var attempt = await store.GetOrCreateRetryAsync(
            newAttemptUuid,
            registrationUuid,
            previous.AttemptUuid,
            Version,
            ct
        );
        return await AdvanceAsync(attempt, ct);
    }

    public Task<SetlistMaterializationAttempt?> GetAsync(
        Guid attemptUuid,
        CancellationToken ct = default
    ) => store.GetAsync(attemptUuid, ct);

    public Task<SetlistMaterializationRegistration?> GetRegistrationAsync(
        Guid registrationUuid,
        CancellationToken ct = default
    ) => store.GetRegistrationAsync(registrationUuid, ct);

    public Task<SetlistMaterializationDecisionPage> GetDecisionsAsync(
        Guid attemptUuid,
        int limit = 100,
        Guid? afterDecisionUuid = null,
        CancellationToken ct = default
    ) => store.GetDecisionsAsync(attemptUuid, limit, afterDecisionUuid, ct);

    public Task<SetlistMaterializationRegistrationPage> GetPendingRegistrationsAsync(
        int limit = 100,
        Guid? afterRegistrationUuid = null,
        CancellationToken ct = default
    ) => store.GetPendingRegistrationsAsync(limit, afterRegistrationUuid, ct);

    public Task<SetlistMaterializationRecoveryPage> GetRecoveryAttemptsAsync(
        int limit = 100,
        Guid? afterUuid = null,
        CancellationToken ct = default
    ) => store.GetRecoveryAttemptsAsync(limit, afterUuid, ct);

    private async Task<SetlistMaterializationAttempt> AdvanceAsync(
        SetlistMaterializationAttempt attempt,
        CancellationToken ct
    )
    {
        if (attempt.Result != null)
        {
            return attempt with { Result = attempt.Result with { Replayed = true } };
        }
        if (
            attempt.Receipt?.Status == NormalizationPublicationStatus.Published
            && attempt.Preparation?.RequiresObservationRetirement == true
        )
        {
            return await ResumeRetirementAsync(attempt, ct);
        }
        if (attempt.ProcessingVersion != Version)
        {
            return await StaleAsync(attempt, "unsupported_processor_version", ct);
        }
        try
        {
            if (attempt.SelectedRepertoireAttemptUuid == null)
            {
                var selected =
                    attempt.Request?.RepertoireResolutionAttemptUuid
                    ?? await FindCurrentRepertoireAsync(
                        attempt.Registration.ProcessingRegistrationUuid,
                        ct
                    );
                if (selected == null)
                {
                    return attempt;
                }
                return await store.SelectRepertoireAttemptAsync(attempt, selected.Value, ct);
            }
            if (attempt.Preparation == null)
            {
                return await PrepareAsync(attempt, ct);
            }
            if (attempt.AdmissionFailureReason != null)
            {
                return await StaleAsync(attempt, attempt.AdmissionFailureReason, ct);
            }
            if (attempt.Ticket == null)
            {
                return await AdmitAsync(attempt, ct);
            }
            if (attempt.Receipt == null)
            {
                var receipt = await music.PublishSetlistMaterializationAsync(
                    attempt.AttemptUuid,
                    attempt.AttemptUuid,
                    ct
                );
                if (receipt.Status == NormalizationPublicationStatus.Stale)
                {
                    return await StaleAsync(attempt, "publication_read_set_changed", ct);
                }
                return (await store.GetProcessingAsync(attempt.AttemptUuid, ct))!;
            }
            if (attempt.Preparation.RequiresObservationRetirement)
            {
                return await AdvanceRetirementAsync(attempt, ct);
            }
            return await store.CompleteAsync(attempt, CompletedResult(attempt), ct);
        }
        catch (CompletedRepertoireConsumptionPendingException)
        {
            // A newer acknowledgment was admitted but is not published. Do not repin this
            // attempt or acknowledge the old receipt while that source publication is pending.
            return attempt;
        }
        catch (SetlistMaterializationTargetChangedException)
        {
            return await StaleAsync(attempt, "canonical_target_changed", ct);
        }
        catch (Npgsql.PostgresException error)
            when (error.SqlState == "23505"
                && error.ConstraintName?.StartsWith("setlist_editions_", StringComparison.Ordinal)
                    == true
            )
        {
            return await StaleAsync(attempt, "canonical_target_changed", ct);
        }
        catch (CompletedRepertoireConsumptionCapacityException)
        {
            return await StaleAsync(
                attempt,
                "materialization_capacity_exceeded",
                ct,
                permanent: true
            );
        }
        catch (NormalizationInputContributionsCapacityException error)
        {
            return await StaleAsync(
                attempt,
                "materialization_capacity_exceeded",
                ct,
                permanent: true,
                capacityOwner: error.Owner,
                capacityMembership: error.Membership
            );
        }
        catch (MusicMaterializationCapacityException error)
        {
            return await StaleAsync(
                attempt,
                "materialization_capacity_exceeded",
                ct,
                permanent: true,
                capacityOwner: error.Owner,
                capacityMembership: error.Membership
            );
        }
        catch (UnsupportedSetlistSourceException)
        {
            return await StaleAsync(attempt, "source_shape_unsupported", ct, permanent: true);
        }
        catch (NormalizationConflictException)
        {
            return await StaleAsync(attempt, "context_changed", ct);
        }
    }

    private async Task<SetlistMaterializationAttempt> PrepareAsync(
        SetlistMaterializationAttempt attempt,
        CancellationToken ct
    )
    {
        if (attempt.RetrySelection is { } retry)
        {
            var previous = await store.GetProcessingAsync(retry.PreviousAttemptUuid, ct);
            var blocked = previous?.Result?.RetryRequirement;
            if (
                previous?.AttemptUuid != attempt.AttemptUuid
                && blocked != null
                && blocked.RepertoireResolutionAttemptUuid == attempt.SelectedRepertoireAttemptUuid
                && blocked.ProcessorVersion == Version
                && blocked.LimitProfile == LimitProfile
                && (
                    blocked.CapacityOwner == null
                    || !await CapacityOwnerChangedAsync(
                        blocked.CapacityOwner,
                        blocked.CapacityMembership,
                        ct
                    )
                )
            )
            {
                return await StaleAsync(attempt, blocked.Reason, ct, permanent: true);
            }
        }
        await policies.ReadStateAsync(ct);
        var prepared = await ReadPreparationAsync(
            attempt.Registration,
            attempt.SelectedRepertoireAttemptUuid!.Value,
            ct
        );
        var context = prepared.Context!;
        try
        {
            MeasureBoundedJson(prepared.Image, MaximumImageBytes);
            MeasureBoundedJson(prepared, 128 * 1024 * 1024);
        }
        catch (MusicMaterializationCapacityException error)
        {
            throw WithCapacityOwner(error, context.OwnerContext.Membership);
        }
        return await store.SavePreparationAsync(attempt, prepared, ct);
    }

    private async Task<SetlistMaterializationAttempt> AdmitAsync(
        SetlistMaterializationAttempt attempt,
        CancellationToken ct
    )
    {
        var context = RequirePreparation(attempt).Context!;
        var ticket = await AdmitContributionAsync(
            attempt.AttemptUuid,
            context.Owner,
            Version,
            ContributionKey(attempt.Registration.RegistrationUuid),
            context.OwnerContext,
            context.OwnReadSet,
            context.InputReferences,
            ct
        );
        if (!AdmissionMatches(ticket, context.ReadSet, context.OwnerContext))
        {
            return await store.SaveFailedAdmissionAsync(
                attempt,
                ticket,
                "admission_read_set_changed",
                ct
            );
        }
        return await store.SaveTicketAsync(attempt, ticket, ct);
    }

    internal static SetlistMaterializationPreparation RequirePreparation(
        SetlistMaterializationAttempt attempt
    )
    {
        if (attempt.Preparation is not { ContextOmitted: false, Context: not null } preparation)
        {
            throw new NormalizationConflictException(
                "Materialization requires its complete frozen preparation."
            );
        }
        return preparation;
    }

    internal static MusicMaterializationResult CompletedResult(
        SetlistMaterializationAttempt attempt
    )
    {
        var decisions = RequirePreparation(attempt).Decisions;
        return new(
            attempt.AttemptUuid,
            MusicMaterializationDisposition.Completed,
            "account_acknowledged",
            decisions.Count(value =>
                value.Disposition
                    is MusicMaterializationDecisionDisposition.Accepted
                        or MusicMaterializationDecisionDisposition.Withdrawn
            ),
            decisions.Count(value =>
                value.Disposition == MusicMaterializationDecisionDisposition.Retained
            ),
            decisions.Count(value =>
                value.Disposition == MusicMaterializationDecisionDisposition.Review
            ),
            decisions.Count(value =>
                value.Disposition == MusicMaterializationDecisionDisposition.Unresolved
            )
        );
    }

    private Task<SetlistMaterializationAttempt> StaleAsync(
        SetlistMaterializationAttempt attempt,
        string reason,
        CancellationToken ct,
        bool permanent = false,
        NormalizationSubjectObservation? capacityOwner = null,
        NormalizationInputContributionsCapacityStamp? capacityMembership = null
    ) =>
        store.CompleteAsync(
            attempt,
            new(
                attempt.AttemptUuid,
                MusicMaterializationDisposition.Stale,
                reason,
                RetryRequirement: permanent
                    ? new(
                        attempt.SelectedRepertoireAttemptUuid,
                        Version,
                        LimitProfile,
                        reason,
                        capacityOwner,
                        capacityMembership
                    )
                    : null
            ),
            ct
        );

    internal static NormalizationSubjectKey ObservationOwner(Guid registration) =>
        new("songs", "setlist_materialization", registration);

    internal static NormalizationResourceKey ContributionKey(Guid registration) =>
        new("relisten", "setlist_materializer", registration.ToString("D"));

    internal static Guid QualifiedUuid(Guid artist, NormalizationResourceKey identity) =>
        PurposeUuid(
            artist,
            JsonSerializer.Serialize(
                new
                {
                    identity.Provider,
                    identity.ResourceType,
                    identity.ProviderKey,
                }
            )
        );

    internal static Guid PurposeUuid(Guid nonce, string purpose)
    {
        var bytes = SHA256.HashData(
            Encoding.UTF8.GetBytes($"setlist-materialization-v1/{nonce:D}/{purpose}")
        );
        return new Guid(bytes.AsSpan(0, 16));
    }

    private Task<Guid> ReadProcessingRegistrationAsync(Guid repertoireUuid, CancellationToken ct) =>
        db.WithWriteConnection(async c =>
            await c.QuerySingleOrDefaultAsync<Guid?>(
                new CommandDefinition(
                    """
                    SELECT source.registration_uuid FROM normalization.repertoire_resolution_attempts a
                    JOIN normalization.repertoire_resolution_registrations r ON r.registration_uuid=a.registration_uuid AND r.current_attempt_uuid=a.attempt_uuid
                    JOIN normalization.setlist_processing_attempts source ON source.attempt_uuid=(a.request->'Source'->>'PublishedProcessingAttemptUuid')::uuid
                    WHERE a.attempt_uuid=@repertoireUuid AND a.request->'Source'->>'kind'='setlist_passages'
                        AND (a.result->>'Disposition')::integer=@completed AND source.disposition='published'
                    """,
                    new
                    {
                        repertoireUuid,
                        completed = (int)RepertoireResolutionDisposition.Completed,
                    },
                    cancellationToken: ct
                )
            )
            ?? throw new ArgumentException(
                "The request requires a current completed setlist repertoire account."
            )
        );

    private Task<Guid?> FindCurrentRepertoireAsync(
        Guid processingRegistrationUuid,
        CancellationToken ct
    ) =>
        db.WithWriteConnection(async c =>
            await c.QuerySingleOrDefaultAsync<Guid?>(
                new CommandDefinition(
                    """
                    SELECT a.attempt_uuid FROM normalization.work_subjects producer
                    JOIN normalization.setlist_processing_attempts extraction ON extraction.attempt_uuid=producer.published_work_uuid
                        AND extraction.registration_uuid=@processingRegistrationUuid AND extraction.disposition='published'
                    JOIN normalization.repertoire_resolution_attempts a ON a.request->'Source'->>'kind'='setlist_passages'
                        AND a.request->'Source'->>'PublishedProcessingAttemptUuid'=extraction.attempt_uuid::text
                    JOIN normalization.repertoire_resolution_registrations r ON r.registration_uuid=a.registration_uuid AND r.current_attempt_uuid=a.attempt_uuid
                    WHERE producer.domain='extractions' AND producer.subject_kind='setlist' AND producer.subject_uuid=@processingRegistrationUuid
                        AND producer.published_generation=producer.desired_generation AND (a.result->>'Disposition')::integer=@completed
                    ORDER BY a.sequence DESC LIMIT 1
                    """,
                    new
                    {
                        processingRegistrationUuid,
                        completed = (int)RepertoireResolutionDisposition.Completed,
                    },
                    cancellationToken: ct
                )
            )
        );

    // Only explicit source-shape refusals are permanent. Serializer and infrastructure
    // failures must leave the durable attempt resumable rather than suppressing recovery.
    private sealed class UnsupportedSetlistSourceException(string message) : Exception(message);
}
