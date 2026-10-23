using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Npgsql;

namespace Relisten.Normalization.Songs;

/// One artist's catalogue against the legacy tables (string-theory docs/catalogue.md
/// §Rollout, docs/matching.md §Evaluation).
/// - Precision vs setlist: of the song parts on tracks of shows whose date has an upstream
///   setlist, the share whose song's name is one that setlist played. Compared by name
///   ("&" as "and", a leading "The" ignored: "Samson & Delilah", "The Promised Land"), never
///   through kb.song_evidence, which the same model writes.
///   Setlists are incomplete (encores, jams), so this is a lower bound; the disagreements
///   are listed for labelling.
/// - Cutover: shows with at least one fully resolved source, against legacy setlist shows.
/// Tracks are counted through their source: source_tracks keeps rows whose source is gone
/// (a third of some artists' rows), and the catalogue never sees those.
public sealed record ArtistEvaluation(
    string Artist,
    int Tracks,
    int TracksResolved,
    int Songs,
    int PartsCheckable,
    int PartsAgreeing,
    int LegacySetlistShows,
    int ShowsResolved,
    IReadOnlyList<string> SampleDisagreements
)
{
    public double ResolvedShare => Tracks == 0 ? 0 : (double)TracksResolved / Tracks;
    public double PrecisionVsSetlist =>
        PartsCheckable == 0 ? double.NaN : (double)PartsAgreeing / PartsCheckable;

    public override string ToString() =>
        $"{Artist}: {TracksResolved}/{Tracks} tracks resolved ({ResolvedShare:P1}), {Songs} songs, "
        + $"precision vs setlist {PrecisionVsSetlist:P1} ({PartsAgreeing}/{PartsCheckable}), "
        + $"shows resolved {ShowsResolved} vs legacy setlist shows {LegacySetlistShows}";
}

public static class KbEvaluation
{
    public static async Task<ArtistEvaluation> EvaluateAsync(
        NpgsqlConnection connection,
        string artistSlug
    )
    {
        var artist = await connection.QuerySingleAsync<(int Id, Guid Uuid)>(
            "SELECT id, uuid FROM artists WHERE slug = @artistSlug",
            new { artistSlug }
        );

        var counts = await connection.QuerySingleAsync<(
            int Tracks,
            int Resolved,
            int Songs,
            int LegacyShows,
            int ShowsResolved
        )>(
            """
            SELECT
                (SELECT count(*) FROM source_tracks t JOIN sources s ON s.id = t.source_id
                 WHERE t.artist_id = @Id AND NOT t.is_orphaned)::int,
                (SELECT count(*) FROM source_tracks t JOIN sources s ON s.id = t.source_id
                 WHERE t.artist_id = @Id AND NOT t.is_orphaned
                   AND EXISTS (SELECT 1 FROM kb.track_songs ts WHERE ts.track_uuid = t.uuid)
                   AND NOT EXISTS (SELECT 1 FROM kb.track_songs ts WHERE ts.track_uuid = t.uuid AND NOT ts.resolved))::int,
                (SELECT count(*) FROM kb.songs WHERE artist_uuid = @Uuid AND merged_into IS NULL)::int,
                (SELECT count(DISTINCT ss.id) FROM setlist_shows ss JOIN shows sh ON sh.artist_id = ss.artist_id AND sh.date = ss.date
                 WHERE ss.artist_id = @Id AND EXISTS (SELECT 1 FROM setlist_songs_plays p WHERE p.played_setlist_show_id = ss.id))::int,
                (SELECT count(DISTINCT s.show_id) FROM sources s
                 WHERE s.artist_id = @Id AND s.show_id IS NOT NULL
                   AND EXISTS (SELECT 1 FROM source_tracks t WHERE t.source_id = s.id AND NOT t.is_orphaned)
                   AND NOT EXISTS (SELECT 1 FROM source_tracks t WHERE t.source_id = s.id AND NOT t.is_orphaned
                                   AND (NOT EXISTS (SELECT 1 FROM kb.track_songs ts WHERE ts.track_uuid = t.uuid)
                                        OR EXISTS (SELECT 1 FROM kb.track_songs ts WHERE ts.track_uuid = t.uuid AND NOT ts.resolved))))::int
            """,
            new { artist.Id, artist.Uuid }
        );

        // Each song part on a show that has a setlist, and whether that setlist played it.
        var parts = (
            await connection.QueryAsync<(bool Agrees, string Date, string Title, string Song)>(
                """
                -- Agreement by name, not through kb.song_evidence: the evidence is written by
                -- the same model as the tracks, so a wrong answer would agree with itself.
                SELECT EXISTS (
                           SELECT 1 FROM setlist_songs_plays p
                           JOIN setlist_songs sso ON sso.id = p.played_setlist_song_id
                           WHERE p.played_setlist_show_id = ss.id
                             AND regexp_replace(regexp_replace(replace(lower(sso.name), '&', ' and '), '^the ', ''), '[^a-z0-9]', '', 'g')
                               = regexp_replace(regexp_replace(replace(lower(k.name), '&', ' and '), '^the ', ''), '[^a-z0-9]', '', 'g')),
                       sh.date::text, t.title, k.name
                FROM kb.track_songs ts
                JOIN source_tracks t ON t.uuid = ts.track_uuid
                JOIN sources s ON s.id = t.source_id
                JOIN shows sh ON sh.id = s.show_id
                JOIN setlist_shows ss ON ss.artist_id = sh.artist_id AND ss.date = sh.date
                JOIN kb.songs k ON k.uuid = ts.song_uuid
                WHERE t.artist_id = @Id AND ts.song_uuid IS NOT NULL
                  AND EXISTS (SELECT 1 FROM setlist_songs_plays p WHERE p.played_setlist_show_id = ss.id)
                """,
                new { artist.Id, artist.Uuid }
            )
        ).ToList();

        return new ArtistEvaluation(
            artistSlug,
            counts.Tracks,
            counts.Resolved,
            counts.Songs,
            parts.Count,
            parts.Count(p => p.Agrees),
            counts.LegacyShows,
            counts.ShowsResolved,
            parts
                .Where(p => !p.Agrees)
                .GroupBy(p => (p.Title, p.Song))
                .OrderByDescending(g => g.Count())
                .Take(20)
                .Select(g =>
                    $"{g.Count()}× \"{g.Key.Title}\" → {g.Key.Song} (e.g. {g.First().Date})"
                )
                .ToList()
        );
    }
}
