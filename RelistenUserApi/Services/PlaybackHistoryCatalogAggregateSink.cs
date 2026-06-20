using Dapper;
using Npgsql;

namespace Relisten.UserApi.Services;

public sealed class PlaybackHistoryCatalogAggregateSink
{
    public async Task EnqueueAcceptedPlay(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid playbackHistoryUuid,
        Guid sourceTrackUuid,
        Guid sourceUuid,
        DateTimeOffset playedAt,
        string platform,
        DateTimeOffset now)
    {
        await connection.ExecuteAsync(
            """
            INSERT INTO user_data.playback_history_catalog_play_queue
                (playback_history_id, source_track_uuid, source_uuid, played_at, platform, created_at)
            VALUES
                (@PlaybackHistoryUuid, @SourceTrackUuid, @SourceUuid, @PlayedAt, @Platform, @Now)
            ON CONFLICT (playback_history_id) DO NOTHING
            """,
            new
            {
                PlaybackHistoryUuid = playbackHistoryUuid,
                SourceTrackUuid = sourceTrackUuid,
                SourceUuid = sourceUuid,
                PlayedAt = playedAt,
                Platform = platform,
                Now = now
            },
            transaction);
    }
}
