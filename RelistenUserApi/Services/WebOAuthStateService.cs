using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Relisten.UserApi.Configuration;

namespace Relisten.UserApi.Services;

public sealed record WebOAuthStateData(
    string State,
    string Nonce,
    string ReturnUrl,
    DateTimeOffset CreatedAt);

public sealed class WebOAuthStateService
{
    private readonly IDataProtector _protector;
    private readonly UserAuthOptions _options;

    public WebOAuthStateService(
        IDataProtectionProvider dataProtectionProvider,
        IOptions<UserAuthOptions> options)
    {
        _protector = dataProtectionProvider.CreateProtector("Relisten.UserApi.WebOAuthState.v1");
        _options = options.Value;
    }

    public (WebOAuthStateData State, string ProtectedState, DateTimeOffset ExpiresAt) Create(string returnUrl)
    {
        var now = DateTimeOffset.UtcNow;
        var state = new WebOAuthStateData(
            RandomToken(),
            RandomToken(),
            returnUrl,
            now);
        var protectedState = _protector.Protect(JsonConvert.SerializeObject(state));
        return (
            state,
            protectedState,
            now.AddMinutes(Math.Max(1, _options.Web.OAuthStateMinutes)));
    }

    public WebOAuthStateData Validate(string? protectedState, string? returnedState)
    {
        if (string.IsNullOrWhiteSpace(protectedState) || string.IsNullOrWhiteSpace(returnedState))
        {
            throw new UserAuthException("invalid_oauth_state");
        }

        WebOAuthStateData state;
        try
        {
            state = JsonConvert.DeserializeObject<WebOAuthStateData>(_protector.Unprotect(protectedState))
                ?? throw new UserAuthException("invalid_oauth_state");
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidOperationException or FormatException or JsonException)
        {
            throw new UserAuthException("invalid_oauth_state");
        }

        if (!string.Equals(state.State, returnedState, StringComparison.Ordinal))
        {
            throw new UserAuthException("invalid_oauth_state");
        }

        var maxAge = TimeSpan.FromMinutes(Math.Max(1, _options.Web.OAuthStateMinutes));
        if (state.CreatedAt.Add(maxAge) < DateTimeOffset.UtcNow)
        {
            throw new UserAuthException("invalid_oauth_state");
        }

        return state;
    }

    private static string RandomToken()
    {
        return WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    }
}
