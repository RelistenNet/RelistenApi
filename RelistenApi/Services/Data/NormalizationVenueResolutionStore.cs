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
public sealed partial class NormalizationVenueResolutionStore(DbService db)
{
    internal const int MaximumCheckpointBytes = 32 * 1024 * 1024;

    public Task<VenueResolutionAttempt?> GetAsync(
        Guid attemptUuid,
        CancellationToken ct = default
    ) => InTransaction((c, t) => Read(c, t, attemptUuid, true, ct), ct);

    /// <summary>Recover the latest immutable request behind a pending Work subject, including interrupted ticket saves.</summary>
    public Task<VenueResolutionAttempt?> GetForSubjectAsync(
        Guid subjectUuid,
        CancellationToken ct = default
    ) =>
        InTransaction(
            async (c, t) =>
            {
                var attemptUuid = await c.QuerySingleOrDefaultAsync<Guid?>(
                    new CommandDefinition(
                        """
                        SELECT a.attempt_uuid
                        FROM normalization.work_runs r
                        JOIN normalization.venue_resolution_stages s ON s.work_uuid=r.uuid
                        JOIN normalization.venue_resolution_attempts a ON a.attempt_uuid=s.attempt_uuid
                        WHERE r.subject_uuid=@subjectUuid
                        ORDER BY a.created_at DESC,a.attempt_uuid DESC LIMIT 1
                        """,
                        new { subjectUuid },
                        t,
                        cancellationToken: ct
                    )
                );
                return attemptUuid == null ? null : await Read(c, t, attemptUuid.Value, true, ct);
            },
            ct
        );

