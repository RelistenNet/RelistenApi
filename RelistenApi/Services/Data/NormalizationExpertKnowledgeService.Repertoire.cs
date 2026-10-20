using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationExpertKnowledgeService
{
    /// <summary>
    /// Reads song knowledge inside the consumer's primary snapshot. Save, revision and withdrawal
    /// bump that artist's repertoire universe in the same transaction as the reviewed rule, so
    /// the consumer must retain its ordinary repertoire-universe observation through publication.
    /// </summary>
    internal static async Task<ExpertKnowledgeSnapshot> ReadRepertoireSnapshotAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid artist,
        CancellationToken ct
    )
    {
        var rows = (
            await c.QueryAsync<RuleRow>(
                new CommandDefinition(
                    $"""
                    SELECT {Columns} FROM normalization.expert_knowledge_rules
                    WHERE domain='songs' AND (definition->'selector'->>'artistUuid' IS NULL
                        OR definition->'selector'->>'artistUuid'=@artist)
                    ORDER BY rule_uuid LIMIT @take
                    """,
                    new
                    {
                        artist = artist.ToString("D"),
                        take = ExpertKnowledgeResolver.MaximumRules + 1,
                    },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        return ExpertKnowledgeResolver.CreateSnapshot(rows.Select(row => row.ToRule()).ToArray());
    }
}
