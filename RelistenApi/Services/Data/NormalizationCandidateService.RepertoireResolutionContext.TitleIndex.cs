using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationCandidateService
{
    // This compact index finds complete alias components before the bounded fact reader runs.
    // The page's repeatable-read transaction and repertoire candidate-universe observation fence
    // the entire graph, including names that did not directly match the incoming title query.
    private static async Task<RepertoireTitleIndex> ReadRepertoireTitleIndex(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid artist,
        CancellationToken ct
    )
    {
        var budget = await c.QuerySingleAsync<RepertoireTitleBudget>(
            new CommandDefinition(
                """
                SELECT count(*) AS "Count",COALESCE(sum(octet_length(display_title)
                    +octet_length(aliases::text)),0)::bigint AS "Bytes"
                FROM normalization.repertoire_songs WHERE artist_uuid=@artist AND NOT retracted
                """,
                new { artist },
                t,
                cancellationToken: ct
            )
        );
        if (
            budget.Count > NormalizationImportedCatalogService.MaximumRepertoireSongs
            || budget.Bytes > 16 * 1024 * 1024
        )
        {
            return new(null);
        }

        var rows = (
            await c.QueryAsync<RepertoireTitleRow>(
                new CommandDefinition(
                    """
                    SELECT uuid AS "Uuid",redirect_uuid AS "RedirectUuid",display_title AS "DisplayTitle",
                        aliases AS "Aliases" FROM normalization.repertoire_songs
                    WHERE artist_uuid=@artist AND NOT retracted
                    """,
                    new { artist },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToDictionary(row => row.Uuid);
        var titles = new Dictionary<Guid, List<string>>();
        foreach (var row in rows.Values)
        {
            var root = row;
            var visited = new HashSet<Guid>();
            while (root.RedirectUuid is { } redirect)
            {
                if (!visited.Add(root.Uuid) || !rows.TryGetValue(redirect, out root))
                {
                    return new(null);
                }
            }

            if (!titles.TryGetValue(root.Uuid, out var names))
            {
                names = [];
                titles.Add(root.Uuid, names);
            }

            names.Add(row.DisplayTitle);
            names.AddRange(row.Aliases);
        }

        return new(
            new ArtistSongTitleResolver(
                titles.Select(entry => new ArtistSongTitleCandidate(
                    entry.Key.ToString("D"),
                    artist,
                    entry.Value,
                    []
                ))
            )
        );
    }

    private sealed record RepertoireTitleIndex(ArtistSongTitleResolver? Resolver)
    {
        public bool Complete => Resolver != null;

        public IEnumerable<Guid> Targets(Guid artist, IReadOnlyList<string> names) =>
            Resolver == null
                ? []
                : names
                    .SelectMany(name => Resolver.Resolve(artist, name).Members)
                    .Select(member => Guid.Parse(member.EntityKey))
                    .Distinct();
    }

    private sealed record RepertoireTitleBudget(long Count, long Bytes);

    private sealed class RepertoireTitleRow
    {
        public Guid Uuid { get; init; }
        public Guid? RedirectUuid { get; init; }
        public string DisplayTitle { get; init; } = "";
        public string[] Aliases { get; init; } = [];
    }
}
