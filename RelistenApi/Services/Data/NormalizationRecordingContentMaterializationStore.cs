using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

/// <summary>Durable RecordingContent preparation and admission; accepted writes and receipts share Work's transaction.</summary>
public sealed partial class NormalizationRecordingContentMaterializationStore(DbService db)
{
    internal const int MaximumPreparationBytes = 128 * 1024 * 1024;
    internal const int MaximumImageBytes = 16 * 1024 * 1024;
    internal const int MaximumSummaryBytes = 1024 * 1024;
    internal const int MaximumDecisionPageBytes = 32 * 1024 * 1024;

    public Task<RecordingContentMaterializationAttempt?> GetAsync(
        Guid attemptUuid,
        CancellationToken ct = default
    ) => InTransaction((c, t) => Read(c, t, attemptUuid, true, false, ct), ct);

    internal Task<RecordingContentMaterializationAttempt?> GetProcessingAsync(
        Guid attemptUuid,
        CancellationToken ct = default
    ) => InTransaction((c, t) => Read(c, t, attemptUuid, true, true, ct), ct);

    public Task<RecordingContentMaterializationRegistration?> GetRegistrationAsync(
        Guid registrationUuid,
        CancellationToken ct = default
    ) =>
        db.WithWriteConnection<RecordingContentMaterializationRegistration?>(c =>
            ReadRegistration((NpgsqlConnection)c, null, registrationUuid, ct)
        );

