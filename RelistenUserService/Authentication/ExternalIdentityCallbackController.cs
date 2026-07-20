using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Client.AspNetCore;
using RelistenUserService.Identity;
using RelistenUserService.Identity.Entities;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace RelistenUserService.Authentication;

public sealed class ExternalIdentityCallbackController(
    ExternalIdentityCompletionService identities,
    TimeProvider timeProvider)
    : Controller
{
    [AcceptVerbs("GET", "POST", Route = "~/signin-google")]
    [IgnoreAntiforgeryToken]
    public Task<IActionResult> Google(CancellationToken cancellationToken) =>
        CompleteAsync(AuthenticationConstants.GoogleProvider, cancellationToken);

    [AcceptVerbs("GET", "POST", Route = "~/signin-apple")]
    [IgnoreAntiforgeryToken]
    public Task<IActionResult> Apple(CancellationToken cancellationToken) =>
        CompleteAsync(AuthenticationConstants.AppleProvider, cancellationToken);

    private async Task<IActionResult> CompleteAsync(
        string provider,
        CancellationToken cancellationToken)
    {
        var result = await HttpContext.AuthenticateAsync(
            OpenIddictClientAspNetCoreDefaults.AuthenticationScheme);
        if (!result.Succeeded
            || result.Principal is not { Identity.IsAuthenticated: true } principal)
        {
            return Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "The external identity response could not be authenticated.");
        }

        var returnUrl = result.Properties?.RedirectUri;
        if (!IsAuthorizationReturnUrl(returnUrl))
        {
            throw new InvalidOperationException(
                "The protected external identity return URL is invalid.");
        }

        User user;
        try
        {
            user = await identities.CompleteAsync(
                ExternalIdentityProfileFactory.Create(provider, principal),
                cancellationToken);
        }
        catch (AccountUnavailableException)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        var identity = new ClaimsIdentity(AuthenticationConstants.ExternalIdentityScheme);
        identity.AddClaim(new Claim(Claims.Subject, user.Id.ToString("D")));
        await HttpContext.SignInAsync(
            AuthenticationConstants.ExternalIdentityScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                AllowRefresh = false,
                IsPersistent = false,
                ExpiresUtc = timeProvider.GetUtcNow().AddMinutes(10)
            });

        return LocalRedirect(returnUrl!);
    }

    private bool IsAuthorizationReturnUrl(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && Url.IsLocalUrl(value)
        && Uri.TryCreate($"http://localhost{value}", UriKind.Absolute, out var uri)
        && uri.AbsolutePath == "/connect/authorize";
}
