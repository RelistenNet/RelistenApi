namespace Relisten.UserApi.Configuration;

public sealed class UserAuthOptions
{
    public const string SectionName = "UserAuth";

    public string? AccessTokenSigningKey { get; init; }
    public string[] AllowedProviders { get; init; } = ["apple", "google"];
}
