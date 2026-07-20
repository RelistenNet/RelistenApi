using System.Net;

namespace RelistenUserService.Configuration;

public sealed record AccountsRuntimeConfiguration(
    AccountsOptions Options,
    Uri Issuer,
    bool AllowLoopbackHttp,
    IReadOnlyList<IPNetwork> TrustedProxyNetworks)
{
    public static AccountsRuntimeConfiguration Create(
        AccountsOptions options,
        IHostEnvironment environment)
    {
        if (!Uri.TryCreate(options.Issuer, UriKind.Absolute, out var issuer))
        {
            throw new InvalidOperationException("Accounts:Issuer must be an absolute URI.");
        }

        var isLoopback = issuer.IsLoopback;
        var allowLoopbackHttp = environment.IsDevelopment()
            && isLoopback
            && issuer.Scheme == Uri.UriSchemeHttp
            && options.AllowInsecureHttp;

        if (issuer.Scheme != Uri.UriSchemeHttps && !allowLoopbackHttp)
        {
            throw new InvalidOperationException(
                "The Relisten issuer must use HTTPS except for a Development loopback issuer.");
        }

        if (options.EnableDevelopmentPersonas
            && (!environment.IsDevelopment() || !isLoopback))
        {
            throw new InvalidOperationException(
                "Development personas require the Development environment and a loopback issuer.");
        }

        if (options.EnableDevelopmentPersonas && options.EnableExternalProviders)
        {
            throw new InvalidOperationException(
                "Development personas and external identity providers cannot both be enabled.");
        }

        if (options.EnableExternalProviders)
        {
            ValidateExternalProviders(options, isLoopback);
        }

        if (options.AllowInsecureHttp && !allowLoopbackHttp)
        {
            throw new InvalidOperationException(
                "Accounts:AllowInsecureHttp requires Development and an HTTP loopback issuer.");
        }

        if (string.IsNullOrWhiteSpace(options.Audience))
        {
            throw new InvalidOperationException("Accounts:Audience is required.");
        }

        var trustedProxyNetworks = options.TrustedProxyNetworks
            .Select(ParseNetwork)
            .ToArray();
        if (!environment.IsDevelopment() && trustedProxyNetworks.Length == 0)
        {
            throw new InvalidOperationException(
                "Accounts:TrustedProxyNetworks must contain the cluster ingress CIDR outside Development.");
        }

        return new AccountsRuntimeConfiguration(
            options,
            issuer,
            allowLoopbackHttp,
            trustedProxyNetworks);
    }

    private static void ValidateExternalProviders(AccountsOptions options, bool isLoopback)
    {
        if (isLoopback)
        {
            throw new InvalidOperationException(
                "External identity providers require the registered HTTPS auth host.");
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
            throw new InvalidOperationException($"{name} is required when external providers are enabled.");
        }
    }

    private static IPNetwork ParseNetwork(string value) =>
        IPNetwork.TryParse(value, out var network)
            ? network
            : throw new InvalidOperationException(
                $"Accounts:TrustedProxyNetworks contains invalid CIDR '{value}'.");
}
