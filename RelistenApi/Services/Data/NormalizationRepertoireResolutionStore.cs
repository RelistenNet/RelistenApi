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

/// <summary>Retains source lineage, immutable page preparations and atomic domain publication receipts.</summary>
public sealed partial class NormalizationRepertoireResolutionStore(DbService db)
{
    internal const int MaximumCheckpointBytes = 32 * 1024 * 1024;
    internal const int MaximumStages = 2000;
    internal const int MaximumPreparationBytes = 128 * 1024 * 1024;
    internal const int MaximumSummaryBytes = 256 * 1024 * 1024;

    public Task<RepertoireResolutionAttempt?> GetAsync(
        Guid attemptUuid,
        CancellationToken ct = default
    ) => InTransaction((c, t) => Read(c, t, attemptUuid, true, ct, includeSourceClaims: false), ct);

    /// <summary>Processor-only preparation; public discovery omits the original claim array in SQL.</summary>
    internal Task<RepertoireResolutionAttempt?> GetProcessingAsync(
        Guid attemptUuid,
        CancellationToken ct = default
    ) => InTransaction((c, t) => Read(c, t, attemptUuid, true, ct), ct);

    public Task<RepertoireResolutionRegistration?> GetRegistrationAsync(
        Guid registrationUuid,
        CancellationToken ct = default
    ) =>
        db.WithWriteConnection<RepertoireResolutionRegistration?>(c =>
            ReadRegistration((NpgsqlConnection)c, null, registrationUuid, ct)
        );

