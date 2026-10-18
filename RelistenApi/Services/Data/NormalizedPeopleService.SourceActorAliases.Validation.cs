using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizedPeopleService
{
    // Work has already locked/fenced the complete admitted envelope. The domain still
    // authenticates why those keys are sufficient and which typed facts authorize each delta.
    // None of the readers below configures an owner or acquires another Work owner lock.
    private async Task ValidateSourceActorAliasStageAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid applicationUuid,
        SourceActorAliasPersonStage stage,
        CancellationToken ct
    )
    {
        var application = (
            await SourceActorAliasApplicationStore.Read(c, t, applicationUuid, false, ct)
        )!;
        if (
            application.Plan == null
            || stage.Ticket == null
            || application.Version != SourceActorAliasApplicationService.Version
            || application.Plan.Proposal.ResolutionAttemptUuid != application.ResolutionAttemptUuid
            || stage.Deltas.Count == 0
            || stage.Deltas.Any(delta => delta.OriginalPersonUuid != stage.OriginalPersonUuid)
        )
        {
            throw new ArgumentException(
                "A source stage requires its complete supported application and one original person."
            );
        }
        await ValidateSourceAliasMap(c, t, stage, ct);
        var source = await AuthenticateSourceAliasFacts(c, t, application, stage, ct);
        await ValidateSourceAliasTargets(c, t, stage, ct);
        var primary = (stage.Facts.CurrentReadSet.OwnerObservations ?? []).Single(value =>
            value.SubjectUuid == stage.OwnerSubjectUuid
        );
        var actual = await SourceActorAliasApplicationService.ReadStageReadSet(
            c,
            t,
            source,
            stage.PriorMap,
            stage.Facts.Targets,
            stage.Facts.Membership,
            ct,
            primary
        );
        if (!SourceAliasSame(actual, stage.Facts.CurrentReadSet))
        {
            throw new NormalizationConflictException(
                "The complete source map observations changed after preparation."
            );
        }
        await ValidateSourceAliasEnvelope(c, t, stage, ct);
        await ValidateSourceAliasClaims(c, t, application, stage, source, ct);
        foreach (var delta in stage.Deltas)
        {
            ct.ThrowIfCancellationRequested();
            switch (delta.Kind)
            {
                case SourceActorAliasDeltaKind.Add:
                    await ValidateSourceAliasAddition(c, t, application, stage, delta, ct);
                    break;
                case SourceActorAliasDeltaKind.ObserveIncomplete:
                    var observation = SourceActorAliasApplicationService
                        .IncompleteObservations(
                            application,
                            source,
                            stage.OriginalPersonUuid,
                            stage.PriorMap
                        )
                        .SingleOrDefault(value => value.Scope == delta.Scope);
                    if (!SourceAliasSame(delta, observation))
                    {
                        throw new ArgumentException(
                            "Incomplete source observation must retain its exact current scope without changing authority."
                        );
                    }
                    break;
                case SourceActorAliasDeltaKind.Retire:
                    await ValidateSourceAliasWithdrawal(c, t, stage, delta, ct);
                    break;
                case SourceActorAliasDeltaKind.FinalizeReplacement:
                case SourceActorAliasDeltaKind.Clear:
                    await ValidateSourceAliasFinalization(
                        c,
                        t,
                        application,
                        stage,
                        source,
                        delta,
                        ct
                    );
                    break;
                default:
                    throw new ArgumentException("Unsupported source alias stage delta.");
            }
        }
        await ValidatePublicationEvidenceAsync(
            c,
            t,
            stage.WorkUuid,
            "people",
            stage
                .Deltas.Where(delta => delta.Kind == SourceActorAliasDeltaKind.Add)
                .SelectMany(delta => delta.Names)
                .Select(name => name.Evidence)
                .ToArray(),
            ct
        );
    }

    private static async Task ValidateSourceAliasMap(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SourceActorAliasPersonStage stage,
        CancellationToken ct
    )
    {
        var membership = await NormalizationWorkService.GetInputContributionsInSnapshotAsync(
            c,
            t,
            new("people", "person", stage.OriginalPersonUuid),
            PeopleResolutionCapacity.ContributionMembership,
            ct
        );
        var configured = await SourceActorAliasApplicationStore.GetConfiguredStageInSnapshotAsync(
            c,
            t,
            stage.OriginalPersonUuid,
            membership,
            ct
        );
        if (
            configured?.StageUuid != stage.StageUuid
            || stage.Facts.Membership.Owner?.SubjectUuid != stage.OwnerSubjectUuid
            || stage.Facts.Membership.Owner.Subject
                != new NormalizationSubjectKey("people", "person", stage.OriginalPersonUuid)
            || stage.ExpectedConfigurationGeneration
                != stage.Facts.Membership.Owner.DesiredGeneration
        )
        {
            throw new NormalizationConflictException(
                "The original person stage no longer owns the exact configured map."
            );
        }
        var prior = stage.PredecessorStageUuid is Guid predecessor
            ? await SourceActorAliasApplicationStore.ReadStage(c, t, predecessor, ct)
            : null;
        if (
            prior != null
                && (prior.OriginalPersonUuid != stage.OriginalPersonUuid || prior.Receipt == null)
            || !SourceAliasSame(stage.PriorMap, prior?.Receipt?.ResultingMap ?? [])
        )
        {
            throw new ArgumentException(
                "A complete carried map must be the exact original committed predecessor receipt."
            );
        }
        var assignments = (
            await c.QueryAsync<Guid>(
                new CommandDefinition(
                    "SELECT uuid FROM normalization.source_actor_alias_assignments WHERE person_uuid=@person AND retired_work_uuid IS NULL",
                    new { person = stage.OriginalPersonUuid },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToHashSet();
        var supports = (
            await c.QueryAsync<SourceAliasSupportKey>(
                new CommandDefinition(
                    """
                    SELECT support.uuid AS "SupportUuid",support.alias_uuid AS "AliasUuid",support.assignment_uuid AS "AssignmentUuid"
                    FROM normalization.person_alias_contributor_support support
                    JOIN normalization.source_actor_alias_assignments assignment ON assignment.uuid=support.assignment_uuid
                    WHERE assignment.person_uuid=@person AND assignment.retired_work_uuid IS NULL AND support.retired_work_uuid IS NULL
                    """,
                    new { person = stage.OriginalPersonUuid },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToHashSet();
        var expected = stage.PriorMap.SelectMany(entry => entry.Bindings).ToArray();
        if (
            !assignments.SetEquals(expected.Select(binding => binding.AssignmentUuid))
            || !supports.SetEquals(
                expected.Select(binding => new SourceAliasSupportKey(
                    binding.SupportUuid,
                    binding.AliasUuid,
                    binding.AssignmentUuid
                ))
            )
        )
        {
            throw new NormalizationConflictException(
                "Active source authority differs from the complete saved original-person map."
            );
        }
    }

    private static async Task ValidateSourceAliasTargets(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SourceActorAliasPersonStage stage,
        CancellationToken ct
    )
    {
        if (
            stage.Facts.Targets.Select(target => target.OriginalPersonUuid).Distinct().Count()
                != stage.Facts.Targets.Count
            || !stage.Facts.Targets.Any(target =>
                target.OriginalPersonUuid == stage.OriginalPersonUuid
            )
        )
        {
            throw new ArgumentException(
                "A source person stage requires its complete original target component."
            );
        }
        // Prepared targets are evidence, not a current cache. Read every original again in
        // this publication transaction, sharing only acquisition of a terminal component.
        var currentTargets = await SourceActorReviewTargets.ReadManyAsync(
            c,
            t,
            stage.Facts.Targets.Select(target => target.OriginalPersonUuid),
            ct
        );
        for (var index = 0; index < stage.Facts.Targets.Count; index++)
        {
            var target = stage.Facts.Targets[index];
            var current = currentTargets[index];
            if (!SourceAliasSame(target, RestoreSourceAliasPrimary(current, stage)))
            {
                throw new NormalizationConflictException(
                    "An original or resolved target component changed after preparation."
                );
            }
        }
    }

    private static SourceActorReviewTarget RestoreSourceAliasPrimary(
        SourceActorReviewTarget target,
        SourceActorAliasPersonStage stage
    ) =>
        target with
        {
            // Admission changes only the configured primary Work state. Work checks that
            // current header; identity revisions and all other component owners stay exact.
            OwnerObservations = target
                .OwnerObservations.Select(owner =>
                    owner.SubjectUuid == stage.OwnerSubjectUuid
                        ? (stage.Facts.CurrentReadSet.OwnerObservations ?? []).Single(value =>
                            value.SubjectUuid == owner.SubjectUuid
                        )
                        : owner
                )
                .ToArray(),
        };

    private static bool SourceAliasSame<T>(T left, T right) =>
        SourceActorAliasApplicationCheckpointCodec.Same(left, right);

    private sealed record SourceAliasSupportKey(
        Guid SupportUuid,
        Guid AliasUuid,
        Guid AssignmentUuid
    );
}
