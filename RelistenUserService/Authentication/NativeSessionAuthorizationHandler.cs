using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using RelistenUserService.Configuration;
using RelistenUserService.Identity.Entities;
using RelistenUserService.Persistence;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace RelistenUserService.Authentication;

public sealed class NativeSessionAuthorizationHandler(
    AccountsDbContext dbContext,
    AccountsRuntimeConfiguration runtime,
    CurrentAccountContext currentAccount,
    TimeProvider timeProvider)
    : AuthorizationHandler<NativeSessionRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        NativeSessionRequirement requirement)
    {
        if (!context.User.HasAudience(runtime.Options.Audience)
            || !Guid.TryParse(context.User.GetClaim(Claims.Subject), out var userId)
            || !Guid.TryParse(context.User.GetClaim(RelistenClaims.SessionId), out var sessionId)
            || !int.TryParse(
                context.User.GetClaim(RelistenClaims.SecurityVersion),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var securityVersion))
        {
            return;
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
            return;
        }

        currentAccount.Set(session.User, session);
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
