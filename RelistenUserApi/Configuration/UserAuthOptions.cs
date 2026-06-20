namespace Relisten.UserApi.Configuration;

public sealed class UserAuthOptions
{
    public const string SectionName = "UserAuth";

    public string? AccessTokenSigningKey { get; init; }
    public string[] AllowedProviders { get; init; } = ["apple", "google"];
    public ProviderTokenValidationOptions Apple { get; init; } = new()
    {
        MetadataAddress = "https://appleid.apple.com/.well-known/openid-configuration"
    };
    public ProviderTokenValidationOptions Google { get; init; } = new()
    {
        MetadataAddress = "https://accounts.google.com/.well-known/openid-configuration"
    };
    public int RecentReauthenticationWindowMinutes { get; init; } = 15;
}

public sealed class ProviderTokenValidationOptions
{
    public string? MetadataAddress { get; init; }
    public string[] Audiences { get; init; } = [];
    public string[] ValidAlgorithms { get; init; } = ["RS256"];
}
