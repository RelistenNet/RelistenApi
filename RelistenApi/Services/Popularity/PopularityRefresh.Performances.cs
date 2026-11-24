using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models;
using Relisten.Search;

namespace Relisten.Services.Popularity;

public partial class PopularityService
{
    internal async Task<(long Performances, long Recordings, long Songs)> RefreshDetailPopulations(
        Guid artist,
        CancellationToken cancellationToken
    )
    {
        var generatedAt = DateTime.UtcNow;
        var (tracks, recordings) = await ReadDetailRollups(generatedAt, artist, cancellationToken);
        await using var connection = db.CreateConnection(longTimeout: true, readOnly: true);
        await connection.OpenAsync(cancellationToken);
        var (performances, songs) = await CalculatePerformances(
            connection,
            artist,
            tracks,
            cancellationToken
        );
        var sources = await CalculateRecordings(connection, artist, recordings, cancellationToken);
        await store.PublishDetails(
            artist,
            performances,
            sources,
            songs,
            generatedAt,
            cancellationToken
        );
        return (performances.Length, sources.Length, songs.Length);
    }

    private async Task<(
        Dictionary<Guid, PlayPopularity> Tracks,
        Dictionary<Guid, PlayPopularity> Recordings
    )> ReadDetailRollups(DateTime generatedAt, Guid artist, CancellationToken cancellationToken)
    {
        // Use the same replica path as other popularity calculations. Release
        // the read-only rollup snapshot before publishing this artist.
        await using var connection = db.CreateConnection(longTimeout: true, readOnly: true);
        await connection.OpenAsync(cancellationToken);
        await using var snapshot = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(
            new CommandDefinition(
                "SET TRANSACTION READ ONLY; SET LOCAL jit = off",
                transaction: snapshot,
                cancellationToken: cancellationToken
            )
        );
        var rows = await PlayRollupReader.ReadTrackPopularity(
            connection,
            snapshot,
            DateOnly.FromDateTime(generatedAt),
            cancellationToken,
            artist
        );
        return DividePlayback(rows);
    }

    private static (
        Dictionary<Guid, PlayPopularity> Tracks,
        Dictionary<Guid, PlayPopularity> Recordings
    ) DividePlayback(IEnumerable<PlayPopularityRow> rows)
    {
        var tracks = new Dictionary<Guid, PlayPopularity>();
        var recordings = new Dictionary<Guid, PlayPopularity>();
        foreach (var row in rows)
        {
            var population = row.IsRecording ? recordings : tracks;
            population.Add(row.Id, row.ToPopularity());
        }
        return (tracks, recordings);
    }

    private static async Task<(
        PerformancePopularity[] Performances,
        SongPopularity[] Songs
    )> CalculatePerformances(
        Npgsql.NpgsqlConnection connection,
        Guid artist,
        IReadOnlyDictionary<Guid, PlayPopularity> tracks,
        CancellationToken cancellationToken
    )
    {
        var rows = await ReadPerformanceTargets(connection, artist, cancellationToken);
        var performances = SumPerformances(rows, tracks);
        var imports = await ReadImportedSongIds(connection, artist, cancellationToken);

        // Compare live versions of the same song across shows.
        var performanceScores = performances
            .GroupBy(row => row.Song.Key)
            .SelectMany(group =>
                ScoreDetails(
                    group,
                    row => row.Playback,
                    (row, metrics) => new PerformancePopularity(row.Show, row.Song.Key, metrics)
                )
            )
            .ToArray();
        return (performanceScores, CalculateSongs(performances, imports));
    }

