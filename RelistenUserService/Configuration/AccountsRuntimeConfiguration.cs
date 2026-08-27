using System.Net;

namespace RelistenUserService.Configuration;

public sealed record AccountsRuntimeConfiguration(
    AccountsOptions Options,
    Uri Issuer,
    bool AllowLoopbackHttp,
    IReadOnlyList<IPNetwork> TrustedProxyNetworks)
{
    private static readonly HashSet<string> SupportedWebOrigins = new(
        [
            "https://relisten.net",
            "https://web.relisten.localhost:5173"
        ],
        StringComparer.Ordinal);

    public IReadOnlyList<string> WebOrigins { get; init; } = Options.WebOrigins;

    public static AccountsRuntimeConfiguration Create(
        AccountsOptions options,
        IHostEnvironment environment)
    {
        if (!Uri.TryCreate(options.Issuer, UriKind.Absolute, out var issuer))
        {
            throw new InvalidOperationException("Accounts:Issuer must be an absolute URI.");
        }

        if (issuer.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                "The Relisten issuer must use HTTPS in every runtime profile.");
        }

        if (options.EnableDevelopmentPersonas
            && (!environment.IsDevelopment()
                || issuer != new Uri("https://auth.relisten.localhost:5443")
                || options.AuthHost != "auth.relisten.localhost:5443"
                || options.AccountsHost != "accounts.relisten.localhost:5443"))
        {
            throw new InvalidOperationException(
                "Development personas require the exact local HTTPS auth and accounts hosts.");
        }

        if (options.EnableDevelopmentPersonas && options.EnableExternalProviders)
        {
            throw new InvalidOperationException(
                "Development personas and external identity providers cannot both be enabled.");
        }

        if (options.EnableDevelopmentPersonas)
        {
            Require(
                options.DevelopmentCertificateAuthorityPath,
                "Accounts:DevelopmentCertificateAuthorityPath");
        }
        else if (!string.IsNullOrWhiteSpace(options.DevelopmentCertificateAuthorityPath))
        {
            throw new InvalidOperationException(
                "Accounts:DevelopmentCertificateAuthorityPath is allowed only for development personas.");
        }

        if (options.EnableExternalProviders)
        {
            ValidateExternalProviders(options, issuer, environment);
        }

        if (options.AllowInsecureHttp)
        {
            throw new InvalidOperationException(
                "Accounts:AllowInsecureHttp is not supported by browser-session profiles.");
        }

        if (string.IsNullOrWhiteSpace(options.Audience))
        {
            throw new InvalidOperationException("Accounts:Audience is required.");
        }

        Require(options.WebClientSecret, "Accounts:WebClientSecret");

        var trustedProxyNetworks = options.TrustedProxyNetworks
            .Select(ParseNetwork)
            .ToArray();
        if (!environment.IsDevelopment() && trustedProxyNetworks.Length == 0)
        {
            throw new InvalidOperationException(
                "Accounts:TrustedProxyNetworks must contain the cluster ingress CIDR outside Development.");
        }

        var webOrigins = options.WebOrigins
            .Select(ParseWebOrigin)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (webOrigins.Length == 0)
        {
            throw new InvalidOperationException(
                "Accounts:WebOrigins must contain at least one exact HTTPS origin.");
        }

        return new AccountsRuntimeConfiguration(
            options,
            issuer,
            AllowLoopbackHttp: false,
            trustedProxyNetworks)
        {
            WebOrigins = webOrigins
        };
    }

    private static void ValidateExternalProviders(
        AccountsOptions options,
        Uri issuer,
        IHostEnvironment environment)
    {
        if (environment.IsProduction()
            && (issuer != new Uri("https://auth.relisten.net")
                || options.AuthHost != "auth.relisten.net"
                || options.AccountsHost != "accounts.relisten.net"))
        {
            throw new InvalidOperationException(
                "Production external identity providers require the exact Relisten hosts.");
        }

        Require(options.Google.ClientId, "Accounts:Google:ClientId");
        Require(options.Google.ClientSecret, "Accounts:Google:ClientSecret");
        Require(options.Apple.ClientId, "Accounts:Apple:ClientId");
        Require(options.Apple.TeamId, "Accounts:Apple:TeamId");
        Require(options.Apple.KeyId, "Accounts:Apple:KeyId");
        Require(options.Apple.PrivateKeyPath, "Accounts:Apple:PrivateKeyPath");
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{name} is required.");
        }
    }

    private static IPNetwork ParseNetwork(string value) =>
        IPNetwork.TryParse(value, out var network)
            ? network
            : throw new InvalidOperationException(
                $"Accounts:TrustedProxyNetworks contains invalid CIDR '{value}'.");

    private static string ParseWebOrigin(string value)
    {
        if (!SupportedWebOrigins.Contains(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out var origin)
            || origin.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(origin.UserInfo)
            || origin.AbsolutePath != "/"
            || !string.IsNullOrEmpty(origin.Query)
            || !string.IsNullOrEmpty(origin.Fragment)
            || !string.Equals(
                value,
                origin.GetLeftPart(UriPartial.Authority),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Accounts:WebOrigins contains unsupported exact HTTPS origin '{value}'.");
        }

        return value;
    }
}
