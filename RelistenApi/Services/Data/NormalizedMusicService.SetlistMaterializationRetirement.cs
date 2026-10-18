using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizedMusicService
{
    internal async Task<NormalizationPublicationResult> PublishSetlistMaterializationRetirementAsync(
        Guid workUuid,
        Guid attemptUuid,
        CancellationToken ct = default
    )
    {
        var materializations = new NormalizationSetlistMaterializationStore(db);
        var original =
            await materializations.GetProcessingAsync(attemptUuid, ct)
            ?? throw new ArgumentException("Unknown setlist materialization attempt.");
        RequireSetlistRetirementIdentifiers(original, workUuid);
        return await work.TryPublishAsync(
            workUuid,
            async (c, t, token) =>
            {
                var attempt = await materializations.RequireRetirementForPublicationAsync(
                    c,
                    t,
                    attemptUuid,
                    workUuid,
                    token
                );
                RequireSetlistRetirementIdentifiers(attempt, workUuid);
                await RequireSetlistRetirementProofAsync(c, t, materializations, attempt, token);
                // This source owner never receives music authority. The callback acknowledges only
                // the already-admitted removal of this consumer's exact old contribution.
                await materializations.WriteRetirementPublishedAsync(
                    c,
                    t,
                    attemptUuid,
                    new(workUuid, NormalizationPublicationStatus.Published, false),
                    token
                );
            },
            ct,
            candidateWriteScopes: []
        );
    }

    private static void RequireSetlistRetirementIdentifiers(
        SetlistMaterializationAttempt attempt,
        Guid workUuid
    )
    {
        var preparation = NormalizationSetlistMaterializer.RequirePreparation(attempt);
        var cleanupOwner = attempt.Retirement?.Owner;
        var requirement = preparation.ContributionsToRetire.SingleOrDefault(value =>
            value.Owner == cleanupOwner && value.Contributor == attempt.Retirement?.Contributor
        );
        var cleanup = attempt.Retirement;
        if (
            !preparation.RequiresObservationRetirement
            || requirement == null
            || attempt.Receipt?.Status != NormalizationPublicationStatus.Published
            || attempt.Receipt.WorkUuid != attempt.AttemptUuid
            || cleanup?.WorkUuid != workUuid
            || cleanup.Ticket?.WorkUuid != workUuid
            || cleanup.FailureReason != null
            || cleanup.Superseded
            || cleanup.AdoptedWorkUuid != attempt.AttemptUuid
            || cleanup.Owner != requirement.Owner
            || cleanup.Contributor != requirement.Contributor
            || requirement.Owner == preparation.Context!.Owner
            || requirement.Owner.Domain != "songs"
            || requirement.Owner.Kind is not ("setlist" or "setlist_materialization")
            || requirement.Contributor
                != NormalizationSetlistMaterializer.ContributionKey(
                    attempt.Registration.RegistrationUuid
                )
            || cleanup.ExpectedContributionRevision < requirement.ContributionRevision
        )
        {
            throw new NormalizationConflictException(
                "Watcher retirement requires this canonical adoption and its exact admitted former owner."
            );
        }
    }

    private static async Task RequireSetlistRetirementProofAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        NormalizationSetlistMaterializationStore materializations,
        SetlistMaterializationAttempt attempt,
        CancellationToken ct
    )
    {
        var preparation = NormalizationSetlistMaterializer.RequirePreparation(attempt);
        var cleanup = attempt.Retirement!;
        var requirement = preparation.ContributionsToRetire.Single(value =>
            value.Owner == cleanup.Owner && value.Contributor == cleanup.Contributor
        );
        var original = await NormalizationSetlistMaterializer.ReadInstallingAdmissionAsync(
            c,
            t,
            attempt.Registration,
            requirement.Owner,
            requirement.ObservedContribution,
            ct
        );
        if (
            original.WorkUuid != requirement.ObservedWorkUuid
            || original.InstalledRevision != requirement.InstalledContributionRevision
            || !NormalizationSetlistMaterializer.Same(original.Ticket, requirement.ObservedTicket)
            || requirement.ObservedContribution.Revision != requirement.ContributionRevision
            || requirement.ObservedContribution.Contributor != requirement.Contributor
        )
        {
            throw new NormalizationConflictException(
                "The obsolete subscription does not match its original actual installing materializer admission."
            );
        }
        var adopted = await c.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM normalization.work_runs WHERE uuid=@uuid AND state='published' AND ticket=@ticket::jsonb)",
                new
                {
                    uuid = attempt.AttemptUuid,
                    ticket = JsonSerializer.Serialize(attempt.Ticket),
                },
                t,
                cancellationToken: ct
            )
        );
        if (!adopted)
        {
            throw new NormalizationConflictException(
                "Subscription retirement requires its real successor publication receipt."
            );
        }
        if (
            !NormalizationSetlistMaterializer.Same(
                cleanup.ReadSet,
                NormalizationSetlistMaterializer.PeerReadSet(
                    cleanup.OwnerContext,
                    cleanup.Contributor
                )
            )
        )
        {
            throw new NormalizationConflictException(
                "Watcher cleanup must consume only its fresh remaining peer bindings."
            );
        }
        var preparedOwn = cleanup.OwnerContext.Membership.Contributions.SingleOrDefault(value =>
            value.Contributor == cleanup.Contributor
        );
        var current = await NormalizationWorkService.GetInputContributionsInSnapshotAsync(
            c,
            t,
            cleanup.Owner,
            new(1000, 100000),
            ct
        );
        var own = current.Contributions.SingleOrDefault(value =>
            value.Contributor == cleanup.Contributor
        );
        if (
            preparedOwn?.Revision != cleanup.ExpectedContributionRevision
            || own?.Revision != cleanup.ExpectedContributionRevision + 1
            || HasSetlistContributionBindings(own)
        )
        {
            throw new NormalizationConflictException(
                "Watcher cleanup requires the exact owned tombstone produced by its admitted replacement."
            );
        }
        if (HasSetlistContributionBindings(preparedOwn!))
        {
            // Current peers may forward our private keys without transferring semantic ownership.
            // Rebase only when the saved pre-admission membership still belongs to the same installer.
            await RequireOriginalSetlistInstallationAsync(
                c,
                t,
                attempt,
                requirement,
                preparedOwn!,
                ct
            );
        }
        else
        {
            await RequireSetlistRetirementChainAsync(c, t, attempt, ct);
        }
    }

    private static async Task RequireSetlistRetirementChainAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SetlistMaterializationAttempt attempt,
        CancellationToken ct
    )
    {
        var cleanup = attempt.Retirement!;
        var requirement = attempt.Preparation!.ContributionsToRetire.Single(value =>
            value.Owner == cleanup.Owner && value.Contributor == cleanup.Contributor
        );
        var rows = (
            await c.QueryAsync<RetirementChainRow>(
                new CommandDefinition(
                    """
                    SELECT old.work_uuid AS "WorkUuid",(old.retirement->>'ExpectedContributionRevision')::bigint AS "Revision",
                        (old.ticket IS NOT NULL AND run.ticket=old.ticket AND run.state IN ('running','stale')
                            AND old.retirement->'Owner'=@owner::jsonb AND old.retirement->'Contributor'=@contributor::jsonb
                            AND old.retirement->>'AdoptedWorkUuid'=@adoption::text) AS "Admitted"
                    FROM normalization.setlist_materialization_retirements old
                    LEFT JOIN normalization.work_runs run ON run.uuid=old.work_uuid
                    WHERE old.attempt_uuid=@attemptUuid AND old.retirement->'Owner'=@owner::jsonb
                        AND old.retirement->'Contributor'=@contributor::jsonb AND old.ordinal<(
                        SELECT ordinal FROM normalization.setlist_materialization_retirements WHERE work_uuid=@workUuid)
                    ORDER BY old.ordinal LIMIT 1000
                    """,
                    new
                    {
                        attemptUuid = attempt.AttemptUuid,
                        workUuid = cleanup.WorkUuid,
                        owner = JsonSerializer.Serialize(requirement.Owner),
                        contributor = JsonSerializer.Serialize(requirement.Contributor),
                        adoption = attempt.AttemptUuid,
                    },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        var admitted = rows.Where(value => value.Admitted).ToArray();
        if (admitted.Length == 0)
        {
            throw new NormalizationConflictException(
                "An empty private contribution needs its original admitted cleanup predecessor."
            );
        }
        var first = admitted[0];
        var baselineJson = await c.QuerySingleAsync<string>(
            new CommandDefinition(
                """
                SELECT member::text FROM normalization.setlist_materialization_retirements old
                CROSS JOIN LATERAL jsonb_array_elements(old.retirement->'OwnerContext'->'Membership'->'Contributions') member
                WHERE old.work_uuid=@workUuid AND member->'Contributor'=@contributor::jsonb
                """,
                new
                {
                    workUuid = first.WorkUuid,
                    contributor = JsonSerializer.Serialize(requirement.Contributor),
                },
                t,
                cancellationToken: ct
            )
        );
        var baseline = JsonSerializer.Deserialize<NormalizationInputContributionState>(
            baselineJson
        )!;
        if (baseline.Revision != first.Revision)
        {
            throw new NormalizationConflictException(
                "The cleanup chain must start at its exact saved private membership revision."
            );
        }
        await RequireOriginalSetlistInstallationAsync(c, t, attempt, requirement, baseline, ct);
        var revision = first.Revision;
        foreach (var row in admitted)
        {
            if (row.Revision < revision)
            {
                throw new NormalizationConflictException(
                    "Watcher cleanup cannot reuse an earlier private contribution revision."
                );
            }
            revision = row.Revision + 1;
        }
        // Peers may forward this already-empty tombstone between real cleanup admissions.
        // The original installer check above excludes a newer materializer installation;
        // monotone revisions and the current empty membership preserve the cleanup obligation.
        if (cleanup.ExpectedContributionRevision < revision)
        {
            throw new NormalizationConflictException(
                "A newer cleanup revision requires its original owned admission predecessor."
            );
        }
    }

    private static async Task RequireOriginalSetlistInstallationAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SetlistMaterializationAttempt attempt,
        SetlistContributionRetirementRequirement requirement,
        NormalizationInputContributionState observed,
        CancellationToken ct
    )
    {
        var installation = await NormalizationSetlistMaterializer.ReadInstallingAdmissionAsync(
            c,
            t,
            attempt.Registration,
            requirement.Owner,
            observed,
            ct
        );
        if (
            installation.WorkUuid != requirement.ObservedWorkUuid
            || installation.InstalledRevision != requirement.InstalledContributionRevision
            || !NormalizationSetlistMaterializer.Same(
                installation.Ticket,
                requirement.ObservedTicket
            )
        )
        {
            throw new NormalizationConflictException(
                "Cleanup cannot retire a newer materializer installation."
            );
        }
    }

    private static bool HasSetlistContributionBindings(NormalizationInputContributionState state) =>
        state.Resources.Count > 0
        || state.DependencySubjectUuids.Count > 0
        || state.CatalogArtistUuids.Count > 0
        || state.SourceIdentifiers.Count > 0
        || state.UsesMappingRegistry
        || (state.SourceUuids?.Count ?? 0) > 0
        || (state.ImportedVenueUuids?.Count ?? 0) > 0
        || (state.CandidateUniverses?.Count ?? 0) > 0
        || (state.OwnerObservationSubjectUuids?.Count ?? 0) > 0;

    private sealed record RetirementChainRow(Guid WorkUuid, long Revision, bool Admitted);
}
