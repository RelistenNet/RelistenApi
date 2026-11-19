using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using RelistenUserService.Authentication;
using RelistenUserService.Configuration;
using RelistenUserService.Persistence;

namespace RelistenUserService.Identity.Deletion;

/// <summary>Revokes Apple credentials for the account being deleted, within one request budget.</summary>
public sealed class AppleTokenRevocation(
    HttpClient client,
    AccountsDbContext database,
    AccountsRuntimeConfiguration runtime,
    TimeProvider clock,
    ILogger<AppleTokenRevocation> logger
)
{
    private static readonly Uri AppleIssuer = new(AuthenticationConstants.AppleIssuer);

    public async Task TryRevokeAsync(
        UserId userId,
        string? code,
        CancellationToken cancellationToken
    )
    {
        var provider = runtime.Options.Apple;
        if (string.IsNullOrWhiteSpace(code) || !provider.Enabled)
        {
            return;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            var subjects = await database
                .ExternalIdentities.Where(identity =>
                    identity.UserId == userId
                    && identity.Issuer == AuthenticationConstants.AppleIssuer
                )
                .Select(identity => identity.ProviderSubject)
                .ToArrayAsync(deadline.Token);
            if (subjects.Length == 0)
            {
                return;
            }

            var secret = CreateClientSecret(provider);
            var tokens = await ExchangeCodeAsync(code, provider.ClientId, secret, deadline.Token);
            var subject = await ValidateIdentityAsync(
                tokens.IdentityToken,
                provider.ClientId,
                deadline.Token
            );
            if (subject is null || !subjects.Contains(subject, StringComparer.Ordinal))
            {
                logger.LogWarning(
                    "Apple revocation skipped because the code did not identify the linked Apple account."
                );
                return;
            }

            await RevokeAsync(provider.ClientId, secret, tokens, deadline.Token);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            // Apple's response, parser errors and request data may contain credentials.
            logger.LogWarning(
                "Apple revocation could not be confirmed ({ErrorType}).",
                error.GetType().Name
            );
        }
    }

    private string CreateClientSecret(AppleProviderOptions provider)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var key = AppleSigningKey.Load(provider);
        using var algorithm = key.ECDsa;
        return new JsonWebTokenHandler().CreateToken(
            new SecurityTokenDescriptor
            {
                Issuer = provider.TeamId,
                Audience = AuthenticationConstants.AppleIssuer,
                Subject = new ClaimsIdentity([
                    new Claim(JwtRegisteredClaimNames.Sub, provider.ClientId),
                ]),
                IssuedAt = now,
                Expires = now.AddMinutes(5),
                SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.EcdsaSha256)
                {
                    CryptoProviderFactory = new CryptoProviderFactory
                    {
                        CacheSignatureProviders = false,
                    },
                },
            }
        );
    }

    private async Task<AppleTokens> ExchangeCodeAsync(
        string code,
        string clientId,
        string secret,
        CancellationToken cancellationToken
    )
    {
        // The configured registration is the browser Services ID.
        // TODO: For iOS native codes, use its App ID and eligible signing key and omit redirect_uri.
        using var form = new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = clientId,
                ["client_secret"] = secret,
                ["code"] = code,
                ["redirect_uri"] = new Uri(
                    runtime.Issuer,
                    AuthenticationConstants.AppleCallbackPath
                ).AbsoluteUri,
            }
        );
        using var response = await client.PostAsync(
            new Uri(AppleIssuer, "/auth/token"),
            form,
            cancellationToken
        );
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AppleTokens>(cancellationToken)
            ?? throw new JsonException("Apple returned no tokens.");
    }

    private async Task<string?> ValidateIdentityAsync(
        string? identityToken,
        string clientId,
        CancellationToken cancellationToken
    )
    {
        var handler = new JsonWebTokenHandler();
        if (identityToken is null || !handler.CanReadToken(identityToken))
        {
            return null;
        }

        var keys = new JsonWebKeySet(
            await client.GetStringAsync(new Uri(AppleIssuer, "/auth/keys"), cancellationToken)
        );
        var validation = await handler.ValidateTokenAsync(
            identityToken,
            new TokenValidationParameters
            {
                ValidIssuer = AuthenticationConstants.AppleIssuer,
                ValidAudience = clientId,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                ValidateIssuerSigningKey = true,
                TryAllIssuerSigningKeys = false,
                IssuerSigningKeys = keys.GetSigningKeys(),
            }
        );
        return validation.IsValid
            ? validation.ClaimsIdentity.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            : null;
    }

    private async Task RevokeAsync(
        string clientId,
        string secret,
        AppleTokens tokens,
        CancellationToken cancellationToken
    )
    {
        var token =
            tokens.RefreshToken
            ?? tokens.AccessToken
            ?? throw new JsonException("Apple returned no revocable token.");
        using var form = new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = secret,
                ["token"] = token,
                ["token_type_hint"] = tokens.RefreshToken is not null
                    ? "refresh_token"
                    : "access_token",
            }
        );
        using var response = await client.PostAsync(
            new Uri(AppleIssuer, "/auth/revoke"),
            form,
            cancellationToken
        );
        response.EnsureSuccessStatusCode();
    }

    private sealed record AppleTokens(
        [property: JsonPropertyName("id_token")] string? IdentityToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("access_token")] string? AccessToken
    );
}
