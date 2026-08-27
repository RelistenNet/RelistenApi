namespace RelistenUserService.Identity.Entities;

public sealed class IdentitySession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Purpose { get; set; } = "";
    // PostgreSQL stores only the validator hash. A database disclosure cannot use the
    // hash as the raw cookie validator for an authenticated browser request.
    public byte[] ValidatorHash { get; set; } = [];
    public int SecurityVersion { get; set; }
    public DateTimeOffset AuthenticatedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset SlidingExpiresAt { get; set; }
    public DateTimeOffset AbsoluteExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? AuthSsoSessionId { get; set; }
    public string? WebOrigin { get; set; }
    public IdentitySessionCapabilities Capabilities { get; set; }

    public User User { get; set; } = null!;
    public IdentitySession? AuthSsoSession { get; set; }
    public ICollection<IdentitySession> WebSessions { get; } = [];
}

public static class IdentitySessionPurposes
{
    public const string AuthSso = "auth_sso";
    public const string Web = "web";
}

[Flags]
public enum IdentitySessionCapabilities
{
    None = 0,
    AccountProfileRead = 1,
    LibraryRead = 2,
    FavoriteMutation = 4,
    AllWeb = AccountProfileRead | LibraryRead | FavoriteMutation
}
