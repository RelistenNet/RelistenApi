using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Kb.Titles;

namespace Relisten.Kb.Catalogue;

/// Taper and duration (seconds) are evidence for the review pass: independent tapers agreeing
/// on a title, and very short tracks leaning towards stage talk.
public sealed record CatalogueTrack(
    Guid Uuid,
    long? ShowId,
    ParsedTitle Title,
    long SourceId = 0,
    string? Taper = null,
    int? Duration = null
);

public sealed record CatalogueSetlistSong(Guid Uuid, string Name, string Key, int Plays);

public sealed record CatalogueSong(Guid Uuid, string Name, string? Kind);

/// One artist's catalogue as it stands: its unresolved tracks (no kb.track_songs rows yet),
/// its unmapped upstream setlist songs, its songs and its alias table. Everything that decides
/// what to ask or match next is read fresh from the database, so a stopped run resumes where
/// it left off and a re-run only sees what is still unresolved.
public sealed class CatalogueState
{
    public required Guid ArtistUuid { get; init; }
    public required string ArtistName { get; init; }
    public required IReadOnlyList<CatalogueTrack> UnresolvedTracks { get; init; }
    public required IReadOnlyList<CatalogueSetlistSong> UnmappedSetlistSongs { get; init; }
    public required IReadOnlyList<CatalogueSong> Songs { get; init; }
    public required IReadOnlyDictionary<string, Guid> Aliases { get; init; }

    public static async Task<CatalogueState> LoadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? tx,
        Guid artistUuid
    )
    {
        var artist = await connection.QuerySingleAsync<(int Id, string Name)>(
            "SELECT id, name FROM artists WHERE uuid = @artistUuid",
            new { artistUuid },
            tx
        );

        var tracks = await connection.QueryAsync<(
            Guid Uuid,
            long? ShowId,
            string Title,
            int Position,
            long SourceId,
            string? Taper,
            int? Duration
        )>(
            """
            SELECT t.uuid, s.show_id, t.title, t.track_position, s.id, nullif(trim(s.taper), ''), t.duration
            FROM source_tracks t JOIN sources s ON s.id = t.source_id
            WHERE t.artist_id = @Id AND NOT t.is_orphaned
              -- unresolved: no parts yet, or a part nobody has placed (an open medley)
              AND (NOT EXISTS (SELECT 1 FROM kb.track_songs ts WHERE ts.track_uuid = t.uuid)
                   OR EXISTS (SELECT 1 FROM kb.track_songs ts WHERE ts.track_uuid = t.uuid AND NOT ts.resolved))
            """,
            new { artist.Id },
            tx
        );

        // Titles repeat across sources: parse each (title, position) once.
        var parsed = new Dictionary<(string, int), ParsedTitle>();
        ParsedTitle Parse(string title, int position)
        {
            if (!parsed.TryGetValue((title, position), out var result))
            {
                parsed[(title, position)] = result = TitleParser.Parse(title, position);
            }

            return result;
        }

        var setlist = await connection.QueryAsync<(Guid Uuid, string Name, int Plays)>(
            """
            SELECT ss.uuid, ss.name,
                   (SELECT count(*) FROM setlist_songs_plays p WHERE p.played_setlist_song_id = ss.id)::int
            FROM setlist_songs ss
            WHERE ss.artist_id = @Id
              AND NOT EXISTS (SELECT 1 FROM kb.song_evidence e WHERE e.setlist_song_uuid = ss.uuid)
            """,
            new { artist.Id },
            tx
        );

        var songs = await connection.QueryAsync<CatalogueSong>(
            """
            SELECT uuid, name, kind FROM kb.songs
            WHERE artist_uuid = @artistUuid AND merged_into IS NULL ORDER BY name, uuid
            """,
            new { artistUuid },
            tx
        );

        var aliases = await connection.QueryAsync<(string Normalized, Guid Song)>(
            "SELECT normalized, song_uuid FROM kb.song_aliases WHERE artist_uuid = @artistUuid",
            new { artistUuid },
            tx
        );

        return new CatalogueState
        {
            ArtistUuid = artistUuid,
            ArtistName = artist.Name,
            UnresolvedTracks = tracks
                .Select(t => new CatalogueTrack(
                    t.Uuid,
                    t.ShowId,
                    Parse(t.Title, t.Position),
                    t.SourceId,
                    t.Taper,
                    t.Duration
                ))
                .ToList(),
            UnmappedSetlistSongs = setlist
                .Select(s => new CatalogueSetlistSong(
                    s.Uuid,
                    s.Name,
                    TitleNormalizer.Normalize(s.Name),
                    s.Plays
                ))
                .Where(s => s.Key.Length > 0)
                .ToList(),
            Songs = songs.ToList(),
            Aliases = aliases.ToDictionary(a => a.Normalized, a => a.Song),
        };
    }
}
