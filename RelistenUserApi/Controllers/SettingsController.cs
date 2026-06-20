using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Relisten.UserApi.Auth;
using Relisten.UserApi.Models;
using Relisten.UserApi.Services;

namespace Relisten.UserApi.Controllers;

[ApiController]
[Authorize]
[Route("api/v3/library/settings")]
[Produces("application/json")]
public sealed class SettingsController : ControllerBase
{
    private readonly IAuthenticatedUserContext _authenticatedUserContext;
    private readonly UserLibrarySyncService _syncService;

    public SettingsController(
        IAuthenticatedUserContext authenticatedUserContext,
        UserLibrarySyncService syncService)
    {
        _authenticatedUserContext = authenticatedUserContext;
        _syncService = syncService;
    }

    [HttpGet]
    [ProducesResponseType(typeof(UserSettingsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<UserSettingsResponse> Get()
    {
        return await _syncService.GetSettings(_authenticatedUserContext.CurrentUser.UserUuid);
    }

    [HttpPut]
    [ProducesResponseType(typeof(UserSettingsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(UserLibrarySyncErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<UserSettingsResponse>> Update([FromBody] UpdateUserSettingsRequest request)
    {
        try
        {
            return await _syncService.UpdateSettings(
                _authenticatedUserContext.CurrentUser.UserUuid,
                request);
        }
        catch (UserLibrarySyncException ex)
        {
            return BadRequest(new UserLibrarySyncErrorResponse { Error = ex.Code });
        }
    }
}
