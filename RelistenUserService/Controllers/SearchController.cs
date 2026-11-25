using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RelistenUserService.Authentication;
using RelistenUserService.Authentication.Authorization;
using RelistenUserService.Authentication.Sessions;
using RelistenUserService.Search;

namespace RelistenUserService.Controllers;

[ApiController]
[Route("v1/search")]
[AllowAnonymous]
public sealed class SearchController(
    CurrentAccountContext account,
    IAuthorizationService authorization
) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] MergedSearchRequest request,
        [FromServices] IServiceProvider services,
        CancellationToken cancellationToken
    )
    {
        Response.Headers.CacheControl = "private, no-store";
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
        return Ok(await search.Run(request, ReadPlaylistViewer, cancellationToken));
    }

    private async Task<UserId?> ReadPlaylistViewer(CancellationToken cancellationToken)
    {
        // Public search only needs a session when it looks up playlists. A supplied
        // bearer has already been validated by the public account middleware.
        var principal = User;
        if (!account.IsLoaded)
        {
            HttpContext.Features.Set(new WebSessionAuthenticationDeadline(cancellationToken));
            var credential = await HttpContext.AuthenticateAsync(
                AuthenticationConstants.AccountCredentialScheme
            );
            principal = credential.Principal;
        }

        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var access = await authorization.AuthorizeAsync(
            principal,
            HttpContext,
            AuthenticationConstants.LibraryAccessPolicy
        );
        return access.Succeeded ? account.User.Id : null;
    }
}
