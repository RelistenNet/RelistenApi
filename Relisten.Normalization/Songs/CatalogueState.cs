using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Normalization.Titles;

namespace Relisten.Normalization.Songs;

/// Taper and duration (seconds) are evidence for the review pass: independent tapers agreeing
/// on a title, and very short tracks leaning towards stage talk.
public sealed record CatalogueTrack(
    Guid Uuid,
    long? ShowId,
    ParsedTitle Title,
    long SourceId = 0,
    string? Taper = null,
    int? Duration = null
)
{
    /// The track's parts as written so far. Its rulings are carried forward when the track is
    /// matched again (a one-off spelling resolved once has no alias to look up); a person's
    /// row locks the track.
    public IReadOnlyList<ExistingPart> Existing { get; init; } = [];

    /// No parts yet, or a part nobody has placed (an open medley).
    public bool Unresolved => Existing.Count == 0 || Existing.Any(e => !e.Resolved);

    public bool Locked => Existing.Any(e => AuthorTypes.IsHuman(e.AuthorType));
}

public sealed record ExistingPart(
    string? Text,
    SongId? SongId,
    bool Resolved,
    float? Confidence,
    string Basis = TrackBasis.Ruling,
    string AuthorType = AuthorTypes.Algorithm,
    bool SegueAfter = false
);

public sealed record CatalogueSetlistSong(Guid Uuid, string Name, string Key, int Plays);

public sealed record CatalogueSong(SongId Id, string Name, SongKind? Kind);

/// One artist's catalogue as it stands: its tracks (only the unresolved ones unless every
/// track is asked for, which a rebuild of derived rows needs), its unmapped upstream setlist
/// songs, its songs and its alias table. Everything that decides what to ask or match next is
/// read fresh from the database, so a stopped run resumes where it left off and a re-run only
/// sees what is still unresolved.
public sealed class CatalogueState
{
    public required Guid ArtistUuid { get; init; }
    public required string ArtistName { get; init; }
    public required IReadOnlyList<CatalogueTrack> Tracks { get; init; }
    public required IReadOnlyList<CatalogueSetlistSong> UnmappedSetlistSongs { get; init; }
    public required IReadOnlyList<CatalogueSong> Songs { get; init; }
    public required IReadOnlyDictionary<string, SongId> Aliases { get; init; }

    public IEnumerable<CatalogueTrack> UnresolvedTracks => Tracks.Where(t => t.Unresolved);

    /// Titles a person has an open review item about: theirs to settle, never the LLM's.
    public IReadOnlySet<string> HeldKeys { get; init; } = new HashSet<string>();

    /// Song names are looked up directly, so a name needs no alias. A name two songs share
    /// matches neither.
    public IReadOnlyDictionary<string, SongId> SongNames =>
        songNames ??= Songs
            .GroupBy(s => TitleNormalizer.Normalize(s.Name))
            .Where(g => g.Key.Length > 0 && g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single().Id);

    private IReadOnlyDictionary<string, SongId>? songNames;

    public static async Task<CatalogueState> LoadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? tx,
        Guid artistUuid,
        bool allTracks = false
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
              AND (@allTracks
                   OR NOT EXISTS (SELECT 1 FROM norm.track_songs ts WHERE ts.track_uuid = t.uuid)
                   OR EXISTS (SELECT 1 FROM norm.track_songs ts WHERE ts.track_uuid = t.uuid AND NOT ts.resolved))
            """,
            new { artist.Id, allTracks },
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

        // The parts already written: rulings survive a re-match, people's rows lock a track.
        var existing = (
            await connection.QueryAsync<(
                Guid Track,
                string? Text,
                SongId? Song,
                bool Resolved,
                float? Confidence,
                string Basis,
                string AuthorType,
                bool SegueAfter
            )>(
                """
                -- A part naming a merged song names its survivor (readers follow merged_into).
                WITH RECURSIVE survivor AS (
                    SELECT id AS song, id, merged_into, 0 AS depth FROM norm.songs WHERE artist_uuid = @artistUuid
                    UNION ALL
                    SELECT s.song, m.id, m.merged_into, s.depth + 1
                    FROM survivor s JOIN norm.songs m ON m.id = s.merged_into
                    WHERE s.depth < 20
                )
                SELECT ts.track_uuid, ts.text, coalesce(v.id, ts.song_id), ts.resolved, ts.confidence, ts.basis,
                       ts.author_type, ts.segue_after
                FROM norm.track_songs ts JOIN source_tracks t ON t.uuid = ts.track_uuid
                LEFT JOIN survivor v ON v.song = ts.song_id AND v.merged_into IS NULL
                WHERE CASE WHEN @allTracks THEN t.artist_id = @Id ELSE ts.track_uuid = ANY(@open) END
                ORDER BY ts.track_uuid, ts.part
                """,
                new
                {
                    artist.Id,
                    artistUuid,
                    allTracks,
                    open = allTracks ? [] : tracks.Select(t => t.Uuid).ToArray(),
                },
                tx
            )
        ).ToLookup(
            r => r.Track,
            r => new ExistingPart(
                r.Text,
                r.Song,
                r.Resolved,
                r.Confidence,
                r.Basis,
                r.AuthorType,
                r.SegueAfter
            )
        );

        var setlist = await connection.QueryAsync<(Guid Uuid, string Name, int Plays)>(
            """
            SELECT ss.uuid, ss.name,
                   (SELECT count(*) FROM setlist_songs_plays p WHERE p.played_setlist_song_id = ss.id)::int
            FROM setlist_songs ss
            WHERE ss.artist_id = @Id
              AND NOT EXISTS (SELECT 1 FROM norm.song_evidence e WHERE e.setlist_song_uuid = ss.uuid)
            """,
            new { artist.Id },
            tx
        );

        var songs = await connection.QueryAsync<CatalogueSong>(
            """
            SELECT id, name, kind FROM norm.songs
            WHERE artist_uuid = @artistUuid AND merged_into IS NULL ORDER BY name, id
            """,
            new { artistUuid },
            tx
        );

        // An alias of a merged song names its survivor (readers follow merged_into).
        var aliases = await connection.QueryAsync<(string Normalized, SongId Song)>(
            """
            WITH RECURSIVE survivor AS (
                SELECT id AS song, id, merged_into, 0 AS depth FROM norm.songs WHERE artist_uuid = @artistUuid
                UNION ALL
                SELECT s.song, m.id, m.merged_into, s.depth + 1
                FROM survivor s JOIN norm.songs m ON m.id = s.merged_into
                WHERE s.depth < 20
            )
            SELECT a.normalized, v.id
            FROM norm.song_aliases a JOIN survivor v ON v.song = a.song_id AND v.merged_into IS NULL
            WHERE a.artist_uuid = @artistUuid
            """,
            new { artistUuid },
            tx
        );

        var held = await connection.QueryAsync<string>(
            """
            SELECT key FROM norm.review_items
            WHERE artist_uuid = @artistUuid AND resolved_at IS NULL AND key IS NOT NULL
              AND author_type IN ('user', 'moderator')
            """,
            new { artistUuid },
            tx
        );

        return new CatalogueState
        {
            HeldKeys = held.ToHashSet(),
            ArtistUuid = artistUuid,
            ArtistName = artist.Name,
            Tracks = tracks
                .Select(t => new CatalogueTrack(
                    t.Uuid,
                    t.ShowId,
                    Parse(t.Title, t.Position),
                    t.SourceId,
                    t.Taper,
                    t.Duration
                )
                {
                    Existing = existing[t.Uuid].ToList(),
                })
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
