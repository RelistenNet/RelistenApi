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

/// <summary>
/// Owns bounded transport attempts, immutable response receipts and one provider admission row.
/// Every mutation locks the gate before command/attempt rows. No transaction performs HTTP or
/// calls EvidenceService: response retention and evidence capture commit independently.
/// </summary>
public sealed partial class NormalizationMusicBrainzInputStore(DbService db)
{
    internal Task<MusicBrainzAcquisition> GetOrCreateAsync(
        Guid commandUuid,
        MusicBrainzLookupRequest request,
        MusicBrainzRequestProfile profile,
        string acquisitionVersion,
        CancellationToken ct = default
    )
    {
        if (commandUuid == Guid.Empty)
        {
            throw new ArgumentException("A stable command UUID is required.", nameof(commandUuid));
        }
        var requestJson = JsonSerializer.Serialize(request);
        var profileJson = JsonSerializer.Serialize(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(acquisitionVersion);

        return InTransaction(
            async (c, t) =>
            {
                await LockGate(c, t, ct);
                await c.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO normalization.musicbrainz_commands(command_uuid,request,profile,acquisition_version)
                        VALUES(@commandUuid,@requestJson::jsonb,@profileJson::jsonb,@acquisitionVersion)
                        ON CONFLICT DO NOTHING
                        """,
                        new
                        {
                            commandUuid,
                            requestJson,
                            profileJson,
                            acquisitionVersion,
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                var command = await RequireCommand(c, t, commandUuid, ct);
                // Replay checks frozen request facts, but terminal commands are not reinterpreted using
                // the caller's current acquisition version or profile defaults.
                if (!SameJson(command.RequestJson, requestJson))
                {
                    throw new NormalizationConflictException(
                        "MusicBrainz command UUID belongs to another request."
                    );
                }
                if (
                    command.ResultJson == null
                    && (
                        !SameJson(command.ProfileJson, profileJson)
                        || command.AcquisitionVersion != acquisitionVersion
                    )
                )
                {
                    throw new NormalizationConflictException(
                        "Unfinished MusicBrainz command uses a different acquisition profile."
                    );
                }
                return await ReadAcquisition(c, t, command, ct);
            },
            ct
        );
    }

    public Task<MusicBrainzAcquisition?> GetAsync(
        Guid commandUuid,
        CancellationToken ct = default
    ) =>
        db.WithWriteConnection<MusicBrainzAcquisition?>(async connection =>
        {
            var c = (NpgsqlConnection)connection;
            await using var t = await c.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            await c.ExecuteAsync(
                new CommandDefinition(
                    "SET TRANSACTION READ ONLY",
                    transaction: t,
                    cancellationToken: ct
                )
            );
            var command = await ReadCommand(c, t, commandUuid, false, ct);
            var result = command == null ? null : await ReadAcquisition(c, t, command, ct);
            await t.CommitAsync(ct);
            return result;
        });

    public Task<MusicBrainzSavedResponse?> GetResponseAsync(
        Guid attemptUuid,
        CancellationToken ct = default
    ) =>
        db.WithWriteConnection(connection =>
            ReadResponse((NpgsqlConnection)connection, null, attemptUuid, ct)
        );

    internal Task<MusicBrainzAcquisition> FailAsync(
        Guid commandUuid,
        string reason,
        CancellationToken ct = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return InTransaction(
            async (c, t) =>
            {
                await LockGate(c, t, ct);
                var command = await RequireCommand(c, t, commandUuid, ct);
                if (command.ResultJson == null)
                {
                    // A terminal command does not revoke an in-flight request's admission lease.
                    // Its eventual response still persists cooldown; otherwise lease expiry recovers it.
                    await CompleteCommand(
                        c,
                        t,
                        command,
                        new(MusicBrainzAcquisitionDisposition.Failed, Reason: reason),
                        ct
                    );
                }
                return await ReadAcquisition(c, t, command, ct);
            },
            ct
        );
    }

    private Task<T> InTransaction<T>(
        Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> action,
        CancellationToken ct
    )
    {
        if (System.Transactions.Transaction.Current != null)
        {
            throw new InvalidOperationException(
                "MusicBrainz acquisition cannot join an ambient transaction."
            );
        }
        return db.WithWriteConnection(async connection =>
        {
            ct.ThrowIfCancellationRequested();
            var c = (NpgsqlConnection)connection;
            await using var t = await c.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            var result = await action(c, t);
            await t.CommitAsync(ct);
            return result;
        });
    }

    private static Task<GateRow> LockGate(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        CancellationToken ct
    ) =>
        c.QuerySingleAsync<GateRow>(
            new CommandDefinition(
                """
                SELECT attempt_uuid AS "AttemptUuid",token AS "Token",lease_until AS "LeaseUntil",
                    CASE WHEN isfinite(not_before) THEN not_before ELSE NULL END AS "NotBefore"
                FROM normalization.musicbrainz_gate WHERE singleton FOR UPDATE
                """,
                transaction: t,
                cancellationToken: ct
            )
        );

    private static Task<DateTime> DatabaseNow(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        CancellationToken ct
    ) =>
        c.ExecuteScalarAsync<DateTime>(
            new CommandDefinition("SELECT clock_timestamp()", transaction: t, cancellationToken: ct)
        );

    private static Task<CommandRow?> ReadCommand(
        NpgsqlConnection c,
        NpgsqlTransaction? t,
        Guid commandUuid,
        bool locked,
        CancellationToken ct
    ) =>
        c.QuerySingleOrDefaultAsync<CommandRow>(
            new CommandDefinition(
                $"""
                SELECT command_uuid AS "CommandUuid",request::text AS "RequestJson",profile::text AS "ProfileJson",
                    acquisition_version AS "AcquisitionVersion",state AS "State",not_before AS "NotBefore",result::text AS "ResultJson"
                FROM normalization.musicbrainz_commands WHERE command_uuid=@commandUuid {(
                    locked ? "FOR UPDATE" : ""
                )}
                """,
                new { commandUuid },
                t,
                cancellationToken: ct
            )
        );

    private static async Task<CommandRow> RequireCommand(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid commandUuid,
        CancellationToken ct
    ) =>
        await ReadCommand(c, t, commandUuid, true, ct)
        ?? throw new ArgumentException("Unknown MusicBrainz command.", nameof(commandUuid));

    private static async Task<AttemptRow[]> ReadAttempts(
        NpgsqlConnection c,
        NpgsqlTransaction? t,
        Guid commandUuid,
        CancellationToken ct
    ) =>
        (
            await c.QueryAsync<AttemptRow>(
                new CommandDefinition(
                    """
                    SELECT preparation::text AS "PreparationJson",ordinal AS "Ordinal",phase AS "Phase",reason AS "Reason"
                    FROM normalization.musicbrainz_attempts WHERE command_uuid=@commandUuid ORDER BY ordinal
                    """,
                    new { commandUuid },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();

    private static async Task<MusicBrainzAcquisition> ReadAcquisition(
        NpgsqlConnection c,
        NpgsqlTransaction? t,
        CommandRow command,
        CancellationToken ct
    )
    {
        var attempts = await ReadAttempts(c, t, command.CommandUuid, ct);
        return new(
            command.CommandUuid,
            command.Request,
            command.AcquisitionVersion,
            Enum.Parse<MusicBrainzAcquisitionState>(command.State),
            command.NotBefore == null ? null : new DateTimeOffset(command.NotBefore.Value),
            command.ResultJson == null
                ? null
                : JsonSerializer.Deserialize<MusicBrainzAcquisitionResult>(command.ResultJson),
            attempts
                .Select(a => new MusicBrainzAcquisitionAttempt(
                    a.Dispatch.AttemptUuid,
                    a.Ordinal,
                    a.Phase,
                    a.Dispatch.ExpectedGeneration,
                    a.Dispatch.HttpDeadline,
                    a.Dispatch.LeaseUntil,
                    a.Reason
                ))
                .ToArray()
        );
    }

    private static async Task CompleteCommand(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        CommandRow command,
        MusicBrainzAcquisitionResult result,
        CancellationToken ct
    )
    {
        command.ResultJson = JsonSerializer.Serialize(result);
        command.State =
            result.Disposition == MusicBrainzAcquisitionDisposition.Failed ? "Failed" : "Completed";
        command.NotBefore = null;
        await c.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE normalization.musicbrainz_commands SET state=@State,result=@ResultJson::jsonb,not_before=NULL
                WHERE command_uuid=@CommandUuid AND result IS NULL
                """,
                command,
                t,
                cancellationToken: ct
            )
        );
    }

    private static bool SameJson(string a, string b)
    {
        using var left = JsonDocument.Parse(a);
        using var right = JsonDocument.Parse(b);
        return JsonElement.DeepEquals(left.RootElement, right.RootElement);
    }

    private sealed class CommandRow
    {
        public Guid CommandUuid { get; init; }
        public string RequestJson { get; init; } = null!;
        public string ProfileJson { get; init; } = null!;
        public string AcquisitionVersion { get; init; } = null!;
        public string State { get; set; } = null!;
        public DateTime? NotBefore { get; set; }
        public string? ResultJson { get; set; }
        public MusicBrainzLookupRequest Request =>
            JsonSerializer.Deserialize<MusicBrainzLookupRequest>(RequestJson)!;
        public MusicBrainzRequestProfile Profile =>
            JsonSerializer.Deserialize<MusicBrainzRequestProfile>(ProfileJson)!;
    }

    private sealed class AttemptRow
    {
        public string PreparationJson { get; init; } = null!;
        public int Ordinal { get; init; }
        public string Phase { get; init; } = null!;
        public string? Reason { get; init; }
        public MusicBrainzDispatch Dispatch =>
            JsonSerializer.Deserialize<MusicBrainzDispatch>(PreparationJson)!;
    }

    private sealed class GateRow
    {
        public Guid? AttemptUuid { get; init; }
        public Guid? Token { get; init; }
        public DateTime? LeaseUntil { get; init; }
        public DateTime? NotBefore { get; init; }
    }
}
