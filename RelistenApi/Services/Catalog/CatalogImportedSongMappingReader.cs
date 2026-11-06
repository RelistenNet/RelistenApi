using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace Relisten.Catalog;

/// <summary>
/// Links imported songs to approved canonical identities in the caller's snapshot.
/// Evidence and merge resolution are batched; titles never imply a mapping.
/// </summary>
public sealed class CatalogImportedSongMappingReader(CatalogSongMetadataReader songs)
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

        var evidence = (
            await session.Connection.QueryAsync<SongEvidence>(
                session.Command(
                    """
                    SELECT e.setlist_song_uuid,e.song_id FROM norm.song_evidence e
                    JOIN setlist_songs s ON s.uuid=e.setlist_song_uuid
                    JOIN features f ON f.artist_id=s.artist_id AND f.norm_catalogue
                    WHERE e.setlist_song_uuid=ANY(@ids)
                    """,
                    new { ids = imported.Select(song => song.uuid).ToArray() },
                    cancellationToken
                )
            )
        ).ToDictionary(row => row.setlist_song_uuid, row => row.song_id);
        var resolved = await songs.Read(session, evidence.Values.ToArray(), cancellationToken);

        // Missing, broken, or cross-artist mappings retain imported access. The link
        // follows merges, but does not replace the imported song's setlist membership.
        foreach (var song in imported)
        {
            song.normalized_song_id =
                evidence.TryGetValue(song.uuid, out var id)
                && resolved.TryGetValue(id, out var canonical)
                && canonical.artist_uuid == song.artist_uuid
                    ? canonical.id
                    : null;
        }
    }

    private sealed class SongEvidence
    {
        public Guid setlist_song_uuid { get; set; }
        public string song_id { get; set; } = null!;
    }
}
