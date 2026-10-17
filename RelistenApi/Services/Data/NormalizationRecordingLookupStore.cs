using System;
using System.Data;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

/// <summary>
/// Retains lookup recipes, immutable attempts and delivery independently of accepted extractions.
/// Store transactions lock only attempt/delivery rows; they never enter WorkService. Publication
/// owns work locks first and then writes this store through the supplied transaction.
/// </summary>
public sealed partial class NormalizationRecordingLookupStore(DbService db)
{
    private const string RegistrationColumns = """
        l.uuid AS "LookupUuid",l.mode AS "Mode",l.provider AS "Provider",
        l.resource_type AS "ResourceType",l.provider_key AS "ProviderKey",
        l.expected_artist_uuid AS "ExpectedArtistUuid"
        """;
    private const string AttemptColumns = """
        a.attempt_uuid AS "AttemptUuid",a.processing_version AS "ProcessingVersion",
        a.request::text AS "RequestJson",a.preparation::text AS "PreparationJson",a.result::text AS "ResultJson"
        """;

    /// <summary>
    /// Registers the logical target once and creates an exact attempt header. Another input
    /// revision is a new attempt of the same registration, never a replacement of its history.
    /// </summary>
    public Task<NormalizationRecordingLookupAttempt> GetOrCreateAttemptAsync(
        Guid attemptUuid,
        NormalizationRecordingLookupRequest request,
        string processingVersion,
        CancellationToken ct = default
    )
    {
        RequireUuid(attemptUuid, nameof(attemptUuid));
        ArgumentException.ThrowIfNullOrWhiteSpace(processingVersion);
        var requestJson = JsonSerializer.Serialize(request);
        request = JsonSerializer.Deserialize<NormalizationRecordingLookupRequest>(requestJson)!;
        ValidateRequest(request);
        return InTransaction(
            async (c, t) =>
            {
                if (request.Input != null)
                {
                    await RequireInputReference(c, t, request.Input, ct);
                }
                foreach (var support in request.SupportingInputs ?? [])
                {
                    await RequireInputReference(c, t, support, ct);
                }
                await c.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO normalization.recording_lookups(mode,provider,resource_type,provider_key,expected_artist_uuid)
                        VALUES(@mode,@Provider,@ResourceType,@ProviderKey,@ExpectedArtistUuid)
                        ON CONFLICT DO NOTHING
                        """,
                        new
                        {
                            mode = request.Mode.ToString(),
                            request.Resource.Provider,
                            request.Resource.ResourceType,
                            request.Resource.ProviderKey,
                            request.ExpectedArtistUuid,
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                var lookupUuid = await c.QuerySingleAsync<Guid>(
                    new CommandDefinition(
                        """
                        SELECT uuid FROM normalization.recording_lookups
                        WHERE mode=@mode AND provider=@Provider AND resource_type=@ResourceType
                            AND provider_key=@ProviderKey AND expected_artist_uuid IS NOT DISTINCT FROM @ExpectedArtistUuid
                        """,
                        new
                        {
                            mode = request.Mode.ToString(),
                            request.Resource.Provider,
                            request.Resource.ResourceType,
                            request.Resource.ProviderKey,
                            request.ExpectedArtistUuid,
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                await c.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO normalization.recording_lookup_attempts(attempt_uuid,lookup_uuid,processing_version,request,input_revision_uuid)
                        VALUES(@attemptUuid,@lookupUuid,@processingVersion,@requestJson::jsonb,@inputRevisionUuid)
                        ON CONFLICT DO NOTHING
                        """,
                        new
                        {
                            attemptUuid,
                            lookupUuid,
                            processingVersion,
                            requestJson,
                            inputRevisionUuid = request.Input?.InputRevisionUuid,
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                var row = await RequireAttempt(c, t, attemptUuid, ct);
                if (
                    row.LookupUuid != lookupUuid
                    || row.ProcessingVersion != processingVersion
                    || !SameJson(row.RequestJson, requestJson)
                )
                {
                    throw new NormalizationConflictException(
                        "Lookup attempt UUID already belongs to a different request."
                    );
                }
                return row.ToAttempt();
            },
            ct
        );
    }

    /// <summary>Returns immutable preparation and terminal result, or null for an unknown attempt.</summary>
    public Task<NormalizationRecordingLookupAttempt?> GetAttemptAsync(
        Guid attemptUuid,
        CancellationToken ct = default
    ) =>
        db.WithWriteConnection<NormalizationRecordingLookupAttempt?>(async c =>
            (await ReadAttempt((NpgsqlConnection)c, null, attemptUuid, false, ct))?.ToAttempt()
        );

    /// <summary>Reads the durable lookup target without interpreting current catalog state.</summary>
    public Task<NormalizationRecordingLookupRegistration?> GetRegistrationAsync(
        Guid lookupUuid,
        CancellationToken ct = default
    ) =>
        db.WithWriteConnection<NormalizationRecordingLookupRegistration?>(async c =>
            (
                await c.QuerySingleOrDefaultAsync<RegistrationRow>(
                    new CommandDefinition(
                        $"SELECT {RegistrationColumns} FROM normalization.recording_lookups l WHERE l.uuid=@lookupUuid",
                        new { lookupUuid },
                        cancellationToken: ct
                    )
                )
            )?.ToRegistration()
        );

    /// <summary>
    /// Reads the last successfully published lookup through its work-owner receipt. A stale
    /// attempt cannot replace that outcome. This read alone does not prove current freshness.
    /// </summary>
    public Task<NormalizationRecordingLookupAttempt?> GetCurrentAsync(
        Guid lookupUuid,
        CancellationToken ct = default
    ) =>
        db.WithWriteConnection<NormalizationRecordingLookupAttempt?>(async c =>
            (
                await c.QuerySingleOrDefaultAsync<AttemptRow>(
                    new CommandDefinition(
                        $"""
                        SELECT {RegistrationColumns},{AttemptColumns}
                        FROM normalization.recording_lookups l JOIN normalization.work_subjects owner
                            ON owner.domain='lookups' AND owner.subject_kind='recording' AND owner.subject_uuid=l.uuid
                        JOIN normalization.recording_lookup_attempts a ON a.lookup_uuid=l.uuid AND a.attempt_uuid=owner.published_work_uuid
                        WHERE l.uuid=@lookupUuid AND a.disposition='Published'
                        """,
                        new { lookupUuid },
                        cancellationToken: ct
                    )
                )
            )?.ToAttempt()
        );

    /// <summary>
    /// Retains the first complete preparation before work admission. Concurrent callers resume
    /// that winner; input receipts and clocks are never refreshed inside the store.
    /// </summary>
    public Task<NormalizationRecordingLookupAttempt> SavePreparationAsync(
        Guid attemptUuid,
        NormalizationRecordingLookupPreparation preparation,
        CancellationToken ct = default
    )
    {
        var json = JsonSerializer.Serialize(preparation);
        var frozen = JsonSerializer.Deserialize<NormalizationRecordingLookupPreparation>(json)!;
        return InTransaction(
            async (c, t) =>
            {
                var row = await RequireAttempt(c, t, attemptUuid, ct);
                if (row.PreparationJson != null || row.ResultJson != null)
                {
                    return row.ToAttempt();
                }
                ValidatePreparation(row.ToAttempt(), frozen);
                await RequirePreparationInputs(c, t, row.ToAttempt(), frozen, ct);
                await c.ExecuteAsync(
                    new CommandDefinition(
                        """
                        UPDATE normalization.recording_lookup_attempts
                        SET preparation=@json::jsonb,preparation_fetch_uuid=@FetchUuid WHERE attempt_uuid=@attemptUuid
                        """,
                        new
                        {
                            json,
                            frozen.FetchUuid,
                            attemptUuid,
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                row.PreparationJson = json;
                return row.ToAttempt();
            },
            ct
        );
    }

    /// <summary>
    /// Records stale work only against the preparation the caller actually observed. A winning
    /// concurrent preparation is returned unfinished; an existing terminal outcome stays fixed.
    /// </summary>
    public Task<NormalizationRecordingLookupAttempt> CompleteStaleAsync(
        NormalizationRecordingLookupAttempt expected,
        string reason,
        CancellationToken ct = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var expectedJson = JsonSerializer.Serialize(expected);
        var frozen = JsonSerializer.Deserialize<NormalizationRecordingLookupAttempt>(expectedJson)!;
        return InTransaction(
            async (c, t) =>
            {
                var row = await RequireAttempt(c, t, frozen.AttemptUuid, ct);
                RequireHeader(row, frozen);
                if (
                    row.ResultJson != null
                    || !SameJson(
                        row.PreparationJson,
                        frozen.Preparation == null
                            ? null
                            : JsonSerializer.Serialize(frozen.Preparation)
                    )
                )
                {
                    return row.ToAttempt();
                }
                var result = new NormalizationRecordingLookupResult(
                    row.AttemptUuid,
                    row.LookupUuid,
                    NormalizationRecordingLookupDisposition.Stale,
                    null,
                    reason
                );
                row.ResultJson = JsonSerializer.Serialize(result);
                await c.ExecuteAsync(
                    new CommandDefinition(
                        """
                        UPDATE normalization.recording_lookup_attempts SET disposition='Stale',result=@ResultJson::jsonb,
                            completed_at=clock_timestamp() WHERE attempt_uuid=@AttemptUuid
                        """,
                        row,
                        t,
                        cancellationToken: ct
                    )
                );
                return row.ToAttempt();
            },
            ct
        );
    }

    private Task<T> InTransaction<T>(
        Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> action,
        CancellationToken ct
    ) =>
        db.WithWriteConnection(async c =>
        {
            ct.ThrowIfCancellationRequested();
            await using var t = await ((NpgsqlConnection)c).BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                ct
            );
            var result = await action((NpgsqlConnection)c, t);
            await t.CommitAsync(ct);
            return result;
        });

    private static async Task<AttemptRow> RequireAttempt(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid uuid,
        CancellationToken ct
    ) =>
        await ReadAttempt(c, t, uuid, true, ct)
        ?? throw new ArgumentException("Unknown recording lookup attempt.", nameof(uuid));

    private static Task<AttemptRow?> ReadAttempt(
        NpgsqlConnection c,
        NpgsqlTransaction? t,
        Guid attemptUuid,
        bool forUpdate,
        CancellationToken ct
    ) =>
        c.QuerySingleOrDefaultAsync<AttemptRow>(
            new CommandDefinition(
                $"SELECT {RegistrationColumns},{AttemptColumns} FROM normalization.recording_lookup_attempts a JOIN normalization.recording_lookups l ON l.uuid=a.lookup_uuid WHERE a.attempt_uuid=@attemptUuid {(forUpdate ? "FOR UPDATE OF a" : "")}",
                new { attemptUuid },
                t,
                cancellationToken: ct
            )
        );

    private static void RequireHeader(AttemptRow row, NormalizationRecordingLookupAttempt expected)
    {
        if (
            !SameJson(
                JsonSerializer.Serialize(row.ToRegistration()),
                JsonSerializer.Serialize(expected.Registration)
            )
            || row.ProcessingVersion != expected.ProcessingVersion
            || !SameJson(row.RequestJson, JsonSerializer.Serialize(expected.Request))
        )
        {
            throw new NormalizationConflictException(
                "Lookup attempt does not match its immutable request."
            );
        }
    }

    private static bool SameJson(string? left, string? right)
    {
        if (left == null || right == null)
        {
            return left == right;
        }
        using var a = JsonDocument.Parse(left);
        using var b = JsonDocument.Parse(right);
        return JsonElement.DeepEquals(a.RootElement, b.RootElement);
    }

    private static void RequireUuid(Guid uuid, string parameter)
    {
        if (uuid == Guid.Empty)
        {
            throw new ArgumentException("A UUID is required.", parameter);
        }
    }

    private class RegistrationRow
    {
        public Guid LookupUuid { get; init; }
        public string Mode { get; init; } = null!;
        public string Provider { get; init; } = null!;
        public string ResourceType { get; init; } = null!;
        public string ProviderKey { get; init; } = null!;
        public Guid? ExpectedArtistUuid { get; init; }

        public NormalizationRecordingLookupRegistration ToRegistration() =>
            new(
                LookupUuid,
                Enum.Parse<NormalizationRecordingLookupMode>(Mode),
                new(Provider, ResourceType, ProviderKey),
                ExpectedArtistUuid
            );
    }

    private sealed class AttemptRow : RegistrationRow
    {
        public Guid AttemptUuid { get; init; }
        public string ProcessingVersion { get; init; } = null!;
        public string RequestJson { get; init; } = null!;
        public string? PreparationJson { get; set; }
        public string? ResultJson { get; set; }

        public NormalizationRecordingLookupAttempt ToAttempt() =>
            new(
                AttemptUuid,
                ToRegistration(),
                JsonSerializer.Deserialize<NormalizationRecordingLookupRequest>(RequestJson)!,
                ProcessingVersion,
                PreparationJson == null
                    ? null
                    : JsonSerializer.Deserialize<NormalizationRecordingLookupPreparation>(
                        PreparationJson
                    ),
                ResultJson == null
                    ? null
                    : JsonSerializer.Deserialize<NormalizationRecordingLookupResult>(ResultJson)
            );
    }
}
