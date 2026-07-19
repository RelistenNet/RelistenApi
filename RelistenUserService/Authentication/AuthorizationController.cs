using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using RelistenUserService.Configuration;
using RelistenUserService.Identity.Entities;
using RelistenUserService.Persistence;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace RelistenUserService.Authentication;

public sealed class AuthorizationController(
    AccountsDbContext dbContext,
    AccountsRuntimeConfiguration runtime,
    IOpenIddictApplicationManager applicationManager,
    IOpenIddictAuthorizationManager authorizationManager,
    NativePrincipalFactory principalFactory,
    TimeProvider timeProvider)
    : Controller
{
    [HttpGet("~/connect/authorize")]
    public async Task<IActionResult> Authorize(CancellationToken cancellationToken)
    {
        var request = HttpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenIddict request is unavailable.");
        if (!runtime.Options.EnableDevelopmentPersonas)
        {
            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "No external identity provider is configured.");
        }

        var authentication = await HttpContext.AuthenticateAsync(
            AuthenticationConstants.DevelopmentIdentityScheme);
        if (!authentication.Succeeded
            || !Guid.TryParse(authentication.Principal?.GetClaim(Claims.Subject), out var userId))
        {
            return Challenge(
                new AuthenticationProperties
                {
                    RedirectUri = Request.PathBase + Request.Path + Request.QueryString
                },
                AuthenticationConstants.DevelopmentIdentityScheme);
        }

        var user = await dbContext.Users.SingleOrDefaultAsync(
            item => item.Id == userId && item.Status == UserStatuses.Active,
            cancellationToken);
        if (user is null || string.IsNullOrWhiteSpace(request.ClientId))
        {
            await HttpContext.SignOutAsync(AuthenticationConstants.DevelopmentIdentityScheme);
            return Forbid();
        }

        var now = timeProvider.GetUtcNow();
        var session = new NativeSession
        {
            Id = Guid.CreateVersion7(),
            UserId = user.Id,
            ClientId = request.ClientId,
            SecurityVersion = user.SecurityVersion,
            AuthenticatedAt = now,
            LastUsedAt = now,
            AbsoluteExpiresAt = now + AuthenticationConstants.NativeSessionAbsoluteLifetime,
            CreatedAt = now,
            UpdatedAt = now
        };
        var principal = principalFactory.Create(user, session, request.GetScopes());

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var application = await applicationManager.FindByClientIdAsync(
            request.ClientId,
            cancellationToken)
            ?? throw new InvalidOperationException("The validated OpenIddict client is unavailable.");
        var applicationId = await applicationManager.GetIdAsync(application, cancellationToken)
            ?? throw new InvalidOperationException("The OpenIddict client has no ID.");
        var authorization = await authorizationManager.CreateAsync(
            principal,
            user.Id.ToString("D"),
            applicationId,
            AuthorizationTypes.Permanent,
            principal.GetScopes(),
            cancellationToken);
        var authorizationId = await authorizationManager.GetIdAsync(
            authorization,
            cancellationToken)
            ?? throw new InvalidOperationException("OpenIddict created an authorization without an ID.");
        session.AuthorizationId = Guid.Parse(authorizationId);
        principal.SetAuthorizationId(authorizationId);
        dbContext.NativeSessions.Add(session);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await HttpContext.SignOutAsync(AuthenticationConstants.DevelopmentIdentityScheme);
        return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }
}
