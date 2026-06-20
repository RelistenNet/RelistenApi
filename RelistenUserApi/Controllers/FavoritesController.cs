using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Relisten.UserApi.Auth;
using Relisten.UserApi.Models;
using Relisten.UserApi.Services;

namespace Relisten.UserApi.Controllers;

[ApiController]
[Authorize]
[Route("api/v3/library/favorites")]
[Produces("application/json")]
public sealed class FavoritesController : ControllerBase
{
    private readonly IAuthenticatedUserContext _authenticatedUserContext;
    private readonly UserLibrarySyncService _syncService;

    public FavoritesController(
        IAuthenticatedUserContext authenticatedUserContext,
        UserLibrarySyncService syncService)
    {
        _authenticatedUserContext = authenticatedUserContext;
        _syncService = syncService;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<FavoriteResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IReadOnlyList<FavoriteResponse>> List()
    {
        return await _syncService.ListFavorites(_authenticatedUserContext.CurrentUser.UserUuid);
    }

    [HttpPut("{entityType}/{entityUuid:guid}")]
    [ProducesResponseType(typeof(FavoriteResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(UserLibrarySyncErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<FavoriteResponse>> Add(
        [FromRoute] string entityType,
        [FromRoute] Guid entityUuid)
    {
        try
        {
            return await _syncService.AddFavorite(
                _authenticatedUserContext.CurrentUser.UserUuid,
                entityType,
                entityUuid);
        }
        catch (UserLibrarySyncException ex)
        {
            return BadRequest(new UserLibrarySyncErrorResponse { Error = ex.Code });
        }
    }

    [HttpDelete("{entityType}/{entityUuid:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(UserLibrarySyncErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Remove(
        [FromRoute] string entityType,
        [FromRoute] Guid entityUuid)
    {
        try
        {
            await _syncService.RemoveFavorite(
                _authenticatedUserContext.CurrentUser.UserUuid,
                entityType,
                entityUuid);
            return NoContent();
        }
        catch (UserLibrarySyncException ex)
        {
            return BadRequest(new UserLibrarySyncErrorResponse { Error = ex.Code });
        }
    }
}
