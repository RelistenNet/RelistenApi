using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Relisten.UserApi.Auth;
using Relisten.UserApi.Models;
using Relisten.UserApi.Services;

namespace Relisten.UserApi.Controllers;

[ApiController]
[Authorize]
[Route("api/v3/library/sync")]
[Produces("application/json")]
public sealed class SyncController : ControllerBase
{
    private readonly IAuthenticatedUserContext _authenticatedUserContext;
    private readonly UserLibrarySyncService _syncService;

    public SyncController(
        IAuthenticatedUserContext authenticatedUserContext,
        UserLibrarySyncService syncService)
    {
        _authenticatedUserContext = authenticatedUserContext;
        _syncService = syncService;
    }

    [HttpGet]
    [ProducesResponseType(typeof(UserLibrarySyncResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(UserLibrarySyncErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<UserLibrarySyncResponse>> Pull([FromQuery] string? cursor = null)
    {
        try
        {
            return await _syncService.Pull(_authenticatedUserContext.CurrentUser.UserUuid, cursor);
        }
        catch (UserLibrarySyncException ex)
        {
            return BadRequest(new UserLibrarySyncErrorResponse { Error = ex.Code });
        }
    }
}
