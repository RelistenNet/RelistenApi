using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Relisten.UserApi.Services;

namespace Relisten.UserApi.Auth;

public sealed class WebSessionCookieEvents : CookieAuthenticationEvents
{
    private readonly IUserAuthStore _authStore;

    public WebSessionCookieEvents(IUserAuthStore authStore)
    {
        _authStore = authStore;
    }

    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var userUuidValue = context.Principal?.FindFirstValue(RelistenUserAuthenticationDefaults.ClaimTypes.UserUuid);
        var sessionUuidValue = context.Principal?.FindFirstValue(RelistenUserAuthenticationDefaults.ClaimTypes.SessionUuid);
        if (!Guid.TryParse(userUuidValue, out var userUuid) ||
            !Guid.TryParse(sessionUuidValue, out var sessionUuid))
        {
            context.RejectPrincipal();
            return;
        }

        var session = await _authStore.GetSession(sessionUuid);
        if (session == null || session.RevokedAt != null || session.UserUuid != userUuid)
        {
            context.RejectPrincipal();
        }
    }

    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}
