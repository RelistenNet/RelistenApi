using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace Relisten.Catalog;

/// <summary>Approved imported-song mappings and survivor resolution shared by REST
/// hydration and paginated browse membership. Titles never imply a mapping.</summary>
public sealed class CatalogImportedSongMappingReader
{
    public async Task Apply(
        CatalogReadSession session,
        IReadOnlyList<CatalogImportedSong> imported,
        CancellationToken cancellationToken
    )
    {
        if (imported.Count == 0)
        {
            return;
        }
        var mappings = (
            await session.Connection.QueryAsync<Mapping>(
                session.Command(
                    CanonicalMappings("e.setlist_song_uuid=ANY(@ids)"),
                    new { ids = imported.Select(s => s.uuid).ToArray() },
                    cancellationToken
                )
            )
        ).ToDictionary(row => row.setlist_song_uuid, row => row.song_id);
        foreach (var song in imported)
        {
            song.normalized_song_id = mappings.GetValueOrDefault(song.uuid);
        }
    }

    // Only internal predicates may be interpolated here. Both transports use this
    // same approval, artist-ownership and bounded merge-chain decision.
    internal static string CanonicalMappings(string predicate) =>
        $$"""
            WITH RECURSIVE chain AS (
              SELECT e.setlist_song_uuid,c.id,c.artist_uuid,c.merged_into,ARRAY[c.id] path
              FROM norm.song_evidence e JOIN setlist_songs imported ON imported.uuid=e.setlist_song_uuid
              JOIN artists a ON a.id=imported.artist_id
              JOIN features f ON f.artist_id=a.id AND f.norm_catalogue
              JOIN norm.songs c ON c.id=e.song_id AND c.artist_uuid=a.uuid
              WHERE {{predicate}}
              UNION ALL
              SELECT chain.setlist_song_uuid,c.id,c.artist_uuid,c.merged_into,chain.path || c.id
              FROM chain JOIN norm.songs c ON c.id=chain.merged_into AND c.artist_uuid=chain.artist_uuid
              WHERE cardinality(chain.path)<32 AND NOT c.id=ANY(chain.path)
            )
            SELECT setlist_song_uuid,id song_id FROM chain WHERE merged_into IS NULL
            """;

    private sealed class Mapping
    {
        public System.Guid setlist_song_uuid { get; set; }
        public string song_id { get; set; } = null!;
    }
}
