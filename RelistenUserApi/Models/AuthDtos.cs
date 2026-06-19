namespace Relisten.UserApi.Models;

public sealed class ProviderSignInRequest
{
    public required string ProviderToken { get; init; }
    public string? Username { get; init; }
    public string? DisplayName { get; init; }
    public required string DeviceId { get; init; }
    public string? DeviceName { get; init; }
    public required string Platform { get; init; }
}

public sealed class DevelopmentSessionRequest
{
    public required string Username { get; init; }
    public string? DisplayName { get; init; }
    public required string DeviceId { get; init; }
    public string? DeviceName { get; init; }
    public required string Platform { get; init; }
}

public sealed class RefreshTokenRequest
{
    public required string RefreshToken { get; init; }
}

public sealed class LogoutRequest
{
    public required string RefreshToken { get; init; }
}

public sealed class AuthTokenResponse
{
    public required string AccessToken { get; init; }
    public required string RefreshToken { get; init; }
    public required DateTimeOffset AccessTokenExpiresAt { get; init; }
    public required DateTimeOffset RefreshTokenExpiresAt { get; init; }
    public required CurrentUserResponse User { get; init; }
    public required UserSessionResponse Session { get; init; }
}

public sealed class UserSessionResponse
{
    public required Guid SessionUuid { get; init; }
    public required string DeviceId { get; init; }
    public string? DeviceName { get; init; }
    public required string Platform { get; init; }
    public required DateTimeOffset LastUsedAt { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
}

public sealed class AuthErrorResponse
{
    public required string Error { get; init; }
}
