using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationReadService
{
    /// <summary>
    /// Lists active accepted sets, including empty sets. Known positions sort before unknown
    /// positions; ties use UUID only for pagination. No source-account prose is exposed.
    /// </summary>
    public Task<NormalizationPage<NormalizedSetlistSet>?> ListSetlistSetsAsync(
        Guid editionUuid,
        int limit = 50,
        string? cursor = null,
        CancellationToken cancellationToken = default
    )
    {
        const string route = "setlist_sets";
        const string order = "position_nulls_last_uuid";
        var after = ParseCursor(cursor, route, editionUuid, null, order, limit);
        var unknown = after?.Sort == "unknown";
        int? position = null;
        if (after != null && !unknown)
        {
            if (
                !int.TryParse(
                    after.Sort,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var parsed
                )
                || parsed < 1
            )
            {
                throw new ArgumentException("Invalid set position cursor.", nameof(cursor));
            }
            position = parsed;
        }
        return ReadAsync(
            async (c, t) =>
            {
                var exists = await c.ExecuteScalarAsync<bool>(
                    new CommandDefinition(
                        "SELECT EXISTS(SELECT 1 FROM normalization.setlist_editions WHERE uuid=@editionUuid AND NOT retracted)",
                        new { editionUuid },
                        t,
                        cancellationToken: cancellationToken
                    )
                );
                if (!exists)
                {
                    return null;
                }
                var rows = (
                    await c.QueryAsync<NormalizedSetlistSet>(
                        new CommandDefinition(
                            """
                            SELECT x.uuid,x.edition_uuid,x.position,x.label,x.encore_number,
                                r.revision,r.basis,r.decision_uuid,r.created_at AS updated_at,x.revision_uuid
                            FROM normalization.setlist_sets x
                            JOIN normalization.setlist_structures structure ON structure.edition_uuid=x.edition_uuid
                                AND NOT structure.retracted AND structure.revision_uuid=x.revision_uuid
                            JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid
                            WHERE x.edition_uuid=@editionUuid AND NOT x.retracted AND (
                                @afterUuid::uuid IS NULL
                                OR (NOT @unknown AND (x.position>@position OR (x.position=@position AND x.uuid>@afterUuid) OR x.position IS NULL))
                                OR (@unknown AND x.position IS NULL AND x.uuid>@afterUuid))
                            ORDER BY x.position NULLS LAST,x.uuid LIMIT @take
                            """,
                            new
                            {
                                editionUuid,
                                afterUuid = after?.Uuid,
                                unknown,
                                position,
                                take = limit + 1,
                            },
                            t,
                            cancellationToken: cancellationToken
                        )
                    )
                ).ToArray();
                await PopulateSetEvidenceAsync(c, t, rows, cancellationToken);
                return Page(
                    rows,
                    limit,
                    row =>
                        EncodeCursor(
                            route,
                            editionUuid,
                            null,
                            order,
                            row.position?.ToString(CultureInfo.InvariantCulture) ?? "unknown",
                            row.uuid
                        )
                );
            },
            cancellationToken
        );
    }

    private static async Task PopulateSetEvidenceAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        IReadOnlyList<NormalizedSetlistSet> sets,
        CancellationToken ct
    )
    {
        if (sets.Count == 0)
        {
            return;
        }
        // A structure revision also retains source accounts and passage evidence. Project only
        // the requested sets' own Evidence arrays, scanning each consumed revision once.
        var evidence = (
            await c.QueryAsync<SetEvidenceRow>(
                new CommandDefinition(
                    """
                    SELECT (observed.value->>'Uuid')::uuid AS set_uuid,
                        (citation.value->>'InputRevisionUuid')::uuid AS input_revision_uuid,
                        citation.value->>'JsonPointer' AS json_pointer,
                        (citation.value->>'StartCodePoint')::integer AS start_code_point,
                        (citation.value->>'EndCodePoint')::integer AS end_code_point,
                        citation.value->>'Quote' AS quote,
                        CASE WHEN input.uuid IS NULL OR resource.current_revision_uuid IS NULL THEN 'missing'
                             WHEN resource.current_revision_uuid=input.uuid THEN 'current' ELSE 'changed' END AS evidence_status
                    FROM normalization.music_revisions revision
                    CROSS JOIN LATERAL jsonb_array_elements(revision.payload->'Sets') observed(value)
                    CROSS JOIN LATERAL jsonb_array_elements(observed.value->'Evidence') WITH ORDINALITY citation(value,ordinal)
                    LEFT JOIN normalization.input_revisions input ON input.uuid=(citation.value->>'InputRevisionUuid')::uuid
                    LEFT JOIN normalization.input_resources resource ON resource.uuid=input.resource_uuid
                    WHERE revision.uuid=ANY(@revisions) AND (observed.value->>'Uuid')::uuid=ANY(@uuids)
                    ORDER BY set_uuid,citation.ordinal
                    """,
                    new
                    {
                        revisions = sets.Select(set => set.revision_uuid).Distinct().ToArray(),
                        uuids = sets.Select(set => set.uuid).ToArray(),
                    },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToLookup(citation => citation.set_uuid);
        foreach (var set in sets)
        {
            set.evidence = evidence[set.uuid]
                .Select(citation => new NormalizationPublicEvidence
                {
                    input_revision_uuid = citation.input_revision_uuid,
                    json_pointer = citation.json_pointer,
                    start_code_point = citation.start_code_point,
                    end_code_point = citation.end_code_point,
                    quote = citation.quote,
                    evidence_status = citation.evidence_status,
                })
                .ToArray();
            set.evidence_status =
                set.evidence.Any(citation => citation.evidence_status == "missing") ? "missing"
                : set.evidence.Any(citation => citation.evidence_status == "changed") ? "changed"
                : set.evidence.Count > 0 ? "current"
                : "missing";
        }
    }

    private sealed class SetEvidenceRow : NormalizationPublicEvidence
    {
        public Guid set_uuid { get; set; }
    }
}
