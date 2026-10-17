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
    private static Guid[]? NormalizeSourceUuidKeys(IEnumerable<Guid>? keys)
    {
        var result = (keys ?? []).Distinct().Order().ToArray();
        if (result.Any(uuid => uuid == Guid.Empty))
        {
            throw new ArgumentException("Source lookup dependencies require nonempty UUIDs.");
        }
        return result.Length == 0 ? null : result;
    }

    private static NormalizationSourceUuidDependency[] NormalizeSourceUuidInputs(
        IReadOnlyList<NormalizationSourceUuidDependency>? inputs
    )
    {
        var result = (inputs ?? []).Distinct().OrderBy(input => input.SourceUuid).ToArray();
        NormalizeSourceUuidKeys(result.Select(input => input.SourceUuid));
        if (
            result.Any(input => input.Generation < 0)
            || result.GroupBy(input => input.SourceUuid).Any(group => group.Count() != 1)
        )
        {
            throw new ArgumentException(
                "Source UUID snapshots require one consistent nonnegative generation per UUID."
            );
        }
        return result;
    }

    private static async Task<NormalizationSourceUuidDependency[]> LockSourceUuidClocks(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid[] sourceUuids,
        bool initialize,
        CancellationToken ct
    )
    {
        if (sourceUuids.Length == 0)
        {
            return [];
        }
        if (initialize)
        {
            // Absence is an original zero observation. Initialize before comparing it so a
            // concurrent arrival cannot be hidden by a freshly initialized clock.
            await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO normalization.catalog_source_uuid_revisions(source_uuid,generation)
                    SELECT source_uuid,0 FROM unnest(@sourceUuids::uuid[]) s(source_uuid)
                    ORDER BY source_uuid ON CONFLICT DO NOTHING
                    """,
                    new { sourceUuids },
                    transaction,
                    cancellationToken: ct
                )
            );
        }
        var rows = (
            await connection.QueryAsync<NormalizationSourceUuidDependency>(
                new CommandDefinition(
                    """
                    SELECT source_uuid AS "SourceUuid",generation AS "Generation"
                    FROM normalization.catalog_source_uuid_revisions WHERE source_uuid=ANY(@sourceUuids)
                    ORDER BY source_uuid FOR SHARE
                    """,
                    new { sourceUuids },
                    transaction,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        if (rows.Length != sourceUuids.Length)
        {
            throw new NormalizationConflictException(
                "Source UUID revisions are missing; reload the catalog lookup."
            );
        }
        return rows;
    }
}
