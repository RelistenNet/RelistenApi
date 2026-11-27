using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Relisten.Search;
using RelistenUserService.Persistence;

namespace RelistenUserService.Playlists;

// Callers supply IDs from a live visibility check. The catalog snapshot contains
// private listening totals so they are available when the owner publishes.
internal sealed record PlaylistPopularity(
    string PlaylistId,
    DateTimeOffset AsOf,
    string ResourceJson
)
{
    public PopularityResource Resource =>
        JsonSerializer.Deserialize<PopularityResource>(ResourceJson)!;

    public static async Task<Dictionary<string, PlaylistPopularity>> ReadAsync(
        AccountsDbContext database,
        IEnumerable<PlaylistId> visible,
        CancellationToken ct
    )
    {
        var ids = visible.Select(id => id.Value).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        return await database
            .Database.SqlQuery<PlaylistPopularity>(
                $"""
                SELECT playlist_id AS "PlaylistId", as_of AS "AsOf", popularity::text AS "ResourceJson"
                FROM public.playlist_popularity WHERE playlist_id = ANY({ids})
                """
            )
            .ToDictionaryAsync(row => row.PlaylistId, ct);
    }
}
