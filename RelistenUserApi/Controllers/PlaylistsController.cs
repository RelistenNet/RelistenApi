using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Relisten.UserApi.Auth;
using Relisten.UserApi.Models;
using Relisten.UserApi.Services;

namespace Relisten.UserApi.Controllers;

[ApiController]
[Authorize]
[Route("api/v3/library/playlists")]
[Produces("application/json")]
public sealed class PlaylistsController : ControllerBase
{
    private readonly IAuthenticatedUserContext _authenticatedUserContext;
    private readonly PlaylistService _playlistService;

    public PlaylistsController(
        IAuthenticatedUserContext authenticatedUserContext,
        PlaylistService playlistService)
    {
        _authenticatedUserContext = authenticatedUserContext;
        _playlistService = playlistService;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<PlaylistResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IReadOnlyList<PlaylistResponse>> List()
    {
        return await _playlistService.ListForUser(_authenticatedUserContext.CurrentUser.UserUuid);
    }

    [HttpPost]
    [ProducesResponseType(typeof(PlaylistResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(PlaylistErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<PlaylistResponse>> Create([FromBody] CreatePlaylistRequest request)
    {
        try
        {
            var playlist = await _playlistService.Create(
                _authenticatedUserContext.CurrentUser.UserUuid,
                request);
            return CreatedAtAction(nameof(Get), new { playlistUuid = playlist.PlaylistUuid }, playlist);
        }
        catch (PlaylistOperationException ex)
        {
            return BadRequest(new PlaylistErrorResponse { Error = ex.Code });
        }
    }

    [HttpGet("{playlistUuid:guid}")]
    [ProducesResponseType(typeof(PlaylistResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PlaylistResponse>> Get([FromRoute] Guid playlistUuid)
    {
        var playlist = await _playlistService.GetForOwner(
            _authenticatedUserContext.CurrentUser.UserUuid,
            playlistUuid);
        return playlist == null ? NotFound() : playlist;
    }

    [HttpPost("{playlistUuid:guid}/operations")]
    [ProducesResponseType(typeof(PlaylistOperationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(PlaylistErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PlaylistOperationResponse>> ApplyOperation(
        [FromRoute] Guid playlistUuid,
        [FromBody] PlaylistOperationRequest request)
    {
        try
        {
            var response = await _playlistService.ApplyOperation(
                _authenticatedUserContext.CurrentUser.UserUuid,
                playlistUuid,
                request);
            return response == null ? NotFound() : response;
        }
        catch (PlaylistOperationException ex)
        {
            return BadRequest(new PlaylistErrorResponse { Error = ex.Code });
        }
    }
}
