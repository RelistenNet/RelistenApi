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
        MetadataAddress = "https://accounts.google.com/.well-known/openid-configuration",
        AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth",
        TokenEndpoint = "https://oauth2.googleapis.com/token"
    };
    public WebSessionOptions Web { get; init; } = new();
    public int RecentReauthenticationWindowMinutes { get; init; } = 15;
}

public sealed class ProviderTokenValidationOptions
{
    public string? MetadataAddress { get; init; }
    public string[] Audiences { get; init; } = [];
    public string[] ValidAlgorithms { get; init; } = ["RS256"];
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public string? RedirectUri { get; init; }
    public string? AuthorizationEndpoint { get; init; }
    public string? TokenEndpoint { get; init; }
}

public sealed class WebSessionOptions
{
    public string SessionCookieName { get; init; } = "relisten_user_session";
    public string CsrfCookieName { get; init; } = "relisten_user_csrf";
    public string OAuthStateCookieName { get; init; } = "relisten_oauth_state";
    public string CsrfHeaderName { get; init; } = "X-Relisten-Csrf";
    public string DefaultReturnUrl { get; init; } = "/";
    public string[] AllowedOrigins { get; init; } = [];
    public bool SecureCookies { get; init; } = true;
    public int SessionCookieDays { get; init; } = 365;
    public int OAuthStateMinutes { get; init; } = 15;
}
