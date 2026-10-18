using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizedPeopleService
{
    /// <summary>
    /// IDs select retained preparations; callers never provide winners or accepted alias data.
    /// Work checks committed replay before freshness or callback locks. Fault/cancellation leaves
    /// the immutable admitted stage retryable; canonical rows, authority, map and receipt share
    /// Work's transaction and cannot leave a partially committed publication.
    /// </summary>
    public async Task<NormalizationPublicationResult> PublishSourceActorAliasStageAsync(
        Guid applicationUuid,
        Guid stageUuid,
        CancellationToken ct = default
    )
    {
        var store = new SourceActorAliasApplicationStore(db);
        // Request identity is immutable and must be checked even for a committed replay,
        // whose Work callback deliberately does not run again. Load no factual preparation
        // until Work has performed its replay/freshness checks and acquired the lock union.
        var workUuid =
            await db.WithWriteConnection(c =>
                c.QuerySingleOrDefaultAsync<Guid?>(
                    new CommandDefinition(
                        "SELECT work_uuid FROM normalization.source_actor_alias_person_stages WHERE application_uuid=@applicationUuid AND stage_uuid=@stageUuid",
                        new { applicationUuid, stageUuid },
                        cancellationToken: ct
                    )
                )
            ) ?? throw new ArgumentException("Unknown source alias application/stage identity.");

        var publication = await work.TryPublishAsync(
            workUuid,
            (c, t, cancellation) =>
                PublishPreparedSourceActorAliasStage(
                    c,
                    t,
                    store,
                    applicationUuid,
                    stageUuid,
                    cancellation
                ),
            ct,
            candidateWriteScopes: IdentityCandidateWriteScopes
        );

        if (publication.Status == NormalizationPublicationStatus.Published)
        {
            var retained = await store.GetStageAsync(stageUuid, ct);
            if (retained?.Receipt == null)
            {
                throw new InvalidOperationException(
                    "Committed source alias Work is missing its atomic domain receipt."
                );
            }
        }

        return publication;
    }

    private async Task PublishPreparedSourceActorAliasStage(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SourceActorAliasApplicationStore store,
        Guid applicationUuid,
        Guid stageUuid,
        CancellationToken ct
    )
    {
        await c.ExecuteAsync(
            new CommandDefinition(
                "SELECT pg_advisory_xact_lock_shared(hashtextextended('normalization:people:identity',0))",
                transaction: t,
                cancellationToken: ct
            )
        );
        var stage = await SourceActorAliasApplicationStore.RequireStage(
            c,
            t,
            applicationUuid,
            stageUuid,
            ct
        );
        await ValidateSourceActorAliasStageAsync(c, t, applicationUuid, stage, ct);

        var map = stage.PriorMap.ToList();
        var names = new List<SourceActorAliasNameOutcome>();
        var retiredAssignments = new List<Guid>();
        var retiredSupports = new List<Guid>();
        foreach (var delta in stage.Deltas)
        {
            switch (delta.Kind)
            {
                case SourceActorAliasDeltaKind.Add:
                    var addition = await AddSourceActorAliases(
                        c,
                        t,
                        applicationUuid,
                        stage,
                        delta,
                        ct
                    );
                    names.AddRange(addition.Names);
                    ApplySourceAliasAddition(map, stage, delta, addition.Bindings);
                    break;
                case SourceActorAliasDeltaKind.ObserveIncomplete:
                    ApplySourceAliasIncompleteObservation(map, delta);
                    break;
                case SourceActorAliasDeltaKind.Retire:
                case SourceActorAliasDeltaKind.FinalizeReplacement:
                case SourceActorAliasDeltaKind.Clear:
                    await RetireSourceActorAliases(c, t, stage, delta, ct);
                    retiredAssignments.AddRange(delta.RetireAssignmentUuids);
                    retiredSupports.AddRange(delta.RetireSupportUuids);
                    ApplySourceAliasRetirement(map, delta);
                    break;
                default:
                    throw new ArgumentException("Unsupported typed source alias delta.");
            }
        }

        var blocked = names.Any(name =>
            name.Disposition
                is not (
                    SourceActorAliasNameDisposition.Accepted
                    or SourceActorAliasNameDisposition.ExactDuplicate
                )
        );
        var incompleteObservation = stage.Deltas.Any(delta =>
            delta.Kind == SourceActorAliasDeltaKind.ObserveIncomplete
        );
        var pendingReplacement =
            stage.Deltas.Any(delta => delta.Kind == SourceActorAliasDeltaKind.Add)
            || incompleteObservation;
        var disposition =
            blocked ? SourceActorAliasApplicationDisposition.Review
            : pendingReplacement ? SourceActorAliasApplicationDisposition.Partial
            : SourceActorAliasApplicationDisposition.Complete;
        var receipt = new SourceActorAliasStageReceipt(
            stage.StageUuid,
            stage.WorkUuid,
            stage.OriginalPersonUuid,
            names,
            retiredAssignments.Distinct().ToArray(),
            retiredSupports.Distinct().ToArray(),
            map,
            disposition,
            blocked ? "canonical_alias_outcomes_preserved_previous_scope_support"
                : incompleteObservation ? "incomplete_source_observation_preserved_support"
                : pendingReplacement ? "accepted_names_require_complete_field_finalization"
                : "prepared_source_alias_retirement_committed"
        );
        await store.WriteStagePublishedAsync(c, t, applicationUuid, receipt, ct);
    }
}
