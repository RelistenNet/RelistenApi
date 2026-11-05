using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;

namespace Relisten.Catalog;

/// <summary>
/// Resolves normalized song IDs in the caller's snapshot. A song may merge only
/// within its artist; unknown IDs and invalid chains are omitted from the result.
/// </summary>
public sealed class CatalogSongMetadataReader(ILogger<CatalogSongMetadataReader> logger)
{
    public async Task<IReadOnlyDictionary<string, CatalogSong>> Read(
        CatalogReadSession session,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken
    )
    {
        var requestedIds = ids.Distinct().ToArray();
        if (requestedIds.Length == 0)
        {
            return new Dictionary<string, CatalogSong>();
        }

        var songs = (
            await session.Connection.QueryAsync<CatalogSong>(
                session.Command(
                    """
                    WITH RECURSIVE chain AS (
                      SELECT s.*,ARRAY[s.id] path FROM norm.songs s WHERE s.id=ANY(@ids)
                      UNION ALL
                      SELECT s.*,c.path || s.id FROM norm.songs s JOIN chain c
                        ON s.id=c.merged_into AND s.artist_uuid=c.artist_uuid
                      WHERE cardinality(c.path)<32 AND NOT s.id=ANY(c.path)
                    ) SELECT DISTINCT id,artist_uuid,name,kind,merged_into FROM chain
                    """,
                    new { ids = requestedIds },
                    cancellationToken
                )
            )
        ).ToDictionary(song => song.id);
        return ResolveSongs(requestedIds, songs);
    }

    private IReadOnlyDictionary<string, CatalogSong> ResolveSongs(
        IReadOnlyList<string> ids,
        IReadOnlyDictionary<string, CatalogSong> songs
    )
    {
        var resolved = new Dictionary<string, CatalogSong>();
        foreach (var requestedId in ids)
        {
            if (!songs.TryGetValue(requestedId, out var requested))
            {
                logger.LogDebug("Unknown normalized song {SongId}", requestedId);
                continue;
            }

            var id = requestedId;
            var seen = new HashSet<string>();
            while (
                seen.Count < 32
                && seen.Add(id)
                && songs.TryGetValue(id, out var song)
                && song.artist_uuid == requested.artist_uuid
            )
            {
                if (song.merged_into is null)
                {
                    resolved.Add(requestedId, song);
                    break;
                }
                id = song.merged_into;
            }

            if (!resolved.ContainsKey(requestedId))
            {
                logger.LogWarning("Invalid normalized song merge chain for {SongId}", requestedId);
            }
        }
        return resolved;
    }
}
