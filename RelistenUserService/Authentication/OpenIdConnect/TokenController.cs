using System.Security.Claims;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using RelistenUserService.Authentication.Sessions;
using RelistenUserService.Identity.Entities;
using RelistenUserService.Persistence;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace RelistenUserService.Authentication.OpenIdConnect;

public sealed class TokenController(
    AccountsDbContext dbContext,
    NativePrincipalFactory principalFactory,
    WebBootstrapPrincipalFactory webPrincipalFactory,
    IdentitySessionLifecycle identitySessions,
    TimeProvider timeProvider
) : Controller
{
    [HttpPost("~/connect/token")]
    [Produces("application/json")]
    public async Task<IActionResult> Exchange(CancellationToken cancellationToken)
    {
        var request =
            HttpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenIddict request is unavailable.");
        if (!request.IsAuthorizationCodeGrantType() && !request.IsRefreshTokenGrantType())
        {
            throw new InvalidOperationException("The token grant is not supported.");
        }

        var authentication = await HttpContext.AuthenticateAsync(
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme
        );
        var source = authentication.Principal;
        if (
            !authentication.Succeeded
            || source is null
            || !UserId.TryParse(source.GetClaim(Claims.Subject), out var userId)
        )
        {
            return InvalidGrant("The authorization code or refresh token is invalid.");
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            cancellationToken
        );
        var user = await OAuthIssuance.LockActiveUserAsync(dbContext, userId, cancellationToken);
        if (user is null)
        {
            return InvalidGrant("The account is no longer active.");
        }

        var principal =
            request.ClientId == AuthenticationConstants.WebClientId
                ? await CreateWebPrincipalAsync(request, source, userId, cancellationToken)
                : await CreateNativePrincipalAsync(request, source, user, cancellationToken);
        if (principal is null)
        {
            return InvalidGrant("The session is no longer active.");
        }

        // OpenIddict commits issuance before writing the response, still inside the user lock.
        await HttpContext.SignInAsync(
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            principal
        );
        return new EmptyResult();
    }

    private async Task<ClaimsPrincipal?> CreateNativePrincipalAsync(
        OpenIddictRequest request,
        ClaimsPrincipal source,
        User user,
        CancellationToken cancellationToken
    )
    {
        if (!SessionId.TryParse(source.GetClaim(RelistenClaims.SessionId), out var sessionId))
        {
            return null;
        }

        var session = await dbContext.NativeSessions.SingleOrDefaultAsync(
            item => item.Id == sessionId && item.UserId == user.Id,
            cancellationToken
        );
        var now = timeProvider.GetUtcNow();
        if (
            session is null
            || session.RevokedAt is not null
            || session.AbsoluteExpiresAt <= now
            || session.LastUsedAt + AuthenticationConstants.NativeSessionInactivityLimit <= now
            || session.ClientId != request.ClientId
            || session.SecurityVersion != user.SecurityVersion
        )
        {
            return null;
        }

        session.LastUsedAt = now;
        session.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);

        return principalFactory.Create(user, session, source.GetScopes());
    }

    private async Task<ClaimsPrincipal?> CreateWebPrincipalAsync(
        OpenIddictRequest request,
        ClaimsPrincipal source,
        UserId userId,
        CancellationToken cancellationToken
    )
    {
        if (
            !request.IsAuthorizationCodeGrantType()
            || !SessionId.TryParse(
                source.GetClaim(RelistenClaims.SessionId),
                out var authSsoSessionId
            )
        )
        {
            return null;
        }

        var authSso = await identitySessions.ValidateAuthSsoBootstrapAsync(
            authSsoSessionId,
            userId,
            cancellationToken
        );
        if (authSso is null)
        {
            return null;
        }

        return webPrincipalFactory.Create(authSso.User, authSso.SessionId, source.GetScopes());
    }

    private ForbidResult InvalidGrant(string description) =>
        Forbid(
            new AuthenticationProperties(
                new Dictionary<string, string?>
                {
                    [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidGrant,
                    [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
                }
            ),
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme
        );
}
