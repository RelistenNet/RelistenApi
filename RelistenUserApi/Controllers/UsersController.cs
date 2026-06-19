using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Relisten.UserApi.Auth;
using Relisten.UserApi.Models;

namespace Relisten.UserApi.Controllers;

[ApiController]
[Authorize]
[Route("api/v3/library/users")]
[Produces("application/json")]
public sealed class UsersController : ControllerBase
{
    private readonly IAuthenticatedUserContext _authenticatedUserContext;

    public UsersController(IAuthenticatedUserContext authenticatedUserContext)
    {
        _authenticatedUserContext = authenticatedUserContext;
    }

    [HttpGet("me")]
    [ProducesResponseType(typeof(CurrentUserResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<CurrentUserResponse> CurrentUser()
    {
        var user = _authenticatedUserContext.CurrentUser;

        return new CurrentUserResponse
        {
            UserUuid = user.UserUuid,
            DisplayName = user.DisplayName,
            Username = user.Username,
            ScopeId = user.ScopeId
        };
    }
}
