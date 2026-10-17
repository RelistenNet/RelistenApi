using System;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationCatalogBindingService
{
    /// <summary>
    /// Observes exact source presence and its retained lookup clock together, without fetching
    /// or inventing input evidence. After a missing legacy refresh, a present result requires
    /// restarting the refresh; only an observed absence can support a negative lookup outcome.
    /// Pass Dependency unchanged into work admission. This read registers no work or clock.
    /// </summary>
    public Task<NormalizationSourceUuidLookup> ProbeRecordingUuidAsync(
        Guid sourceUuid,
        CancellationToken cancellationToken = default
    )
    {
        if (sourceUuid == Guid.Empty)
        {
            throw new ArgumentException("A catalog source UUID is required.", nameof(sourceUuid));
        }

        return WithCatalogSnapshotAsync(
            async (connection, transaction, ct) =>
            {
                var row = await connection.QuerySingleAsync<SourcePresenceRow>(
                    new CommandDefinition(
                        """
                        SELECT EXISTS(SELECT 1 FROM sources WHERE uuid=@sourceUuid) AS "Exists",
                            COALESCE((SELECT generation FROM normalization.catalog_source_uuid_revisions
                                WHERE source_uuid=@sourceUuid),0) AS "Generation"
                        """,
                        new { sourceUuid },
                        transaction,
                        cancellationToken: ct
                    )
                );

                return new NormalizationSourceUuidLookup(
                    row.Exists,
                    new(sourceUuid, row.Generation)
                );
            },
            cancellationToken
        );
    }

    private sealed record SourcePresenceRow(bool Exists, long Generation);
}