    private static PerformancePlayback[] SumPerformances(
        IEnumerable<PerformanceTarget> rows,
        IReadOnlyDictionary<Guid, PlayPopularity> tracks
    )
    {
        var totals = new Dictionary<(Guid Show, PerformanceSong Song), PlayPopularity>();
        foreach (var track in rows.GroupBy(row => row.Track))
        {
            if (!tracks.TryGetValue(track.Key, out var playback))
            {
                continue;
            }

            var first = track.First();
            var canonical = track.Select(row => row.Song).OfType<string>().Distinct().ToArray();
            if (canonical.Length == 0)
            {
                var title = SearchText.Fold(SearchText.RawTitle(first.Title));
                var key = PopularityPerformanceKey.ForStorage(
                    PopularityPerformanceKey.RawTitle(title)
                );
                Add(new(key, title), playback);
                continue;
            }

            var share = playback.SplitTime(canonical.Length);
            foreach (var song in canonical)
            {
                Add(new(song, null), share);
            }

            void Add(PerformanceSong song, PlayPopularity value)
            {
                var key = (first.Show, song);
                totals[key] = totals.GetValueOrDefault(key) + value;
            }
        }

        return totals
            .Select(row => new PerformancePlayback(row.Key.Show, row.Key.Song, row.Value))
            .ToArray();
    }

    private static SongPopularity[] CalculateSongs(
        IEnumerable<PerformancePlayback> performances,
        IReadOnlyDictionary<(Guid Show, string Title), string> imports
    )
    {
        var songs = new Dictionary<string, PlayPopularity>(StringComparer.Ordinal);
        foreach (var row in performances)
        {
            // A raw title can count for an imported song only if the setlist
            // for this show has exactly one title that folds to the same text.
            var song = row.Song.RawTitle is { } title
                ? imports.GetValueOrDefault((row.Show, title))
                : row.Song.Key;
            if (song != null)
            {
                songs[song] = songs.GetValueOrDefault(song) + row.Playback;
            }
        }

        // Compare song totals within the artist's catalog.
        return ScoreDetails(
            songs,
            row => row.Value,
            (row, metrics) => new SongPopularity(row.Key, metrics)
        );
    }

    private sealed record PerformanceSong(string Key, string? RawTitle);

    private sealed record PerformancePlayback(
        Guid Show,
        PerformanceSong Song,
        PlayPopularity Playback
    );

    private static Task<IEnumerable<PerformanceTarget>> ReadPerformanceTargets(
        Npgsql.NpgsqlConnection connection,
        Guid artist,
        CancellationToken cancellationToken
    ) =>
        connection.QueryAsync<PerformanceTarget>(
            new CommandDefinition(
                """
                WITH owner AS (
                    SELECT a.id,a.uuid,coalesce(f.norm_catalogue,false) AS approved
                    FROM artists a LEFT JOIN features f ON f.artist_id=a.id WHERE a.uuid=@artist
                ), matches AS MATERIALIZED (
                    SELECT m.track_uuid,m.song_id
                    FROM owner a CROSS JOIN LATERAL norm.song_recording_matches(a.uuid,NULL::text[]) m
                    WHERE a.approved
                )
                SELECT h.uuid AS show,t.uuid AS track,m.song_id AS song,coalesce(t.title,'') AS title
                FROM owner a JOIN shows h ON h.artist_id=a.id
                JOIN sources s ON s.show_id=h.id AND s.artist_id=a.id
                JOIN source_tracks t ON t.source_id=s.id AND t.artist_id=a.id AND NOT t.is_orphaned
                JOIN source_sets ss ON ss.id=t.source_set_id AND ss.source_id=s.id
                LEFT JOIN matches m ON m.track_uuid=t.uuid
                WHERE m.song_id IS NOT NULL OR nullif(t.mp3_url,'') IS NOT NULL OR nullif(t.flac_url,'') IS NOT NULL
                """,
                new { artist },
                commandTimeout: 600,
                cancellationToken: cancellationToken
            )
        );

