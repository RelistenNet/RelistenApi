namespace RelistenUserService.Identity.Entities;

public sealed class User
{
    public Guid Id { get; set; }
    public string Status { get; set; } = UserStatuses.Active;
    public string Username { get; set; } = "";
    public long UsernameVersion { get; set; } = 1;
    public DateTimeOffset? UsernameReviewedAt { get; set; }
    public DateTimeOffset? UsernameChangedAt { get; set; }
    public int SecurityVersion { get; set; } = 1;
    public long LifecycleGeneration { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset LastLoginAt { get; set; }

    public ICollection<ExternalIdentity> ExternalIdentities { get; } = [];
    public ICollection<NativeSession> NativeSessions { get; } = [];
}

public static class UserStatuses
{
    public const string Active = "active";
    public const string Disabled = "disabled";
    public const string Deleting = "deleting";
}
