using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models;
using Relisten.Search;

namespace Relisten.Services.Popularity;

public partial class PopularityService
{
    private const int DetailShowBatchSize = 64;

    internal async Task<(long Performances, long Recordings, long Songs)> RefreshDetailPopulations(
        Guid artist,
        CancellationToken cancellationToken,
        int showBatchSize = DetailShowBatchSize
    )
    {
        var generatedAt = DateTime.UtcNow;
        var shows = await ReadDetailShowIds(artist, cancellationToken);

        var performances = new List<PerformancePlayback>();
        var recordings = new List<RecordingPopularity>();
        var importedSongs = new Dictionary<(Guid Show, string Title), string>();
        foreach (var batch in shows.Chunk(showBatchSize))
        {
            var detail = await ReadDetailBatchWithRetry(
                artist,
                batch,
                generatedAt,
                cancellationToken
            );
            performances.AddRange(detail.Performances);
            recordings.AddRange(detail.Recordings);
            foreach (var (key, song) in detail.ImportedSongs)
            {
                importedSongs.Add(key, song);
            }
        }

        // Song totals and momentum still compare the artist's complete results.
        var performanceScores = ScorePerformances(performances);
        var songs = CalculateSongs(performances, importedSongs);
        await store.PublishDetails(
            artist,
            performanceScores,
            recordings.ToArray(),
            songs,
            generatedAt,
            cancellationToken
        );
        return (performanceScores.Length, recordings.Count, songs.Length);
    }

    private async Task<IEnumerable<Guid>> ReadDetailShowIds(
        Guid artist,
        CancellationToken cancellationToken
    )
    {
        await using var connection = db.CreateReplicaConnection();
        return await connection.QueryAsync<Guid>(
            new CommandDefinition(
                """
                SELECT sh.uuid FROM shows sh JOIN artists a ON a.id=sh.artist_id
                WHERE a.uuid=@artist ORDER BY sh.id
                """,
                new { artist },
                cancellationToken: cancellationToken
            )
        );
    }

    private async Task<DetailBatch> ReadDetailBatchWithRetry(
        Guid artist,
        Guid[] shows,
        DateTime generatedAt,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await ReadDetailBatch(artist, shows, generatedAt, cancellationToken);
        }
        catch (PostgresException error)
            when (error.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            // Recovery can cancel even a short read if the standby is already behind.
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            return await ReadDetailBatch(artist, shows, generatedAt, cancellationToken);
        }
    }

    private async Task<DetailBatch> ReadDetailBatch(
        Guid artist,
        Guid[] shows,
        DateTime generatedAt,
        CancellationToken cancellationToken
    )
    {
        // Each SELECT gets its own snapshot. A playback or catalog change
        // between statements can affect this run's approximate totals.
        await using var connection = db.CreateReplicaConnection();
        await connection.OpenAsync(cancellationToken);
        var targets = await ReadPerformanceTargets(connection, artist, shows, cancellationToken);
        var recordingTargets = await ReadRecordingTargets(
            connection,
            artist,
            shows,
            cancellationToken
        );
        var rows = await PlayRollupReader.ReadTrackPopularity(
            connection,
            generatedAt,
            cancellationToken,
            artist,
            targets.Select(target => target.Track).Distinct().ToArray(),
            recordingTargets.Select(target => target.Source).ToArray()
        );
        var (tracks, recordings) = DividePlayback(rows);
        var imports = await ReadImportedSongIds(connection, artist, shows, cancellationToken);
        var sources = ScoreRecordings(recordingTargets, recordings);
        return new(SumPerformances(targets, tracks), sources, imports);
    }

    private sealed record DetailBatch(
        PerformancePlayback[] Performances,
        RecordingPopularity[] Recordings,
        Dictionary<(Guid Show, string Title), string> ImportedSongs
    );

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

    // Compare plays of the same song across shows, including across batches.
    private static PerformancePopularity[] ScorePerformances(
        IEnumerable<PerformancePlayback> performances
    ) =>
        performances
            .GroupBy(row => row.Song.Key)
            .SelectMany(group =>
                ScoreDetails(
                    group,
                    row => row.Playback,
                    (row, metrics) => new PerformancePopularity(row.Show, row.Song.Key, metrics)
                )
            )
            .ToArray();

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
                var key = PopularityPerformanceKey.StoredRawTitle(title);
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
        NpgsqlConnection connection,
        Guid artist,
        Guid[] shows,
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
                    FROM owner a
                    CROSS JOIN LATERAL norm.song_recording_matches(a.uuid,NULL::text[],@shows,include_orphaned => true) m
                    WHERE a.approved
                )
                SELECT h.uuid AS show,t.uuid AS track,m.song_id AS song,coalesce(t.title,'') AS title
                FROM owner a JOIN shows h ON h.artist_id=a.id
                JOIN sources s ON s.show_id=h.id AND s.artist_id=a.id
                JOIN source_tracks t ON t.source_id=s.id AND t.artist_id=a.id
                LEFT JOIN source_sets ss ON ss.id=t.source_set_id AND ss.source_id=s.id
                LEFT JOIN matches m ON m.track_uuid=t.uuid
                WHERE h.uuid=ANY(@shows)
                  AND (t.is_orphaned OR ss.id IS NOT NULL)
                  AND (t.is_orphaned OR m.song_id IS NOT NULL OR nullif(t.mp3_url,'') IS NOT NULL OR nullif(t.flac_url,'') IS NOT NULL)
                """,
                new { artist, shows },
                commandTimeout: 600,
                cancellationToken: cancellationToken
            )
        );

    private static async Task<Dictionary<(Guid Show, string Title), string>> ReadImportedSongIds(
        NpgsqlConnection connection,
        Guid artist,
        Guid[] shows,
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
                WHERE a.uuid=@artist AND sh.uuid=ANY(@shows)
                """,
                new { artist, shows },
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

    private static Task<IEnumerable<RecordingTarget>> ReadRecordingTargets(
        NpgsqlConnection connection,
        Guid artist,
        Guid[] shows,
        CancellationToken cancellationToken
    ) =>
        connection.QueryAsync<RecordingTarget>(
            new CommandDefinition(
                """
                SELECT s.uuid AS source,s.show_id::bigint AS show
                FROM sources s JOIN shows sh ON sh.id=s.show_id
                JOIN artists a ON a.id=s.artist_id
                WHERE a.uuid=@artist AND sh.uuid=ANY(@shows)
                """,
                new { artist, shows },
                commandTimeout: 120,
                cancellationToken: cancellationToken
            )
        );

    // All recordings of a show are in the same batch, so their scores can be
    // calculated before the other batches finish.
    private static RecordingPopularity[] ScoreRecordings(
        IEnumerable<RecordingTarget> targets,
        IReadOnlyDictionary<Guid, PlayPopularity> recordings
    ) =>
        targets
            .Where(row => recordings.ContainsKey(row.Source))
            .GroupBy(row => row.Show)
            .SelectMany(group =>
                ScoreDetails(
                    group,
                    row => recordings[row.Source],
                    (row, metrics) => new RecordingPopularity(row.Source, metrics)
                )
            )
            .ToArray();

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
        FoldedRawTitle(titleKey) is { } title ? StoredRawTitle(title) : titleKey;

    public static string StoredRawTitle(string foldedTitle) =>
        RawPrefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(foldedTitle)));
}

internal sealed record PerformancePopularity(Guid Show, string Song, StoredPopularity Metrics);

internal sealed record RecordingPopularity(Guid Source, StoredPopularity Metrics);

internal sealed record SongPopularity(string Song, StoredPopularity Metrics);
