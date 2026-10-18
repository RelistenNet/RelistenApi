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

public sealed partial class NormalizationReadService
{
    private const int MaximumTrackTitleFactsBytes = 32 * 1024 * 1024;

    // Title facts come only from the immutable revision referenced by the current content row.
    // Keep the parent page and playback available if this optional detail exceeds its page bound.
    private static async Task PopulateTrackTitleFactsAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        IEnumerable<NormalizedTrackContent> contents,
        CancellationToken ct
    )
    {
        var rows = contents.ToArray();
        if (rows.Length == 0)
        {
            return;
        }
        var ids = rows.Select(row => row.uuid).ToArray();
        var metadata = (
            await c.QueryAsync<TrackTitleFactsMetadata>(
                new CommandDefinition(
                    """
                    SELECT x.uuid AS "Uuid",octet_length((r.payload->'TitleFacts')::text)::bigint AS "Bytes"
                    FROM normalization.track_contents x JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid
                    WHERE x.uuid=ANY(@ids) AND NOT x.retracted
                        AND r.payload->'TitleFacts' IS NOT NULL AND r.payload->'TitleFacts'<>'null'::jsonb
                    """,
                    new { ids },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        var present = metadata.Select(row => row.Uuid).ToHashSet();
        if (metadata.Sum(row => row.Bytes) > MaximumTrackTitleFactsBytes)
        {
            foreach (var row in rows.Where(row => present.Contains(row.uuid)))
            {
                row.title_facts_status = "size_limit";
            }
            return;
        }

        var details = (
            await c.QueryAsync<TrackTitleFactsRow>(
                new CommandDefinition(
                    """
                    SELECT x.uuid AS "Uuid",(r.payload->'TitleFacts')::text AS "Payload"
                    FROM normalization.track_contents x JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid
                    WHERE x.uuid=ANY(@ids) AND NOT x.retracted
                        AND r.payload->'TitleFacts' IS NOT NULL AND r.payload->'TitleFacts'<>'null'::jsonb
                    """,
                    new { ids },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToDictionary(
            row => row.Uuid,
            row => JsonSerializer.Deserialize<TrackContentTitleFacts>(row.Payload)!
        );
        var inputIds = details
            .Values.SelectMany(detail =>
                detail
                    .Qualifiers.Select(value => value.Evidence)
                    .Concat(detail.SourceEvidence is { } source ? [source] : [])
            )
            .Select(evidence => evidence.InputRevisionUuid)
            .Distinct()
            .ToArray();
        var statuses = (
            await c.QueryAsync<TrackTitleEvidenceStatus>(
                new CommandDefinition(
                    """
                    SELECT input.uuid AS "Uuid",CASE WHEN resource.current_revision_uuid IS NULL THEN 'missing'
                        WHEN resource.current_revision_uuid=input.uuid THEN 'current' ELSE 'changed' END AS "Status"
                    FROM normalization.input_revisions input
                    LEFT JOIN normalization.input_resources resource ON resource.uuid=input.resource_uuid
                    WHERE input.uuid=ANY(@inputIds)
                    """,
                    new { inputIds },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToDictionary(row => row.Uuid, row => row.Status);
        foreach (var row in rows)
        {
            if (!details.TryGetValue(row.uuid, out var detail))
            {
                continue;
            }
            row.title_facts = new(
                CoverageName(detail.Coverage),
                detail.TransitionAfter == TitleTransition.SegueMarker ? "segue_marker" : "unknown",
                detail
                    .Qualifiers.Select(qualifier => new NormalizedTrackTitleQualifier(
                        qualifier.Kind,
                        PublicTitleEvidence(qualifier.Evidence, statuses)
                    ))
                    .ToArray(),
                detail.SourceEvidence is { } source ? PublicTitleEvidence(source, statuses) : null
            );
            row.title_facts_status = "available";
        }
    }

    private static string CoverageName(TitleCoverage value) =>
        value switch
        {
            TitleCoverage.Partial => "partial",
            TitleCoverage.PartialMarker => "partial_marker",
            _ => "unknown",
        };

    private static NormalizationPublicEvidence PublicTitleEvidence(
        NormalizedEvidenceReference evidence,
        IReadOnlyDictionary<Guid, string> statuses
    ) =>
        new()
        {
            input_revision_uuid = evidence.InputRevisionUuid,
            json_pointer = evidence.JsonPointer,
            start_code_point = evidence.StartCodePoint,
            end_code_point = evidence.EndCodePoint,
            quote = evidence.Quote,
            evidence_status = statuses.GetValueOrDefault(evidence.InputRevisionUuid, "missing"),
        };

    private sealed record TrackTitleFactsMetadata(Guid Uuid, long Bytes);

    private sealed record TrackTitleFactsRow(Guid Uuid, string Payload);

    private sealed record TrackTitleEvidenceStatus(Guid Uuid, string Status);
}
