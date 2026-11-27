using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Relisten.Api.Models;
using Relisten.Api.Models.Api.V4;
using Relisten.Search;

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
        var cutoff = await RefreshClosedPlaylistDaysAsync(connection, ct);
        var rows = await ReadPlaylistWindows(connection, cutoff, ct);
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

    private static async Task<DateTime> RefreshClosedPlaylistDaysAsync(
        NpgsqlConnection connection,
        CancellationToken ct
    )
    {
        var cutoff = await connection.QuerySingleAsync<DateTime>(
            new CommandDefinition("SELECT date_trunc('day', now(), 'UTC')", cancellationToken: ct)
        );

        // Timescale only advances the watermark when it materializes a row for a later day.
        // Refresh the policy's recent range here so the cutoff also covers complete quiet days.
        await connection.ExecuteAsync(
            new CommandDefinition(
                "CALL refresh_continuous_aggregate('public.source_track_plays_playlist_daily', @start, @cutoff)",
                new { start = cutoff.AddDays(-3), cutoff },
                cancellationToken: ct
            )
        );
        return cutoff;
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

    private static PlaylistSnapshot[] ScorePlaylists(PlaylistWindows[] rows)
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
            .Select(playlist => new PlaylistSnapshot(
                playlist.Id,
                V4CatalogProjection.ProjectPopularity(playlist.Metrics)!
            ))
            .ToArray();
    }

    private sealed record PlaylistSnapshot(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("popularity")] PopularityResource Popularity
    );

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
