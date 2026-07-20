namespace RelistenUserService.Configuration;

public sealed class GoogleProviderOptions
{
    public string ClientId { get; init; } = "";
    public string ClientSecret { get; init; } = "";
}

public sealed class AppleProviderOptions
{
    public string ClientId { get; init; } = "";
    public string TeamId { get; init; } = "";
    public string KeyId { get; init; } = "";
    public string PrivateKeyPath { get; init; } = "";
}
