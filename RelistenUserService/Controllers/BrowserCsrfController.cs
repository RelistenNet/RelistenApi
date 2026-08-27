using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Relisten.Accounts.Contracts.Authentication;
using RelistenUserService.Authentication;

namespace RelistenUserService.Controllers;

[ApiController]
[Route("api/user/v1/csrf")]
public sealed class BrowserCsrfController(IAntiforgery antiforgery) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = AuthenticationConstants.BrowserProfileReadPolicy)]
    public ActionResult<CsrfTokenResponse> Get()
    {
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return tokens.RequestToken is null
            ? Problem(statusCode: StatusCodes.Status500InternalServerError)
            : Ok(new CsrfTokenResponse(tokens.RequestToken));
    }
}
