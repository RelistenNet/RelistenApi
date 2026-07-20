namespace RelistenUserService.Identity;

public sealed record ExternalIdentityProfile(
    string Issuer,
    string Subject,
    string? Email,
    bool? EmailVerified,
    bool? EmailIsPrivateRelay);
