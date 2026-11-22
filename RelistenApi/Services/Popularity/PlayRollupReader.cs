using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace Relisten.Services.Popularity;

/// <summary>Reads anonymous track starts from the Timescale rollups.</summary>
public static class PlayRollupReader
{
    /// <summary>
    /// Reads 365 UTC days ending on the given date for each performance: the plays
    /// of its files with the union of their play days, and the same totals for its
    /// representative source. One source can appear for several performances.
    /// Returns exactly one row per request, in request order; a performance with
    /// no plays has zero counts.
    /// </summary>
    internal static Task<IEnumerable<PerformancePlays>> ReadPerformances(
        IDbConnection connection,
        IReadOnlyList<PerformancePlaysRequest> performances,
        DateOnly through,
        IDbTransaction? transaction,
        CancellationToken cancellationToken
    )
    {
        // A page holds a few dozen files, so the daily rollups stay cheap and
        // count each play day once across a performance's overlapping files.
        var files = performances
            .SelectMany((performance, index) => performance.Tracks.Select(id => (id, index)))
            .ToArray();
        var window = Window(through);
        return connection.QueryAsync<PerformancePlays>(
            new CommandDefinition(
                """
                WITH performances AS (
                    SELECT (ordinal - 1)::int AS index, source_uuid
                    FROM unnest(@sources::uuid[]) WITH ORDINALITY p(source_uuid, ordinal)
                ), file_plays AS (
                    SELECT f.index, sum(p.plays)::bigint AS plays, count(DISTINCT p.play_day) AS days
                    FROM unnest(@files::uuid[], @fileIndices::int[]) f(id, index)
                    JOIN source_track_plays_daily p ON p.source_track_uuid = f.id
                    WHERE p.play_day >= @startDay AND p.play_day < @endDay
                    GROUP BY f.index
                ), source_plays AS (
                    SELECT p.source_uuid, sum(p.plays)::bigint AS plays, count(*) AS days
                    FROM source_plays_daily p
                    WHERE p.source_uuid = ANY(@sources)
                      AND p.play_day >= @startDay AND p.play_day < @endDay
                    GROUP BY p.source_uuid
                )
                SELECT p.index, coalesce(f.plays, 0) AS plays, coalesce(f.days, 0) AS days,
                       coalesce(s.plays, 0) AS sourcePlays, coalesce(s.days, 0) AS sourceDays
                FROM performances p
                LEFT JOIN file_plays f ON f.index = p.index
                LEFT JOIN source_plays s ON s.source_uuid = p.source_uuid
                ORDER BY p.index
                """,
                new
                {
                    sources = performances.Select(performance => performance.Source).ToArray(),
                    files = files.Select(file => file.id).ToArray(),
                    fileIndices = files.Select(file => file.index).ToArray(),
                    window.StartDay,
                    window.EndDay,
                },
                transaction: transaction,
                commandTimeout: 30,
                cancellationToken: cancellationToken
            )
        );
    }

    internal static Task<IEnumerable<YearlyPopularityTotal>> ReadPopularity(
        IDbConnection connection,
        IDbTransaction transaction,
        PopularityPopulation population,
        DateOnly through
    )
    {
        var (key, join) = population.Kind switch
        {
            PopularityPopulationKind.Artist => ("plays.artist_uuid", ""),
            PopularityPopulationKind.Show => ("plays.show_uuid", ""),
            PopularityPopulationKind.Year => (
                "y.uuid",
                "JOIN shows s ON s.uuid = plays.show_uuid JOIN years y ON y.id = s.year_id"
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(population)),
        };

        return connection.QueryAsync<YearlyPopularityTotal>(
            $"""
            WITH plays AS ({YearlyTrackRows(
                population.Artist.HasValue ? "artist_uuid = @artist" : "true"
            )})
            SELECT {key} AS id, sum(plays.plays)::bigint AS plays,
                   sum(plays.total_track_seconds)::bigint AS seconds
            FROM plays {join}
            GROUP BY 1
            """,
            Window(through, population.Artist),
            transaction
        );
    }

    internal static Task<IEnumerable<TrackStartTotal>> ReadTrackStarts(
        IDbConnection connection,
        IDbTransaction transaction,
        DateOnly through,
        CancellationToken cancellationToken
    ) =>
        connection.QueryAsync<TrackStartTotal>(
            new CommandDefinition(
                $"""
                SELECT source_track_uuid AS id, sum(plays)::bigint AS plays
                FROM ({YearlyTrackRows("true", "source_track_uuid")}) AS track_rollups
                GROUP BY source_track_uuid
                """,
                Window(through),
                transaction,
                commandTimeout: 600,
                cancellationToken: cancellationToken
            )
        );

    // The three half-open ranges partition 365 UTC play days. Passing timestamp
    // bounds lets each branch exclude irrelevant Timescale chunks and use its indexes.
    private static string YearlyTrackRows(
        string filter,
        string columns = "artist_uuid, show_uuid, total_track_seconds"
    ) =>
        $"""
            SELECT {columns}, plays
            FROM source_track_plays_monthly
            WHERE play_month >= @firstWholeMonth AND play_month < @endOfWholeMonths AND {filter}
            UNION ALL
            SELECT {columns}, plays
            FROM source_track_plays_daily
            WHERE play_day >= @startDay AND play_day < @firstWholeMonth AND {filter}
            UNION ALL
            SELECT {columns}, plays
            FROM source_track_plays_daily
            WHERE play_day >= @endOfWholeMonths AND play_day < @endDay AND {filter}
            """;

    private static YearlyWindow Window(DateOnly through, Guid? artist = null)
    {
        var start = through.AddDays(-364);
        var end = through.AddDays(1);
        var firstWholeMonth =
            start.Day > 1 ? new DateOnly(start.Year, start.Month, 1).AddMonths(1) : start;
        var endOfWholeMonths = new DateOnly(end.Year, end.Month, 1);

        static DateTime Utc(DateOnly date) => date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        return new(artist, Utc(start), Utc(end), Utc(firstWholeMonth), Utc(endOfWholeMonths));
    }

    // Query parameters for one yearly window; the artist is null for global scopes.
    private sealed record YearlyWindow(
        Guid? Artist,
        DateTime StartDay,
        DateTime EndDay,
        DateTime FirstWholeMonth,
        DateTime EndOfWholeMonths
    );
}

internal sealed record YearlyPopularityTotal(Guid Id, long Plays, long Seconds);

internal readonly record struct TrackStartTotal(Guid Id, long Plays);

internal sealed record PerformancePlaysRequest(Guid[] Tracks, Guid Source);

internal sealed record PerformancePlays(
    int Index,
    long Plays,
    long Days,
    long SourcePlays,
    long SourceDays
);
