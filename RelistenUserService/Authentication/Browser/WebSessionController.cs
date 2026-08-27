using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using OpenIddict.Client.AspNetCore;
using OpenIddict.Abstractions;
using Relisten.Accounts.Contracts.Authentication;
using RelistenUserService.Authentication.Authorization;
using RelistenUserService.Authentication.Sessions;
using RelistenUserService.Configuration;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace RelistenUserService.Authentication.Browser;

[ApiController]
[Route("auth/session")]
[Authorize(Policy = AuthenticationConstants.BrowserProfileReadPolicy)]
public sealed class WebSessionController(
    AccountsRuntimeConfiguration runtime,
    IdentitySessionLifecycle sessions,
    SessionCookieManager cookies,
    IAntiforgery antiforgery,
    CurrentAccountContext currentAccount)
    : ControllerBase
{
    [HttpGet("start")]
    [AllowAnonymous]
    public IActionResult Start(
        [FromQuery(Name = "return_to")] string? returnTo,
        [FromQuery(Name = "select_account")] bool selectAccount = false)
    {
        if (!ApplicationReturnPath.TryValidate(returnTo, out var returnPath)
            || HttpContext.Features.Get<IWebOriginFeature>() is not { } webOrigin)
        {
            return BadRequest();
        }

        var registrationId = webOrigin.Origin switch
        {
            AuthenticationConstants.CanonicalWebOrigin =>
                AuthenticationConstants.CanonicalWebRegistration,
            AuthenticationConstants.LocalWebOrigin =>
                AuthenticationConstants.LocalWebRegistration,
            _ => null
        };
        if (registrationId is null)
        {
            return BadRequest();
        }

        var properties = new AuthenticationProperties(
            new Dictionary<string, string?>
            {
                [OpenIddictClientAspNetCoreConstants.Properties.RegistrationId] = registrationId
            })
        {
            RedirectUri = returnPath
        };
        if (selectAccount)
        {
            properties.Parameters[Parameters.Prompt] = PromptValues.SelectAccount;
        }
        properties.Parameters["provider"] = runtime.Options.Google.Enabled
            ? AuthenticationConstants.GoogleProvider
            : AuthenticationConstants.AppleProvider;

        return Challenge(
            properties,
            OpenIddictClientAspNetCoreDefaults.AuthenticationScheme);
    }

    [HttpGet("callback")]
    [AllowAnonymous]
    public async Task<IActionResult> Callback(CancellationToken cancellationToken)
    {
        var result = await HttpContext.AuthenticateAsync(
            OpenIddictClientAspNetCoreDefaults.AuthenticationScheme);
        if (!result.Succeeded
            || result.Principal is not { Identity.IsAuthenticated: true } principal
            || !Guid.TryParse(principal.GetClaim(Claims.Subject), out var userId)
            || !Guid.TryParse(
                principal.GetClaim(RelistenClaims.SessionId),
                out var authSsoSessionId)
            || HttpContext.Features.Get<IWebOriginFeature>() is not { } webOrigin
            || !ApplicationReturnPath.TryValidate(
                result.Properties?.RedirectUri,
                out var returnPath))
        {
            return Unauthorized();
        }

        var expectedRegistration = webOrigin.Origin switch
        {
            AuthenticationConstants.CanonicalWebOrigin =>
                AuthenticationConstants.CanonicalWebRegistration,
            AuthenticationConstants.LocalWebOrigin =>
                AuthenticationConstants.LocalWebRegistration,
            _ => null
        };
        string? registrationId = null;
        var hasRegistration = result.Properties is not null
            && result.Properties.Items.TryGetValue(
                OpenIddictClientAspNetCoreConstants.Properties.RegistrationId,
                out registrationId);
        if (expectedRegistration is null
            || !hasRegistration
            || registrationId != expectedRegistration)
        {
            return Unauthorized();
        }

        IssuedIdentitySession session;
        try
        {
            session = await sessions.CreateWebAsync(
                userId,
                authSsoSessionId,
                webOrigin.Origin,
                cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return Unauthorized();
        }

        cookies.SetWeb(Response, session);
        // The callback ensures one antiforgery cookie exists before concurrent tabs request tokens.
        // Each request token binds the reused cookie to the new web session ID.
        _ = antiforgery.GetAndStoreTokens(HttpContext);
        return LocalRedirect(returnPath);
    }

    [HttpPost("logout")]
    public async Task<ActionResult<SessionNavigationResponse>> Logout(
        CancellationToken cancellationToken)
    {
        await sessions.RevokeWebAndParentAsync(
            currentAccount.WebSessionId,
            cancellationToken);
        cookies.ClearWeb(Response);
        cookies.ClearCsrf(Response);
        return Ok(new SessionNavigationResponse(AuthCookieClearUrl("/")));
    }

    [HttpPost("switch-account")]
    public async Task<ActionResult<SessionNavigationResponse>> SwitchAccount(
        [FromQuery(Name = "return_to")] string? returnTo,
        CancellationToken cancellationToken)
    {
        if (!ApplicationReturnPath.TryValidate(returnTo, out var returnPath))
        {
            return BadRequest();
        }

        await sessions.RevokeWebAndParentAsync(
            currentAccount.WebSessionId,
            cancellationToken);
        cookies.ClearWeb(Response);
        cookies.ClearCsrf(Response);
        var restartPath = QueryHelpers.AddQueryString(
            "/auth/session/start",
            new Dictionary<string, string?>
            {
                ["return_to"] = returnPath,
                ["select_account"] = "true"
            });
        return Ok(new SessionNavigationResponse(AuthCookieClearUrl(restartPath)));
    }

    private string AuthCookieClearUrl(string returnPath)
    {
        var webOrigin = currentAccount.WebOrigin
            ?? throw new InvalidOperationException("The web session origin is unavailable.");
        return QueryHelpers.AddQueryString(
            new Uri(runtime.Issuer, "/auth/sso/clear").AbsoluteUri,
            new Dictionary<string, string?>
            {
                ["web_origin"] = webOrigin,
                ["return_to"] = returnPath
            });
    }
}

[ApiController]
public sealed class AuthSsoCookieController(
    AccountsRuntimeConfiguration runtime,
    IdentitySessionLifecycle sessions,
    SessionCookieManager cookies)
    : ControllerBase
{
    [HttpGet("/auth/sso/clear")]
    public async Task<IActionResult> Clear(
        [FromQuery(Name = "web_origin")] string? webOrigin,
        [FromQuery(Name = "return_to")] string? returnTo,
        CancellationToken cancellationToken)
    {
        if (webOrigin is null
            || !runtime.WebOrigins.Contains(webOrigin, StringComparer.Ordinal)
            || !ApplicationReturnPath.TryValidate(returnTo, out var returnPath))
        {
            return BadRequest();
        }

        var authCookie = Request.Cookies[AuthenticationConstants.AuthSsoCookie];
        // The logout POST revokes the auth SSO session before this auth-host request.
        // An active __Host-relisten_auth cookie cannot authorize its own deletion.
        if (authCookie is not null
            && await sessions.CanClearRevokedAuthSsoAsync(
                authCookie,
                webOrigin,
                cancellationToken))
        {
            cookies.ClearAuthSso(Response);
            cookies.ClearCsrf(Response);
        }

        return Redirect(webOrigin + returnPath);
    }
}
