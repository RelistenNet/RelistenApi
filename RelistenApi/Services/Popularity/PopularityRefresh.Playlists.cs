using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Relisten.Api.Models;
using Relisten.Api.Models.Api.V4;

namespace Relisten.Services.Popularity;

public partial class PopularityService
{
    private async Task RefreshPlaylistPopularity(CancellationToken ct)
    {
        var elapsed = Stopwatch.StartNew();
        await using var connection = db.CreateConnection(longTimeout: false, readOnly: false);
        await connection.OpenAsync(ct);
        var count = await PublishPlaylistPopularity(connection, ct);
        logger.LogInformation(
            "Published popularity for {Playlists} playlists in {Elapsed}",
            count,
            elapsed.Elapsed
        );
    }

    internal static async Task<int> PublishPlaylistPopularity(
        NpgsqlConnection connection,
        CancellationToken ct
    )
    {
        // The watermark is the exclusive end of materialized data. A day without
        // playlist plays still advances the window when Timescale materializes it.
        var cutoff = await connection.QuerySingleAsync<DateTime?>(
            new CommandDefinition(
                """
                SELECT CASE WHEN watermark >= 0 THEN least(date_trunc('day', now(), 'UTC'),
                    date_trunc('day', _timescaledb_functions.to_timestamp(watermark), 'UTC')) END
                FROM (
                    SELECT _timescaledb_functions.cagg_watermark(mat_hypertable_id) AS watermark
                    FROM _timescaledb_catalog.continuous_agg
                    WHERE user_view_schema = 'public' AND user_view_name = 'source_track_plays_playlist_daily'
                ) AS aggregate
                """,
                cancellationToken: ct
            )
        );
        if (cutoff is null)
        {
            return 0;
        }

        var rows = await ReadPlaylistWindows(connection, cutoff.Value, ct);
        var snapshots = ScorePlaylists(rows);
        // A single statement publishes the new snapshot and retires expired rows together.
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                WITH snapshots AS MATERIALIZED (
                    SELECT * FROM jsonb_to_recordset(@json::jsonb) AS snapshot(id text, popularity jsonb)
                ), expired AS (
                    DELETE FROM public.playlist_popularity WHERE playlist_id NOT IN (SELECT id FROM snapshots)
                )
                INSERT INTO public.playlist_popularity (playlist_id, as_of, popularity)
                SELECT id, @cutoff, popularity FROM snapshots
                ON CONFLICT (playlist_id) DO UPDATE SET as_of = excluded.as_of, popularity = excluded.popularity
                """,
                new { json = JsonSerializer.Serialize(snapshots), cutoff },
                cancellationToken: ct
            )
        );
        return snapshots.Length;
    }

    private static async Task<PlaylistWindows[]> ReadPlaylistWindows(
        NpgsqlConnection connection,
        DateTime cutoff,
        CancellationToken ct
    ) =>
        (
            await connection.QueryAsync<PlaylistWindows>(
                new CommandDefinition(
                    """
                    SELECT playlist_id AS Id,
                        COALESCE(sum(plays) FILTER (WHERE play_day >= @day), 0)::bigint AS Plays1d,
                        COALESCE(sum(plays) FILTER (WHERE play_day >= @twoDays), 0)::bigint AS Plays48h,
                        COALESCE(sum(plays) FILTER (WHERE play_day >= @week), 0)::bigint AS Plays7d,
                        COALESCE(sum(plays) FILTER (WHERE play_day >= @month), 0)::bigint AS Plays30d,
                        COALESCE(sum(plays) FILTER (WHERE play_day >= @quarter), 0)::bigint AS Plays90d,
                        sum(plays)::bigint AS PlaysYear,
                        COALESCE(sum(total_track_seconds) FILTER (WHERE play_day >= @twoDays), 0)::double precision AS Seconds48h,
                        COALESCE(sum(total_track_seconds) FILTER (WHERE play_day >= @week), 0)::double precision AS Seconds7d,
                        COALESCE(sum(total_track_seconds) FILTER (WHERE play_day >= @month), 0)::double precision AS Seconds30d,
                        sum(total_track_seconds)::double precision AS SecondsYear
                    FROM public.source_track_plays_playlist_daily
                    WHERE play_day >= @year AND play_day < @cutoff
                    GROUP BY playlist_id
                    """,
                    new
                    {
                        cutoff,
                        day = cutoff.AddDays(-1),
                        twoDays = cutoff.AddDays(-2),
                        week = cutoff.AddDays(-7),
                        month = cutoff.AddDays(-30),
                        quarter = cutoff.AddDays(-90),
                        year = cutoff.AddDays(-365),
                    },
                    cancellationToken: ct
                )
            )
        ).ToArray();

    private static object[] ScorePlaylists(PlaylistWindows[] rows)
    {
        var playlists = rows.Select(row =>
            {
                var metrics = CreateMetrics(
                    row.Plays30d,
                    row.Plays7d,
                    plays6h: 0,
                    row.Plays48h,
                    row.Plays90d,
                    row.Seconds30d,
                    row.Seconds7d,
                    row.Seconds48h
                );
                metrics.windows.Year = new PopularityWindowMetrics
                {
                    plays = row.PlaysYear,
                    hours = row.SecondsYear / 3600.0,
                    hot_score = Math.Sqrt(row.PlaysYear),
                };
                // Playlists use the latest complete day in place of catalog's six-hour input.
                var shortTrend = row.Plays7d == 0 ? 0 : 7.0 * row.Plays1d / row.Plays7d;
                return (row.Id, Metrics: metrics, ShortTrend: shortTrend);
            })
            .ToArray();
        var active = playlists
            .Where(playlist => playlist.Metrics.windows.days_30d.plays > 0)
            .ToArray();
        ApplyMomentumScores(
            active.Select(playlist => playlist.Metrics).ToArray(),
            active.Select(playlist => playlist.ShortTrend).ToArray()
        );
        return playlists
            .Select(playlist => new
            {
                id = playlist.Id,
                popularity = V4CatalogProjection.ProjectPopularity(playlist.Metrics),
            })
            .ToArray();
    }

    private sealed record PlaylistWindows(
        string Id,
        long Plays1d,
        long Plays48h,
        long Plays7d,
        long Plays30d,
        long Plays90d,
        long PlaysYear,
        double Seconds48h,
        double Seconds7d,
        double Seconds30d,
        double SecondsYear
    );
}
