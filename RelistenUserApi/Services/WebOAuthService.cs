using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Relisten.UserApi.Configuration;

namespace Relisten.UserApi.Services;

public sealed record WebOAuthStartResult(
    string AuthorizationUrl,
    string ProtectedState,
    DateTimeOffset StateExpiresAt);

public sealed record WebOAuthCallbackResult(
    UserAuthSessionResult Session,
    string ReturnUrl);

public sealed class WebOAuthService
{
    private readonly IAuthProviderVerifier _providerVerifier;
    private readonly IWebOAuthCodeExchanger _codeExchanger;
    private readonly WebOAuthStateService _stateService;
    private readonly UserAuthService _authService;
    private readonly UserAuthOptions _options;

    public WebOAuthService(
        IAuthProviderVerifier providerVerifier,
        IWebOAuthCodeExchanger codeExchanger,
        WebOAuthStateService stateService,
        UserAuthService authService,
        IOptions<UserAuthOptions> options)
    {
        _providerVerifier = providerVerifier;
        _codeExchanger = codeExchanger;
        _stateService = stateService;
        _authService = authService;
        _options = options.Value;
    }

    public WebOAuthStartResult StartGoogle(HttpRequest request, string? returnUrl)
    {
        var google = _options.Google;
        if (string.IsNullOrWhiteSpace(google.ClientId) ||
            string.IsNullOrWhiteSpace(google.AuthorizationEndpoint))
        {
            throw new UserAuthException("provider_not_configured");
        }

        var clientId = google.ClientId.Trim();
        if (!google.Audiences.Any(audience => string.Equals(
                audience?.Trim(),
                clientId,
                StringComparison.Ordinal)))
        {
            throw new UserAuthException("provider_not_configured");
        }

        var state = _stateService.Create(ValidateReturnUrl(returnUrl));
        var authorizationUrl = QueryHelpers.AddQueryString(
            google.AuthorizationEndpoint,
            new Dictionary<string, string?>
            {
                ["client_id"] = clientId,
                ["redirect_uri"] = ResolveGoogleRedirectUri(request),
                ["response_type"] = "code",
                ["scope"] = "openid profile email",
                ["state"] = state.State.State,
                ["nonce"] = state.State.Nonce
            });

        return new WebOAuthStartResult(
            authorizationUrl,
            state.ProtectedState,
            state.ExpiresAt);
    }

    public async Task<WebOAuthCallbackResult> CompleteGoogleCallback(
        HttpRequest request,
        string? code,
        string? returnedState,
        string? protectedState)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new UserAuthException("authorization_code_required");
        }

        var state = _stateService.Validate(protectedState, returnedState);
        var tokenSet = await _codeExchanger.ExchangeCode(
            "google",
            code,
            ResolveGoogleRedirectUri(request));
        var identity = await _providerVerifier.Verify("google", tokenSet.IdToken, state.Nonce);
        var session = await _authService.SignInWithVerifiedIdentity(
            identity,
            username: null,
            displayName: identity.DisplayName,
            device: new DeviceDescriptor($"web:{Guid.NewGuid():N}", "Web Browser", "web"),
            allowGeneratedUsername: true);

        return new WebOAuthCallbackResult(session, state.ReturnUrl);
    }

    private string ResolveGoogleRedirectUri(HttpRequest request)
    {
        if (!string.IsNullOrWhiteSpace(_options.Google.RedirectUri))
        {
            return _options.Google.RedirectUri.Trim();
        }

        return $"{request.Scheme}://{request.Host}/api/v3/library/auth/web/google/callback";
    }

    private string ValidateReturnUrl(string? returnUrl)
    {
        var value = string.IsNullOrWhiteSpace(returnUrl)
            ? _options.Web.DefaultReturnUrl
            : returnUrl.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return "/";
        }

        if (!value.StartsWith("/", StringComparison.Ordinal) ||
            value.StartsWith("//", StringComparison.Ordinal) ||
            value.Contains('\\', StringComparison.Ordinal) ||
            value.Any(char.IsControl))
        {
            throw new UserAuthException("invalid_return_url");
        }

        return value;
    }
}
