using Dapper;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;
using Relisten.Services.Popularity;

namespace RelistenUserServiceTests;

[TestFixture, NonParallelizable]
public sealed class TestPlaylistPopularityRefresh
{
    [Test]
    public async Task Quiet_days_advance_the_published_windows_without_advancing_the_watermark()
    {
        var database = new PostgresIntegrationDatabase();
        try
        {
            await database.StartAsync();
            await database.LoadCatalogSchemaAsync();
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync();

            var cutoff = await connection.QuerySingleAsync<DateTime>(
                "SELECT date_trunc('day', now(), 'UTC')"
            );
            var playedDay = cutoff.AddDays(-3);
            await connection.ExecuteAsync(
                """
                WITH artist AS (
                    INSERT INTO public.artists (uuid, name, slug, updated_at)
                    VALUES (gen_random_uuid(), 'Quiet playlist fixture', 'quiet-playlist', now()) RETURNING id
                ), track AS (
                    INSERT INTO public.source_tracks (uuid, artist_id, track_position, title, slug, mp3_url, duration, updated_at)
                    SELECT gen_random_uuid(), id, 1, 'Track', 'track', 'https://example.test/track.mp3', 60, now()
                    FROM artist RETURNING uuid
                )
                INSERT INTO public.source_track_plays (created_at, source_track_uuid, app_type, playlist_id)
                SELECT @playedAt, uuid, 2, 'playlist_quiet_fixture' FROM track
                """,
                new { playedAt = playedDay.AddHours(12) }
            );
            await RefreshDays(connection, playedDay, playedDay.AddDays(1));
            var watermark = await ReadWatermark(connection);
            watermark.Should().Be(playedDay.AddDays(1));

            await RefreshDays(connection, playedDay.AddDays(1), cutoff);
            (await ReadWatermark(connection)).Should().Be(watermark);

            await PopularityService.PublishPlaylistPopularity(connection, CancellationToken.None);
            var actual = await connection.QuerySingleAsync<(
                DateTime Cutoff,
                long Last48Hours,
                long LastWeek
            )>(
                """
                SELECT as_of, (popularity #>> '{windows,48h,plays}')::bigint,
                       (popularity #>> '{windows,7d,plays}')::bigint
                FROM public.playlist_popularity WHERE playlist_id = 'playlist_quiet_fixture'
                """
            );
            actual.Should().Be((cutoff, 0L, 1L));
        }
        finally
        {
            await database.StopAsync();
        }
    }

    private static Task RefreshDays(NpgsqlConnection connection, DateTime start, DateTime end) =>
        connection.ExecuteAsync(
            "CALL refresh_continuous_aggregate('public.source_track_plays_playlist_daily', @start, @end)",
            new { start, end }
        );

    private static Task<DateTime> ReadWatermark(NpgsqlConnection connection) =>
        connection.QuerySingleAsync<DateTime>(
            """
            SELECT _timescaledb_functions.to_timestamp(_timescaledb_functions.cagg_watermark(mat_hypertable_id))
            FROM _timescaledb_catalog.continuous_agg
            WHERE user_view_schema = 'public' AND user_view_name = 'source_track_plays_playlist_daily'
            """
        );
}