    internal Task<RecordingContentMaterializationAttempt> GetOrCreateAsync(
        Guid uuid,
        RecordingContentMaterializationRequest request,
        string version,
        Guid sourceUuid,
        CancellationToken ct = default
    )
    {
        if (
            uuid == Guid.Empty
            || request.RepertoireResolutionAttemptUuid == Guid.Empty
            || sourceUuid == Guid.Empty
            || string.IsNullOrWhiteSpace(version)
        )
        {
            throw new ArgumentException(
                "An attempt, exact repertoire receipt, source lineage and version are required."
            );
        }
        var json = Serialize(request, MaximumDecisionPageBytes);
        return InTransaction(
            async (c, t) =>
            {
                var existing = await Read(c, t, uuid, true, true, ct);
                if (existing != null)
                {
                    if (
                        existing.Request != request
                        || existing.RetrySelection != null
                        || existing.Registration.SourceUuid != sourceUuid
                    )
                    {
                        throw new NormalizationConflictException(
                            "The materialization attempt is bound to another source request."
                        );
                    }
                    RequireVersion(existing.ProcessingVersion, version, existing.Result);
                    return existing;
                }
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "SELECT pg_advisory_xact_lock(hashtextextended(@key,0))",
                        new
                        {
                            key = "normalization:recording_content_materialization:"
                                + sourceUuid.ToString("D"),
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                var registrationUuid = await c.QuerySingleOrDefaultAsync<Guid?>(
                    new CommandDefinition(
                        "SELECT registration_uuid FROM normalization.recording_content_materialization_registrations WHERE source_uuid=@lineage",
                        new { lineage = sourceUuid },
                        t,
                        cancellationToken: ct
                    )
                );
                if (registrationUuid == null)
                {
                    registrationUuid = Guid.NewGuid();
                    await c.ExecuteAsync(
                        new CommandDefinition(
                            "INSERT INTO normalization.recording_content_materialization_registrations(registration_uuid,source_uuid) VALUES(@uuid,@lineage)",
                            new { uuid = registrationUuid, lineage = sourceUuid },
                            t,
                            cancellationToken: ct
                        )
                    );
                }
                var registration = (await ReadRegistration(c, t, registrationUuid.Value, ct))!;
                await RequireSelection(
                    c,
                    t,
                    registration,
                    request.RepertoireResolutionAttemptUuid,
                    ct
                );
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "INSERT INTO normalization.recording_content_materialization_attempts(attempt_uuid,registration_uuid,registration,request,selected_repertoire_attempt_uuid,processing_version) VALUES(@uuid,@registrationUuid,@registration::jsonb,@json::jsonb,@selected,@version) ON CONFLICT DO NOTHING",
                        new
                        {
                            uuid,
                            registrationUuid,
                            registration = Serialize(registration, MaximumDecisionPageBytes),
                            json,
                            selected = request.RepertoireResolutionAttemptUuid,
                            version,
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                var current = (await Read(c, t, uuid, false, true, ct))!;
                if (
                    current.Request != request
                    || current.RetrySelection != null
                    || current.Registration.RegistrationUuid != registrationUuid
                )
                {
                    throw new NormalizationConflictException(
                        "The materialization attempt is bound to another source request."
                    );
                }
                RequireVersion(current.ProcessingVersion, version, current.Result);
                return current;
            },
            ct
        );
    }

    internal Task<RecordingContentMaterializationAttempt> GetOrCreateRetryAsync(
        Guid uuid,
        Guid registrationUuid,
        Guid previousAttemptUuid,
        string version,
        CancellationToken ct = default
    )
    {
        if (uuid == Guid.Empty || uuid == previousAttemptUuid || string.IsNullOrWhiteSpace(version))
        {
            throw new ArgumentException("A retry requires a new UUID and processing version.");
        }
        return InTransaction(
            async (c, t) =>
            {
                var existing = await Read(c, t, uuid, true, true, ct);
                if (existing != null)
                {
                    if (
                        existing.Request != null
                        || existing.RetrySelection
                            != new MusicMaterializationRetrySelection(
                                registrationUuid,
                                previousAttemptUuid
                            )
                    )
                    {
                        throw new NormalizationConflictException(
                            "The retry UUID is bound to another selection header."
                        );
                    }
                    RequireVersion(existing.ProcessingVersion, version, existing.Result);
                    return existing;
                }
                var registration =
                    await ReadRegistration(c, t, registrationUuid, ct)
                    ?? throw new ArgumentException("Unknown materialization registration.");
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "SELECT pg_advisory_xact_lock(hashtextextended(@key,0))",
                        new
                        {
                            key = "normalization:recording_content_materialization:"
                                + registration.SourceUuid.ToString("D"),
                        },
                        t,
                        cancellationToken: ct
                    )
                );

                // Another invocation may have created this exact retry while the lineage lock waited.
                existing = await Read(c, t, uuid, true, true, ct);
                if (existing != null)
                {
                    if (
                        existing.Request != null
                        || existing.RetrySelection
                            != new MusicMaterializationRetrySelection(
                                registrationUuid,
                                previousAttemptUuid
                            )
                    )
                    {
                        throw new NormalizationConflictException(
                            "The retry UUID is bound to another selection header."
                        );
                    }
                    RequireVersion(existing.ProcessingVersion, version, existing.Result);
                    return existing;
                }

                await RequireCurrentRetryParent(
                    c,
                    t,
                    registrationUuid,
                    previousAttemptUuid,
                    version,
                    ct
                );
                registration = (await ReadRegistration(c, t, registrationUuid, ct))!;
                var retry = new MusicMaterializationRetrySelection(
                    registrationUuid,
                    previousAttemptUuid
                );
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "INSERT INTO normalization.recording_content_materialization_attempts(attempt_uuid,registration_uuid,registration,retry_selection,previous_attempt_uuid,processing_version) VALUES(@uuid,@registrationUuid,@registration::jsonb,@retry::jsonb,@previousAttemptUuid,@version) ON CONFLICT DO NOTHING",
                        new
                        {
                            uuid,
                            registrationUuid,
                            registration = Serialize(registration, MaximumDecisionPageBytes),
                            retry = Serialize(retry, MaximumDecisionPageBytes),
                            previousAttemptUuid,
                            version,
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                var current = (await Read(c, t, uuid, false, true, ct))!;
                if (current.RetrySelection != retry || current.Request != null)
                {
                    throw new NormalizationConflictException(
                        "The retry UUID is bound to another selection header."
                    );
                }
                RequireVersion(current.ProcessingVersion, version, current.Result);
                return current;
            },
            ct
        );
    }

    internal Task<RecordingContentMaterializationAttempt> SelectRepertoireAttemptAsync(
        RecordingContentMaterializationAttempt expected,
        Guid selectedUuid,
        CancellationToken ct = default
    ) =>
        InTransaction(
            async (c, t) =>
            {
                var current = await RequireAttempt(c, t, expected.AttemptUuid, ct);
                if (current.SelectedRepertoireAttemptUuid is { } selected)
                {
                    if (selected != selectedUuid)
                    {
                        throw new NormalizationConflictException(
                            "A selected repertoire account is immutable."
                        );
                    }
                    return current;
                }
                if (!SameCheckpoint(current, expected) || current.Result != null)
                {
                    return current;
                }
                if (current.RetrySelection == null)
                {
                    throw new NormalizationConflictException(
                        "Only an internal retry can wait for account selection."
                    );
                }
                await RequireSelection(c, t, current.Registration, selectedUuid, ct);
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "UPDATE normalization.recording_content_materialization_attempts SET selected_repertoire_attempt_uuid=@selectedUuid,checkpoint_revision=checkpoint_revision+1 WHERE attempt_uuid=@uuid",
                        new { selectedUuid, uuid = current.AttemptUuid },
                        t,
                        cancellationToken: ct
                    )
                );
                return (await Read(c, t, current.AttemptUuid, false, true, ct))!;
            },
            ct
        );

    internal Task<RecordingContentMaterializationAttempt> SavePreparationAsync(
        RecordingContentMaterializationAttempt expected,
        RecordingContentMaterializationPreparation preparation,
        CancellationToken ct = default
    )
    {
        if (preparation.ContextOmitted || preparation.Context == null || preparation.Image == null)
        {
            throw new ArgumentException("A summary cannot supply materialization authority.");
        }
        var json = Serialize(preparation, MaximumPreparationBytes);
        var frozen = JsonSerializer.Deserialize<RecordingContentMaterializationPreparation>(json)!;
        if (
            frozen.Decisions.Count > 20000
            || frozen.Decisions.Select(value => value.DecisionUuid).Distinct().Count()
                != frozen.Decisions.Count
            || frozen.Decisions.Select(value => value.Key).Distinct(StringComparer.Ordinal).Count()
                != frozen.Decisions.Count
        )
        {
            throw new ArgumentException(
                "Materialization decisions require bounded distinct identifiers and source keys."
            );
        }
        Serialize(frozen.Image, MaximumImageBytes);
        return InTransaction(
            async (c, t) =>
            {
                var current = await RequireAttempt(c, t, expected.AttemptUuid, ct);
                if (current.Preparation != null)
                {
                    RequireSame(
                        current.Preparation,
                        frozen,
                        "The original materialization preparation is immutable."
                    );
                    return current;
                }
                if (current.Result != null || !SameCheckpoint(current, expected))
                {
                    return current;
                }
                if (
                    current.SelectedRepertoireAttemptUuid
                    != frozen.Context!.Consumption.Attempt.AttemptUuid
                )
                {
                    throw new NormalizationConflictException(
                        "Preparation differs from the saved completed repertoire selection."
                    );
                }
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "UPDATE normalization.recording_content_materialization_attempts SET preparation=@json::jsonb,checkpoint_revision=checkpoint_revision+1 WHERE attempt_uuid=@uuid",
                        new { uuid = current.AttemptUuid, json },
                        t,
                        cancellationToken: ct
                    )
                );
                for (var ordinal = 0; ordinal < frozen.Decisions.Count; ordinal++)
                {
                    var decision = frozen.Decisions[ordinal];
                    await c.ExecuteAsync(
                        new CommandDefinition(
                            "INSERT INTO normalization.recording_content_materialization_decisions(uuid,attempt_uuid,ordinal,decision_key,decision) VALUES(@DecisionUuid,@uuid,@ordinal,@Key,@decision::jsonb)",
                            new
                            {
                                decision.DecisionUuid,
                                uuid = current.AttemptUuid,
                                ordinal,
                                decision.Key,
                                decision = Serialize<RecordingContentMaterializationDecision>(
                                    decision,
                                    MaximumDecisionPageBytes
                                ),
                            },
                            t,
                            cancellationToken: ct
                        )
                    );
                }
                return (await Read(c, t, current.AttemptUuid, false, true, ct))!;
            },
            ct
        );
    }

    internal Task<RecordingContentMaterializationAttempt> SaveTicketAsync(
        RecordingContentMaterializationAttempt expected,
        NormalizationWorkTicket ticket,
        CancellationToken ct = default
    ) => SaveAdmissionAsync(expected, ticket, null, ct);

    internal Task<RecordingContentMaterializationAttempt> SaveFailedAdmissionAsync(
        RecordingContentMaterializationAttempt expected,
        NormalizationWorkTicket ticket,
        string reason,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 1024)
        {
            throw new ArgumentException("A failed admission requires a bounded reason.");
        }
        return SaveAdmissionAsync(expected, ticket, reason, ct);
    }

    private Task<RecordingContentMaterializationAttempt> SaveAdmissionAsync(
        RecordingContentMaterializationAttempt expected,
        NormalizationWorkTicket ticket,
        string? reason,
        CancellationToken ct
    )
    {
        var json = Serialize(ticket, MaximumDecisionPageBytes);
        var frozen = JsonSerializer.Deserialize<NormalizationWorkTicket>(json)!;
        return InTransaction(
            async (c, t) =>
            {
                var current = await RequireAttempt(c, t, expected.AttemptUuid, ct);
                if (current.Ticket != null)
                {
                    RequireSame(
                        current.Ticket,
                        frozen,
                        "The saved materialization admission ticket is immutable."
                    );
                    if (reason == null || current.AdmissionFailureReason != null)
                    {
                        return current;
                    }
                }
                if (current.Result != null || !SameCheckpoint(current, expected))
                {
                    return current;
                }
                if (current.Preparation == null || current.AdmissionFailureReason != null)
                {
                    throw new NormalizationConflictException(
                        "Admission requires an unfinished full preparation."
                    );
                }
                await RequireActualRun(c, t, current, frozen, ct);
                if (reason == null)
                {
                    RequireTicket(current, frozen);
                }
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "UPDATE normalization.recording_content_materialization_attempts SET ticket=@json::jsonb,admitted_work_uuid=attempt_uuid,admission_failure_reason=@reason,checkpoint_revision=checkpoint_revision+1 WHERE attempt_uuid=@uuid",
                        new
                        {
                            uuid = current.AttemptUuid,
                            json,
                            reason,
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                return (await Read(c, t, current.AttemptUuid, false, true, ct))!;
            },
            ct
        );
    }

    internal Task<RecordingContentMaterializationAttempt> CompleteAsync(
        RecordingContentMaterializationAttempt expected,
        MusicMaterializationResult result,
        CancellationToken ct = default
    )
    {
        var json = Serialize(result, MaximumDecisionPageBytes);
        var frozen = JsonSerializer.Deserialize<MusicMaterializationResult>(json)!;
        if (
            frozen.AttemptUuid != expected.AttemptUuid
            || frozen.Disposition == MusicMaterializationDisposition.Pending
            || string.IsNullOrWhiteSpace(frozen.Reason)
        )
        {
            throw new ArgumentException("Completion requires this attempt's terminal result.");
        }
        return InTransaction(
            async (c, t) =>
            {
                var current = await RequireAttempt(c, t, expected.AttemptUuid, ct);
                if (current.Result != null || !SameCheckpoint(current, expected))
                {
                    return current;
                }
                if (frozen.Disposition == MusicMaterializationDisposition.Completed)
                {
                    RequireCompletedImage(current);
                }
                await SaveResult(c, t, current, frozen, ct);
                return (await Read(c, t, current.AttemptUuid, false, true, ct))!;
            },
            ct
        );
    }

    private static bool SameCheckpoint(
        RecordingContentMaterializationAttempt left,
        RecordingContentMaterializationAttempt right
    ) =>
        left.AttemptUuid == right.AttemptUuid
        && left.CheckpointRevision == right.CheckpointRevision
        && left.Registration == right.Registration
        && left.Request == right.Request
        && left.RetrySelection == right.RetrySelection
        && left.ProcessingVersion == right.ProcessingVersion;

    private static void RequireVersion(
        string saved,
        string requested,
        MusicMaterializationResult? result
    )
    {
        if (result == null && saved != requested)
        {
            throw new NormalizationConflictException(
                "An unfinished materialization requires its original processor version."
            );
        }
    }

    internal static bool Same<T>(T left, T right) =>
        JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(left),
            JsonSerializer.SerializeToElement(right)
        );

    private static void RequireSame<T>(T left, T right, string message)
    {
        if (!Same(left, right))
        {
            throw new NormalizationConflictException(message);
        }
    }

    private static string Serialize<T>(T value, int maximumBytes)
    {
        var json = JsonSerializer.Serialize(value);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > maximumBytes)
        {
            throw new ArgumentException(
                "Materialization representation exceeds its bounded capacity profile."
            );
        }
        return json;
    }

    private Task<T> InTransaction<T>(
        Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> apply,
        CancellationToken ct
    ) =>
        db.WithWriteConnection<T>(async connection =>
        {
            var c = (NpgsqlConnection)connection;
            await using var t = await c.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            var result = await apply(c, t);
            ct.ThrowIfCancellationRequested();
            await t.CommitAsync(ct);
            return result;
        });
}
