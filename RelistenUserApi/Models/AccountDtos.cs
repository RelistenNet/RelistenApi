namespace Relisten.UserApi.Models;

public sealed class AccountExportResponse
{
    public required DateTimeOffset ExportedAt { get; init; }
    public required CurrentUserResponse User { get; init; }
    public required IReadOnlyList<AccountAuthMethodExportResponse> AuthMethods { get; init; }
    public required IReadOnlyList<AccountSessionExportResponse> Sessions { get; init; }
    public required IReadOnlyList<AccountFavoriteExportResponse> Favorites { get; init; }
    public required UserSettingsResponse Settings { get; init; }
    public required IReadOnlyList<PlaylistResponse> Playlists { get; init; }
    public required IReadOnlyList<AccountPlaybackHistoryExportResponse> PlaybackHistory { get; init; }
}

public sealed class AccountAuthMethodExportResponse
{
    public required Guid AuthMethodUuid { get; init; }
    public required string Provider { get; init; }
    public required string ProviderSubject { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed class AccountSessionExportResponse
{
    public required Guid SessionUuid { get; init; }
    public required string DeviceId { get; init; }
    public string? DeviceName { get; init; }
    public required string Platform { get; init; }
    public required DateTimeOffset LastUsedAt { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
}

public sealed class AccountFavoriteExportResponse
{
    public required string EntityType { get; init; }
    public required Guid EntityUuid { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? DeletedAt { get; init; }
}

public sealed class AccountPlaybackHistoryExportResponse
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
    public required string Platform { get; init; }
    public required string AppVersion { get; init; }
    public required string DeviceId { get; init; }
}

public sealed class AccountDeletionErrorResponse
{
    public required string Error { get; init; }
}
