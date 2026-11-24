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
    internal static Task<IEnumerable<PlayPopularityRow>> ReadTrackPopularity(
        IDbConnection connection,
        IDbTransaction? transaction,
        DateOnly through,
        CancellationToken cancellationToken,
        Guid artist
    )
    {
        const string filter = "artist_uuid = @artist";
        var window = Window(through, artist);
        var now = DateTime.UtcNow;
        var currentHour = now.Date.AddHours(now.Hour);
        var lastHour = currentHour < window.EndDay ? currentHour : window.EndDay;

        return connection.QueryAsync<PlayPopularityRow>(
            new CommandDefinition(
                $"""
                WITH windows AS (
                    SELECT source_track_uuid, source_uuid, 1 AS grain, plays, total_track_seconds AS seconds,
                           play_day >= @week AS is_week, play_day >= @month AS is_month, false AS is_recent
                    FROM source_track_plays_daily
                    WHERE play_day >= @quarter AND play_day < @endDay AND {filter}
                    UNION ALL
                    SELECT source_track_uuid, source_uuid, 2, plays, total_track_seconds, false, false, play_hour >= @shortWindow
                    FROM source_track_plays_hourly
                    WHERE play_hour >= @recent AND play_hour < @lastHour AND {filter}
                    UNION ALL
                    SELECT source_track_uuid, source_uuid, 3, plays, total_track_seconds, false, false, false
                    FROM ({YearlyTrackRows(
                    filter,
                    "source_track_uuid,source_uuid,total_track_seconds"
                )}) AS yearly
                )
                SELECT coalesce(source_track_uuid,source_uuid) AS id,
                       source_track_uuid IS NULL AS isRecording,
                       coalesce(sum(plays) FILTER (WHERE grain = 2), 0)::bigint AS plays48h,
                       coalesce(sum(seconds) FILTER (WHERE grain = 2), 0)::bigint AS seconds48h,
                       coalesce(sum(plays) FILTER (WHERE is_week), 0)::bigint AS plays7d,
                       coalesce(sum(seconds) FILTER (WHERE is_week), 0)::bigint AS seconds7d,
                       coalesce(sum(plays) FILTER (WHERE is_month), 0)::bigint AS plays30d,
                       coalesce(sum(seconds) FILTER (WHERE is_month), 0)::bigint AS seconds30d,
                       coalesce(sum(plays) FILTER (WHERE grain = 1), 0)::bigint AS plays90d,
                       coalesce(sum(plays) FILTER (WHERE is_recent), 0)::bigint AS plays6h,
                       coalesce(sum(plays) FILTER (WHERE grain = 3), 0)::bigint AS plays1y,
                       coalesce(sum(seconds) FILTER (WHERE grain = 3), 0)::bigint AS seconds1y
                FROM windows
                GROUP BY GROUPING SETS ((source_track_uuid), (source_uuid))
                """,
                new
                {
                    artist,
                    window.StartDay,
                    window.EndDay,
                    window.FirstWholeMonth,
                    window.EndOfWholeMonths,
                    week = window.EndDay.AddDays(-7),
                    month = window.EndDay.AddDays(-30),
                    quarter = window.EndDay.AddDays(-90),
                    recent = lastHour.AddHours(-48),
                    shortWindow = lastHour.AddHours(-6),
                    lastHour,
                },
                transaction,
                commandTimeout: 600,
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
        CancellationToken cancellationToken,
        Guid? artist = null
    ) =>
        connection.QueryAsync<TrackStartTotal>(
            new CommandDefinition(
                $"""
                SELECT source_track_uuid AS id, sum(plays)::bigint AS plays
                FROM ({YearlyTrackRows(
                    artist is null ? "true" : "artist_uuid = @artist",
                    "source_track_uuid"
                )}) AS track_rollups
                GROUP BY source_track_uuid
                """,
                Window(through, artist),
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