    internal Task<VenueResolutionAttempt> GetOrCreateAsync(
        Guid uuid,
        VenueResolutionRequest request,
        string version,
        Guid? previousAttemptUuid,
        CancellationToken ct
    )
    {
        if (uuid == Guid.Empty || string.IsNullOrWhiteSpace(version))
        {
            throw new ArgumentException(
                "An attempt, exact source request and processing version are required."
            );
        }
        if (previousAttemptUuid == uuid)
        {
            throw new ArgumentException("A retry requires a different attempt UUID.");
        }
        ValidateRequest(request);
        var json = Serialize(request);
        var frozen = JsonSerializer.Deserialize<VenueResolutionRequest>(json)!;
        return InTransaction(
            async (c, t) =>
            {
                if (previousAttemptUuid.HasValue)
                {
                    var previous =
                        await Read(c, t, previousAttemptUuid.Value, false, ct)
                        ?? throw new ArgumentException(
                            "Unknown previous venue resolution attempt."
                        );
                    if (!SameRequestOwner(previous.Request, frozen))
                    {
                        throw new NormalizationConflictException(
                            "A retry must identify the same source claim."
                        );
                    }
                }
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "INSERT INTO normalization.venue_resolution_attempts(attempt_uuid,request,processing_version,previous_attempt_uuid) VALUES(@uuid,@json::jsonb,@version,@previousAttemptUuid) ON CONFLICT DO NOTHING",
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
                    !Same(current.Request, frozen)
                    || current.PreviousAttemptUuid != previousAttemptUuid
                    || current.Result == null && current.ProcessingVersion != version
                )
                {
                    throw new NormalizationConflictException(
                        "Venue resolution UUID already belongs to another request or unfinished version."
                    );
                }
                return current;
            },
            ct
        );
    }

    internal Task<VenueResolutionAttempt> SavePreparationAsync(
        Guid uuid,
        VenueResolutionPreparation preparation,
        CancellationToken ct
    )
    {
        var json = Serialize(preparation);
        var frozen = JsonSerializer.Deserialize<VenueResolutionPreparation>(json)!;
        return InTransaction(
            async (c, t) =>
            {
                var current = await RequireAttempt(c, t, uuid, ct);
                if (!MatchesSource(current.Request, frozen.Source))
                {
                    throw new NormalizationConflictException(
                        "Preparation does not belong to the requested source claim."
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
                        "UPDATE normalization.venue_resolution_attempts SET preparation=@json::jsonb WHERE attempt_uuid=@uuid",
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

    internal Task<VenueResolutionAttempt> SaveDecisionAsync(
        Guid uuid,
        VenueResolutionDecision decision,
        CancellationToken ct
    )
    {
        var json = Serialize(decision);
        var frozen = JsonSerializer.Deserialize<VenueResolutionDecision>(json)!;
        return InTransaction(
            async (c, t) =>
            {
                var current = await RequireAttempt(c, t, uuid, ct);
                if (
                    frozen.AttemptUuid != current.AttemptUuid
                    || frozen.ProcessorVersion != current.ProcessingVersion
                    || !Same(frozen.Claim, current.Preparation?.Source.Claim)
                    || frozen.AssignmentUuid == Guid.Empty
                )
                {
                    throw new NormalizationConflictException(
                        "A correspondence receipt must bind its original attempt, claim and processor."
                    );
                }
                if (current.Decision != null)
                {
                    RequireSame(current.Decision, frozen, "A correspondence receipt is immutable.");
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
                        "UPDATE normalization.venue_resolution_attempts SET decision=@json::jsonb WHERE attempt_uuid=@uuid",
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

    internal Task<VenueResolutionAttempt> SaveStageAsync(
        Guid uuid,
        VenueResolutionStage stage,
        CancellationToken ct
    )
    {
        if (
            stage.WorkUuid == Guid.Empty
            || stage.Ticket != null
            || stage.Receipt != null
            || stage.Outcome != null
        )
        {
            throw new ArgumentException(
                "A stage with a stable Work UUID is saved before admission and publication."
            );
        }
        var json = Serialize(stage);
        var frozen = JsonSerializer.Deserialize<VenueResolutionStage>(json)!;
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
                            Outcome = null,
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
                    current.Preparation == null
                    || (
                        current.Decision == null
                        && frozen.Kind
                            is not (
                                VenueResolutionStageKind.AcknowledgeMissing
                                or VenueResolutionStageKind.AcknowledgeDecision
                                or VenueResolutionStageKind.SupersedeObservation
                            )
                    )
                    || current.Stages.Count >= 3
                    || (
                        (
                            frozen.Kind
                            is VenueResolutionStageKind.AcknowledgeMissing
                                or VenueResolutionStageKind.AcknowledgeDecision
                        )
                        && current.Stages.Count != 0
                    )
                    || current.Stages.Any(value =>
                        (
                            value.Kind
                            is VenueResolutionStageKind.AcknowledgeMissing
                                or VenueResolutionStageKind.SupersedeObservation
                        )
                        || (
                            frozen.Kind != VenueResolutionStageKind.SupersedeObservation
                            && value.Kind
                                is (
                                    VenueResolutionStageKind.Assignment
                                    or VenueResolutionStageKind.AcknowledgeDecision
                                )
                        )
                    )
                    || current.Stages.Any(value => value.Kind == frozen.Kind)
                    || current.Stages.Any(value => value.Receipt == null)
                )
                {
                    throw new NormalizationConflictException(
                        "Publication requires a decision and completion of the preceding distinct owner stage."
                    );
                }
                RequireStageShape(current, frozen);
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "INSERT INTO normalization.venue_resolution_stages(work_uuid,attempt_uuid,ordinal,stage) VALUES(@workUuid,@uuid,@ordinal,@json::jsonb)",
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

    internal Task<VenueResolutionAttempt> AttachTicketAsync(
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
                        "UPDATE normalization.venue_resolution_stages SET ticket=@json::jsonb,admitted_work_uuid=work_uuid WHERE attempt_uuid=@uuid AND work_uuid=@workUuid",
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

    internal Task<VenueResolutionAttempt> CompleteAsync(
        VenueResolutionAttempt expected,
        VenueResolutionDisposition disposition,
        string reason,
        CancellationToken ct
    )
    {
        if (disposition == VenueResolutionDisposition.Pending || string.IsNullOrWhiteSpace(reason))
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
                        JsonSerializer.Deserialize<VenueResolutionAttempt>(expectedJson)
                    )
                )
                {
                    return current;
                }
                if (
                    disposition == VenueResolutionDisposition.Applied
                    && (
                        !current.Stages.Any(value =>
                            value.Kind == VenueResolutionStageKind.Assignment
                            && value.Receipt != null
                            && value.Outcome is { Applied: true }
                        ) || current.Stages.Any(value => value.Receipt == null)
                    )
                )
                {
                    throw new NormalizationConflictException(
                        "Applied resolution requires every saved stage and its final assignment publication."
                    );
                }
                if (
                    disposition == VenueResolutionDisposition.Missing
                    && !current.Stages.Any(value =>
                        value.Kind == VenueResolutionStageKind.AcknowledgeMissing
                        && value.Receipt != null
                    )
                )
                {
                    throw new NormalizationConflictException(
                        "Missing resolution requires its acknowledged absence publication."
                    );
                }
                if (
                    (
                        disposition
                        is VenueResolutionDisposition.Review
                            or VenueResolutionDisposition.Unresolved
                    )
                    && current.Preparation?.Source.Claim != null
                    && !current.Stages.Any(value =>
                        value.Receipt != null
                        && (
                            value.Kind
                            is VenueResolutionStageKind.AcknowledgeDecision
                                or VenueResolutionStageKind.Assignment
                        )
                    )
                )
                {
                    throw new NormalizationConflictException(
                        "An enrolled source decision must acknowledge its original Work read set before completion."
                    );
                }
                if (
                    (
                        disposition
                        is VenueResolutionDisposition.Applied
                            or VenueResolutionDisposition.Review
                            or VenueResolutionDisposition.Unresolved
                    )
                    && current.Preparation?.Context.ObservationSubscription != null
                    && !current.Stages.Any(value =>
                        value.Kind == VenueResolutionStageKind.SupersedeObservation
                        && value.Receipt?.Status == NormalizationPublicationStatus.Published
                    )
                )
                {
                    throw new NormalizationConflictException(
                        "A bound resolution must retire its previous observation subscription before completion."
                    );
                }
                var result = new VenueResolutionResult(
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
                        "UPDATE normalization.venue_resolution_attempts SET result=@json::jsonb WHERE attempt_uuid=@uuid",
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

    private static async Task<VenueResolutionAttempt> RequireAttempt(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid uuid,
        CancellationToken ct
    ) =>
        await Read(c, t, uuid, true, ct)
        ?? throw new ArgumentException("Unknown venue resolution attempt.");

    private static async Task<VenueResolutionAttempt?> Read(
        NpgsqlConnection c,
        NpgsqlTransaction? t,
        Guid uuid,
        bool locked,
        CancellationToken ct
    )
    {
        var row = await c.QuerySingleOrDefaultAsync<AttemptRow>(
            new CommandDefinition(
                "SELECT attempt_uuid AS \"AttemptUuid\",request::text AS \"Request\",processing_version AS \"ProcessingVersion\",previous_attempt_uuid AS \"PreviousAttemptUuid\",preparation::text AS \"Preparation\",decision::text AS \"Decision\",result::text AS \"Result\" FROM normalization.venue_resolution_attempts WHERE attempt_uuid=@uuid"
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
                "SELECT stage::text AS \"Stage\",ticket::text AS \"Ticket\",receipt::text AS \"Receipt\",outcome::text AS \"Outcome\" FROM normalization.venue_resolution_stages WHERE attempt_uuid=@uuid ORDER BY ordinal",
                new { uuid },
                t,
                cancellationToken: ct
            )
        );
        return new(
            row.AttemptUuid,
            JsonSerializer.Deserialize<VenueResolutionRequest>(row.Request)!,
            row.ProcessingVersion,
            row.PreviousAttemptUuid,
            row.Preparation == null
                ? null
                : JsonSerializer.Deserialize<VenueResolutionPreparation>(row.Preparation),
            row.Decision == null
                ? null
                : JsonSerializer.Deserialize<VenueResolutionDecision>(row.Decision),
            stages
                .Select(value =>
                    JsonSerializer.Deserialize<VenueResolutionStage>(value.Stage)! with
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
                        Outcome =
                            value.Outcome == null
                                ? null
                                : JsonSerializer.Deserialize<VenueResolutionAssignmentOutcome>(
                                    value.Outcome
                                ),
                    }
                )
                .ToArray(),
            row.Result == null
                ? null
                : JsonSerializer.Deserialize<VenueResolutionResult>(row.Result)
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

    private static void ValidateRequest(VenueResolutionRequest request)
    {
        var valid = request.Kind switch
        {
            VenueResolutionRequestKind.ImportedVenue => request.ImportedVenueUuid is { } imported
                && imported != Guid.Empty
                && request.Show == null,
            VenueResolutionRequestKind.ShowObservation => request.ImportedVenueUuid == null
                && request.Show is { } show
                && Enum.IsDefined(show.SourceKind)
                && show.PublishedAttemptUuid != Guid.Empty
                && show.InputRevisionUuid != Guid.Empty
                && show.VenueJsonPointer != null
                && (show.VenueJsonPointer.Length == 0 || show.VenueJsonPointer.StartsWith('/'))
                && (!show.AccountIndex.HasValue || show.AccountIndex >= 0),
            _ => false,
        };
        if (!valid)
        {
            throw new ArgumentException(
                "A venue resolution identifies exactly one imported row or published observation."
            );
        }
    }

    private static bool SameRequestOwner(
        VenueResolutionRequest previous,
        VenueResolutionRequest current
    ) =>
        previous.Kind == current.Kind
        && (
            current.Kind == VenueResolutionRequestKind.ImportedVenue
                ? previous.ImportedVenueUuid == current.ImportedVenueUuid
                : Same(previous.Show, current.Show)
        );

    private static bool MatchesSource(
        VenueResolutionRequest request,
        VenueResolutionSource source
    ) =>
        request.Kind == VenueResolutionRequestKind.ImportedVenue
            ? source.ShowReference == null
                && (
                    source.Claim is { IsShow: false } claim
                        && claim.CatalogUuid == request.ImportedVenueUuid
                    || source.Claim == null
                        && source.Absence is { Exists: false } absence
                        && absence.ImportedVenueUuid == request.ImportedVenueUuid
                )
            : source.Claim is { IsShow: true } && Same(request.Show, source.ShowReference);

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
            throw new ArgumentException("Venue resolution checkpoint exceeds its bounded size.");
        }
        return json;
    }

    private sealed record AttemptRow(
        Guid AttemptUuid,
        string Request,
        string ProcessingVersion,
        Guid? PreviousAttemptUuid,
        string? Preparation,
        string? Decision,
        string? Result
    );

    private sealed record StageRow(string Stage, string? Ticket, string? Receipt, string? Outcome);
}