    internal Task<RepertoireResolutionAttempt> GetOrCreateAsync(
        Guid attemptUuid,
        RepertoireResolutionRequest request,
        string version,
        NormalizationResourceKey sourceLineage,
        Guid? previousAttemptUuid = null,
        CancellationToken ct = default
    )
    {
        if (
            attemptUuid == Guid.Empty
            || previousAttemptUuid == attemptUuid
            || string.IsNullOrWhiteSpace(version)
            || string.IsNullOrWhiteSpace(sourceLineage.Provider)
            || string.IsNullOrWhiteSpace(sourceLineage.ResourceType)
            || string.IsNullOrWhiteSpace(sourceLineage.ProviderKey)
        )
        {
            throw new ArgumentException(
                "An attempt, authenticated source lineage and processing version are required."
            );
        }
        var requestJson = Serialize(request);
        var frozen = JsonSerializer.Deserialize<RepertoireResolutionRequest>(requestJson)!;
        return InTransaction(
            async (c, t) =>
            {
                var existing = await Read(c, t, attemptUuid, false, ct);
                if (existing != null)
                {
                    RequireRequest(existing, frozen, version, sourceLineage, previousAttemptUuid);
                    return existing;
                }
                // Serialize first enrollment by qualified lineage and expected-artist scope. No
                // completed publication locks an existing attempt while holding this lineage lock.
                var lineageKey = Serialize(new { sourceLineage, frozen.ExpectedArtistUuid });
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "SELECT pg_advisory_xact_lock(hashtextextended(@lineageKey,0))",
                        new { lineageKey },
                        t,
                        cancellationToken: ct
                    )
                );
                var registrationUuid = await c.QuerySingleOrDefaultAsync<Guid?>(
                    new CommandDefinition(
                        "SELECT registration_uuid FROM normalization.repertoire_resolution_registrations WHERE provider=@Provider AND resource_type=@ResourceType AND provider_key=@ProviderKey AND expected_artist_uuid IS NOT DISTINCT FROM @ExpectedArtistUuid::uuid",
                        new
                        {
                            sourceLineage.Provider,
                            sourceLineage.ResourceType,
                            sourceLineage.ProviderKey,
                            frozen.ExpectedArtistUuid,
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                if (registrationUuid == null)
                {
                    registrationUuid = Guid.NewGuid();
                    await c.ExecuteAsync(
                        new CommandDefinition(
                            "INSERT INTO normalization.repertoire_resolution_registrations(registration_uuid,provider,resource_type,provider_key,expected_artist_uuid) VALUES(@uuid,@Provider,@ResourceType,@ProviderKey,@ExpectedArtistUuid)",
                            new
                            {
                                uuid = registrationUuid,
                                sourceLineage.Provider,
                                sourceLineage.ResourceType,
                                sourceLineage.ProviderKey,
                                frozen.ExpectedArtistUuid,
                            },
                            t,
                            cancellationToken: ct
                        )
                    );
                }
                var registration = (await ReadRegistration(c, t, registrationUuid.Value, ct))!;
                if (previousAttemptUuid is { } previousUuid)
                {
                    var previous =
                        await Read(c, t, previousUuid, false, ct)
                        ?? throw new ArgumentException("Unknown previous repertoire attempt.");
                    if (previous.Registration.RegistrationUuid != registrationUuid)
                    {
                        throw new NormalizationConflictException(
                            "A retry must retain its authenticated source lineage and artist scope."
                        );
                    }
                }
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "INSERT INTO normalization.repertoire_resolution_attempts(attempt_uuid,registration_uuid,registration,request,processing_version,previous_attempt_uuid) VALUES(@attemptUuid,@registrationUuid,@registration::jsonb,@requestJson::jsonb,@version,@previousAttemptUuid) ON CONFLICT DO NOTHING",
                        new
                        {
                            attemptUuid,
                            registrationUuid,
                            registration = Serialize(registration),
                            requestJson,
                            version,
                            previousAttemptUuid,
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                var current = (await Read(c, t, attemptUuid, false, ct))!;
                RequireRequest(current, frozen, version, sourceLineage, previousAttemptUuid);
                return current;
            },
            ct
        );
    }

    private static void RequireRequest(
        RepertoireResolutionAttempt current,
        RepertoireResolutionRequest request,
        string version,
        NormalizationResourceKey lineage,
        Guid? previous
    )
    {
        if (
            !Same(current.Request, request)
            || current.Registration.Lineage != lineage
            || current.PreviousAttemptUuid != previous
            || current.Result == null && current.ProcessingVersion != version
        )
        {
            throw new NormalizationConflictException(
                "Repertoire attempt UUID is bound to another request or unfinished version."
            );
        }
    }

    internal Task<RepertoireResolutionAttempt> SavePreparationAsync(
        RepertoireResolutionAttempt expected,
        RepertoireResolutionPreparation preparation,
        CancellationToken ct = default
    )
    {
        var json = Serialize(preparation, MaximumPreparationBytes);
        var frozen = JsonSerializer.Deserialize<RepertoireResolutionPreparation>(json)!;
        if (
            frozen.Source.ClaimsOmitted
            || frozen.Source.TotalClaims != null
            || frozen.Source.Claims.Count > 50000
            || frozen.PageSize is < 100 or > 1000
            || frozen.Source.Claims.Select(value => value.ClaimUuid).Distinct().Count()
                != frozen.Source.Claims.Count
            || frozen
                .Source.Claims.Select(value => value.CorrespondenceKey)
                .Distinct(StringComparer.Ordinal)
                .Count() != frozen.Source.Claims.Count
        )
        {
            throw new ArgumentException(
                "Preparation requires bounded, distinct source claims and page size."
            );
        }
        return InTransaction(
            async (c, t) =>
            {
                var current = await RequireAttempt(c, t, expected.AttemptUuid, ct);
                if (!SameCheckpoint(current, expected) || current.Result != null)
                {
                    return current;
                }
                if (current.Preparation != null)
                {
                    RequireSame(
                        current.Preparation,
                        frozen,
                        "Original repertoire preparation is immutable."
                    );
                    return current;
                }
                if (current.Registration.Lineage != frozen.Source.Lineage)
                {
                    throw new NormalizationConflictException(
                        "Preparation belongs to another source lineage."
                    );
                }
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "UPDATE normalization.repertoire_resolution_attempts SET preparation=@json::jsonb,checkpoint_revision=checkpoint_revision+1 WHERE attempt_uuid=@uuid",
                        new { uuid = current.AttemptUuid, json },
                        t,
                        cancellationToken: ct
                    )
                );
                return (await Read(c, t, current.AttemptUuid, false, ct))!;
            },
            ct
        );
    }

