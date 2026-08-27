using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using RelistenUserService.Configuration;
using RelistenUserService.Identity.Entities;
using RelistenUserService.Persistence;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace RelistenUserService.Authentication;

public sealed record AuthenticatedNativeSession(User User, Guid SessionId);

public sealed class NativeSessionAuthenticator(
    AccountsDbContext dbContext,
    AccountsRuntimeConfiguration runtime,
    TimeProvider timeProvider)
{
    public async Task<AuthenticatedNativeSession?> AuthenticateAsync(ClaimsPrincipal principal)
    {
        if (!principal.HasAudience(runtime.Options.Audience)
            || !Guid.TryParse(principal.GetClaim(Claims.Subject), out var userId)
            || !Guid.TryParse(principal.GetClaim(RelistenClaims.SessionId), out var sessionId)
            || !int.TryParse(
                principal.GetClaim(RelistenClaims.SecurityVersion),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var securityVersion))
        {
            return null;
        }

        var session = await dbContext.NativeSessions
            .Include(item => item.User)
            .SingleOrDefaultAsync(item => item.Id == sessionId && item.UserId == userId);
        var now = timeProvider.GetUtcNow();
        if (session is null
            || session.RevokedAt is not null
            || session.AbsoluteExpiresAt <= now
            || session.SecurityVersion != securityVersion
            || session.User.SecurityVersion != securityVersion
            || session.User.Status != UserStatuses.Active)
        {
            return null;
        }

        return new AuthenticatedNativeSession(session.User, session.Id);
    }
}

public sealed class NativeSessionAuthorizationHandler(
    NativeSessionAuthenticator authenticator,
    CurrentAccountContext currentAccount)
    : AuthorizationHandler<NativeSessionRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        NativeSessionRequirement requirement)
    {
        var session = await authenticator.AuthenticateAsync(context.User);
        if (session is null)
        {
            return;
        }

        currentAccount.SetNative(session.User, session.SessionId);
        context.Succeed(requirement);
    }
}

public sealed class ScopeAuthorizationHandler : AuthorizationHandler<ScopeRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ScopeRequirement requirement)
    {
        if (context.User.HasScope(requirement.Scope))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
