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

    /// <summary>Whether iOS should offer Apple reauthorization before deleting this account.</summary>
    public async Task<bool> CanRevokeNativeAsync(
        UserId userId,
        CancellationToken cancellationToken
    ) =>
        runtime.Options.Apple is { Enabled: true } provider
        && !string.IsNullOrWhiteSpace(provider.NativeClientId)
        && !string.IsNullOrWhiteSpace(provider.PrivateKeyPath)
        && await LinkedSubjects(userId).AnyAsync(cancellationToken);

    public async Task TryRevokeAsync(
        UserId userId,
        string? browserCode,
        string? nativeCode,
        CancellationToken cancellationToken
    )
    {
        var provider = runtime.Options.Apple;
        var native = !string.IsNullOrWhiteSpace(nativeCode);
        var code = native ? nativeCode : browserCode;
        var clientId = native ? provider.NativeClientId : provider.ClientId;
        if (
            !provider.Enabled
            || string.IsNullOrWhiteSpace(code)
            || string.IsNullOrWhiteSpace(clientId)
        )
        {
            return;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            var subjects = await LinkedSubjects(userId).ToArrayAsync(deadline.Token);
            if (subjects.Length == 0)
            {
                return;
            }

            var redirectUri = native
                ? null
                : new Uri(runtime.Issuer, AuthenticationConstants.AppleCallbackPath).AbsoluteUri;
            var secret = CreateClientSecret(provider, clientId);
            var tokens = await ExchangeCodeAsync(
                code,
                clientId,
                secret,
                redirectUri,
                deadline.Token
            );
            var subject = await ValidateIdentityAsync(
                tokens.IdentityToken,
                clientId,
                deadline.Token
            );
            if (subject is null || !subjects.Contains(subject, StringComparer.Ordinal))
            {
                logger.LogWarning(
                    "Apple revocation skipped because the code did not identify the linked Apple account."
                );
                return;
            }

            await RevokeAsync(clientId, secret, tokens, deadline.Token);
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

    private IQueryable<string> LinkedSubjects(UserId userId) =>
        database
            .ExternalIdentities.Where(identity =>
                identity.UserId == userId && identity.Issuer == AuthenticationConstants.AppleIssuer
            )
            .Select(identity => identity.ProviderSubject);

    private string CreateClientSecret(AppleProviderOptions provider, string clientId)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var key = AppleSigningKey.Load(provider);
        using var algorithm = key.ECDsa;
        return new JsonWebTokenHandler().CreateToken(
            new SecurityTokenDescriptor
            {
                Issuer = provider.TeamId,
                Audience = AuthenticationConstants.AppleIssuer,
                Subject = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, clientId)]),
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
        string? redirectUri,
        CancellationToken cancellationToken
    )
    {
        var fields = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["client_secret"] = secret,
            ["code"] = code,
        };
        // Apple requires this field only when the authorization request supplied one.
        if (redirectUri is not null)
        {
            fields["redirect_uri"] = redirectUri;
        }

        using var form = new FormUrlEncodedContent(fields);
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
