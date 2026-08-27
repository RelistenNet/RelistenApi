using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using RelistenUserService.Identity.Entities;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace RelistenUserService.Authentication;

public sealed class AuthSsoAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IdentitySessionLifecycle sessions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var session = await sessions.AuthenticateAsync(
            Request.Cookies[AuthenticationConstants.AuthSsoCookie],
            IdentitySessionPurposes.AuthSso,
            Context.RequestAborted);
        if (session is null)
        {
            return AuthenticateResult.NoResult();
        }

        return AuthenticateResult.Success(
            new AuthenticationTicket(CreatePrincipal(session), Scheme.Name));
    }

    private static ClaimsPrincipal CreatePrincipal(AuthenticatedIdentitySession session)
    {
        var identity = new ClaimsIdentity(
            AuthenticationConstants.AuthSsoScheme,
            Claims.Name,
            Claims.Role);
        identity.AddClaim(new Claim(Claims.Subject, session.User.Id.ToString("D")));
        identity.AddClaim(new Claim(Claims.Name, session.User.Username));
        identity.AddClaim(new Claim(RelistenClaims.SessionId, session.SessionId.ToString("D")));
        return new ClaimsPrincipal(identity);
    }
}

public sealed class WebSessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IdentitySessionLifecycle sessions,
    SessionCookieManager cookies,
    CurrentAccountContext currentAccount)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Context.Features.Get<IWebOriginFeature>() is not { } webOrigin)
        {
            return AuthenticateResult.NoResult();
        }

        var cookieValue = Request.Cookies[AuthenticationConstants.WebSessionCookie];
        var session = await sessions.AuthenticateAsync(
            cookieValue,
            IdentitySessionPurposes.Web,
            Context.RequestAborted);
        if (session is null
            || session.WebOrigin is null
            || !string.Equals(session.WebOrigin, webOrigin.Origin, StringComparison.Ordinal))
        {
            return AuthenticateResult.NoResult();
        }

        currentAccount.SetWeb(
            session.User,
            session.SessionId,
            session.WebOrigin,
            session.Capabilities);
        if (session.WasTouched && cookieValue is not null)
        {
            cookies.RenewWeb(Response, cookieValue, session.ExpiresAt);
        }

        var identity = new ClaimsIdentity(
            AuthenticationConstants.WebSessionScheme,
            Claims.Name,
            Claims.Role);
        identity.AddClaim(new Claim(Claims.Subject, session.User.Id.ToString("D")));
        identity.AddClaim(new Claim(Claims.Name, session.User.Username));
        identity.AddClaim(new Claim(RelistenClaims.SessionId, session.SessionId.ToString("D")));
        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
