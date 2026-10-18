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
using Relisten.Normalization;

namespace Relisten.Data;

/// <summary>
/// Immutable request/plan/stage storage. Short checkpoint transactions never enter Work;
/// publication methods use the caller's existing Work transaction, so authority, complete map,
/// receipt and watermark either all commit or all roll back. Cancellation retains preparation.
/// </summary>
public sealed partial class SourceActorAliasApplicationStore(DbService db)
{
    /// <summary>Returns the retained plan, bounded stage headers and terminal result, or null for an unknown request.</summary>
    public Task<SourceActorAliasApplication?> GetApplicationAsync(
        Guid applicationUuid,
        CancellationToken ct = default
    ) =>
        db.WithWriteConnection<SourceActorAliasApplication?>(connection =>
            Read((NpgsqlConnection)connection, null, applicationUuid, false, ct)
        );

    /// <summary>Loads one independently bounded original proof with its attached admission and receipt; this is an audit read.</summary>
    public Task<SourceActorAliasPersonStage?> GetStageAsync(
        Guid stageUuid,
        CancellationToken ct = default
    ) =>
        db.WithWriteConnection<SourceActorAliasPersonStage?>(connection =>
            ReadStage((NpgsqlConnection)connection, null, stageUuid, ct)
        );

    /// <summary>Finds a stage's owning application without loading its full factual preparation.</summary>
    public Task<SourceActorAliasApplication?> GetApplicationForStageAsync(
        Guid stageUuid,
        CancellationToken ct = default
    ) =>
        db.WithWriteConnection<SourceActorAliasApplication?>(async connection =>
        {
            var c = (NpgsqlConnection)connection;
            var applicationUuid = await c.QuerySingleOrDefaultAsync<Guid?>(
                new CommandDefinition(
                    "SELECT application_uuid FROM normalization.source_actor_alias_person_stages WHERE stage_uuid=@stageUuid",
                    new { stageUuid },
                    cancellationToken: ct
                )
            );
            return applicationUuid == null
                ? null
                : await Read(c, null, applicationUuid.Value, false, ct);
        });

