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
    internal const int MaximumCheckpointBytes = PeopleCheckpointCodec.MaximumCheckpointBytes;

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
        return InTransaction(
            async (c, t) =>
            {
                var current = (await Read(c, t, uuid, true, ct))!;
                if (current.Result == null && current.Plan == null)
                {
                    var json = PeopleCheckpointCodec.WritePlan(plan);
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
        return InTransaction(
            async (c, t) =>
            {
                var current = (await Read(c, t, uuid, true, ct))!;
                if (
                    current.Result == null
                    && !current.Stages.Any(s => s.WorkUuid == stage.WorkUuid)
                )
                {
                    var json = PeopleCheckpointCodec.WriteStage(stage);
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

    // The owner generation describes a UNION of keys, not their allocation to contributors.
    // Match the complete admitted revision vector: replacements advance once; untouched peers
    // and empty tombstones retain their prior revisions. Admission may precede publication.
    // Credit review advances the owner's generation without replacing these contributions.
    // The complete vector narrows admission history; ordinary Work may retain those headers.
    // Authenticate the owning application, exact contributed request and full fact ticket too.
    // Ambiguous authenticated history is unsupported, never "latest".
    // Historical payloads without both arrays cannot prove this identity and remain unsupported.
    internal Task<PeopleAcceptanceStage?> GetConfiguredStageAsync(
        Guid sourceUuid,
        NormalizationInputContributionsSnapshot membership,
        CancellationToken ct
    ) =>
        db.WithWriteConnection<PeopleAcceptanceStage?>(async c =>
        {
            var rows = await c.QueryAsync<ConfiguredCreditAdmission>(
                new CommandDefinition(
                    """
                    SELECT a.work_uuid AS "WorkUuid",a.stage::text AS "StageJson",
                        a.ticket::text AS "AttachedTicketJson",application.version AS "Version",
                        r.ticket::text AS "TicketJson",r.request_hash AS "RequestHash",s.uuid AS "OwnerUuid"
                    FROM normalization.people_acceptance_stages a
                    JOIN normalization.people_acceptance_applications application ON application.attempt_uuid=a.application_uuid
                    JOIN normalization.work_runs r ON r.uuid=a.work_uuid
                    JOIN normalization.work_subjects s ON s.uuid=r.subject_uuid
                    CROSS JOIN LATERAL (
                        SELECT CASE
                            WHEN a.stage->>'Format'=@checkpointFormat AND a.stage->>'Kind'=@checkpointKind
                                THEN a.stage->'Value'
                            WHEN NOT (a.stage ? 'Format') THEN a.stage
                        END AS payload,
                            (a.stage->>'Format'=@checkpointFormat AND a.stage->>'Kind'=@checkpointKind)
                                IS TRUE AS compact
                    ) AS stored
                    CROSS JOIN LATERAL (
                        SELECT CASE WHEN jsonb_typeof(stored.payload->'ExpectedContributions')='array'
                                THEN stored.payload->'ExpectedContributions' END AS prior_headers,
                            CASE WHEN jsonb_typeof(stored.payload->'Contributions')='array' THEN
                                CASE WHEN stored.compact THEN (
                                    SELECT COALESCE(jsonb_agg(item->'Value' ORDER BY ordinal),'[]'::jsonb)
                                    FROM jsonb_array_elements(stored.payload->'Contributions')
                                        WITH ORDINALITY AS contribution(item,ordinal)
                                ) ELSE stored.payload->'Contributions' END
                            END AS replacements
                    ) AS checkpoint
                    WHERE application.source_uuid=@sourceUuid
                        AND s.domain='people' AND s.subject_kind='recording' AND s.subject_uuid=@sourceUuid
                        AND (r.ticket->>'DesiredGeneration')::bigint<=@generation
                        AND r.ticket->>'PolicyVersion'=s.policy_version
                        AND checkpoint.prior_headers IS NOT NULL AND checkpoint.replacements IS NOT NULL
                        AND NOT EXISTS (
                            SELECT 1
                            FROM jsonb_to_recordset(@contributions::jsonb)
                                AS observed("Contributor" jsonb,"Revision" bigint)
                            FULL JOIN (
                                SELECT item->'Contribution'->'Contributor' AS contributor,
                                    (item->'Contribution'->>'ExpectedRevision')::bigint+1 AS revision
                                FROM jsonb_array_elements(checkpoint.replacements) AS replacement(item)
                                UNION ALL
                                SELECT prior."Contributor",prior."Revision"
                                FROM jsonb_to_recordset(checkpoint.prior_headers)
                                    AS prior("Contributor" jsonb,"Revision" bigint)
                                WHERE NOT EXISTS (
                                    SELECT 1 FROM jsonb_array_elements(checkpoint.replacements) AS replacement(item)
                                    WHERE item->'Contribution'->'Contributor'=prior."Contributor"
                                )
                            ) AS admitted ON observed."Contributor"=admitted.contributor
                            WHERE observed."Revision" IS DISTINCT FROM admitted.revision
                        )
                    """,
                    new
                    {
                        sourceUuid,
                        generation = membership.Owner!.DesiredGeneration,
                        checkpointFormat = PeopleCheckpointCodec.FormatVersion,
                        checkpointKind = PeopleCheckpointCodec.StageKind,
                        contributions = JsonSerializer.Serialize(
                            membership.Contributions.Select(
                                value => new NormalizationInputContributionRevision(
                                    value.Contributor,
                                    value.Revision
                                )
                            )
                        ),
                    },
                    cancellationToken: ct
                )
            );
            PeopleAcceptanceStage? matched = null;
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                var stage = PeopleCheckpointCodec.ReadStage(row.StageJson);
                var ticket = JsonSerializer.Deserialize<NormalizationWorkTicket>(row.TicketJson)!;
                if (
                    stage.PersonUuid != null
                    || stage.WorkUuid != row.WorkUuid
                    || ticket.WorkUuid != row.WorkUuid
                    || ticket.SubjectUuid != row.OwnerUuid
                    || ticket.PolicyVersion != row.Version
                    || row.AttachedTicketJson != null
                        && !Same(
                            ticket,
                            JsonSerializer.Deserialize<NormalizationWorkTicket>(
                                row.AttachedTicketJson
                            )
                        )
                    || !NormalizationWorkService.MatchesContributedBatchRequest(
                        row.RequestHash,
                        new("people", "recording", sourceUuid),
                        row.Version,
                        stage.Contributions,
                        stage.ExpectedConfigurationGeneration,
                        stage.ExpectedContributions
                    )
                )
                {
                    continue;
                }
                PeopleAcceptanceReadSet.RequireTicket(stage, ticket, sourceUuid, row.Version);
                if (matched != null)
                {
                    // A retained vector must identify one authenticated replacement. A
                    // timestamp or arbitrary stage UUID cannot settle conflicting ownership.
                    return null;
                }
                matched = stage;
            }
            return matched;
        });

    private sealed record ConfiguredCreditAdmission(
        Guid WorkUuid,
        string StageJson,
        string? AttachedTicketJson,
        string Version,
        string TicketJson,
        string RequestHash,
        Guid OwnerUuid
    );

    internal Task<NormalizationWorkTicket?> ReadPublishedTicketAsync(
        Guid workUuid,
        Guid subjectUuid,
        CancellationToken ct
    ) => db.WithWriteConnection(c => ReadPublishedTicket(c, null, workUuid, subjectUuid, ct));

    private static async Task<NormalizationWorkTicket?> ReadPublishedTicket(
        IDbConnection c,
        NpgsqlTransaction? t,
        Guid workUuid,
        Guid subjectUuid,
        CancellationToken ct
    )
    {
        var json = await c.QuerySingleOrDefaultAsync<string>(
            new CommandDefinition(
                "SELECT ticket::text FROM normalization.work_runs WHERE uuid=@workUuid AND subject_uuid=@subjectUuid AND state='published'",
                new { workUuid, subjectUuid },
                t,
                cancellationToken: ct
            )
        );
        return json == null ? null : JsonSerializer.Deserialize<NormalizationWorkTicket>(json);
    }

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
                if (
                    current.Result != null
                    || !PeopleCheckpointCodec.EquivalentApplication(current, expected)
                )
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
            row.Plan == null ? null : PeopleCheckpointCodec.ReadPlan(row.Plan),
            stages
                .Select(s =>
                    PeopleCheckpointCodec.ReadStage(s.Stage) with
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
