using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;
using RelistenUserService.Authentication;
using RelistenUserService.Authentication.Authorization;
using RelistenUserService.Search;

namespace RelistenUserService.Controllers;

[ApiController]
[Route("v1/search")]
[AllowAnonymous]
public sealed class SearchController(NativeSessionAuthenticator sessions) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] MergedSearchRequest request,
        [FromServices] IServiceProvider services,
        CancellationToken cancellationToken
    )
    {
        Response.Headers.CacheControl = "private, no-store";
        Func<CancellationToken, Task<UserId?>>? viewer = null;
        if (!string.IsNullOrWhiteSpace(Request.Headers.Authorization))
        {
            var credential = await HttpContext
                .AuthenticateAsync(AuthenticationConstants.AccountCredentialScheme)
                .WaitAsync(cancellationToken);
            if (!credential.Succeeded)
            {
                return Challenge();
            }

            viewer = token => Viewer(credential.Principal!, token);
        }

        if (!request.IsValid())
        {
            return UnprocessableEntity(
                new
                {
                    code = "invalid_query",
                    detail = "The query contains an invalid search value.",
                }
            );
        }

        var search = services.GetRequiredService<MergedSearch>();
        try
        {
            return Ok(await search.Run(request, viewer, cancellationToken));
        }
        catch (InvalidSearchSessionException)
        {
            Response.Headers.WWWAuthenticate = "Bearer";
            return Unauthorized();
        }
    }

    private async Task<UserId?> Viewer(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken
    )
    {
        var session =
            await sessions.AuthenticateAsync(principal, cancellationToken)
            ?? throw new InvalidSearchSessionException();
        return principal.HasScope(RelistenScopes.LibraryRead) ? session.User.Id : null;
    }

    private sealed class InvalidSearchSessionException : Exception;
}
