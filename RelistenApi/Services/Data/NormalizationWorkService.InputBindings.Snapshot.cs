using System;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationWorkService
{
    /// <summary>
    /// Reads the owner and every contributor, including tombstones, in one primary read-only
    /// repeatable-read transaction. Unmanaged inputs are exposed as an unpersisted baseline;
    /// they are never silently dropped. Bounds fail explicitly before loading membership rows.
    /// The result contains ownership keys, not refreshed evidence or producer revisions.
    /// </summary>
    public async Task<NormalizationInputContributionsSnapshot> GetInputContributionsAsync(
        NormalizationSubjectKey subject,
        NormalizationInputContributionsLimits? limits = null,
        CancellationToken cancellationToken = default
    )
    {
        limits ??= new();
        if (
            limits.MaximumContributors is < 1 or > 1000
            || limits.MaximumBindings is < 1 or > 100000
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(limits),
                "Use 1–1000 contributors and 1–100000 bindings."
            );
        }
        await using var connection = db.CreateConnection(false, false);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken
        );
        await connection.ExecuteAsync(
            new CommandDefinition(
                "SET TRANSACTION READ ONLY",
                transaction: transaction,
                cancellationToken: cancellationToken
            )
        );
        var result = await GetInputContributionsInSnapshotAsync(
            connection,
            transaction,
            subject,
            limits,
            cancellationToken
        );
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    // Domain facts readers join this metadata-only membership read to their own primary snapshot.
    // It never refreshes evidence, configures an owner or opens a second transaction.
    internal static async Task<NormalizationInputContributionsSnapshot> GetInputContributionsInSnapshotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        NormalizationSubjectKey subject,
        NormalizationInputContributionsLimits limits,
        CancellationToken cancellationToken
    )
    {
        var owner = await connection.QuerySingleOrDefaultAsync<SubjectRow>(
            new CommandDefinition(
                $"SELECT {SubjectColumns} FROM normalization.work_subjects WHERE domain=@Domain AND subject_kind=@Kind AND subject_uuid=@Uuid",
                subject,
                transaction,
                cancellationToken: cancellationToken
            )
        );
        if (owner == null)
        {
            return new(null, []);
        }
        var counts = await CountContributionBindings(
            connection,
            transaction,
            owner.Uuid,
            cancellationToken
        );
        if (
            counts.Contributors > limits.MaximumContributors
            || counts.Bindings > limits.MaximumBindings
        )
        {
            throw ContributionSnapshotTooLarge(owner, counts);
        }
        var contributions = await ReadContributionStates(
            connection,
            transaction,
            owner.Uuid,
            null,
            cancellationToken
        );
        NormalizationInputContributionState? unmanaged = null;
        if (contributions.Length == 0)
        {
            var bindings = await CountUnmanagedBindings(
                connection,
                transaction,
                owner.Uuid,
                cancellationToken
            );
            if (bindings > limits.MaximumBindings)
            {
                throw ContributionSnapshotTooLarge(owner, counts, bindings);
            }
            if (bindings != 0)
            {
                var original = await ReadUnmanagedBindings(
                    connection,
                    transaction,
                    owner.Uuid,
                    cancellationToken
                );
                unmanaged = ToContributionState(
                    owner.Uuid,
                    new(
                        new("relisten", "preexisting_owner_bindings", owner.Uuid.ToString("D")),
                        0,
                        original
                    ),
                    0
                );
            }
        }
        var result = new NormalizationInputContributionsSnapshot(
            owner.ToState(),
            contributions
                .OrderBy(state => state.Contributor.Provider, StringComparer.Ordinal)
                .ThenBy(state => state.Contributor.ResourceType, StringComparer.Ordinal)
                .ThenBy(state => state.Contributor.ProviderKey, StringComparer.Ordinal)
                .ToArray(),
            unmanaged
        );
        return result;
    }

    private static Task<ContributionBindingCounts> CountContributionBindings(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid subjectUuid,
        CancellationToken ct
    ) =>
        c.QuerySingleAsync<ContributionBindingCounts>(
            new CommandDefinition(
                """
                WITH headers AS MATERIALIZED (SELECT uuid,revision FROM normalization.work_input_contributions WHERE subject_uuid=@subjectUuid)
                SELECT (SELECT count(*) FROM headers) AS "Contributors",
                    (SELECT COALESCE(sum(revision::numeric),0)::text FROM headers) AS "RevisionSum",
                    (SELECT count(*) FROM normalization.work_contribution_inputs i JOIN headers h ON h.uuid=i.contribution_uuid)
                    +(SELECT count(*) FROM normalization.work_contribution_dependencies i JOIN headers h ON h.uuid=i.contribution_uuid)
                    +(SELECT count(*) FROM normalization.work_contribution_catalog_inputs i JOIN headers h ON h.uuid=i.contribution_uuid)
                    +(SELECT count(*) FROM normalization.work_contribution_source_identities i JOIN headers h ON h.uuid=i.contribution_uuid)
                    +(SELECT count(*) FROM normalization.work_contribution_mapping_registry_inputs i JOIN headers h ON h.uuid=i.contribution_uuid)
                    +(SELECT count(*) FROM normalization.work_contribution_venue_inputs i JOIN headers h ON h.uuid=i.contribution_uuid)
                    +(SELECT count(*) FROM normalization.work_contribution_candidate_inputs i JOIN headers h ON h.uuid=i.contribution_uuid)
                    +(SELECT count(*) FROM normalization.work_contribution_owner_observations i JOIN headers h ON h.uuid=i.contribution_uuid)
                    +(SELECT count(*) FROM normalization.work_contribution_source_uuids i JOIN headers h ON h.uuid=i.contribution_uuid) AS "Bindings"
                """,
                new { subjectUuid },
                t,
                cancellationToken: ct
            )
        );

    private static Task<long> CountUnmanagedBindings(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid subjectUuid,
        CancellationToken ct
    ) =>
        c.ExecuteScalarAsync<long>(
            new CommandDefinition(
                """
                SELECT (SELECT count(*) FROM normalization.work_inputs WHERE subject_uuid=@subjectUuid)
                    +(SELECT count(*) FROM normalization.work_dependencies WHERE subject_uuid=@subjectUuid)
                    +(SELECT count(*) FROM normalization.work_catalog_inputs WHERE subject_uuid=@subjectUuid)
                    +(SELECT count(*) FROM normalization.work_source_identity_inputs WHERE subject_uuid=@subjectUuid)
                    +(SELECT count(*) FROM normalization.work_mapping_registry_inputs WHERE subject_uuid=@subjectUuid)
                    +(SELECT count(*) FROM normalization.work_venue_inputs WHERE subject_uuid=@subjectUuid)
                    +(SELECT count(*) FROM normalization.work_candidate_inputs WHERE subject_uuid=@subjectUuid)
                    +(SELECT count(*) FROM normalization.work_owner_observations WHERE subject_uuid=@subjectUuid)
                    +(SELECT count(*) FROM normalization.work_source_uuid_inputs WHERE subject_uuid=@subjectUuid)
                """,
                new { subjectUuid },
                t,
                cancellationToken: ct
            )
        );

    /// <summary>Metadata-only retry observation; it grants no source or publication authority.</summary>
    internal static async Task<NormalizationInputContributionsCapacityStamp> ReadInputContributionCapacityStampAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid subjectUuid,
        CancellationToken ct
    )
    {
        var counts = await CountContributionBindings(connection, transaction, subjectUuid, ct);
        var unmanaged =
            counts.Contributors == 0
                ? await CountUnmanagedBindings(connection, transaction, subjectUuid, ct)
                : 0;
        return new(counts.Contributors, counts.Bindings, counts.RevisionSum, unmanaged);
    }

    private static NormalizationInputContributionsCapacityException ContributionSnapshotTooLarge(
        SubjectRow owner,
        ContributionBindingCounts counts,
        long unmanagedBindings = 0
    ) =>
        new(
            new(
                owner.Uuid,
                owner.DesiredGeneration,
                owner.EditorialRevision,
                owner.PublishedWorkUuid
            ),
            new(counts.Contributors, counts.Bindings, counts.RevisionSum, unmanagedBindings)
        );

    private sealed record ContributionBindingCounts(
        long Contributors,
        string RevisionSum,
        long Bindings
    );
}
