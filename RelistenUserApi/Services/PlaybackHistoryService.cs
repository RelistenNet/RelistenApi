using System.Globalization;
using Dapper;
using Relisten.UserApi.Models;

namespace Relisten.UserApi.Services;

public sealed class PlaybackHistoryService
{
    private const int MaxBatchSize = 500;
    private const int DefaultRecentLimit = 50;
    private const int MaxRecentLimit = 100;

    private readonly UserApiDbService _db;

    public PlaybackHistoryService(UserApiDbService db)
    {
        _db = db;
    }

    public async Task<PlaybackHistoryBatchResponse> IngestBatch(
        Guid userUuid,
        PlaybackHistoryBatchRequest request)
    {
        if (request.Events == null || request.Events.Count == 0 || request.Events.Count > MaxBatchSize)
        {
            throw new PlaybackHistoryException("invalid_history_batch");
        }

        foreach (var historyEvent in request.Events)
        {
            ValidateEvent(historyEvent);
        }

        await using var connection = _db.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        if (!await IsHistoryEnabled(connection, transaction, userUuid))
        {
            await transaction.CommitAsync();
            return new PlaybackHistoryBatchResponse
            {
                HistoryEnabled = false,
                AcceptedCount = 0,
                DuplicateCount = 0,
                Results = request.Events
                    .Select(historyEvent => new PlaybackHistoryEventResultResponse
                    {
                        ClientEventUuid = historyEvent.ClientEventUuid,
                        Status = "rejected_history_disabled"
                    })
                    .ToList()
            };
        }

        var acceptedCount = 0;
        var duplicateCount = 0;
        var results = new List<PlaybackHistoryEventResultResponse>(request.Events.Count);
        foreach (var historyEvent in request.Events)
        {
            var normalized = NormalizeEvent(historyEvent);
            var now = DateTimeOffset.UtcNow;
            var historyUuid = Guid.NewGuid();
            var insertedHistoryUuid = await connection.QuerySingleOrDefaultAsync<Guid?>(
                """
                INSERT INTO user_data.playback_history_ingest_keys
                    (user_id, device_id, client_event_uuid, playback_history_id,
                    playback_history_played_at, created_at)
                VALUES
                    (@UserUuid, @DeviceId, @ClientEventUuid, @PlaybackHistoryUuid, @PlayedAt, @Now)
                ON CONFLICT (user_id, device_id, client_event_uuid) DO NOTHING
                RETURNING playback_history_id
                """,
                new
                {
                    UserUuid = userUuid,
                    normalized.DeviceId,
                    historyEvent.ClientEventUuid,
                    PlaybackHistoryUuid = historyUuid,
                    historyEvent.PlayedAt,
                    Now = now
                },
                transaction);

            if (!insertedHistoryUuid.HasValue)
            {
                duplicateCount++;
                results.Add(new PlaybackHistoryEventResultResponse
                {
                    ClientEventUuid = historyEvent.ClientEventUuid,
                    Status = "duplicate"
                });
                continue;
            }

            await connection.ExecuteAsync(
                """
                INSERT INTO user_data.playback_history
                    (id, user_id, client_event_uuid, source_track_uuid, source_uuid,
                     playlist_uuid, playlist_entry_uuid, block_uuid, block_position,
                     played_at, platform, app_version, device_id, created_at)
                VALUES
                    (@PlaybackHistoryUuid, @UserUuid, @ClientEventUuid, @SourceTrackUuid, @SourceUuid,
                     @PlaylistUuid, @PlaylistEntryUuid, @BlockUuid, @BlockPosition,
                     @PlayedAt, @Platform, @AppVersion, @DeviceId, @Now)
                """,
                new
                {
                    PlaybackHistoryUuid = insertedHistoryUuid.Value,
                    UserUuid = userUuid,
                    historyEvent.ClientEventUuid,
                    historyEvent.SourceTrackUuid,
                    historyEvent.SourceUuid,
                    historyEvent.PlaylistUuid,
                    historyEvent.PlaylistEntryUuid,
                    historyEvent.BlockUuid,
                    historyEvent.BlockPosition,
                    historyEvent.PlayedAt,
                    normalized.Platform,
                    normalized.AppVersion,
                    normalized.DeviceId,
                    Now = now
                },
                transaction);

            acceptedCount++;
            results.Add(new PlaybackHistoryEventResultResponse
            {
                ClientEventUuid = historyEvent.ClientEventUuid,
                Status = "accepted"
            });
        }

        await transaction.CommitAsync();
        return new PlaybackHistoryBatchResponse
        {
            HistoryEnabled = true,
            AcceptedCount = acceptedCount,
            DuplicateCount = duplicateCount,
            Results = results
        };
    }

