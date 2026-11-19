using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace Relisten.Services.Popularity;

/// <summary>Plays, listening seconds, and the number of days at the requested track or source grain.</summary>
public sealed record PlayRollupTotals(Guid Id, long Plays, long Seconds, long DaysWithPlays);

/// <summary>Reads anonymous track starts from the Timescale rollups.</summary>
public static class PlayRollupReader
{
    /// <summary>Reads all track history, or 365 UTC days ending on the given date.</summary>
    public static Task<IEnumerable<PlayRollupTotals>> ReadTracks(
        IDbConnection connection,
        Guid[] ids,
        DateOnly? through = null,
        CancellationToken cancellationToken = default,
        IDbTransaction? transaction = null
    )
    {
        var buckets = through.HasValue
            ? YearlyTrackRows("source_track_uuid = ANY(@ids)")
            : """
                SELECT source_track_uuid, plays, total_track_seconds, days_with_plays
                FROM source_track_plays_monthly
                WHERE play_month < @currentMonth AND source_track_uuid = ANY(@ids)
                UNION ALL
                SELECT source_track_uuid, plays, total_track_seconds, 1::bigint
                FROM source_track_plays_daily
                WHERE play_day >= @currentMonth AND source_track_uuid = ANY(@ids)
                """;
        return connection.QueryAsync<PlayRollupTotals>(
            new CommandDefinition(
                $"""
                WITH plays AS ({buckets})
                SELECT source_track_uuid AS id, sum(plays)::bigint AS plays,
                       sum(total_track_seconds)::bigint AS seconds,
                       sum(days_with_plays)::bigint AS daysWithPlays
                FROM plays GROUP BY 1
                """,
                Parameters(through, ids: ids),
                transaction: transaction,
                cancellationToken: cancellationToken
            )
        );
    }

    /// <summary>Reads source-day totals for all history, or 365 UTC days ending on the given date.</summary>
    public static Task<IEnumerable<PlayRollupTotals>> ReadSources(
        IDbConnection connection,
        Guid[] ids,
        DateOnly? through = null,
        CancellationToken cancellationToken = default,
        IDbTransaction? transaction = null
    ) =>
        connection.QueryAsync<PlayRollupTotals>(
            new CommandDefinition(
                """
                SELECT source_uuid AS id, sum(plays)::bigint AS plays,
                       sum(total_track_seconds)::bigint AS seconds, count(*) AS daysWithPlays
                FROM source_plays_daily
                WHERE source_uuid = ANY(@ids)
                  AND (@startDay::timestamptz IS NULL OR play_day >= @startDay AND play_day < @endDay)
                GROUP BY 1
                """,
                Parameters(through, ids: ids),
                transaction: transaction,
                cancellationToken: cancellationToken
            )
        );

    internal static Task<IEnumerable<YearlyPopularityTotal>> ReadPopularity(
        IDbConnection connection,
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
            Parameters(through, artist: population.Artist)
        );
    }

    // The three half-open ranges partition 365 UTC play days. Passing timestamp
    // bounds lets each branch exclude irrelevant Timescale chunks and use its indexes.
    private static string YearlyTrackRows(string filter) =>
        $"""
            SELECT source_track_uuid, artist_uuid, show_uuid, plays, total_track_seconds, days_with_plays
            FROM source_track_plays_monthly
            WHERE play_month >= @firstWholeMonth AND play_month < @endOfWholeMonths AND {filter}
            UNION ALL
            SELECT source_track_uuid, artist_uuid, show_uuid, plays, total_track_seconds, 1::bigint
            FROM source_track_plays_daily
            WHERE play_day >= @startDay AND play_day < @firstWholeMonth AND {filter}
            UNION ALL
            SELECT source_track_uuid, artist_uuid, show_uuid, plays, total_track_seconds, 1::bigint
            FROM source_track_plays_daily
            WHERE play_day >= @endOfWholeMonths AND play_day < @endDay AND {filter}
            """;

    private static object Parameters(DateOnly? through, Guid[]? ids = null, Guid? artist = null)
    {
        var today = DateTime.UtcNow;
        var start = through?.AddDays(-364);
        var end = through?.AddDays(1);
        var firstWholeMonth = start is { Day: > 1 } day
            ? new DateOnly(day.Year, day.Month, 1).AddMonths(1)
            : start;
        var endOfWholeMonths = end is { } endDate
            ? new DateOnly(endDate.Year, endDate.Month, 1)
            : (DateOnly?)null;

        static DateTime? Utc(DateOnly? date) =>
            date?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        return new
        {
            ids,
            artist,
            startDay = Utc(start),
            endDay = Utc(end),
            firstWholeMonth = Utc(firstWholeMonth),
            endOfWholeMonths = Utc(endOfWholeMonths),
            currentMonth = Utc(new DateOnly(today.Year, today.Month, 1)),
        };
    }
}

internal sealed record YearlyPopularityTotal(Guid Id, long Plays, long Seconds);
