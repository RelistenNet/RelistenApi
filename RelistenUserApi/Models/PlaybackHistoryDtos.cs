namespace Relisten.UserApi.Models;

public sealed class PlaybackHistoryBatchRequest
{
    public required IReadOnlyList<PlaybackHistoryEventRequest> Events { get; init; }
}

public sealed class PlaybackHistoryEventRequest
{
    public required Guid ClientEventUuid { get; init; }
    public required Guid SourceTrackUuid { get; init; }
    public required Guid SourceUuid { get; init; }
    public Guid? PlaylistUuid { get; init; }
    public Guid? PlaylistEntryUuid { get; init; }
    public Guid? BlockUuid { get; init; }
    public int? BlockPosition { get; init; }
    public required DateTimeOffset PlayedAt { get; init; }
    public required string Platform { get; init; }
    public required string AppVersion { get; init; }
    public required string DeviceId { get; init; }
}

public sealed class PlaybackHistoryBatchResponse
{
    public required bool HistoryEnabled { get; init; }
    public required int AcceptedCount { get; init; }
    public required int DuplicateCount { get; init; }
    public required IReadOnlyList<PlaybackHistoryEventResultResponse> Results { get; init; }
}

public sealed class PlaybackHistoryRecentResponse
{
    public required IReadOnlyList<PlaybackHistoryItemResponse> Items { get; init; }
}

public sealed class PlaybackHistoryItemResponse
{
    public required Guid HistoryUuid { get; init; }
    public required Guid ClientEventUuid { get; init; }
    public required Guid SourceTrackUuid { get; init; }
    public required Guid SourceUuid { get; init; }
    public Guid? PlaylistUuid { get; init; }
    public Guid? PlaylistEntryUuid { get; init; }
    public Guid? BlockUuid { get; init; }
    public int? BlockPosition { get; init; }
    public required DateTimeOffset PlayedAt { get; init; }
}

public sealed class PlaybackHistoryEventResultResponse
{
    public required Guid ClientEventUuid { get; init; }
    public required string Status { get; init; }
}

public sealed class PlaybackHistoryErrorResponse
{
    public required string Error { get; init; }
}