    public async Task<PlaybackHistoryRecentResponse> GetRecent(Guid userUuid, string? limit)
    {
        var normalizedLimit = NormalizeRecentLimit(limit);
        await using var connection = _db.CreateConnection();
        var items = await connection.QueryAsync<PlaybackHistoryItemResponse>(
            """
            SELECT
                id AS "HistoryUuid",
                client_event_uuid AS "ClientEventUuid",
                source_track_uuid AS "SourceTrackUuid",
                source_uuid AS "SourceUuid",
                playlist_uuid AS "PlaylistUuid",
                playlist_entry_uuid AS "PlaylistEntryUuid",
                block_uuid AS "BlockUuid",
                block_position AS "BlockPosition",
                played_at AS "PlayedAt"
            FROM user_data.playback_history
            WHERE user_id = @UserUuid
            ORDER BY played_at DESC, id DESC
            LIMIT @Limit
            """,
            new
            {
                UserUuid = userUuid,
                Limit = normalizedLimit
            });

        return new PlaybackHistoryRecentResponse
        {
            Items = items.ToList()
        };
    }

    private static void ValidateEvent(PlaybackHistoryEventRequest historyEvent)
    {
        if (historyEvent.ClientEventUuid == Guid.Empty ||
            historyEvent.SourceTrackUuid == Guid.Empty ||
            historyEvent.SourceUuid == Guid.Empty ||
            historyEvent.PlayedAt == default ||
            string.IsNullOrWhiteSpace(historyEvent.Platform) ||
            string.IsNullOrWhiteSpace(historyEvent.AppVersion) ||
            string.IsNullOrWhiteSpace(historyEvent.DeviceId) ||
            historyEvent.Platform.Trim().Length > 40 ||
            historyEvent.AppVersion.Trim().Length > 80 ||
            historyEvent.DeviceId.Trim().Length > 200)
        {
            throw new PlaybackHistoryException("invalid_history_event");
        }

        if ((historyEvent.PlaylistUuid.HasValue && !historyEvent.PlaylistEntryUuid.HasValue) ||
            (!historyEvent.PlaylistUuid.HasValue && historyEvent.PlaylistEntryUuid.HasValue))
        {
            throw new PlaybackHistoryException("invalid_playlist_attribution");
        }

        if ((historyEvent.BlockUuid.HasValue || historyEvent.BlockPosition.HasValue) &&
            !historyEvent.PlaylistUuid.HasValue)
        {
            throw new PlaybackHistoryException("invalid_playlist_attribution");
        }
    }

    private static NormalizedHistoryEvent NormalizeEvent(PlaybackHistoryEventRequest historyEvent)
    {
        return new NormalizedHistoryEvent(
            historyEvent.Platform.Trim().ToLowerInvariant(),
            historyEvent.AppVersion.Trim(),
            historyEvent.DeviceId.Trim());
    }

    private static int NormalizeRecentLimit(string? limit)
    {
        if (limit == null)
        {
            return DefaultRecentLimit;
        }

        if (!int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedLimit) ||
            parsedLimit <= 0 ||
            parsedLimit > MaxRecentLimit)
        {
            throw new PlaybackHistoryException("invalid_history_limit");
        }

        return parsedLimit;
    }

    private static async Task<bool> IsHistoryEnabled(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        Guid userUuid)
    {
        return await connection.QuerySingleAsync<bool>(
            """
            SELECT COALESCE(
                (
                    SELECT CASE
                        WHEN jsonb_typeof(settings -> 'history_enabled') = 'boolean'
                        THEN (settings ->> 'history_enabled')::boolean
                        ELSE TRUE
                    END
                    FROM user_data.user_settings
                    WHERE user_id = @UserUuid
                ),
                TRUE
            )
            """,
            new { UserUuid = userUuid },
            transaction);
    }

    private sealed record NormalizedHistoryEvent(string Platform, string AppVersion, string DeviceId);
}

public sealed class PlaybackHistoryException : Exception
{
    public PlaybackHistoryException(string code)
        : base(code)
    {
        Code = code;
    }

    public string Code { get; }
}
