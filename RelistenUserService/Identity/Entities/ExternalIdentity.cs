namespace RelistenUserService.Identity.Entities;

public sealed class ExternalIdentity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Issuer { get; set; } = "";
    public string ProviderSubject { get; set; } = "";
    public string? EmailAtProvider { get; set; }
    public bool? EmailVerifiedAtProvider { get; set; }
    public bool? EmailIsPrivateRelay { get; set; }
    public DateTimeOffset? EmailObservedAt { get; set; }
    public DateTimeOffset LastLoginAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public User User { get; set; } = null!;
}
