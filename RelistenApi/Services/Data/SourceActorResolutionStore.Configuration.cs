using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class SourceActorResolutionStore
{
    // Replace only bindings installed by this protocol. Human review advances desired
    // generation without changing membership, so the admitted monotonic contributor revision
    // vector identifies configuration. Full binding keys are checked separately before replacement.
    // Retained empty headers remain part of that vector; no original fact is refreshed.
    internal static async Task<SourceActorResolutionPreparation?> GetConfiguredPreparationAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid sourceUuid,
        NormalizationInputContributionsSnapshot membership,
        CancellationToken ct
    )
    {
        var json = await c.QuerySingleOrDefaultAsync<string>(
            new CommandDefinition(
                """
                SELECT a.preparation::text FROM normalization.source_actor_resolution_attempts a
                JOIN normalization.work_runs r ON r.uuid=a.attempt_uuid
                JOIN normalization.work_subjects s ON s.uuid=r.subject_uuid
                CROSS JOIN LATERAL (
                    SELECT CASE
                        WHEN a.preparation->>'Format'=@checkpointFormat
                            AND a.preparation->>'Kind'=@checkpointKind THEN a.preparation->'Value'
                    END AS payload
                ) AS stored
                CROSS JOIN LATERAL (
                    SELECT CASE WHEN jsonb_typeof(stored.payload->'ExpectedContributions')='array'
                            THEN stored.payload->'ExpectedContributions' END AS prior_headers,
                        CASE WHEN jsonb_typeof(stored.payload->'Inputs')='array'
                            THEN stored.payload->'Inputs' END AS replacements
                ) AS checkpoint
                WHERE s.domain='people' AND s.subject_kind='source_actor_resolution' AND s.subject_uuid=@sourceUuid
                    AND checkpoint.prior_headers IS NOT NULL AND checkpoint.replacements IS NOT NULL
                    AND NOT EXISTS (
                        SELECT 1
                        FROM jsonb_to_recordset(@contributions::jsonb)
                            AS observed("Contributor" jsonb,"Revision" bigint)
                        FULL JOIN (
                            SELECT item->'OriginalInput'->'Contributor' AS contributor,
                                (item->>'ExpectedContributionRevision')::bigint+1 AS revision
                            FROM jsonb_array_elements(checkpoint.replacements) AS replacement(item)
                            UNION ALL
                            SELECT prior."Contributor",prior."Revision"
                            FROM jsonb_to_recordset(checkpoint.prior_headers)
                                AS prior("Contributor" jsonb,"Revision" bigint)
                            WHERE NOT EXISTS (
                                SELECT 1 FROM jsonb_array_elements(checkpoint.replacements) AS replacement(item)
                                WHERE item->'OriginalInput'->'Contributor'=prior."Contributor"
                            )
                        ) AS admitted ON observed."Contributor"=admitted.contributor
                        WHERE observed."Revision" IS DISTINCT FROM admitted.revision
                    ) AND a.preparation IS NOT NULL
                ORDER BY a.created_at DESC,a.attempt_uuid DESC LIMIT 1
                """,
                new
                {
                    sourceUuid,
                    checkpointFormat = SourceActorResolutionCheckpoint.FormatVersion,
                    checkpointKind = SourceActorResolutionCheckpoint.PreparationKind,
                    contributions = JsonSerializer.Serialize(
                        membership.Contributions.Select(
                            value => new NormalizationInputContributionRevision(
                                value.Contributor,
                                value.Revision
                            )
                        )
                    ),
                },
                transaction: t,
                cancellationToken: ct
            )
        );
        return json == null ? null : SourceActorResolutionCheckpoint.ReadPreparation(json);
    }
}
