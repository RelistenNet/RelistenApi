using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;

namespace Relisten.Catalog;

/// <summary>
/// Applies one batched interpretation to playable files in the caller's snapshot.
/// Show, source, direct-track and discovery reads share this publication policy.
/// </summary>
public sealed class CatalogTrackNormalizationReader(
    CatalogSongMetadataReader songMetadata,
    ILogger<CatalogTrackNormalizationReader> logger
)
{
    public async Task Apply(
        CatalogReadSession session,
        IReadOnlyList<CatalogTrack> tracks,
        bool approved,
        CancellationToken cancellationToken
    )
    {
        if (!approved)
        {
            foreach (var track in tracks)
            {
                track.Normalization = new("unavailable", "disabled", []);
            }
            return;
        }
        if (tracks.Count == 0)
        {
            return;
        }

        var uuids = tracks.Select(track => track.uuid).ToArray();
        var parts = (
            await session.Connection.QueryAsync<CatalogPart>(
                session.Command(
                    "SELECT * FROM norm.track_songs WHERE track_uuid=ANY(@uuids) ORDER BY track_uuid,part",
                    new { uuids },
                    cancellationToken
                )
            )
        ).ToArray();
        var songs = await songMetadata.Read(
            session,
            parts
                .Where(part => part.resolved)
                .Select(part => part.song_id)
                .OfType<string>()
                .ToArray(),
            cancellationToken
        );
        var partsByTrack = parts.ToLookup(part => part.track_uuid);
        foreach (var track in tracks)
        {
            ApplyInterpretation(track, partsByTrack[track.uuid].ToArray(), songs);
        }
    }

    private void ApplyInterpretation(
        CatalogTrack track,
        CatalogPart[] parts,
        IReadOnlyDictionary<string, CatalogSong> songs
    )
    {
        var consistent = true;
        foreach (var part in parts)
        {
            if (!part.resolved || part.song_id is null)
            {
                continue;
            }
            if (
                songs.TryGetValue(part.song_id, out var song)
                && song.artist_uuid == track.artist_uuid
            )
            {
                part.Song = song;
            }
            else
            {
                consistent = false;
            }
        }

        // Publish the interpretation as a whole: a corrupt linked song must not
        // turn a partially trusted file into an apparently complete match.
        if (!consistent)
        {
            logger.LogWarning("Invalid normalized song link for track {TrackUuid}", track.uuid);
            track.Normalization = new("unavailable", "inconsistent", []);
        }
        else if (parts.Length > 0)
        {
            track.Normalization = new(
                parts.Any(part => !part.resolved) ? "partial" : "interpreted",
                null,
                parts
            );
        }
    }

    internal const string TrackSelect = """
        SELECT t.*, src.uuid source_uuid, sets.uuid source_set_uuid, sh.uuid show_uuid, a.uuid artist_uuid
        FROM source_tracks t JOIN sources src ON src.id=t.source_id
        JOIN source_sets sets ON sets.id=t.source_set_id AND sets.source_id=src.id
        JOIN shows sh ON sh.id=src.show_id JOIN artists a ON a.id=src.artist_id
        """;
}
