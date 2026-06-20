using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Relisten.UserApi.Auth;
using Relisten.UserApi.Models;
using Relisten.UserApi.Services;

namespace Relisten.UserApi.Controllers;

[ApiController]
[Authorize]
[Route("api/v3/library/users")]
[Produces("application/json")]
public sealed class UsersController : ControllerBase
{
    private readonly IAuthenticatedUserContext _authenticatedUserContext;
    private readonly UserAccountDataService _accountDataService;
    private readonly UserAuthService _authService;

    public UsersController(
        IAuthenticatedUserContext authenticatedUserContext,
        UserAccountDataService accountDataService,
        UserAuthService authService)
    {
        _authenticatedUserContext = authenticatedUserContext;
        _accountDataService = accountDataService;
        _authService = authService;
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

    [HttpGet("me/sessions")]
    [ProducesResponseType(typeof(IReadOnlyList<UserSessionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IReadOnlyList<UserSessionResponse>> Sessions()
    {
        var user = _authenticatedUserContext.CurrentUser;
        return await _authService.ListSessions(user.UserUuid);
    }

    [HttpPost("me/export")]
    [ProducesResponseType(typeof(AccountExportResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AuthErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AccountExportResponse>> Export()
    {
        var user = _authenticatedUserContext.CurrentUser;
        try
        {
            await _authService.RequireRecentReauthentication(user.UserUuid, user.SessionUuid);
            return await _accountDataService.Export(user.UserUuid);
        }
        catch (UserAuthException ex)
        {
            return Unauthorized(new AuthErrorResponse { Error = ex.Code });
        }
    }

    [HttpDelete("me")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(AccountDeletionErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(AuthErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteAccount()
    {
        var user = _authenticatedUserContext.CurrentUser;
        try
        {
            await _authService.RequireRecentReauthentication(user.UserUuid, user.SessionUuid);
            await _accountDataService.Delete(user.UserUuid);
            return NoContent();
        }
        catch (UserAuthException ex)
        {
            return Unauthorized(new AuthErrorResponse { Error = ex.Code });
        }
        catch (UserAccountDeletionException ex)
        {
            return Conflict(new AccountDeletionErrorResponse { Error = ex.Code });
        }
    }

    [HttpDelete("me/sessions/{sessionUuid:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RevokeSession([FromRoute] Guid sessionUuid)
    {
        var user = _authenticatedUserContext.CurrentUser;
        await _authService.RevokeSession(user.UserUuid, sessionUuid);
        return NoContent();
    }
}
