using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Relisten.Accounts.Contracts.Accounts;
using RelistenUserService.Authentication;
using RelistenUserService.Identity;

namespace RelistenUserService.Controllers;

[ApiController]
[Route("api/user/v1/me")]
public sealed class BrowserMeController(CurrentAccountContext currentAccount)
    : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = AuthenticationConstants.BrowserProfileReadPolicy)]
    public ActionResult<BrowserAccountProfileResponse> Get() =>
        Ok(AccountProfileFactory.CreateBrowser(currentAccount.User));
}
