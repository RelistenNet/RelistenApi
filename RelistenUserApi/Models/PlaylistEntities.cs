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

public sealed class PlaylistShareTokenRecord
{
    public required Guid ShareTokenUuid { get; init; }
    public required Guid PlaylistUuid { get; init; }
    public required Guid CreatedByUserUuid { get; init; }
    public required string Role { get; init; }
    public required string TokenHash { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed class PlaylistMobileAccessGrantRecord
{
    public required Guid MobileAccessGrantUuid { get; init; }
    public required Guid PlaylistUuid { get; init; }
    public Guid? SourceShareTokenUuid { get; init; }
    public required string DeviceId { get; init; }
    public required string Platform { get; init; }
    public required string Role { get; init; }
    public required string TokenSelector { get; init; }
    public required string TokenSecretHash { get; init; }
    public required DateTimeOffset IssuedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
}

public sealed class PlaylistAccess
{
    public required PlaylistSnapshot Snapshot { get; init; }
    public required PlaylistViewerStateResponse ViewerState { get; init; }
}

public sealed record PlaylistMobileGrantCredential(string Token, string DeviceId);
