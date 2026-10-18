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
/// One application checkpoint and immutable per-owner preparations. Short checkpoint transactions
/// never enter Work; accepted writer callbacks enter with Work locks and atomically save receipts.
/// </summary>
public sealed partial class PeopleAcceptanceApplicationStore(DbService db)
{
    internal const int MaximumCheckpointBytes = 32 * 1024 * 1024;

    public Task<PeopleAcceptanceApplication?> GetApplicationAsync(
        Guid applicationAttemptUuid,
        CancellationToken ct = default
    ) =>
        db.WithWriteConnection<PeopleAcceptanceApplication?>(c =>
            Read((NpgsqlConnection)c, null, applicationAttemptUuid, false, ct)
        );

    internal Task<PeopleAcceptanceApplication> GetOrCreateAsync(
        Guid uuid,
        Guid resolutionUuid,
        string version,
        CancellationToken ct
    ) =>
        InTransaction(
            async (c, t) =>
            {
                if (uuid == Guid.Empty || resolutionUuid == Guid.Empty)
                {
                    throw new ArgumentException("Application and resolution UUIDs are required.");
                }
                var source =
                    await c.QuerySingleOrDefaultAsync<Guid?>(
                        new CommandDefinition(
                            "SELECT source_uuid FROM normalization.people_resolution_attempts WHERE attempt_uuid=@resolutionUuid AND disposition='published' AND batch IS NOT NULL",
                            new { resolutionUuid },
                            t,
                            cancellationToken: ct
                        )
                    )
                    ?? throw new ArgumentException(
                        "Acceptance requires a published resolution proposal."
                    );
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "INSERT INTO normalization.people_acceptance_applications(attempt_uuid,resolution_attempt_uuid,source_uuid,version) VALUES(@uuid,@resolutionUuid,@source,@version) ON CONFLICT DO NOTHING",
                        new
                        {
                            uuid,
                            resolutionUuid,
                            source,
                            version,
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                var current = (await Read(c, t, uuid, true, ct))!;
                if (current.ResolutionAttemptUuid != resolutionUuid || current.Version != version)
                {
                    throw new NormalizationConflictException(
                        "Application UUID already belongs to another request or version."
                    );
                }
                return current;
            },
            ct
        );

    internal Task<PeopleAcceptanceApplication> SavePlanAsync(
        Guid uuid,
        PeopleAcceptancePlan plan,
        CancellationToken ct
    )
    {
        var json = Serialize(plan);
        return InTransaction(
            async (c, t) =>
            {
                var current = (await Read(c, t, uuid, true, ct))!;
                if (current.Result == null && current.Plan == null)
                {
                    await c.ExecuteAsync(
                        new CommandDefinition(
                            "UPDATE normalization.people_acceptance_applications SET plan=@json::jsonb WHERE attempt_uuid=@uuid",
                            new { uuid, json },
                            t,
                            cancellationToken: ct
                        )
                    );
                    current = (await Read(c, t, uuid, false, ct))!;
                }
                return current;
            },
            ct
        );
    }

    internal Task<PeopleAcceptanceApplication> SaveStageAsync(
        Guid uuid,
        PeopleAcceptanceStage stage,
        CancellationToken ct
    )
    {
        if (stage.Ticket != null)
        {
            throw new ArgumentException("A stage is saved before Work admission.");
        }
        var json = Serialize(stage);
        return InTransaction(
            async (c, t) =>
            {
                var current = (await Read(c, t, uuid, true, ct))!;
                if (
                    current.Result == null
                    && !current.Stages.Any(s => s.WorkUuid == stage.WorkUuid)
                )
                {
                    await c.ExecuteAsync(
                        new CommandDefinition(
                            "INSERT INTO normalization.people_acceptance_stages(work_uuid,application_uuid,ordinal,stage) VALUES(@WorkUuid,@uuid,@ordinal,@json::jsonb)",
                            new
                            {
                                stage.WorkUuid,
                                uuid,
                                ordinal = current.Stages.Count,
                                json,
                            },
                            t,
                            cancellationToken: ct
                        )
                    );
                    current = (await Read(c, t, uuid, false, ct))!;
                }
                return current;
            },
            ct
        );
    }

    internal Task<PeopleAcceptanceApplication> AttachTicketAsync(
        Guid uuid,
        Guid workUuid,
        NormalizationWorkTicket ticket,
        CancellationToken ct
    ) =>
        InTransaction(
            async (c, t) =>
            {
                var current = (await Read(c, t, uuid, true, ct))!;
                var stage = current.Stages.Single(s => s.WorkUuid == workUuid);
                PeopleAcceptanceReadSet.RequireTicket(
                    stage,
                    ticket,
                    current.SourceUuid,
                    current.Version
                );
                if (stage.Ticket != null && !Same(stage.Ticket, ticket))
                {
                    throw new NormalizationConflictException(
                        "Stage already has another admission receipt."
                    );
                }
                if (current.Result == null && stage.Ticket == null)
                {
                    await c.ExecuteAsync(
                        new CommandDefinition(
                            "UPDATE normalization.people_acceptance_stages SET ticket=@json::jsonb,admitted_work_uuid=work_uuid WHERE application_uuid=@uuid AND work_uuid=@workUuid",
                            new
                            {
                                uuid,
                                workUuid,
                                json = Serialize(ticket),
                            },
                            t,
                            cancellationToken: ct
                        )
                    );
                }
                return (await Read(c, t, uuid, false, ct))!;
            },
            ct
        );

    internal Task<PeopleAcceptanceStage?> GetConfiguredStageAsync(
        Guid sourceUuid,
        long generation,
        CancellationToken ct
    ) =>
        db.WithWriteConnection<PeopleAcceptanceStage?>(async c =>
        {
            var json = await c.QuerySingleOrDefaultAsync<string>(
                new CommandDefinition(
                    """
                    SELECT a.stage::text FROM normalization.people_acceptance_stages a
                    JOIN normalization.work_runs r ON r.uuid=a.work_uuid
                    JOIN normalization.work_subjects s ON s.uuid=r.subject_uuid
                    WHERE s.domain='people' AND s.subject_kind='recording' AND s.subject_uuid=@sourceUuid
                        AND (r.ticket->>'DesiredGeneration')::bigint=@generation
                    ORDER BY a.work_uuid LIMIT 1
                    """,
                    new { sourceUuid, generation },
                    cancellationToken: ct
                )
            );
            return json == null ? null : JsonSerializer.Deserialize<PeopleAcceptanceStage>(json);
        });

    internal Task<PeopleAcceptanceApplication> CompleteAsync(
        PeopleAcceptanceApplication expected,
        PeopleAcceptanceDisposition disposition,
        string reason,
        CancellationToken ct
    ) =>
        InTransaction(
            async (c, t) =>
            {
                var current = (await Read(c, t, expected.AttemptUuid, true, ct))!;
                // A concurrent worker may already have saved or published the winning next stage.
                // An older local failure cannot terminalize that newer recovery checkpoint.
                if (current.Result != null || !Same(current, expected))
                {
                    return current;
                }
                var receipts = await ReadReceipts(c, t, current.AttemptUuid, ct);
                var result = new PeopleAcceptanceResult(
                    current.AttemptUuid,
                    current.SourceUuid,
                    disposition,
                    reason,
                    receipts
                );
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "UPDATE normalization.people_acceptance_applications SET result=@json::jsonb WHERE attempt_uuid=@uuid",
                        new { uuid = current.AttemptUuid, json = Serialize(result) },
                        t,
                        cancellationToken: ct
                    )
                );
                return current with { Result = result };
            },
            ct
        );

    internal static async Task<IReadOnlyList<PeopleAcceptanceStageReceipt>> ReadReceipts(
        NpgsqlConnection c,
        NpgsqlTransaction? t,
        Guid uuid,
        CancellationToken ct
    ) =>
        (
            await c.QueryAsync<string>(
                new CommandDefinition(
                    "SELECT receipt::text FROM normalization.people_acceptance_stages WHERE application_uuid=@uuid AND receipt IS NOT NULL ORDER BY ordinal",
                    new { uuid },
                    t,
                    cancellationToken: ct
                )
            )
        )
            .Select(json => JsonSerializer.Deserialize<PeopleAcceptanceStageReceipt>(json)!)
            .ToArray();

    private static async Task<PeopleAcceptanceApplication?> Read(
        NpgsqlConnection c,
        NpgsqlTransaction? t,
        Guid uuid,
        bool locked,
        CancellationToken ct
    )
    {
        var row = await c.QuerySingleOrDefaultAsync<ApplicationRow>(
            new CommandDefinition(
                "SELECT attempt_uuid AS \"AttemptUuid\",resolution_attempt_uuid AS \"ResolutionAttemptUuid\",source_uuid AS \"SourceUuid\",version AS \"Version\",plan::text AS \"Plan\",result::text AS \"Result\" FROM normalization.people_acceptance_applications WHERE attempt_uuid=@uuid"
                    + (locked ? " FOR UPDATE" : ""),
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
            new CommandDefinition(
                "SELECT stage::text AS \"Stage\",ticket::text AS \"Ticket\",receipt::text AS \"Receipt\" FROM normalization.people_acceptance_stages WHERE application_uuid=@uuid ORDER BY ordinal",
                new { uuid },
                t,
                cancellationToken: ct
            )
        );
        return new(
            row.AttemptUuid,
            row.ResolutionAttemptUuid,
            row.SourceUuid,
            row.Version,
            row.Plan == null ? null : JsonSerializer.Deserialize<PeopleAcceptancePlan>(row.Plan),
            stages
                .Select(s =>
                    JsonSerializer.Deserialize<PeopleAcceptanceStage>(s.Stage)! with
                    {
                        Ticket =
                            s.Ticket == null
                                ? null
                                : JsonSerializer.Deserialize<NormalizationWorkTicket>(s.Ticket),
                        Receipt =
                            s.Receipt == null
                                ? null
                                : JsonSerializer.Deserialize<PeopleAcceptanceStageReceipt>(
                                    s.Receipt
                                ),
                    }
                )
                .ToArray(),
            row.Result == null
                ? null
                : JsonSerializer.Deserialize<PeopleAcceptanceResult>(row.Result)
        );
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

    internal static bool Same<T>(T left, T right) =>
        JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(left),
            JsonSerializer.SerializeToElement(right)
        );

    private static string Serialize<T>(T value)
    {
        var json = JsonSerializer.Serialize(value);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumCheckpointBytes)
        {
            throw new ArgumentException("People acceptance checkpoint exceeds its bounded size.");
        }
        return json;
    }

    private sealed record ApplicationRow(
        Guid AttemptUuid,
        Guid ResolutionAttemptUuid,
        Guid SourceUuid,
        string Version,
        string? Plan,
        string? Result
    );

    private sealed record StageRow(string Stage, string? Ticket, string? Receipt);
}
