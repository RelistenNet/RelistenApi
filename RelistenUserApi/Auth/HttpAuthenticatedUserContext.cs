using System.Security.Claims;

namespace Relisten.UserApi.Auth;

public sealed class HttpAuthenticatedUserContext : IAuthenticatedUserContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpAuthenticatedUserContext(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public AuthenticatedUser CurrentUser
    {
        get
        {
            var principal = _httpContextAccessor.HttpContext?.User
                ?? throw new InvalidOperationException("No HTTP user is available.");

            var userUuid = Guid.Parse(GetRequiredClaim(principal, RelistenUserAuthenticationDefaults.ClaimTypes.UserUuid));
            var displayName = GetRequiredClaim(principal, RelistenUserAuthenticationDefaults.ClaimTypes.DisplayName);
            var username = GetRequiredClaim(principal, RelistenUserAuthenticationDefaults.ClaimTypes.Username);
            var scopeId = GetRequiredClaim(principal, RelistenUserAuthenticationDefaults.ClaimTypes.ScopeId);

            return new AuthenticatedUser(userUuid, displayName, username, scopeId);
        }
    }

    private static string GetRequiredClaim(ClaimsPrincipal principal, string claimType)
    {
        return principal.FindFirstValue(claimType)
            ?? throw new InvalidOperationException($"Authenticated user is missing claim '{claimType}'.");
    }
}
