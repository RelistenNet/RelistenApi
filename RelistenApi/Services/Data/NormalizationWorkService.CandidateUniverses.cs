using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationWorkService
{
    private static NormalizationCandidateUniverseKey[] NormalizeCandidateKeys(
        IEnumerable<NormalizationCandidateUniverseKey>? keys
    )
    {
        var result = (keys ?? [])
            .Distinct()
            .OrderBy(key => key.Domain, StringComparer.Ordinal)
            .ThenBy(key => key.ScopeUuid)
            .ToArray();
        foreach (var key in result)
        {
            var global = key.Domain is "people" or "venues" or "compositions";
            var valid = global
                ? key.ScopeUuid == Guid.Empty
                : key.Domain == "repertoire" && key.ScopeUuid != Guid.Empty;
            if (!valid)
            {
                throw new ArgumentException(
                    "Candidate pools require a supported global domain or one repertoire artist."
                );
            }
        }
        return result;
    }

    private static NormalizationCandidateUniverseDependency[] NormalizeCandidateUniverses(
        IReadOnlyList<NormalizationCandidateUniverseDependency>? inputs
    )
    {
        var result = (inputs ?? [])
            .Distinct()
            .OrderBy(input => input.Universe.Domain, StringComparer.Ordinal)
            .ThenBy(input => input.Universe.ScopeUuid)
            .ThenBy(input => input.Generation)
            .ToArray();
        NormalizeCandidateKeys(result.Select(input => input.Universe));
        if (
            result.Any(input => input.Generation < 0)
            || result.GroupBy(input => input.Universe).Any(group => group.Count() != 1)
        )
        {
            throw new ArgumentException(
                "Candidate pool snapshots require consistent nonnegative generations."
            );
        }
        return result;
    }

    /// <summary>
    /// Catalog clocks precede candidate scopes; resources and owners follow them. Lock every
    /// scope in the same order, choosing UPDATE immediately for a scope this callback can write.
    /// Taking SHARE first and upgrading later can deadlock two publications of the same pool.
    /// </summary>
    private static async Task<NormalizationCandidateUniverseDependency[]> LockCandidateUniverses(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<NormalizationCandidateUniverseKey> consumed,
        IReadOnlyList<NormalizationCandidateUniverseKey> written,
        bool initializeConsumed,
        CancellationToken ct
    )
    {
        var keys = NormalizeCandidateKeys(consumed.Concat(written));
        var writeSet = written.ToHashSet();
        var result = new List<NormalizationCandidateUniverseDependency>();
        foreach (var key in keys)
        {
            if (initializeConsumed || writeSet.Contains(key))
            {
                // Missing repertoire pools still participate in an empty-result read. The
                // original observed zero must be checked after initialization, not refreshed.
                await connection.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO normalization.candidate_universe_revisions(domain,scope_uuid,generation)
                        VALUES(@Domain,@ScopeUuid,0) ON CONFLICT DO NOTHING
                        """,
                        key,
                        transaction,
                        cancellationToken: ct
                    )
                );
            }
            var lockMode = writeSet.Contains(key) ? "UPDATE" : "SHARE";
            var generation = await connection.QuerySingleOrDefaultAsync<long?>(
                new CommandDefinition(
                    $"SELECT generation FROM normalization.candidate_universe_revisions WHERE domain=@Domain AND scope_uuid=@ScopeUuid FOR {lockMode}",
                    key,
                    transaction,
                    cancellationToken: ct
                )
            );
            if (generation == null)
            {
                throw new NormalizationConflictException(
                    "Candidate pool revision is missing; reload its facts."
                );
            }
            if (consumed.Contains(key))
            {
                result.Add(new(key, generation.Value));
            }
        }
        return result.ToArray();
    }

    private static void RequireCandidateUniverses(
        IReadOnlyList<NormalizationCandidateUniverseDependency> current,
        IReadOnlyList<NormalizationCandidateUniverseDependency> expected
    )
    {
        if (expected.Any(input => !current.Contains(input)))
        {
            throw new NormalizationConflictException(
                "Candidate pools changed after their facts were read; reload them before starting work."
            );
        }
    }

    private static Task ConfigureCandidateBindings(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid subjectUuid,
        IReadOnlyList<NormalizationCandidateUniverseDependency> inputs,
        CancellationToken ct
    ) =>
        connection.ExecuteAsync(
            new CommandDefinition(
                """
                DELETE FROM normalization.work_candidate_inputs i WHERE subject_uuid=@subjectUuid AND NOT EXISTS(
                    SELECT 1 FROM unnest(@domains::text[],@scopes::uuid[]) u(domain,scope_uuid)
                    WHERE u.domain=i.domain AND u.scope_uuid=i.scope_uuid);
                INSERT INTO normalization.work_candidate_inputs(subject_uuid,domain,scope_uuid)
                    SELECT @subjectUuid,domain,scope_uuid FROM unnest(@domains::text[],@scopes::uuid[]) u(domain,scope_uuid)
                    ON CONFLICT DO NOTHING
                """,
                new
                {
                    subjectUuid,
                    domains = inputs.Select(input => input.Universe.Domain).ToArray(),
                    scopes = inputs.Select(input => input.Universe.ScopeUuid).ToArray(),
                },
                transaction,
                cancellationToken: ct
            )
        );

    private static Task RecordCandidatePublication(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        NormalizationWorkTicket ticket,
        CancellationToken ct
    ) =>
        connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE normalization.work_candidate_inputs i SET published_generation=r.generation
                FROM normalization.candidate_universe_revisions r
                WHERE i.subject_uuid=@SubjectUuid AND r.domain=i.domain AND r.scope_uuid=i.scope_uuid
                """,
                ticket,
                transaction,
                cancellationToken: ct
            )
        );
}