    internal Task<SourceActorAliasApplication> GetOrCreateAsync(
        Guid applicationUuid,
        Guid resolutionUuid,
        string version,
        CancellationToken ct
    ) =>
        InTransaction(
            async (c, t) =>
            {
                if (
                    applicationUuid == Guid.Empty
                    || resolutionUuid == Guid.Empty
                    || string.IsNullOrWhiteSpace(version)
                )
                {
                    throw new ArgumentException(
                        "Application, resolution and processing version are required."
                    );
                }
                var source =
                    await c.QuerySingleOrDefaultAsync<Guid?>(
                        new CommandDefinition(
                            "SELECT source_uuid FROM normalization.source_actor_resolution_attempts WHERE attempt_uuid=@resolutionUuid AND disposition='published' AND batch IS NOT NULL",
                            new { resolutionUuid },
                            t,
                            cancellationToken: ct
                        )
                    )
                    ?? throw new ArgumentException(
                        "Source alias application requires an exact published 55 batch."
                    );
                await c.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO normalization.source_actor_alias_applications(attempt_uuid,resolution_attempt_uuid,source_uuid,version)
                        VALUES(@applicationUuid,@resolutionUuid,@source,@version) ON CONFLICT DO NOTHING
                        """,
                        new
                        {
                            applicationUuid,
                            resolutionUuid,
                            source,
                            version,
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                var current = (await Read(c, t, applicationUuid, true, ct))!;
                if (
                    current.ResolutionAttemptUuid != resolutionUuid
                    || current.SourceUuid != source
                    || current.Version != version
                )
                {
                    throw new NormalizationConflictException(
                        "Source alias application UUID already names another request or version."
                    );
                }
                return current;
            },
            ct
        );

    internal Task<SourceActorAliasApplication> SavePlanAsync(
        Guid applicationUuid,
        SourceActorAliasApplicationPlan plan,
        CancellationToken ct
    ) =>
        InTransaction(
            async (c, t) =>
            {
                var current = await RequireApplication(c, t, applicationUuid, ct);
                if (plan.Proposal.ResolutionAttemptUuid != current.ResolutionAttemptUuid)
                {
                    throw new ArgumentException(
                        "The first plan must address the application's original proposal."
                    );
                }
                if (current.Plan != null || current.Result != null)
                {
                    // First preparation wins. Return it without acquiring or restamping new facts.
                    return current;
                }
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "UPDATE normalization.source_actor_alias_applications SET plan=@json::jsonb WHERE attempt_uuid=@applicationUuid",
                        new
                        {
                            applicationUuid,
                            json = SourceActorAliasApplicationCheckpointCodec.WritePlan(plan),
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                return (await Read(c, t, applicationUuid, false, ct))!;
            },
            ct
        );

    internal Task<SourceActorAliasApplication> SaveStageAsync(
        Guid applicationUuid,
        SourceActorAliasPersonStage stage,
        CancellationToken ct
    ) =>
        InTransaction(
            async (c, t) =>
            {
                var current = await RequireApplication(c, t, applicationUuid, ct);
                var existing = current.Stages.SingleOrDefault(value =>
                    value.StageUuid == stage.StageUuid || value.WorkUuid == stage.WorkUuid
                );
                if (existing != null)
                {
                    if (
                        existing.StageUuid != stage.StageUuid
                        || existing.WorkUuid != stage.WorkUuid
                    )
                    {
                        throw new NormalizationConflictException(
                            "Stage identity already belongs to another immutable application step."
                        );
                    }
                    // Concurrent service preparations share the deterministic next-step identity.
                    // First save wins; resume its original facts instead of replacing or restamping.
                    return current;
                }
                if (current.Result != null || current.Plan == null)
                {
                    throw new NormalizationConflictException(
                        "A stage requires a saved plan and an unfinished application."
                    );
                }
                if (
                    current.Stages.Any(value =>
                        value.Receipt == null && value.TerminalReason == null
                    )
                )
                {
                    throw new NormalizationConflictException(
                        "Resume the exact unfinished stage before saving another preparation."
                    );
                }
                RequireStageIdentity(stage);
                await RequirePredecessor(
                    c,
                    t,
                    stage.PredecessorStageUuid,
                    stage.OriginalPersonUuid,
                    true,
                    ct
                );
                await RequirePredecessor(
                    c,
                    t,
                    stage.ConfiguredPredecessorStageUuid,
                    stage.OriginalPersonUuid,
                    false,
                    ct
                );
                var header = WriteStageHeader(stage);
                var conservative = new SourceActorAliasStageReceiptHeader(
                    stage
                        .Deltas.Where(delta => delta.Kind == SourceActorAliasDeltaKind.Add)
                        .SelectMany(delta =>
                            delta.Names.Select(name => new SourceActorAliasNameOutcome(
                                delta.Acceptance!.Claim,
                                name.RequestedAliasUuid,
                                name.Wording,
                                SourceActorAliasNameDisposition.ProtectedReviewedNaturalClaim,
                                "canonical_alias_review_preserved_previous_support",
                                name.RequestedAliasUuid,
                                name.RequestedAliasUuid,
                                name.RequestedAliasUuid
                            ))
                        )
                        .ToArray(),
                    stage.Deltas.Sum(delta => (long)delta.RetireAssignmentUuids.Count),
                    stage.Deltas.Sum(delta => (long)delta.RetireSupportUuids.Count),
                    SourceActorAliasApplicationDisposition.Review,
                    "canonical_alias_outcomes_preserved_previous_scope_support",
                    PublishedStageCertificateVersion,
                    new string('0', 64)
                );
                await RequireApplicationHeaderCapacity(
                    c,
                    t,
                    applicationUuid,
                    header,
                    JsonSerializer.Serialize(conservative),
                    ct
                );
                await c.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO normalization.source_actor_alias_person_stages(
                            stage_uuid,work_uuid,application_uuid,ordinal,person_uuid,owner_subject_uuid,contribution_revision,
                            predecessor_stage_uuid,configured_predecessor_stage_uuid,extraction_attempt_uuid,stage,stage_header)
                        VALUES(@StageUuid,@WorkUuid,@applicationUuid,@ordinal,@OriginalPersonUuid,@OwnerSubjectUuid,@contributionRevision,
                            @PredecessorStageUuid,@ConfiguredPredecessorStageUuid,@ExtractionAttemptUuid,@json::jsonb,@header::jsonb)
                        """,
                        new
                        {
                            stage.StageUuid,
                            stage.WorkUuid,
                            applicationUuid,
                            ordinal = current.Stages.Count,
                            stage.OriginalPersonUuid,
                            stage.OwnerSubjectUuid,
                            stage.PredecessorStageUuid,
                            contributionRevision = checked(
                                stage
                                    .Contributions.Single(value =>
                                        value.Contribution.Contributor == StableContributor
                                    )
                                    .Contribution.ExpectedRevision + 1
                            ),
                            stage.ConfiguredPredecessorStageUuid,
                            stage.Facts.ExtractionAttemptUuid,
                            json = SourceActorAliasApplicationCheckpointCodec.WriteStage(stage),
                            header,
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                return (await Read(c, t, applicationUuid, false, ct))!;
            },
            ct
        );

    internal Task<SourceActorAliasApplication> AttachTicketAsync(
        Guid applicationUuid,
        Guid stageUuid,
        NormalizationWorkTicket ticket,
        CancellationToken ct
    ) =>
        InTransaction(
            async (c, t) =>
            {
                var current = await RequireApplication(c, t, applicationUuid, ct);
                var stage =
                    await ReadStage(
                        c,
                        t,
                        current.Stages.Single(value => value.StageUuid == stageUuid).StageUuid,
                        ct
                    ) ?? throw new InvalidOperationException("Saved source stage body is missing.");
                if (stage.Ticket != null)
                {
                    if (!SourceActorAliasApplicationCheckpointCodec.Same(stage.Ticket, ticket))
                    {
                        throw new NormalizationConflictException(
                            "Stage already retains another admitted ticket."
                        );
                    }
                    return current;
                }
                if (
                    current.Result != null
                    || stage.TerminalReason != null
                    || ticket.WorkUuid != stage.WorkUuid
                    || ticket.SubjectUuid != stage.OwnerSubjectUuid
                    || ticket.PolicyVersion != current.Version
                )
                {
                    throw new NormalizationConflictException(
                        "Admission does not belong to this unfinished person stage."
                    );
                }
                var saved = await c.QuerySingleOrDefaultAsync<AdmissionRow>(
                    new CommandDefinition(
                        "SELECT ticket::text AS \"Ticket\",request_hash AS \"RequestHash\" FROM normalization.work_runs WHERE uuid=@WorkUuid AND subject_uuid=@OwnerSubjectUuid",
                        new { stage.WorkUuid, stage.OwnerSubjectUuid },
                        t,
                        cancellationToken: ct
                    )
                );
                if (
                    saved == null
                    || !SourceActorAliasApplicationCheckpointCodec.Same(
                        System.Text.Json.JsonSerializer.Deserialize<NormalizationWorkTicket>(
                            saved.Ticket
                        ),
                        ticket
                    )
                    || !NormalizationWorkService.MatchesContributedBatchRequest(
                        saved.RequestHash,
                        new("people", "person", stage.OriginalPersonUuid),
                        current.Version,
                        stage.Contributions,
                        stage.ExpectedConfigurationGeneration,
                        stage.ExpectedContributions
                    )
                )
                {
                    throw new NormalizationConflictException(
                        "Admission must attach the exact retained Work ticket."
                    );
                }
                await c.ExecuteAsync(
                    new CommandDefinition(
                        """
                        UPDATE normalization.source_actor_alias_person_stages
                        SET ticket=@json::jsonb,admitted_work_uuid=work_uuid
                        WHERE application_uuid=@applicationUuid AND stage_uuid=@stageUuid
                        """,
                        new
                        {
                            applicationUuid,
                            stageUuid,
                            json = SourceActorAliasApplicationCheckpointCodec.WriteTicket(ticket),
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                return (await Read(c, t, applicationUuid, false, ct))!;
            },
            ct
        );

    /// <summary>
    /// A rejected admission can leave a durable preparation with no Work run. Serialize the
    /// absence check with Work's existing configuration gate before locking the application.
    /// Call only after BeginContributedBatch rejected this exact fully prepared stage. Its
    /// original non-null input generations and other saved CAS conditions are monotonic,
    /// so the same rejected preparation cannot later become admissible. The reason is audit
    /// text, not evidence of staleness. A callback fault never enters this transition.
    /// This enters no Work callback or additional Work owner.
    /// </summary>
    internal Task<SourceActorAliasApplication> MarkAdmissionStaleAsync(
        Guid applicationUuid,
        Guid stageUuid,
        string reason,
        CancellationToken ct
    ) =>
        InTransaction(
            async (c, t) =>
            {
                var saved =
                    await ReadStage(c, t, stageUuid, ct)
                    ?? throw new ArgumentException("Unknown source alias stage.");
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "SELECT pg_advisory_xact_lock(hashtextextended(@key,0))",
                        new
                        {
                            key = $"normalization:configuration:people:person:{saved.OriginalPersonUuid}",
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                var current = await RequireApplication(c, t, applicationUuid, ct);
                var stage =
                    await ReadStage(
                        c,
                        t,
                        current.Stages.Single(value => value.StageUuid == stageUuid).StageUuid,
                        ct
                    ) ?? throw new InvalidOperationException("Saved source stage body is missing.");
                if (current.Result != null || stage.Receipt != null || stage.TerminalReason != null)
                {
                    return current;
                }
                if (
                    string.IsNullOrWhiteSpace(reason)
                    || stage
                        .Contributions.SelectMany(value => value.ExpectedInputRevisions)
                        .Any(input => input.Generation == null)
                )
                {
                    throw new ArgumentException(
                        "Terminal admission refusal requires a qualified monotonic preparation with every original input generation."
                    );
                }
                var state = await c.QuerySingleOrDefaultAsync<string>(
                    new CommandDefinition(
                        "SELECT state FROM normalization.work_runs WHERE uuid=@WorkUuid",
                        new { stage.WorkUuid },
                        t,
                        cancellationToken: ct
                    )
                );
                if (state is "running" or "published")
                {
                    // Another worker's admitted configuration owns recovery. It cannot be killed
                    // by this caller's older refusal, and its preparation cannot be refreshed.
                    return current;
                }
                if (state != null && state != "stale")
                {
                    throw new InvalidOperationException("Unknown retained Work admission state.");
                }
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "UPDATE normalization.source_actor_alias_person_stages SET terminal_reason=@reason WHERE application_uuid=@applicationUuid AND stage_uuid=@stageUuid",
                        new
                        {
                            applicationUuid,
                            stageUuid,
                            reason,
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                return (await Read(c, t, applicationUuid, false, ct))!;
            },
            ct
        );

    internal Task<SourceActorAliasApplication> MarkStaleAsync(
        Guid applicationUuid,
        Guid stageUuid,
        string reason,
        CancellationToken ct
    ) =>
        InTransaction(
            async (c, t) =>
            {
                var current = await RequireApplication(c, t, applicationUuid, ct);
                var stage =
                    await ReadStage(
                        c,
                        t,
                        current.Stages.Single(value => value.StageUuid == stageUuid).StageUuid,
                        ct
                    ) ?? throw new InvalidOperationException("Saved source stage body is missing.");
                if (stage.Receipt != null || stage.TerminalReason != null)
                {
                    return current;
                }
                var stale = await c.QuerySingleOrDefaultAsync<Guid?>(
                    new CommandDefinition(
                        "SELECT uuid FROM normalization.work_runs WHERE uuid=@WorkUuid AND state='stale'",
                        new { stage.WorkUuid },
                        t,
                        cancellationToken: ct
                    )
                );
                if (stale == null || string.IsNullOrWhiteSpace(reason))
                {
                    throw new NormalizationConflictException(
                        "Only a terminal stale Work run can terminalize an admitted stage."
                    );
                }
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "UPDATE normalization.source_actor_alias_person_stages SET terminal_reason=@reason WHERE application_uuid=@applicationUuid AND stage_uuid=@stageUuid",
                        new
                        {
                            applicationUuid,
                            stageUuid,
                            reason,
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                return (await Read(c, t, applicationUuid, false, ct))!;
            },
            ct
        );

    internal Task<SourceActorAliasApplication> CompleteAsync(
        SourceActorAliasApplication expected,
        SourceActorAliasApplicationDisposition disposition,
        string reason,
        IReadOnlyList<SourceActorAliasReplacementReadiness> readiness,
        CancellationToken ct
    ) =>
        InTransaction(
            async (c, t) =>
            {
                var current = await RequireApplication(c, t, expected.AttemptUuid, ct);
                if (
                    current.Result != null
                    || !SourceActorAliasApplicationCheckpointCodec.SameApplication(
                        current,
                        expected
                    )
                )
                {
                    return current;
                }
                if (
                    current.Stages.Any(stage =>
                        stage.Receipt == null && stage.TerminalReason == null
                    )
                )
                {
                    throw new NormalizationConflictException(
                        "Pending saved/admitted stages must remain resumable."
                    );
                }
                if (string.IsNullOrWhiteSpace(reason))
                {
                    throw new ArgumentException(
                        "Completion requires an explicit disposition reason."
                    );
                }
                var plan = current.Plan;
                if (plan == null)
                {
                    if (
                        disposition
                        is not (
                            SourceActorAliasApplicationDisposition.Stale
                            or SourceActorAliasApplicationDisposition.Unresolved
                        )
                    )
                    {
                        throw new ArgumentException(
                            "An application refused before planning cannot report accepted or complete work."
                        );
                    }
                    plan = await ReadRefusalPlan(c, t, current, reason, ct);
                }

                var result = new SourceActorAliasApplicationResult(
                    current.AttemptUuid,
                    current.SourceUuid,
                    disposition,
                    reason,
                    current
                        .Stages.Where(stage => stage.Receipt != null)
                        .Select(stage => stage.StageUuid)
                        .ToArray(),
                    readiness
                );
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "UPDATE normalization.source_actor_alias_applications SET plan=COALESCE(plan,@plan::jsonb),result=@json::jsonb WHERE attempt_uuid=@applicationUuid",
                        new
                        {
                            applicationUuid = current.AttemptUuid,
                            plan = current.Plan == null
                                ? SourceActorAliasApplicationCheckpointCodec.WritePlan(plan)
                                : null,
                            json = SourceActorAliasApplicationCheckpointCodec.WriteResult(result),
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                return current with { Plan = plan, Result = result };
            },
            ct
        );

    private static async Task<SourceActorAliasApplicationPlan> ReadRefusalPlan(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SourceActorAliasApplication application,
        string reason,
        CancellationToken ct
    )
    {
        // Every terminal application retains a plan. A pre-plan refusal records only its
        // immutable request header and explicit reason; no source claims were authenticated.
        // Save it atomically with the result, so cancellation cannot leave a partial plan.
        var proposal =
            await c.QuerySingleOrDefaultAsync<SourceActorAliasProposalReference>(
                new CommandDefinition(
                    """
                    SELECT attempt_uuid AS "ResolutionAttemptUuid",extraction_attempt_uuid AS "ExtractionAttemptUuid",
                        processing_version AS "ProcessingVersion"
                    FROM normalization.source_actor_resolution_attempts
                    WHERE attempt_uuid=@ResolutionAttemptUuid AND source_uuid=@SourceUuid
                        AND disposition='published' AND batch IS NOT NULL
                    """,
                    new { application.ResolutionAttemptUuid, application.SourceUuid },
                    t,
                    cancellationToken: ct
                )
            )
            ?? throw new NormalizationConflictException(
                "The refused application's original published resolution is unavailable."
            );
        return new(proposal, [], reason);
    }

    private static void RequireStageIdentity(SourceActorAliasPersonStage stage)
    {
        var owner = stage.Facts.Membership.Owner;
        if (
            stage.StageUuid == Guid.Empty
            || stage.WorkUuid == Guid.Empty
            || owner == null
            || owner.SubjectUuid != stage.OwnerSubjectUuid
            || owner.Subject
                != new NormalizationSubjectKey("people", "person", stage.OriginalPersonUuid)
            || stage.Ticket != null
            || stage.Receipt != null
            || stage.TerminalReason != null
            || stage.Deltas.Any(delta => delta.OriginalPersonUuid != stage.OriginalPersonUuid)
            || stage.Contributions.Count(value =>
                value.Contribution.Contributor == StableContributor
            ) != 1
        )
        {
            throw new ArgumentException(
                "A source alias stage requires one existing original person and exactly one stable source alias contribution."
            );
        }
        var observed = stage
            .Facts.Membership.Contributions.Select(
                value => new NormalizationInputContributionRevision(
                    value.Contributor,
                    value.Revision
                )
            )
            .Concat(
                stage.Facts.Membership.UnmanagedBaseline is { } baseline
                    ? [new(baseline.Contributor, 0)]
                    : []
            )
            .ToArray();
        RequireUniqueHeaders(observed);
        RequireUniqueHeaders(stage.ExpectedContributions);
        if (
            stage.ExpectedConfigurationGeneration != owner.DesiredGeneration
            || !SameHeaders(observed, stage.ExpectedContributions)
        )
        {
            throw new ArgumentException(
                "A stage must retain the complete original same-snapshot contribution revision vector."
            );
        }
    }

    internal static NormalizationResourceKey StableContributor { get; } =
        new("relisten", "source_actor_aliases", "person_metadata");

    private static async Task RequirePredecessor(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid? uuid,
        Guid personUuid,
        bool committed,
        CancellationToken ct
    )
    {
        if (uuid == null)
        {
            return;
        }
        var predecessor = await ReadStage(c, t, uuid.Value, ct);
        if (
            predecessor == null
            || predecessor.OriginalPersonUuid != personUuid
            || (committed && predecessor.Receipt == null)
        )
        {
            throw new NormalizationConflictException(
                "Stage predecessor must identify the retained original-person map and its required committed receipt."
            );
        }
    }

    private static async Task<SourceActorAliasApplication> RequireApplication(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid uuid,
        CancellationToken ct
    ) =>
        await Read(c, t, uuid, true, ct)
        ?? throw new ArgumentException("Unknown source alias application.");

    internal static async Task<SourceActorAliasApplication?> Read(
        NpgsqlConnection c,
        NpgsqlTransaction? t,
        Guid uuid,
        bool locked,
        CancellationToken ct
    )
    {
        var row = await c.QuerySingleOrDefaultAsync<ApplicationRow>(
            new CommandDefinition(
                """
                SELECT attempt_uuid AS "AttemptUuid",resolution_attempt_uuid AS "ResolutionAttemptUuid",source_uuid AS "SourceUuid",
                    version AS "Version",plan::text AS "Plan",result::text AS "Result"
                FROM normalization.source_actor_alias_applications WHERE attempt_uuid=@uuid
                """ + (locked ? " FOR UPDATE" : ""),
                new { uuid },
                t,
                cancellationToken: ct
            )
        );
        if (row == null)
        {
            return null;
        }
        var bytes = await c.QuerySingleAsync<long>(
            new CommandDefinition(
                """
                SELECT coalesce(sum(octet_length(stage_header::text)+coalesce(octet_length(receipt_header::text),0)),0)
                FROM normalization.source_actor_alias_person_stages WHERE application_uuid=@uuid
                """,
                new { uuid },
                t,
                cancellationToken: ct
            )
        );
        if (bytes > SourceActorAliasApplicationCheckpointCodec.MaximumCheckpointBytes)
        {
            throw new SourceActorReviewCapacityException(
                "source_alias_application_header_capacity_exceeded"
            );
        }
        var rows = await c.QueryAsync<StageHeaderRow>(
            new CommandDefinition(
                """
                SELECT stage_uuid AS "StageUuid",work_uuid AS "WorkUuid",person_uuid AS "PersonUuid",owner_subject_uuid AS "OwnerUuid",
                    predecessor_stage_uuid AS "PredecessorUuid",ticket IS NOT NULL AS "Admitted",stage_header::text AS "Header",
                    receipt_header::text AS "Receipt",terminal_reason AS "TerminalReason"
                FROM normalization.source_actor_alias_person_stages WHERE application_uuid=@uuid ORDER BY ordinal
                """,
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
            row.Plan == null ? null : SourceActorAliasApplicationCheckpointCodec.ReadPlan(row.Plan),
            rows.Select(DecodeHeader).ToArray(),
            row.Result == null
                ? null
                : SourceActorAliasApplicationCheckpointCodec.ReadResult(row.Result)
        );
    }

    private static async Task RequireApplicationHeaderCapacity(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid applicationUuid,
        string? additionalStage,
        string additionalReceipt,
        CancellationToken ct
    )
    {
        var bytes = await c.QuerySingleAsync<long>(
            new CommandDefinition(
                """
                SELECT coalesce(sum(octet_length(stage_header::text)+coalesce(octet_length(receipt_header::text),0)),0)
                    + coalesce(octet_length(@additionalStage::jsonb::text),0) + octet_length(@additionalReceipt::jsonb::text)
                FROM normalization.source_actor_alias_person_stages WHERE application_uuid=@applicationUuid
                """,
                new
                {
                    applicationUuid,
                    additionalStage,
                    additionalReceipt,
                },
                t,
                cancellationToken: ct
            )
        );
        if (bytes > SourceActorAliasApplicationCheckpointCodec.MaximumCheckpointBytes)
        {
            throw new SourceActorReviewCapacityException(
                "source_alias_application_header_capacity_exceeded"
            );
        }
    }

    private static string WriteStageHeader(SourceActorAliasPersonStage stage) =>
        System.Text.Json.JsonSerializer.Serialize(
            new StageHeaderMetadata(
                stage
                    .Deltas.Where(delta => delta.Kind == SourceActorAliasDeltaKind.Add)
                    .SelectMany(delta =>
                        delta.Names.Select(name => new SourceActorAliasPreparedNameReference(
                            delta.Acceptance!.Claim,
                            name
                        ))
                    )
                    .ToArray(),
                stage
                    .Deltas.Where(delta => delta.Acceptance != null)
                    .Select(delta => delta.Acceptance!)
                    .ToArray()
            )
        );

    private static SourceActorAliasPersonStageHeader DecodeHeader(StageHeaderRow row)
    {
        var value = System.Text.Json.JsonSerializer.Deserialize<StageHeaderMetadata>(row.Header)!;
        return new(
            row.StageUuid,
            row.WorkUuid,
            row.PersonUuid,
            row.OwnerUuid,
            row.PredecessorUuid,
            row.Admitted,
            value.PreparedNames,
            value.Acceptances,
            row.Receipt == null
                ? null
                : System.Text.Json.JsonSerializer.Deserialize<SourceActorAliasStageReceiptHeader>(
                    row.Receipt
                ),
            row.TerminalReason
        );
    }

    internal static async Task<SourceActorAliasPersonStage?> ReadStage(
        NpgsqlConnection c,
        NpgsqlTransaction? t,
        Guid uuid,
        CancellationToken ct
    )
    {
        var row = await c.QuerySingleOrDefaultAsync<StageRow>(
            new CommandDefinition(
                StageColumns + " WHERE stage_uuid=@uuid",
                new { uuid },
                t,
                cancellationToken: ct
            )
        );
        return row == null ? null : DecodeStage(row);
    }

    private static SourceActorAliasPersonStage DecodeStage(StageRow row) =>
        SourceActorAliasApplicationCheckpointCodec.ReadStage(row.Stage) with
        {
            Ticket =
                row.Ticket == null
                    ? null
                    : SourceActorAliasApplicationCheckpointCodec.ReadTicket(row.Ticket),
            Receipt =
                row.Receipt == null
                    ? null
                    : SourceActorAliasApplicationCheckpointCodec.ReadReceipt(row.Receipt),
            TerminalReason = row.TerminalReason,
        };

    private const string StageColumns = """
        SELECT stage::text AS "Stage",ticket::text AS "Ticket",receipt::text AS "Receipt",terminal_reason AS "TerminalReason"
        FROM normalization.source_actor_alias_person_stages
        """;

    private sealed record StageHeaderMetadata(
        IReadOnlyList<SourceActorAliasPreparedNameReference> PreparedNames,
        IReadOnlyList<SourceActorAliasAcceptanceReference> Acceptances
    );

    private sealed record StageHeaderRow(
        Guid StageUuid,
        Guid WorkUuid,
        Guid PersonUuid,
        Guid OwnerUuid,
        Guid? PredecessorUuid,
        bool Admitted,
        string Header,
        string? Receipt,
        string? TerminalReason
    );

    private sealed record AdmissionRow(string Ticket, string RequestHash);

    private sealed record ApplicationRow(
        Guid AttemptUuid,
        Guid ResolutionAttemptUuid,
        Guid SourceUuid,
        string Version,
        string? Plan,
        string? Result
    );

    private sealed record StageRow(
        string Stage,
        string? Ticket,
        string? Receipt,
        string? TerminalReason
    );

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
