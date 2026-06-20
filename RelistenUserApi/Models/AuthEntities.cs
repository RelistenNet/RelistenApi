namespace Relisten.UserApi.Models;

public sealed class UserAccount
{
    public required Guid UserUuid { get; init; }
    public required string Username { get; init; }
    public required string DisplayName { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed class UserAuthMethod
{
    public required Guid AuthMethodUuid { get; init; }
    public required Guid UserUuid { get; init; }
    public required string Provider { get; init; }
    public required string ProviderSubject { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed class UserSession
{
    public required Guid SessionUuid { get; init; }
    public required Guid UserUuid { get; init; }
    public required string DeviceId { get; init; }
    public string? DeviceName { get; init; }
    public required string Platform { get; init; }
    public required DateTimeOffset LastUsedAt { get; set; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ReauthenticatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class RefreshTokenRecord
{
    public required Guid RefreshTokenUuid { get; init; }
    public required Guid SessionUuid { get; init; }
    public required string Selector { get; init; }
    public required string SecretHash { get; init; }
    public required RefreshTokenStatus Status { get; set; }
    public required DateTimeOffset IssuedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? RotatedAt { get; set; }
    public Guid? ReplacedByTokenUuid { get; set; }
    public DateTimeOffset? ReuseDetectedAt { get; set; }
}

public enum RefreshTokenStatus
{
    Active,
    Rotated,
    Revoked,
    ReuseDetected
}
