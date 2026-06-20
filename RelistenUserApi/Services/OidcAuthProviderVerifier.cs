using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Relisten.UserApi.Configuration;

namespace Relisten.UserApi.Services;

public interface IOpenIdConnectConfigurationSource
{
    Task<OpenIdConnectConfiguration> GetConfiguration(string metadataAddress);
    void RequestRefresh(string metadataAddress);
}

public sealed class OpenIdConnectConfigurationSource : IOpenIdConnectConfigurationSource
{
    private readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> _managers = new();

    public Task<OpenIdConnectConfiguration> GetConfiguration(string metadataAddress)
    {
        return Manager(metadataAddress).GetConfigurationAsync(CancellationToken.None);
    }

    public void RequestRefresh(string metadataAddress)
    {
        Manager(metadataAddress).RequestRefresh();
    }

    private ConfigurationManager<OpenIdConnectConfiguration> Manager(string metadataAddress)
    {
        return _managers.GetOrAdd(
            metadataAddress,
            address => new ConfigurationManager<OpenIdConnectConfiguration>(
                address,
                new OpenIdConnectConfigurationRetriever()));
    }
}

public sealed class OidcAuthProviderVerifier : IAuthProviderVerifier
{
    private readonly IOpenIdConnectConfigurationSource _configurationSource;
    private readonly UserAuthOptions _options;
    private readonly JwtSecurityTokenHandler _tokenHandler = new() { MapInboundClaims = false };

    public OidcAuthProviderVerifier(
        IOptions<UserAuthOptions> options,
        IOpenIdConnectConfigurationSource configurationSource)
    {
        _options = options.Value;
        _configurationSource = configurationSource;
    }

    public async Task<ProviderIdentity> Verify(string provider, string providerToken, string? nonce)
    {
        var normalizedProvider = NormalizeProvider(provider);
        var providerOptions = OptionsForProvider(normalizedProvider);
        var audiences = providerOptions.Audiences
            .Where(audience => !string.IsNullOrWhiteSpace(audience))
            .Select(audience => audience!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var algorithms = providerOptions.ValidAlgorithms
            .Where(algorithm => !string.IsNullOrWhiteSpace(algorithm))
            .Select(algorithm => algorithm.Trim())
            .ToArray();
        if (audiences.Length == 0 ||
            algorithms.Length == 0 ||
            string.IsNullOrWhiteSpace(providerOptions.MetadataAddress))
        {
            throw new UserAuthException("provider_not_configured");
        }

        if (string.IsNullOrWhiteSpace(nonce))
        {
            throw new UserAuthException("nonce_required");
        }

        var configuration = await _configurationSource.GetConfiguration(providerOptions.MetadataAddress);
        var principal = await ValidateToken(providerToken, providerOptions, audiences, algorithms, configuration);
        var tokenNonce = principal.FindFirstValue("nonce");
        if (!string.Equals(tokenNonce, nonce, StringComparison.Ordinal))
        {
            throw new UserAuthException("invalid_nonce");
        }

        var subject = principal.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new UserAuthException("invalid_provider_token");
        }

        return new ProviderIdentity(
            normalizedProvider,
            subject,
            OptionalClaim(principal, "name"));
    }

    private async Task<ClaimsPrincipal> ValidateToken(
        string providerToken,
        ProviderTokenValidationOptions providerOptions,
        string[] audiences,
        string[] algorithms,
        OpenIdConnectConfiguration configuration)
    {
        var validationParameters = TokenValidationParameters(audiences, algorithms, configuration);

        try
        {
            return _tokenHandler.ValidateToken(providerToken, validationParameters, out _);
        }
        catch (SecurityTokenSignatureKeyNotFoundException)
        {
            _configurationSource.RequestRefresh(providerOptions.MetadataAddress!);
            var refreshed = await _configurationSource.GetConfiguration(providerOptions.MetadataAddress!);

            try
            {
                return _tokenHandler.ValidateToken(
                    providerToken,
                    TokenValidationParameters(audiences, algorithms, refreshed),
                    out _);
            }
            catch (Exception ex) when (ex is SecurityTokenException or ArgumentException)
            {
                throw new UserAuthException("invalid_provider_token");
            }
        }
        catch (Exception ex) when (ex is SecurityTokenException or ArgumentException)
        {
            throw new UserAuthException("invalid_provider_token");
        }
    }

    private static TokenValidationParameters TokenValidationParameters(
        string[] audiences,
        string[] algorithms,
        OpenIdConnectConfiguration configuration)
    {
        return new TokenValidationParameters
        {
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateAudience = true,
            ValidAudiences = audiences,
            ValidateIssuer = true,
            ValidIssuer = configuration.Issuer,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = configuration.SigningKeys,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
            ValidAlgorithms = algorithms
        };
    }

    private ProviderTokenValidationOptions OptionsForProvider(string provider)
    {
        return provider switch
        {
            "apple" => _options.Apple,
            "google" => _options.Google,
            _ => throw new UserAuthException("provider_not_supported")
        };
    }

    private static string NormalizeProvider(string provider)
    {
        return string.IsNullOrWhiteSpace(provider)
            ? string.Empty
            : provider.Trim().ToLowerInvariant();
    }

    private static string? OptionalClaim(ClaimsPrincipal principal, string claimType)
    {
        var value = principal.FindFirstValue(claimType);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
