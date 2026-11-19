namespace RelistenUserService.Configuration;

public sealed class AccountsOptions
{
    public const string SectionName = "Accounts";

    public string Issuer { get; init; } = "";
    public string Audience { get; init; } = "https://accounts.relisten.net";
    public string AuthHost { get; init; } = "auth.relisten.net";
    public string AccountsHost { get; init; } = "accounts.relisten.net";
    public string? McpHost { get; init; }
    public string? McpResourceUri { get; init; }
    public string McpCatalogBaseUrl { get; init; } = "https://api.relisten.net/";
    public string[] WebOrigins { get; init; } =
    ["https://relisten.net", "https://web.relisten.localhost:5173"];
    public string[] TrustedProxyNetworks { get; init; } = [];

    /// <summary>Optional projected JSON file containing exact preview origins. Disabled when unset.</summary>
    public string? PreviewOriginsFile { get; init; }
    public bool EnableDevelopmentPersonas { get; init; }
    public bool EnableExternalProviders { get; init; }
    public bool AllowInsecureHttp { get; init; }
    public bool ApplyMigrationsOnStartup { get; init; }
    public string WebClientSecret { get; init; } = "";
    public string DevelopmentCertificateAuthorityPath { get; init; } = "";
    public GoogleProviderOptions Google { get; init; } = new();
    public AppleProviderOptions Apple { get; init; } = new();
    public string? SigningCertificatePath { get; init; }
    public string? SigningCertificatePassword { get; init; }
    public string? PreviousSigningCertificatePath { get; init; }
    public string? PreviousSigningCertificatePassword { get; init; }
    public string? EncryptionCertificatePath { get; init; }
    public string? EncryptionCertificatePassword { get; init; }
    public string? PreviousEncryptionCertificatePath { get; init; }
    public string? PreviousEncryptionCertificatePassword { get; init; }
    public string? DataProtectionCertificatePath { get; init; }
    public string? DataProtectionCertificatePassword { get; init; }
    public string? PreviousDataProtectionCertificatePath { get; init; }
    public string? PreviousDataProtectionCertificatePassword { get; init; }
}
