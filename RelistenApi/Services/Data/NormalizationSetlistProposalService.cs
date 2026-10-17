using System;
using System.Data;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

/// <summary>
/// Retains setlist extraction requests, original preparations and immutable proposal checkpoints.
/// Attempt operations never acquire work locks. Publication enters with work locks already held,
/// then locks its attempt; neither path writes accepted music identities or playback data.
/// </summary>
public sealed partial class NormalizationSetlistProposalService(DbService db)
{
    public const int MaximumCheckpointBytes = 16 * 1024 * 1024;
    private const string Columns = """
        r.uuid AS "RegistrationUuid",r.provider AS "Provider",r.resource_type AS "ResourceType",
        r.provider_key AS "ProviderKey",r.expected_artist_uuid AS "ExpectedArtistUuid",
        a.attempt_uuid AS "AttemptUuid",a.processing_version AS "ProcessingVersion",
        a.request::text AS "RequestJson",a.preparation::text AS "PreparationJson",
        a.disposition AS "Disposition",a.reason AS "Reason"
        """;

    public Task<NormalizationSetlistProcessingAttempt> GetOrCreateAttemptAsync(
        Guid attemptUuid,
        NormalizationSetlistProcessingRequest request,
        string processingVersion,
        CancellationToken ct = default
    )
    {
        RequireUuid(attemptUuid);
        ArgumentException.ThrowIfNullOrWhiteSpace(processingVersion);
        ValidateRequest(request);
        var json = JsonSerializer.Serialize(request);
        return InTransaction(
            async (c, t) =>
            {
                await RequireInputReference(c, t, request.Input, ct);
                await c.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO normalization.setlist_processing_registrations(provider,resource_type,provider_key,expected_artist_uuid)
                        VALUES(@Provider,@ResourceType,@ProviderKey,@ExpectedArtistUuid) ON CONFLICT DO NOTHING
                        """,
                        new
                        {
                            request.AccountIdentity.Provider,
                            request.AccountIdentity.ResourceType,
                            request.AccountIdentity.ProviderKey,
                            request.ExpectedArtistUuid,
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                var registration = await c.QuerySingleAsync<Guid>(
                    new CommandDefinition(
                        """
                        SELECT uuid FROM normalization.setlist_processing_registrations
                        WHERE provider=@Provider AND resource_type=@ResourceType AND provider_key=@ProviderKey
                            AND expected_artist_uuid IS NOT DISTINCT FROM @ExpectedArtistUuid
                        """,
                        new
                        {
                            request.AccountIdentity.Provider,
                            request.AccountIdentity.ResourceType,
                            request.AccountIdentity.ProviderKey,
                            request.ExpectedArtistUuid,
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                await c.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO normalization.setlist_processing_attempts(attempt_uuid,registration_uuid,processing_version,request,input_revision_uuid)
                        VALUES(@attemptUuid,@registration,@processingVersion,@json::jsonb,@InputRevisionUuid) ON CONFLICT DO NOTHING
                        """,
                        new
                        {
                            attemptUuid,
                            registration,
                            processingVersion,
                            json,
                            request.Input.InputRevisionUuid,
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                var row = await RequireAttempt(c, t, attemptUuid, ct);
                if (
                    row.RegistrationUuid != registration
                    || row.ProcessingVersion != processingVersion
                    || !SameJson(row.RequestJson, json)
                )
                {
                    throw new NormalizationConflictException(
                        "Setlist attempt UUID already belongs to another request or version."
                    );
                }
                return row.ToAttempt();
            },
            ct
        );
    }

    public Task<NormalizationSetlistProcessingAttempt?> GetAttemptAsync(
        Guid attemptUuid,
        CancellationToken ct = default
    ) =>
        db.WithWriteConnection<NormalizationSetlistProcessingAttempt?>(async c =>
            (await ReadAttempt((NpgsqlConnection)c, null, attemptUuid, false, ct))?.ToAttempt()
        );

    public Task<NormalizationSetlistProcessingRegistration?> GetRegistrationAsync(
        Guid registrationUuid,
        CancellationToken ct = default
    ) =>
        db.WithWriteConnection<NormalizationSetlistProcessingRegistration?>(async c =>
        {
            var row = await c.QuerySingleOrDefaultAsync<RegistrationRow>(
                new CommandDefinition(
                    """
                    SELECT uuid AS "RegistrationUuid",provider AS "Provider",resource_type AS "ResourceType",
                        provider_key AS "ProviderKey",expected_artist_uuid AS "ExpectedArtistUuid"
                    FROM normalization.setlist_processing_registrations WHERE uuid=@registrationUuid
                    """,
                    new { registrationUuid },
                    cancellationToken: ct
                )
            );
            return row?.ToRegistration();
        });

    public Task<NormalizationSetlistProcessingAttempt?> GetLatestAttemptAsync(
        Guid registrationUuid,
        CancellationToken ct = default
    ) =>
        db.WithWriteConnection<NormalizationSetlistProcessingAttempt?>(async c =>
            (
                await c.QuerySingleOrDefaultAsync<AttemptRow>(
                    new CommandDefinition(
                        $"""
                        SELECT {Columns} FROM normalization.setlist_processing_attempts a
                        JOIN normalization.setlist_processing_registrations r ON r.uuid=a.registration_uuid
                        WHERE r.uuid=@registrationUuid ORDER BY a.created_at DESC,a.attempt_uuid DESC LIMIT 1
                        """,
                        new { registrationUuid },
                        cancellationToken: ct
                    )
                )
            )?.ToAttempt()
        );

    /// <summary>The last published batch remains readable after later attempts become stale or unresolved.</summary>
    public Task<NormalizationSetlistProposalBatch?> GetAsync(
        Guid registrationUuid,
        CancellationToken ct = default
    ) =>
        db.WithWriteConnection<NormalizationSetlistProposalBatch?>(async c =>
        {
            var json = await c.QuerySingleOrDefaultAsync<string>(
                new CommandDefinition(
                    """
                    SELECT a.batch::text FROM normalization.work_subjects s
                    JOIN normalization.setlist_processing_attempts a ON a.attempt_uuid=s.published_work_uuid
                        AND a.registration_uuid=s.subject_uuid AND a.disposition='published'
                    WHERE s.domain='extractions' AND s.subject_kind='setlist' AND s.subject_uuid=@registrationUuid
                    """,
                    new { registrationUuid },
                    cancellationToken: ct
                )
            );
            return json == null
                ? null
                : JsonSerializer.Deserialize<NormalizationSetlistProposalBatch>(json);
        });

    public Task<NormalizationSetlistProcessingAttempt> SavePreparationAsync(
        Guid attemptUuid,
        NormalizationSetlistPreparation preparation,
        CancellationToken ct = default
    )
    {
        var json = SerializeCheckpoint(preparation);
        var frozen = JsonSerializer.Deserialize<NormalizationSetlistPreparation>(json)!;
        return InTransaction(
            async (c, t) =>
            {
                var row = await RequireAttempt(c, t, attemptUuid, ct);
                if (row.PreparationJson != null || row.Disposition != null)
                {
                    return row.ToAttempt();
                }
                await ValidatePreparation(c, t, row.ToAttempt(), frozen, ct);
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "UPDATE normalization.setlist_processing_attempts SET preparation=@json::jsonb WHERE attempt_uuid=@attemptUuid",
                        new { json, attemptUuid },
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

    /// <summary>First terminal result wins; a concurrent preparation winner is returned for the caller to resume.</summary>
    public Task<NormalizationSetlistProcessingAttempt> CompleteAsync(
        NormalizationSetlistProcessingAttempt expected,
        NormalizationSetlistProcessingDisposition disposition,
        string reason,
        CancellationToken ct = default
    )
    {
        if (
            disposition
            is not (
                NormalizationSetlistProcessingDisposition.Unresolved
                or NormalizationSetlistProcessingDisposition.Stale
            )
        )
        {
            throw new ArgumentException(
                "Only unresolved or stale attempts complete outside publication."
            );
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return InTransaction(
            async (c, t) =>
            {
                var row = await RequireAttempt(c, t, expected.AttemptUuid, ct);
                RequireHeader(
                    row,
                    expected.Registration.RegistrationUuid,
                    expected.ProcessingVersion
                );
                if (!SameJson(row.RequestJson, JsonSerializer.Serialize(expected.Request)))
                {
                    throw new NormalizationConflictException(
                        "Setlist completion does not match its request."
                    );
                }
                if (
                    row.Disposition != null
                    || !SameJson(
                        row.PreparationJson,
                        expected.Preparation == null
                            ? null
                            : JsonSerializer.Serialize(expected.Preparation)
                    )
                )
                {
                    return row.ToAttempt();
                }
                await c.ExecuteAsync(
                    new CommandDefinition(
                        """
                        UPDATE normalization.setlist_processing_attempts SET disposition=@state,reason=@reason,completed_at=clock_timestamp()
                        WHERE attempt_uuid=@AttemptUuid
                        """,
                        new
                        {
                            state = disposition.ToString().ToLowerInvariant(),
                            reason,
                            expected.AttemptUuid,
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                row.Disposition = disposition.ToString();
                row.Reason = reason;
                return row.ToAttempt();
            },
            ct
        );
    }

    private Task<T> InTransaction<T>(
        Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> action,
        CancellationToken ct
    ) =>
        db.WithWriteConnection(async connection =>
        {
            ct.ThrowIfCancellationRequested();
            await using var transaction = await (
                (NpgsqlConnection)connection
            ).BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            var result = await action((NpgsqlConnection)connection, transaction);
            await transaction.CommitAsync(ct);
            return result;
        });

    private static async Task<AttemptRow> RequireAttempt(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid uuid,
        CancellationToken ct
    ) =>
        await ReadAttempt(c, t, uuid, true, ct)
        ?? throw new ArgumentException("Unknown setlist processing attempt.");

    private static Task<AttemptRow?> ReadAttempt(
        NpgsqlConnection c,
        NpgsqlTransaction? t,
        Guid uuid,
        bool forUpdate,
        CancellationToken ct
    ) =>
        c.QuerySingleOrDefaultAsync<AttemptRow>(
            new CommandDefinition(
                $"""
                SELECT {Columns} FROM normalization.setlist_processing_attempts a
                JOIN normalization.setlist_processing_registrations r ON r.uuid=a.registration_uuid
                WHERE a.attempt_uuid=@uuid {(forUpdate ? "FOR UPDATE OF a" : "")}
                """,
                new { uuid },
                t,
                cancellationToken: ct
            )
        );

    private static bool SameJson(string? a, string? b) =>
        a == null || b == null
            ? a == b
            : JsonElement.DeepEquals(
                JsonSerializer.Deserialize<JsonElement>(a),
                JsonSerializer.Deserialize<JsonElement>(b)
            );

    private static string SerializeCheckpoint<T>(T value)
    {
        var json = JsonSerializer.Serialize(value);
        if (Encoding.UTF8.GetByteCount(json) > MaximumCheckpointBytes)
        {
            throw new ArgumentException(
                "Setlist checkpoints must fit within 16 MiB; no proposals were truncated."
            );
        }
        return json;
    }

    private static void RequireUuid(Guid uuid)
    {
        if (uuid == Guid.Empty)
        {
            throw new ArgumentException("A stable UUID is required.");
        }
    }

    private static void RequireHeader(AttemptRow row, Guid registration, string version)
    {
        if (row.RegistrationUuid != registration || row.ProcessingVersion != version)
        {
            throw new NormalizationConflictException(
                "Setlist extraction does not match its attempt request."
            );
        }
    }

    private class RegistrationRow
    {
        public Guid RegistrationUuid { get; init; }
        public string Provider { get; init; } = null!;
        public string ResourceType { get; init; } = null!;
        public string ProviderKey { get; init; } = null!;
        public Guid? ExpectedArtistUuid { get; init; }

        public NormalizationSetlistProcessingRegistration ToRegistration() =>
            new(RegistrationUuid, new(Provider, ResourceType, ProviderKey), ExpectedArtistUuid);
    }

    private sealed class AttemptRow : RegistrationRow
    {
        public Guid AttemptUuid { get; init; }
        public string ProcessingVersion { get; init; } = null!;
        public string RequestJson { get; init; } = null!;
        public string? PreparationJson { get; set; }
        public string? Disposition { get; set; }
        public string? Reason { get; set; }

        public NormalizationSetlistProcessingAttempt ToAttempt() =>
            new(
                AttemptUuid,
                ToRegistration(),
                JsonSerializer.Deserialize<NormalizationSetlistProcessingRequest>(RequestJson)!,
                ProcessingVersion,
                PreparationJson == null
                    ? null
                    : JsonSerializer.Deserialize<NormalizationSetlistPreparation>(PreparationJson),
                Disposition == null
                    ? null
                    : new(
                        AttemptUuid,
                        RegistrationUuid,
                        Enum.Parse<NormalizationSetlistProcessingDisposition>(Disposition, true),
                        Reason!
                    )
            );
    }
}
