namespace Relisten.UserApi.Models;

public sealed class CreatePlaylistRequest
{
    public Guid? PlaylistUuid { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
}

public sealed class PlaylistResponse
{
    public required Guid PlaylistUuid { get; init; }
    public required string ShortId { get; init; }
    public required Guid OwnerUserUuid { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string Visibility { get; init; }
    public required long CurrentRevision { get; init; }
    public required IReadOnlyList<PlaylistEntryResponse> Entries { get; init; }
}

public sealed class PlaylistEntryResponse
{
    public required Guid PlaylistEntryUuid { get; init; }
    public required Guid SourceTrackUuid { get; init; }
    public Guid? BlockUuid { get; init; }
    public int? BlockPosition { get; init; }
    public required string Position { get; init; }
    public required Guid AddedByUserUuid { get; init; }
}

public sealed class PlaylistOperationRequest
{
    public required string Op { get; init; }
    public required Guid IdempotencyKey { get; init; }
    public long? BaseRevision { get; init; }
    public Guid? EntryUuid { get; init; }
    public Guid? SourceTrackUuid { get; init; }
    public Guid? BlockUuid { get; init; }
    public List<Guid>? EntryUuids { get; init; }
    public List<Guid>? SourceTrackUuids { get; init; }
    public PlaylistPlacementRequest? Placement { get; init; }
}

public sealed class PlaylistPlacementRequest
{
    public Guid? AfterEntryUuid { get; init; }
    public Guid? BeforeEntryUuid { get; init; }
    public string? PositionHint { get; init; }
}

public sealed class PlaylistOperationResponse
{
    public required long ResultRevision { get; init; }
    public required string ResultStatus { get; init; }
    public required PlaylistResponse Playlist { get; init; }
}

public sealed class PlaylistErrorResponse
{
    public required string Error { get; init; }
}
