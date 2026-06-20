using Newtonsoft.Json.Linq;

namespace Relisten.UserApi.Models;

public sealed class FavoriteResponse
{
    public required string EntityType { get; init; }
    public required Guid EntityUuid { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed class UserSettingsResponse
{
    public required JObject Settings { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

public sealed class UpdateUserSettingsRequest
{
    public required JObject Settings { get; init; }
}

public sealed class UserLibrarySyncResponse
{
    public required IReadOnlyList<UserLibrarySyncChangeResponse> Changes { get; init; }
    public required IReadOnlyList<UserLibraryTombstoneResponse> Tombstones { get; init; }
    public required string NextCursor { get; init; }
}

public sealed class UserLibrarySyncChangeResponse
{
    public required string ResourceType { get; init; }
    public FavoriteResponse? Favorite { get; init; }
    public UserSettingsResponse? Settings { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed class UserLibraryTombstoneResponse
{
    public required string ResourceType { get; init; }
    public string? EntityType { get; init; }
    public Guid? EntityUuid { get; init; }
    public required DateTimeOffset DeletedAt { get; init; }
}

public sealed class UserLibrarySyncErrorResponse
{
    public required string Error { get; init; }
}