    private static async Task<Dictionary<(Guid Show, string Title), string>> ReadImportedSongIds(
        Npgsql.NpgsqlConnection connection,
        Guid artist,
        CancellationToken cancellationToken
    )
    {
        var rows = await connection.QueryAsync<ImportedSongTarget>(
            new CommandDefinition(
                """
                SELECT DISTINCT sh.uuid AS Show, song.uuid AS Song, song.name
                FROM artists a JOIN shows sh ON sh.artist_id=a.id
                JOIN setlist_shows ss ON ss.artist_id=a.id AND ss.date=sh.date
                JOIN setlist_songs_plays p ON p.played_setlist_show_id=ss.id
                JOIN setlist_songs song ON song.id=p.played_setlist_song_id AND song.artist_id=a.id
                WHERE a.uuid=@artist
                """,
                new { artist },
                commandTimeout: 120,
                cancellationToken: cancellationToken
            )
        );
        return rows.GroupBy(row => (row.Show, SearchText.Fold(row.Name)))
            .Where(group => group.Count() == 1)
            .ToDictionary(
                group => group.Key,
                group =>
                    Relisten.Identifiers.UuidTypeId.Format("imported_song", group.Single().Song)
            );
    }

    private static async Task<RecordingPopularity[]> CalculateRecordings(
        Npgsql.NpgsqlConnection connection,
        Guid artist,
        IReadOnlyDictionary<Guid, PlayPopularity> recordings,
        CancellationToken cancellationToken
    )
    {
        var rows = await connection.QueryAsync<RecordingTarget>(
            new CommandDefinition(
                "SELECT s.uuid AS source,s.show_id::bigint AS show FROM sources s JOIN artists a ON a.id=s.artist_id WHERE a.uuid=@artist",
                new { artist },
                commandTimeout: 120,
                cancellationToken: cancellationToken
            )
        );
        // Compare recordings of the same show.
        return rows.Where(row => recordings.ContainsKey(row.Source))
            .GroupBy(row => row.Show)
            .SelectMany(group =>
                ScoreDetails(
                    group,
                    row => recordings[row.Source],
                    (row, metrics) => new RecordingPopularity(row.Source, metrics)
                )
            )
            .ToArray();
    }

    private static TRow[] ScoreDetails<TTarget, TRow>(
        IEnumerable<TTarget> cohort,
        Func<TTarget, PlayPopularity> playback,
        Func<TTarget, StoredPopularity, TRow> row
    )
    {
        var rows = new List<TRow>();
        var active = new List<(StoredPopularity Row, PopularityMetrics Metrics)>();
        foreach (var target in cohort)
        {
            var plays = playback(target);
            if (!plays.HasPlays)
            {
                continue;
            }

            var metrics = plays.ToMetrics();
            var stored = StoredPopularity.From(Guid.Empty, metrics);
            stored.momentum_score = null;
            if (plays.Plays30d > 0)
            {
                active.Add((stored, metrics));
            }
            rows.Add(row(target, stored));
        }

        var scores = CalculateMomentumScores(active.Select(item => item.Metrics).ToArray());
        for (var index = 0; index < active.Count; index++)
        {
            active[index].Row.momentum_score = Math.Round(scores[index], 4);
        }
        return rows.ToArray();
    }

    private sealed record PerformanceTarget(Guid Show, Guid Track, string? Song, string Title);

    private sealed record RecordingTarget(Guid Source, long Show);

    private sealed record ImportedSongTarget(Guid Show, Guid Song, string Name);
}

internal static class PopularityPerformanceKey
{
    // Raw titles can exceed PostgreSQL's B-tree key length. The identity is local
    // to matching and storage; canonical performance IDs keep their song ID.
    private const string RawPrefix = "raw:";

    public static string RawTitle(string foldedTitle) => RawPrefix + foldedTitle;

    public static string? FoldedRawTitle(string key) =>
        key.StartsWith(RawPrefix, StringComparison.Ordinal) ? key[RawPrefix.Length..] : null;

    public static string ForStorage(string titleKey) =>
        FoldedRawTitle(titleKey) is { } title
            ? RawPrefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(title)))
            : titleKey;
}

internal sealed record PerformancePopularity(Guid Show, string Song, StoredPopularity Metrics);

internal sealed record RecordingPopularity(Guid Source, StoredPopularity Metrics);

internal sealed record SongPopularity(string Song, StoredPopularity Metrics);
