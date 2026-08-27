using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using OpenIddict.Validation.AspNetCore;

namespace RelistenUserService.Authentication;

public static class AccountCredentialSelector
{
    public static bool IsAmbiguous(HttpRequest request) =>
        HasAuthorizationCredential(request) && HasWebSessionCredential(request);

    public static string SelectScheme(HttpContext context)
    {
        if (IsAmbiguous(context.Request))
        {
            return AuthenticationConstants.RejectedAccountCredentialScheme;
        }

        return HasWebSessionCredential(context.Request)
            ? AuthenticationConstants.WebSessionScheme
            : OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;
    }

    private static bool HasAuthorizationCredential(HttpRequest request) =>
        !string.IsNullOrWhiteSpace(request.Headers.Authorization);

    private static bool HasWebSessionCredential(HttpRequest request) =>
        request.Cookies.ContainsKey(AuthenticationConstants.WebSessionCookie);
}

public sealed class RejectedAccountCredentialAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
        Task.FromResult(AuthenticateResult.Fail(
            "A request cannot use bearer and web-session credentials together."));
}
