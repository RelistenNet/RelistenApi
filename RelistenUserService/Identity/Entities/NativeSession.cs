namespace RelistenUserService.Identity.Entities;

public sealed class NativeSession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid AuthorizationId { get; set; }
    public string ClientId { get; set; } = "";
    public string? DeviceName { get; set; }
    public string? Platform { get; set; }
    public int SecurityVersion { get; set; }
    public DateTimeOffset AuthenticatedAt { get; set; }
    public DateTimeOffset LastUsedAt { get; set; }
    public DateTimeOffset AbsoluteExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public User User { get; set; } = null!;
}
