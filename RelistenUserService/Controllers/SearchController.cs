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
public sealed class SearchController(CurrentAccountContext account) : ControllerBase
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
        UserId? viewer =
            account.IsNative && User.HasScope(RelistenScopes.LibraryRead) ? account.User.Id : null;
        return Ok(await search.Run(request, _ => Task.FromResult(viewer), cancellationToken));
    }
}
