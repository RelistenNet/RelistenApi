namespace Relisten.UserApi.Models;

public sealed class PlaylistRecord
{
    public required Guid PlaylistUuid { get; init; }
    public required string ShortId { get; init; }
    public required Guid OwnerUserUuid { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string Visibility { get; init; }
    public required long CurrentRevision { get; init; }
    public DateTimeOffset? ArchivedAt { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed class PlaylistEntryRecord
{
    public required Guid PlaylistEntryUuid { get; init; }
    public required Guid PlaylistUuid { get; init; }
    public required Guid SourceTrackUuid { get; init; }
    public Guid? BlockUuid { get; init; }
    public int? BlockPosition { get; init; }
    public required string Position { get; init; }
    public required Guid AddedByUserUuid { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed class PlaylistSnapshot
{
    public required PlaylistRecord Playlist { get; init; }
    public required IReadOnlyList<PlaylistEntryRecord> Entries { get; init; }
}
