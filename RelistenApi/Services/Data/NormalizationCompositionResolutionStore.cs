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
/// Short checkpoint transactions preserve processor observations. Accepted writer callbacks use
/// their existing Work transaction to authenticate the saved stage and record its publication.
/// </summary>
public sealed partial class NormalizationCompositionResolutionStore(DbService db)
{
    internal const int MaximumCheckpointBytes = 32 * 1024 * 1024;
    internal const int MaximumCheckpoints = 128;

    public Task<CompositionResolutionAttempt?> GetAsync(
        Guid attemptUuid,
        CancellationToken ct = default
    ) =>
        db.WithWriteConnection<CompositionResolutionAttempt?>(c =>
            Read((NpgsqlConnection)c, null, attemptUuid, false, ct)
        );

    internal Task<CompositionResolutionAttempt> GetOrCreateAsync(
        Guid uuid,
        CatalogWorkClaimReference claim,
        string version,
        Guid? previousAttemptUuid,
        CancellationToken ct
    )
    {
        if (
            uuid == Guid.Empty
            || claim.RepertoireSongUuid == Guid.Empty
            || claim.MusicRevisionUuid == Guid.Empty
            || string.IsNullOrWhiteSpace(version)
        )
        {
            throw new ArgumentException(
                "An attempt, exact accepted claim and processing version are required."
            );
        }
        if (previousAttemptUuid == uuid)
        {
            throw new ArgumentException("A retry requires a different attempt UUID.");
        }
        var json = Serialize(claim);
        return InTransaction(
            async (c, t) =>
            {
                if (previousAttemptUuid.HasValue)
                {
                    var previous =
                        await Read(c, t, previousAttemptUuid.Value, false, ct)
                        ?? throw new ArgumentException(
                            "Unknown previous composition resolution attempt."
                        );
                    if (previous.Claim.RepertoireSongUuid != claim.RepertoireSongUuid)
                    {
                        throw new NormalizationConflictException(
                            "A retry must identify the same repertoire song."
                        );
                    }
                }
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "INSERT INTO normalization.composition_resolution_attempts(attempt_uuid,claim,processing_version,previous_attempt_uuid) VALUES(@uuid,@json::jsonb,@version,@previousAttemptUuid) ON CONFLICT DO NOTHING",
                        new
                        {
                            uuid,
                            json,
                            version,
                            previousAttemptUuid,
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                var current = (await Read(c, t, uuid, true, ct))!;
                if (
                    !Same(current.Claim, claim)
                    || current.PreviousAttemptUuid != previousAttemptUuid
                    || current.Result == null && current.ProcessingVersion != version
                )
                {
                    throw new NormalizationConflictException(
                        "Composition resolution UUID already belongs to another request or unfinished version."
                    );
                }
                return current;
            },
            ct
        );
    }

    internal Task<CompositionResolutionAttempt> SavePreparationAsync(
        Guid uuid,
        CompositionResolutionPreparation preparation,
        CancellationToken ct
    )
    {
        var json = Serialize(preparation);
        var frozen = JsonSerializer.Deserialize<CompositionResolutionPreparation>(json)!;
        return InTransaction(
            async (c, t) =>
            {
                var current = await RequireAttempt(c, t, uuid, ct);
                if (!Same(current.Claim, frozen.Context.Claim))
                {
                    throw new NormalizationConflictException(
                        "Preparation does not belong to the requested accepted claim."
                    );
                }
                if (current.Preparation != null)
                {
                    RequireSame(current.Preparation, frozen, "Preparation is immutable.");
                    return current;
                }
                if (current.Result != null)
                {
                    return current;
                }
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "UPDATE normalization.composition_resolution_attempts SET preparation=@json::jsonb WHERE attempt_uuid=@uuid",
                        new { uuid, json },
                        t,
                        cancellationToken: ct
                    )
                );
                return (await Read(c, t, uuid, false, ct))!;
            },
            ct
        );
    }

    internal Task<CompositionResolutionAttempt> SaveSearchAsync(
        CompositionResolutionAttempt expected,
        CompositionResolutionSearchCheckpoint search,
        CancellationToken ct
    ) =>
        SaveCheckpointAsync(
            expected,
            new(expected.Checkpoints.Count, CompositionResolutionPhase.Search, Search: search),
            ct
        );

    internal Task<CompositionResolutionAttempt> SaveHydrationAsync(
        CompositionResolutionAttempt expected,
        CompositionResolutionHydrationCheckpoint hydration,
        CancellationToken ct
    ) =>
        SaveCheckpointAsync(
            expected,
            new(
                expected.Checkpoints.Count,
                CompositionResolutionPhase.Hydration,
                Hydration: hydration
            ),
            ct
        );

    private Task<CompositionResolutionAttempt> SaveCheckpointAsync(
        CompositionResolutionAttempt expected,
        CompositionResolutionCheckpoint checkpoint,
        CancellationToken ct
    )
    {
        var expectedJson = Serialize(expected);
        var json = Serialize(checkpoint);
        return InTransaction(
            async (c, t) =>
            {
                var current = await RequireAttempt(c, t, expected.AttemptUuid, ct);
                if (
                    current.Result != null
                    || !Same(
                        current,
                        JsonSerializer.Deserialize<CompositionResolutionAttempt>(expectedJson)
                    )
                )
                {
                    return current;
                }
                if (
                    current.Preparation == null
                    || current.Proposal != null
                    || current.Checkpoints.Count >= MaximumCheckpoints
                )
                {
                    throw new NormalizationConflictException(
                        "Acquisition requires a prepared unfinished attempt within its checkpoint bound."
                    );
                }
                if (
                    current.Checkpoints.LastOrDefault() is { } previous
                    && previous.Phase > checkpoint.Phase
                )
                {
                    throw new NormalizationConflictException(
                        "Acquisition checkpoints cannot return to an earlier phase."
                    );
                }
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "INSERT INTO normalization.composition_resolution_checkpoints(attempt_uuid,ordinal,checkpoint) VALUES(@uuid,@ordinal,@json::jsonb)",
                        new
                        {
                            uuid = current.AttemptUuid,
                            ordinal = checkpoint.Ordinal,
                            json,
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                return (await Read(c, t, current.AttemptUuid, false, ct))!;
            },
            ct
        );
    }

    internal Task<CompositionResolutionAttempt> SaveProposalAsync(
        Guid uuid,
        EvaluatedCatalogWorkCorrespondenceReceipt proposal,
        CancellationToken ct
    )
    {
        var json = Serialize(proposal);
        var frozen = JsonSerializer.Deserialize<EvaluatedCatalogWorkCorrespondenceReceipt>(json)!;
        return InTransaction(
            async (c, t) =>
            {
                var current = await RequireAttempt(c, t, uuid, ct);
                if (
                    frozen.AttemptUuid != current.AttemptUuid
                    || frozen.ProcessorVersion != current.ProcessingVersion
                    || !Same(frozen.Claim, current.Claim)
                    || frozen.ReceiptUuid == Guid.Empty
                    || frozen.LinkUuid == Guid.Empty
                )
                {
                    throw new NormalizationConflictException(
                        "A correspondence receipt must bind its original attempt, claim and processor."
                    );
                }
                if (current.Proposal != null)
                {
                    RequireSame(current.Proposal, frozen, "A correspondence receipt is immutable.");
                    return current;
                }
                if (current.Result != null)
                {
                    return current;
                }
                if (current.Preparation == null)
                {
                    throw new NormalizationConflictException(
                        "A correspondence receipt requires its original preparation."
                    );
                }
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "UPDATE normalization.composition_resolution_attempts SET proposal=@json::jsonb WHERE attempt_uuid=@uuid",
                        new { uuid, json },
                        t,
                        cancellationToken: ct
                    )
                );
                return (await Read(c, t, uuid, false, ct))!;
            },
            ct
        );
    }

    internal Task<CompositionResolutionAttempt> SaveStageAsync(
        Guid uuid,
        CompositionResolutionStage stage,
        CancellationToken ct
    )
    {
        if (stage.WorkUuid == Guid.Empty || stage.Ticket != null || stage.Receipt != null)
        {
            throw new ArgumentException(
                "A stage with a stable Work UUID is saved before admission and publication."
            );
        }
        var json = Serialize(stage);
        var frozen = JsonSerializer.Deserialize<CompositionResolutionStage>(json)!;
        return InTransaction(
            async (c, t) =>
            {
                var current = await RequireAttempt(c, t, uuid, ct);
                var existing = current.Stages.SingleOrDefault(value =>
                    value.WorkUuid == frozen.WorkUuid
                );
                if (existing != null)
                {
                    RequireSame(
                        existing with
                        {
                            Ticket = null,
                            Receipt = null,
                        },
                        frozen,
                        "A saved publication stage is immutable."
                    );
                    return current;
                }
                if (current.Result != null)
                {
                    return current;
                }
                if (
                    current.Proposal == null
                    || current.Stages.Count >= 2
                    || current.Stages.Any(value =>
                        value.Kind == CompositionResolutionStageKind.Link
                    )
                    || current.Stages.Any(value => value.Kind == frozen.Kind)
                    || current.Stages.Any(value => value.Receipt == null)
                )
                {
                    throw new NormalizationConflictException(
                        "Publication requires a proposal and completion of the preceding distinct owner stage."
                    );
                }
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "INSERT INTO normalization.composition_resolution_stages(work_uuid,attempt_uuid,ordinal,stage) VALUES(@workUuid,@uuid,@ordinal,@json::jsonb)",
                        new
                        {
                            workUuid = frozen.WorkUuid,
                            uuid,
                            ordinal = current.Stages.Count,
                            json,
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                return (await Read(c, t, uuid, false, ct))!;
            },
            ct
        );
    }

    internal Task<CompositionResolutionAttempt> AttachTicketAsync(
        Guid uuid,
        Guid workUuid,
        NormalizationWorkTicket ticket,
        CancellationToken ct
    )
    {
        var json = Serialize(ticket);
        return InTransaction(
            async (c, t) =>
            {
                var current = await RequireAttempt(c, t, uuid, ct);
                var stage = current.Stages.Single(value => value.WorkUuid == workUuid);
                var frozen = JsonSerializer.Deserialize<NormalizationWorkTicket>(json)!;
                RequireTicket(stage, frozen, current.ProcessingVersion);
                if (stage.Ticket != null)
                {
                    RequireSame(
                        stage.Ticket,
                        frozen,
                        "A stage already has another admission receipt."
                    );
                    return current;
                }
                if (current.Result != null)
                {
                    return current;
                }
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "UPDATE normalization.composition_resolution_stages SET ticket=@json::jsonb,admitted_work_uuid=work_uuid WHERE attempt_uuid=@uuid AND work_uuid=@workUuid",
                        new
                        {
                            uuid,
                            workUuid,
                            json,
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                return (await Read(c, t, uuid, false, ct))!;
            },
            ct
        );
    }

    internal Task<CompositionResolutionAttempt> CompleteAsync(
        CompositionResolutionAttempt expected,
        CompositionResolutionDisposition disposition,
        string reason,
        CancellationToken ct
    )
    {
        if (
            disposition == CompositionResolutionDisposition.Pending
            || string.IsNullOrWhiteSpace(reason)
        )
        {
            throw new ArgumentException("Completion requires a terminal disposition and reason.");
        }
        var expectedJson = Serialize(expected);
        return InTransaction(
            async (c, t) =>
            {
                var current = await RequireAttempt(c, t, expected.AttemptUuid, ct);
                // A losing worker cannot terminalize progress checkpointed by another invocation.
                if (
                    current.Result != null
                    || !Same(
                        current,
                        JsonSerializer.Deserialize<CompositionResolutionAttempt>(expectedJson)
                    )
                )
                {
                    return current;
                }
                if (
                    disposition == CompositionResolutionDisposition.Applied
                    && (
                        !current.Stages.Any(value =>
                            value.Kind == CompositionResolutionStageKind.Link
                            && value.Receipt != null
                        ) || current.Stages.Any(value => value.Receipt == null)
                    )
                )
                {
                    throw new NormalizationConflictException(
                        "Applied resolution requires every saved stage and its final link publication."
                    );
                }
                var result = new CompositionResolutionResult(
                    current.AttemptUuid,
                    disposition,
                    reason,
                    current
                        .Stages.Select(value => value.Receipt)
                        .OfType<NormalizationPublicationResult>()
                        .ToArray()
                );
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "UPDATE normalization.composition_resolution_attempts SET result=@json::jsonb WHERE attempt_uuid=@uuid",
                        new { uuid = current.AttemptUuid, json = Serialize(result) },
                        t,
                        cancellationToken: ct
                    )
                );
                return current with { Result = result };
            },
            ct
        );
    }

    private static async Task<CompositionResolutionAttempt> RequireAttempt(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid uuid,
        CancellationToken ct
    ) =>
        await Read(c, t, uuid, true, ct)
        ?? throw new ArgumentException("Unknown composition resolution attempt.");

    private static async Task<CompositionResolutionAttempt?> Read(
        NpgsqlConnection c,
        NpgsqlTransaction? t,
        Guid uuid,
        bool locked,
        CancellationToken ct
    )
    {
        var row = await c.QuerySingleOrDefaultAsync<AttemptRow>(
            new CommandDefinition(
                "SELECT attempt_uuid AS \"AttemptUuid\",claim::text AS \"Claim\",processing_version AS \"ProcessingVersion\",previous_attempt_uuid AS \"PreviousAttemptUuid\",preparation::text AS \"Preparation\",proposal::text AS \"Proposal\",result::text AS \"Result\" FROM normalization.composition_resolution_attempts WHERE attempt_uuid=@uuid"
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
        var checkpoints = await c.QueryAsync<string>(
            new CommandDefinition(
                "SELECT checkpoint::text FROM normalization.composition_resolution_checkpoints WHERE attempt_uuid=@uuid ORDER BY ordinal",
                new { uuid },
                t,
                cancellationToken: ct
            )
        );
        var stages = await c.QueryAsync<StageRow>(
            new CommandDefinition(
                "SELECT stage::text AS \"Stage\",ticket::text AS \"Ticket\",receipt::text AS \"Receipt\" FROM normalization.composition_resolution_stages WHERE attempt_uuid=@uuid ORDER BY ordinal",
                new { uuid },
                t,
                cancellationToken: ct
            )
        );
        return new(
            row.AttemptUuid,
            JsonSerializer.Deserialize<CatalogWorkClaimReference>(row.Claim)!,
            row.ProcessingVersion,
            row.PreviousAttemptUuid,
            row.Preparation == null
                ? null
                : JsonSerializer.Deserialize<CompositionResolutionPreparation>(row.Preparation),
            checkpoints
                .Select(value =>
                    JsonSerializer.Deserialize<CompositionResolutionCheckpoint>(value)!
                )
                .ToArray(),
            row.Proposal == null
                ? null
                : JsonSerializer.Deserialize<EvaluatedCatalogWorkCorrespondenceReceipt>(
                    row.Proposal
                ),
            stages
                .Select(value =>
                    JsonSerializer.Deserialize<CompositionResolutionStage>(value.Stage)! with
                    {
                        Ticket =
                            value.Ticket == null
                                ? null
                                : JsonSerializer.Deserialize<NormalizationWorkTicket>(value.Ticket),
                        Receipt =
                            value.Receipt == null
                                ? null
                                : JsonSerializer.Deserialize<NormalizationPublicationResult>(
                                    value.Receipt
                                ),
                    }
                )
                .ToArray(),
            row.Result == null
                ? null
                : JsonSerializer.Deserialize<CompositionResolutionResult>(row.Result)
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

    private static void RequireSame<T>(T expected, T actual, string message)
    {
        if (!Same(expected, actual))
        {
            throw new NormalizationConflictException(message);
        }
    }

    private static string Serialize<T>(T value)
    {
        var json = JsonSerializer.Serialize(value);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumCheckpointBytes)
        {
            throw new ArgumentException(
                "Composition resolution checkpoint exceeds its bounded size."
            );
        }
        return json;
    }

    private sealed record AttemptRow(
        Guid AttemptUuid,
        string Claim,
        string ProcessingVersion,
        Guid? PreviousAttemptUuid,
        string? Preparation,
        string? Proposal,
        string? Result
    );

    private sealed record StageRow(string Stage, string? Ticket, string? Receipt);
}
