using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationRepertoireResolutionStore
{
    internal async Task<RepertoireResolutionAttempt?> ReadForPublicationAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid attemptUuid,
        CancellationToken ct,
        Guid? requiredStageUuid = null
    )
    {
        var attempt = await Read(c, t, attemptUuid, false, ct);
        if (attempt == null)
        {
            return null;
        }
        var requested = requiredStageUuid.HasValue
            ? attempt.Stages.SingleOrDefault(value => value.StageUuid == requiredStageUuid)
            : attempt.Stages.SingleOrDefault(value =>
                value.Kind != RepertoireResolutionStageKind.DecisionPage
                && value.Receipt == null
                && !value.Superseded
                && value.FailureReason == null
            );
        if (requested == null)
        {
            return attempt;
        }
        var ids = attempt
            .Stages.Where(value =>
                value.StageUuid == requested.StageUuid
                || value.PageIndex == requested.PageIndex
                    && value.Kind
                        is RepertoireResolutionStageKind.DecisionPage
                            or RepertoireResolutionStageKind.AcceptedWrite
            )
            .Select(value => value.StageUuid)
            .ToHashSet();
        var stages = attempt.Stages.ToArray();
        for (var index = 0; index < stages.Length; index++)
        {
            if (ids.Contains(stages[index].StageUuid))
            {
                stages[index] = (await ReadStage(c, t, attemptUuid, stages[index].StageUuid, ct))!;
            }
        }
        return attempt with { Stages = stages };
    }

    internal static void RequireTicket(
        RepertoireResolutionStage stage,
        NormalizationWorkTicket ticket,
        string version
    )
    {
        var read = stage.ReadSet;
        if (
            stage.Kind == RepertoireResolutionStageKind.DecisionPage
            || ticket.WorkUuid != stage.WorkUuid
            || ticket.PolicyVersion != version
            || !SetEquals(ticket.Inputs, read.Inputs)
            || !SetEquals(ticket.Dependencies, read.Dependencies)
            || !SetEquals(ticket.CatalogInputs ?? [], read.CatalogInputs)
            || !SetEquals(ticket.SourceIdentities ?? [], read.SourceIdentities)
            || ticket.MappingRegistry != read.MappingRegistry
            || !SetEquals(ticket.ImportedVenues ?? [], read.ImportedVenues)
            || !SetEquals(ticket.CandidateUniverses ?? [], read.CandidateUniverses)
            || !SetEquals(ticket.OwnerObservations ?? [], read.OwnerObservations)
            || !SetEquals(ticket.SourceUuids ?? [], read.SourceUuids)
        )
        {
            throw new NormalizationConflictException(
                "Repertoire publication must consume its exact complete saved read set."
            );
        }
    }

    internal async Task<RepertoireResolutionStage> RequireStageAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid attemptUuid,
        Guid stageUuid,
        Guid workUuid,
        RepertoireResolutionStageKind kind,
        CancellationToken ct
    )
    {
        var attempt = await RequireAttempt(c, t, attemptUuid, ct);
        var stage =
            await ReadStage(c, t, attemptUuid, stageUuid, ct)
            ?? throw new ArgumentException("Unknown repertoire resolution stage.");
        if (
            attempt.Result != null
            || attempt.Preparation == null
            || stage.Kind == RepertoireResolutionStageKind.DecisionPage
            || stage.Kind != kind
            || stage.WorkUuid != workUuid
            || stage.Ticket == null
            || stage.Receipt != null
            || stage.Superseded
            || stage.FailureReason != null
        )
        {
            throw new NormalizationConflictException(
                "Publication requires its unfinished admitted repertoire stage."
            );
        }
        var run = await c.QuerySingleOrDefaultAsync<WorkRow>(
            new CommandDefinition(
                "SELECT s.domain AS \"Domain\",s.subject_kind AS \"Kind\",s.subject_uuid AS \"Uuid\",r.state AS \"State\",r.ticket::text AS \"Ticket\" FROM normalization.work_runs r JOIN normalization.work_subjects s ON s.uuid=r.subject_uuid WHERE r.uuid=@workUuid",
                new { workUuid },
                t,
                cancellationToken: ct
            )
        );
        if (
            run == null
            || run.State != "running"
            || stage.Owner != new NormalizationSubjectKey(run.Domain, run.Kind, run.Uuid)
            || !Same(stage.Ticket, JsonSerializer.Deserialize<NormalizationWorkTicket>(run.Ticket))
        )
        {
            throw new NormalizationConflictException(
                "Repertoire publication requires its exact running Work owner and ticket."
            );
        }
        RequireTicket(stage, stage.Ticket, attempt.ProcessingVersion);
        if (
            !attempt.Preparation.Source.ReadSet.Inputs.All(stage.ReadSet.Inputs.Contains)
            && stage.Kind != RepertoireResolutionStageKind.RetireArtistContribution
        )
        {
            throw new NormalizationConflictException(
                "Repertoire publication cannot replace original source input generations."
            );
        }
        return stage;
    }

    internal Task<RepertoireResolutionAttempt> MarkStageSupersededAsync(
        RepertoireResolutionAttempt expected,
        Guid stageUuid,
        CancellationToken ct
    )
    {
        return InTransaction(
            async (c, t) =>
            {
                var current = await RequireAttempt(c, t, expected.AttemptUuid, ct);
                if (current.Result != null || !SameCheckpoint(current, expected))
                {
                    return current;
                }
                var stage = current.Stages.Single(value => value.StageUuid == stageUuid);
                if (
                    stage.Kind != RepertoireResolutionStageKind.RetireArtistContribution
                    || stage.Receipt != null
                )
                {
                    throw new NormalizationConflictException(
                        "Only an obsolete unfinished artist retirement may be superseded."
                    );
                }
                if (!stage.Superseded)
                {
                    await c.ExecuteAsync(
                        new CommandDefinition(
                            "UPDATE normalization.repertoire_resolution_stages SET superseded=true WHERE stage_uuid=@stageUuid",
                            new { stageUuid },
                            t,
                            cancellationToken: ct
                        )
                    );
                    await AdvanceCheckpoint(c, t, current.AttemptUuid, ct);
                }
                return (await Read(c, t, current.AttemptUuid, false, ct))!;
            },
            ct
        );
    }

    /// <summary>A failure is an audit outcome, never a synthetic accepted Work receipt.</summary>
    internal Task<RepertoireResolutionAttempt> FailStageAsync(
        RepertoireResolutionAttempt expected,
        Guid stageUuid,
        string reason,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 1024)
        {
            throw new ArgumentException("A stage failure requires a bounded reason.");
        }
        return InTransaction(
            async (c, t) =>
            {
                var current = await RequireAttempt(c, t, expected.AttemptUuid, ct);
                if (current.Result != null || !SameCheckpoint(current, expected))
                {
                    return current;
                }
                var stage =
                    (await ReadStage(c, t, current.AttemptUuid, stageUuid, ct))
                    ?? throw new ArgumentException("Unknown repertoire stage.");
                if (
                    stage.Kind == RepertoireResolutionStageKind.DecisionPage
                    || stage.Receipt != null
                    || stage.Superseded
                )
                {
                    throw new NormalizationConflictException(
                        "Only an unfinished Work stage can record failure."
                    );
                }
                if (stage.FailureReason != null)
                {
                    RequireSame(
                        stage.FailureReason,
                        reason,
                        "A stage failure reason is immutable."
                    );
                    return current;
                }
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "UPDATE normalization.repertoire_resolution_stages SET failure_reason=@reason WHERE stage_uuid=@stageUuid",
                        new { stageUuid, reason },
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

    internal async Task WriteAssociationsAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        RepertoireResolutionAttempt attempt,
        RepertoireResolutionStage stage,
        CancellationToken ct
    )
    {
        var decisions =
            stage.Kind == RepertoireResolutionStageKind.AcknowledgeSource
                ? attempt
                    .Stages.Where(value => value.Kind == RepertoireResolutionStageKind.DecisionPage)
                    .SelectMany(value => value.Decisions)
                    .ToArray()
                : stage.Decisions.ToArray();
        if (decisions.Select(value => value.ClaimUuid).Distinct().Count() != decisions.Length)
        {
            throw new NormalizationConflictException(
                "A source account cannot publish two decisions for one claim."
            );
        }
        foreach (var decision in decisions)
        {
            var evidence = Serialize(decision.Evidence);
            await c.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO normalization.repertoire_resolution_associations(decision_uuid,registration_uuid,attempt_uuid,claim_uuid,correspondence_key,repertoire_song_uuid,disposition,reason,authority,evidence)
                    VALUES(@DecisionUuid,@registrationUuid,@attemptUuid,@ClaimUuid,@CorrespondenceKey,@RepertoireSongUuid,@disposition,@Reason,@Authority,@evidence::jsonb) ON CONFLICT DO NOTHING
                    """,
                    new
                    {
                        decision.DecisionUuid,
                        registrationUuid = attempt.Registration.RegistrationUuid,
                        attemptUuid = attempt.AttemptUuid,
                        decision.ClaimUuid,
                        decision.CorrespondenceKey,
                        decision.RepertoireSongUuid,
                        disposition = decision.Disposition.ToString(),
                        decision.Reason,
                        decision.Authority,
                        evidence,
                    },
                    t,
                    cancellationToken: ct
                )
            );
            var exact = await c.ExecuteScalarAsync<bool>(
                new CommandDefinition(
                    """
                    SELECT EXISTS(SELECT 1 FROM normalization.repertoire_resolution_associations WHERE decision_uuid=@DecisionUuid
                        AND registration_uuid=@registrationUuid AND attempt_uuid=@attemptUuid AND claim_uuid=@ClaimUuid AND correspondence_key=@CorrespondenceKey
                        AND repertoire_song_uuid IS NOT DISTINCT FROM @RepertoireSongUuid::uuid AND disposition=@disposition AND reason=@Reason AND authority=@Authority AND evidence=@evidence::jsonb)
                    """,
                    new
                    {
                        decision.DecisionUuid,
                        registrationUuid = attempt.Registration.RegistrationUuid,
                        attemptUuid = attempt.AttemptUuid,
                        decision.ClaimUuid,
                        decision.CorrespondenceKey,
                        decision.RepertoireSongUuid,
                        disposition = decision.Disposition.ToString(),
                        decision.Reason,
                        decision.Authority,
                        evidence,
                    },
                    t,
                    cancellationToken: ct
                )
            );
            if (!exact)
            {
                throw new NormalizationConflictException(
                    "A claim already has another durable correspondence decision."
                );
            }
        }
    }

    internal async Task WriteStagePublishedAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid attemptUuid,
        Guid stageUuid,
        NormalizationPublicationResult receipt,
        CancellationToken ct
    )
    {
        var json = Serialize(receipt);
        var attempt = await RequireAttempt(c, t, attemptUuid, ct);
        var stage = (await ReadStage(c, t, attemptUuid, stageUuid, ct))!;
        if (
            attempt.Result != null
            || stage.Ticket == null
            || stage.Superseded
            || stage.FailureReason != null
            || stage.Kind == RepertoireResolutionStageKind.DecisionPage
            || receipt.WorkUuid != stage.WorkUuid
            || receipt.Status != NormalizationPublicationStatus.Published
        )
        {
            throw new NormalizationConflictException(
                "A publication receipt requires its unfinished admitted repertoire stage."
            );
        }
        if (stage.Receipt != null)
        {
            RequireSame(stage.Receipt, receipt, "A repertoire publication receipt is immutable.");
            return;
        }
        var effects = await CaptureOwnEffectsAsync(c, t, stage, ct);
        var effectsJson = effects == null ? null : Serialize(effects);
        await c.ExecuteAsync(
            new CommandDefinition(
                "UPDATE normalization.repertoire_resolution_stages SET receipt=@json::jsonb,own_effects=@effectsJson::jsonb WHERE stage_uuid=@stageUuid",
                new
                {
                    stageUuid,
                    json,
                    effectsJson,
                },
                t,
                cancellationToken: ct
            )
        );
        await AdvanceCheckpoint(c, t, attemptUuid, ct);
        if (stage.Kind == RepertoireResolutionStageKind.AcknowledgeSource)
        {
            if (attempt.Stages.Any(value => value.FailureReason != null))
            {
                throw new NormalizationConflictException(
                    "A failed repertoire page prevents complete-source acknowledgment."
                );
            }
            if (
                attempt.Stages.Any(value =>
                    value.StageUuid != stageUuid
                    && value.Kind != RepertoireResolutionStageKind.DecisionPage
                    && value.Receipt == null
                    && !value.Superseded
                    && value.FailureReason == null
                )
            )
            {
                throw new NormalizationConflictException(
                    "Finish every accepted page and its retirement before acknowledging the account."
                );
            }
            var count = await c.ExecuteScalarAsync<int>(
                new CommandDefinition(
                    "SELECT count(*) FROM normalization.repertoire_resolution_associations WHERE attempt_uuid=@attemptUuid",
                    new { attemptUuid },
                    t,
                    cancellationToken: ct
                )
            );
            if (count != attempt.Preparation!.Source.Claims.Count)
            {
                throw new NormalizationConflictException(
                    "Source acknowledgment requires a durable decision for every exact claim."
                );
            }
            var changed = await c.ExecuteAsync(
                new CommandDefinition(
                    "UPDATE normalization.repertoire_resolution_registrations SET current_attempt_uuid=@attemptUuid WHERE registration_uuid=@registrationUuid AND current_attempt_uuid IS NOT DISTINCT FROM @previous::uuid",
                    new
                    {
                        attemptUuid,
                        registrationUuid = attempt.Registration.RegistrationUuid,
                        previous = attempt.Registration.CurrentAttemptUuid,
                    },
                    t,
                    cancellationToken: ct
                )
            );
            if (changed != 1)
            {
                throw new NormalizationConflictException(
                    "A newer completed source account already owns this registration."
                );
            }
            await SaveResult(
                c,
                t,
                await BuildResult(
                    c,
                    t,
                    attemptUuid,
                    RepertoireResolutionDisposition.Completed,
                    "source_acknowledged",
                    ct
                ),
                ct
            );
        }
    }

    private static async Task<RepertoireResolutionResult> BuildResult(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid uuid,
        RepertoireResolutionDisposition disposition,
        string reason,
        CancellationToken ct
    )
    {
        var counts = (
            await c.QueryAsync<DispositionCount>(
                new CommandDefinition(
                    "SELECT disposition AS \"Disposition\",count(*)::integer AS \"Count\" FROM normalization.repertoire_resolution_associations WHERE attempt_uuid=@uuid GROUP BY disposition",
                    new { uuid },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToDictionary(value => value.Disposition, value => value.Count);
        return new(
            uuid,
            disposition,
            reason,
            counts.GetValueOrDefault("Bound"),
            counts.GetValueOrDefault("Provisional"),
            counts.GetValueOrDefault("Review"),
            counts.GetValueOrDefault("Unresolved")
        );
    }

    private static Task SaveResult(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        RepertoireResolutionResult result,
        CancellationToken ct
    ) =>
        c.ExecuteAsync(
            new CommandDefinition(
                "UPDATE normalization.repertoire_resolution_attempts SET result=@json::jsonb,checkpoint_revision=checkpoint_revision+1 WHERE attempt_uuid=@uuid",
                new { uuid = result.AttemptUuid, json = Serialize(result) },
                t,
                cancellationToken: ct
            )
        );

    private static bool SetEquals<T>(IEnumerable<T> left, IEnumerable<T> right) =>
        left.ToHashSet().SetEquals(right);

    private sealed record WorkRow(
        string Domain,
        string Kind,
        Guid Uuid,
        string State,
        string Ticket
    );

    private sealed record DispositionCount(string Disposition, int Count);
}
