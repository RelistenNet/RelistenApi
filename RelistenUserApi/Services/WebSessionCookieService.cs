using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Relisten.UserApi.Auth;
using Relisten.UserApi.Configuration;

namespace Relisten.UserApi.Services;

public sealed class WebSessionCookieService
{
    private readonly UserAuthOptions _options;

    public WebSessionCookieService(IOptions<UserAuthOptions> options)
    {
        _options = options.Value;
    }

    public Task SignIn(HttpContext context, UserAuthSessionResult session)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddDays(Math.Max(1, _options.Web.SessionCookieDays));
        var claims = new[]
        {
            new Claim(RelistenUserAuthenticationDefaults.ClaimTypes.UserUuid, session.User.UserUuid.ToString()),
            new Claim(RelistenUserAuthenticationDefaults.ClaimTypes.DisplayName, session.User.DisplayName),
            new Claim(RelistenUserAuthenticationDefaults.ClaimTypes.Username, session.User.Username),
            new Claim(RelistenUserAuthenticationDefaults.ClaimTypes.ScopeId, $"user:{session.User.UserUuid}"),
            new Claim(RelistenUserAuthenticationDefaults.ClaimTypes.SessionUuid, session.Session.SessionUuid.ToString())
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            claims,
            RelistenUserAuthenticationDefaults.WebSessionScheme));

        context.Response.Cookies.Append(
            _options.Web.CsrfCookieName,
            RandomToken(),
            BuildCookieOptions(httpOnly: false, expiresAt));

        return context.SignInAsync(
            RelistenUserAuthenticationDefaults.WebSessionScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = expiresAt,
                AllowRefresh = true
            });
    }

    public async Task SignOut(HttpContext context)
    {
        context.Response.Cookies.Delete(_options.Web.CsrfCookieName, DeleteCookieOptions());
        await context.SignOutAsync(RelistenUserAuthenticationDefaults.WebSessionScheme);
    }

    public void SetOAuthStateCookie(HttpResponse response, string protectedState, DateTimeOffset expiresAt)
    {
        response.Cookies.Append(
            _options.Web.OAuthStateCookieName,
            protectedState,
            BuildCookieOptions(httpOnly: true, expiresAt));
    }

    public void DeleteOAuthStateCookie(HttpResponse response)
    {
        response.Cookies.Delete(_options.Web.OAuthStateCookieName, DeleteCookieOptions());
    }

    public string? ReadOAuthStateCookie(HttpRequest request)
    {
        return request.Cookies[_options.Web.OAuthStateCookieName];
    }

    private CookieOptions BuildCookieOptions(bool httpOnly, DateTimeOffset expiresAt)
    {
        return new CookieOptions
        {
            HttpOnly = httpOnly,
            Secure = _options.Web.SecureCookies,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = expiresAt
        };
    }

    private CookieOptions DeleteCookieOptions()
    {
        return new CookieOptions
        {
            Secure = _options.Web.SecureCookies,
            SameSite = SameSiteMode.Lax,
            Path = "/"
        };
    }

    private static string RandomToken()
    {
        return WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    }
}