    internal Task<RepertoireResolutionAttempt> SaveStageAsync(
        RepertoireResolutionAttempt expected,
        RepertoireResolutionStage stage,
        CancellationToken ct = default
    )
    {
        var json = Serialize(stage);
        var frozen = JsonSerializer.Deserialize<RepertoireResolutionStage>(json)!;
        if (
            frozen.StageUuid == Guid.Empty
            || frozen.WorkUuid == Guid.Empty
            || frozen.Ticket != null
            || frozen.Receipt != null
            || (
                frozen.Kind == RepertoireResolutionStageKind.AcknowledgeSource
                    ? frozen.PageIndex != -1
                    : frozen.PageIndex < 0
            )
            || frozen.ContextOmitted
            || frozen.FailureReason != null
            || frozen.OwnEffects != null
            || !Enum.IsDefined(frozen.Kind)
        )
        {
            throw new ArgumentException("A stable typed stage is saved before Work admission.");
        }
        return InTransaction(
            async (c, t) =>
            {
                var current = await RequireAttempt(c, t, expected.AttemptUuid, ct);
                var existing = current.Stages.SingleOrDefault(value =>
                    value.StageUuid == frozen.StageUuid
                );
                if (existing != null)
                {
                    existing = await ReadStage(c, t, current.AttemptUuid, existing.StageUuid, ct);
                }
                if (existing != null)
                {
                    RequireSame(
                        existing with
                        {
                            Ticket = null,
                            Receipt = null,
                            FailureReason = null,
                            OwnEffects = null,
                        },
                        frozen,
                        "A repertoire page stage is immutable."
                    );
                    return current;
                }
                if (current.Result != null || !SameCheckpoint(current, expected))
                {
                    return current;
                }
                if (
                    current.Preparation == null
                    || current.Stages.Count >= MaximumStages
                    || current.Stages.Any(value =>
                        (
                            value.Receipt == null
                            && !value.Superseded
                            && value.FailureReason == null
                            && value.Kind != RepertoireResolutionStageKind.DecisionPage
                        )
                        || value.Kind == RepertoireResolutionStageKind.AcknowledgeSource
                    )
                )
                {
                    throw new NormalizationConflictException(
                        "Finish the previous bounded page stage before saving another."
                    );
                }
                var claims = current.Preparation.Source.Claims.ToDictionary(value =>
                    value.ClaimUuid
                );
                if (
                    frozen.Decisions.Select(value => value.ClaimUuid).Distinct().Count()
                        != frozen.Decisions.Count
                    || frozen.Decisions.Any(value =>
                        !claims.TryGetValue(value.ClaimUuid, out var claim)
                        || claim.CorrespondenceKey != value.CorrespondenceKey
                    )
                )
                {
                    throw new NormalizationConflictException(
                        "Stage decisions require their exact original claim coordinates."
                    );
                }
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "INSERT INTO normalization.repertoire_resolution_stages(stage_uuid,work_uuid,attempt_uuid,ordinal,stage) VALUES(@StageUuid,@WorkUuid,@uuid,@ordinal,@json::jsonb)",
                        new
                        {
                            frozen.StageUuid,
                            frozen.WorkUuid,
                            uuid = current.AttemptUuid,
                            ordinal = current.Stages.Count,
                            json,
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                await AdvanceCheckpoint(c, t, current.AttemptUuid, ct);
                return (await Read(c, t, current.AttemptUuid, false, ct))!;
            },
            ct
        );
    }

    internal Task<RepertoireResolutionAttempt> SaveTicketAsync(
        Guid attemptUuid,
        Guid stageUuid,
        NormalizationWorkTicket ticket,
        CancellationToken ct = default
    )
    {
        var json = Serialize(ticket);
        var frozen = JsonSerializer.Deserialize<NormalizationWorkTicket>(json)!;
        return InTransaction(
            async (c, t) =>
            {
                var current = await RequireAttempt(c, t, attemptUuid, ct);
                var stage = (await ReadStage(c, t, attemptUuid, stageUuid, ct))!;
                if (stage.FailureReason != null)
                {
                    throw new NormalizationConflictException(
                        "A failed repertoire stage cannot be admitted again."
                    );
                }
                RequireTicket(stage, frozen, current.ProcessingVersion);
                if (stage.Ticket != null)
                {
                    RequireSame(
                        stage.Ticket,
                        frozen,
                        "The repertoire stage already has another admission ticket."
                    );
                    return current;
                }
                if (current.Result == null)
                {
                    await c.ExecuteAsync(
                        new CommandDefinition(
                            "UPDATE normalization.repertoire_resolution_stages SET ticket=@json::jsonb,admitted_work_uuid=work_uuid WHERE stage_uuid=@stageUuid",
                            new { stageUuid, json },
                            t,
                            cancellationToken: ct
                        )
                    );
                    await AdvanceCheckpoint(c, t, attemptUuid, ct);
                }
                return (await Read(c, t, attemptUuid, false, ct))!;
            },
            ct
        );
    }

    internal Task<RepertoireResolutionAttempt> CompleteAsync(
        RepertoireResolutionAttempt expected,
        RepertoireResolutionDisposition disposition,
        string reason,
        CancellationToken ct = default
    )
    {
        if (
            disposition == RepertoireResolutionDisposition.Pending
            || string.IsNullOrWhiteSpace(reason)
        )
        {
            throw new ArgumentException(
                "Only source acknowledgment can complete an account; other terminal results require a reason."
            );
        }
        return InTransaction(
            async (c, t) =>
            {
                var current = await RequireAttempt(c, t, expected.AttemptUuid, ct);
                if (current.Result != null || !SameCheckpoint(current, expected))
                {
                    return current;
                }
                if (disposition == RepertoireResolutionDisposition.Completed)
                {
                    throw new NormalizationConflictException(
                        "Only atomic source acknowledgment can complete a repertoire account."
                    );
                }
                var result = await BuildResult(c, t, current.AttemptUuid, disposition, reason, ct);
                await SaveResult(c, t, result, ct);
                return (await Read(c, t, current.AttemptUuid, false, ct))!;
            },
            ct
        );
    }

    private static async Task<RepertoireResolutionAttempt> RequireAttempt(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid uuid,
        CancellationToken ct
    ) =>
        await Read(c, t, uuid, true, ct)
        ?? throw new ArgumentException("Unknown repertoire resolution attempt.");

    private static async Task<RepertoireResolutionAttempt?> Read(
        NpgsqlConnection c,
        NpgsqlTransaction? t,
        Guid uuid,
        bool locked,
        CancellationToken ct,
        bool includeSourceClaims = true
    )
    {
        if (locked)
        {
            await c.ExecuteScalarAsync<Guid?>(
                new CommandDefinition(
                    "SELECT attempt_uuid FROM normalization.repertoire_resolution_attempts WHERE attempt_uuid=@uuid FOR UPDATE",
                    new { uuid },
                    t,
                    cancellationToken: ct
                )
            );
        }
        await RequireSummarySize(c, t, uuid, ct, includeSourceClaims);
        var row = await c.QuerySingleOrDefaultAsync<AttemptRow>(
            new CommandDefinition(
                "SELECT attempt_uuid AS \"AttemptUuid\",registration::text AS \"Registration\",request::text AS \"Request\",processing_version AS \"Version\",previous_attempt_uuid AS \"Previous\","
                    + (includeSourceClaims ? "preparation" : PublicPreparationProjection)
                    + "::text AS \"Preparation\",result::text AS \"Result\",checkpoint_revision AS \"CheckpointRevision\" FROM normalization.repertoire_resolution_attempts a WHERE a.attempt_uuid=@uuid",
                new { uuid },
                t,
                cancellationToken: ct
            )
        );
        if (row == null)
        {
            return null;
        }
        var stages = await c.QueryAsync<StageRow>(
            new CommandDefinition(StageSummarySql, new { uuid }, t, cancellationToken: ct)
        );
        return new(
            row.AttemptUuid,
            JsonSerializer.Deserialize<RepertoireResolutionRegistration>(row.Registration)!,
            JsonSerializer.Deserialize<RepertoireResolutionRequest>(row.Request)!,
            row.Version,
            row.Previous,
            row.Preparation == null
                ? null
                : JsonSerializer.Deserialize<RepertoireResolutionPreparation>(row.Preparation),
            stages.Select(DecodeStage).ToArray(),
            row.Result == null
                ? null
                : JsonSerializer.Deserialize<RepertoireResolutionResult>(row.Result),
            row.CheckpointRevision
        );
    }

    private static async Task<RepertoireResolutionRegistration?> ReadRegistration(
        NpgsqlConnection c,
        NpgsqlTransaction? t,
        Guid uuid,
        CancellationToken ct
    )
    {
        var row = await c.QuerySingleOrDefaultAsync<RegistrationRow>(
            new CommandDefinition(
                "SELECT registration_uuid AS \"Uuid\",provider AS \"Provider\",resource_type AS \"Type\",provider_key AS \"Key\",expected_artist_uuid AS \"Artist\",current_attempt_uuid AS \"Current\" FROM normalization.repertoire_resolution_registrations WHERE registration_uuid=@uuid",
                new { uuid },
                t,
                cancellationToken: ct
            )
        );
        return row == null
            ? null
            : new(row.Uuid, new(row.Provider, row.Type, row.Key), row.Artist, row.Current);
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

    private static bool SameCheckpoint(
        RepertoireResolutionAttempt left,
        RepertoireResolutionAttempt right
    ) =>
        left.AttemptUuid == right.AttemptUuid
        && left.CheckpointRevision == right.CheckpointRevision
        && left.ProcessingVersion == right.ProcessingVersion
        && left.PreviousAttemptUuid == right.PreviousAttemptUuid
        && left.Registration == right.Registration
        && Same(left.Request, right.Request);

    private static Task AdvanceCheckpoint(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid uuid,
        CancellationToken ct
    ) =>
        c.ExecuteAsync(
            new CommandDefinition(
                "UPDATE normalization.repertoire_resolution_attempts SET checkpoint_revision=checkpoint_revision+1 WHERE attempt_uuid=@uuid",
                new { uuid },
                t,
                cancellationToken: ct
            )
        );

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

    private static string Serialize<T>(T value, int maximumBytes = MaximumCheckpointBytes)
    {
        var json = JsonSerializer.Serialize(value);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > maximumBytes)
        {
            throw new ArgumentException(
                $"Repertoire checkpoint exceeds its {maximumBytes / (1024 * 1024)} MiB bound."
            );
        }
        return json;
    }

    private sealed record AttemptRow(
        Guid AttemptUuid,
        string Registration,
        string Request,
        string Version,
        Guid? Previous,
        string? Preparation,
        string? Result,
        long CheckpointRevision
    );

    private sealed record StageRow(
        string Stage,
        string? Ticket,
        string? Receipt,
        bool Superseded,
        string? FailureReason,
        string? OwnEffects
    );

    private sealed record RegistrationRow(
        Guid Uuid,
        string Provider,
        string Type,
        string Key,
        Guid? Artist,
        Guid? Current
    );
}
