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
        return Ok(await search.Run(request, Viewer, cancellationToken));
    }

    private async Task<UserId?> Viewer(CancellationToken cancellationToken)
    {
        var credential = await HttpContext
            .AuthenticateAsync(AuthenticationConstants.AccountCredentialScheme)
            .WaitAsync(cancellationToken);
        if (!credential.Succeeded || !credential.Principal!.HasScope(RelistenScopes.LibraryRead))
        {
            return null;
        }

        return (await sessions.AuthenticateAsync(credential.Principal, cancellationToken))?.User.Id;
    }
}
