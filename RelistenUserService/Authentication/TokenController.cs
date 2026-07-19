using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using RelistenUserService.Identity.Entities;
using RelistenUserService.Persistence;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace RelistenUserService.Authentication;

public sealed class TokenController(
    AccountsDbContext dbContext,
    NativePrincipalFactory principalFactory,
    TimeProvider timeProvider)
    : Controller
{
    [HttpPost("~/connect/token")]
    [Produces("application/json")]
    public async Task<IActionResult> Exchange(CancellationToken cancellationToken)
    {
        var request = HttpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenIddict request is unavailable.");
        if (!request.IsAuthorizationCodeGrantType() && !request.IsRefreshTokenGrantType())
        {
            throw new InvalidOperationException("The token grant is not supported.");
        }

        var authentication = await HttpContext.AuthenticateAsync(
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        var source = authentication.Principal;
        if (!authentication.Succeeded
            || source is null
            || !Guid.TryParse(source.GetClaim(RelistenClaims.SessionId), out var sessionId))
        {
            return InvalidGrant("The authorization code or refresh token is invalid.");
        }

        var session = await dbContext.NativeSessions
            .Include(item => item.User)
            .SingleOrDefaultAsync(item => item.Id == sessionId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (session is null
            || session.RevokedAt is not null
            || session.AbsoluteExpiresAt <= now
            || session.LastUsedAt + AuthenticationConstants.NativeSessionInactivityLimit <= now
            || session.ClientId != request.ClientId
            || session.User.Status != UserStatuses.Active
            || session.SecurityVersion != session.User.SecurityVersion)
        {
            return InvalidGrant("The native session is no longer active.");
        }

        session.LastUsedAt = now;
        session.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);

        var principal = principalFactory.Create(session.User, session, source.GetScopes());
        return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private ForbidResult InvalidGrant(string description) => Forbid(
        new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidGrant,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description
        }),
        OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
}
