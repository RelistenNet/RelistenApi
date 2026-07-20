using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.Client.WebIntegration;
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
        if (!runtime.Options.EnableDevelopmentPersonas
            && !runtime.Options.EnableExternalProviders)
        {
            // Keeping the service available with sign-in disabled is useful during a
            // controlled rollout, but the authorization endpoint must fail predictably.
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new ProblemDetails
                {
                    Status = StatusCodes.Status503ServiceUnavailable,
                    Title = "Account sign-in is not configured."
                });
        }

        var identityScheme = runtime.Options.EnableDevelopmentPersonas
            ? AuthenticationConstants.DevelopmentIdentityScheme
            : AuthenticationConstants.ExternalIdentityScheme;
        var authentication = await HttpContext.AuthenticateAsync(
            identityScheme);
        if (!authentication.Succeeded
            || !Guid.TryParse(authentication.Principal?.GetClaim(Claims.Subject), out var userId))
        {
            if (runtime.Options.EnableExternalProviders)
            {
                return ChallengeExternalProvider(request);
            }

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
            await HttpContext.SignOutAsync(identityScheme);
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

        await HttpContext.SignOutAsync(identityScheme);
        return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private IActionResult ChallengeExternalProvider(OpenIddictRequest request)
    {
        var scheme = request.GetParameter("provider").ToString() switch
        {
            AuthenticationConstants.GoogleProvider =>
                OpenIddictClientWebIntegrationConstants.Providers.Google,
            AuthenticationConstants.AppleProvider =>
                OpenIddictClientWebIntegrationConstants.Providers.Apple,
            _ => null
        };
        if (scheme is null)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "A supported identity provider is required."
            });
        }

        var properties = new AuthenticationProperties
        {
            RedirectUri = Request.PathBase + Request.Path + Request.QueryString
        };
        if (scheme == OpenIddictClientWebIntegrationConstants.Providers.Google
            && request.HasPromptValue(PromptValues.SelectAccount))
        {
            // Google supports a first-class account picker. Apple does not document
            // the OIDC prompt parameter, so forwarding it could invalidate the request.
            properties.Parameters[Parameters.Prompt] = PromptValues.SelectAccount;
        }

        return Challenge(properties, scheme);
    }
}
