using System.Security.Claims;
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
    private readonly PlaylistSharingService _sharingService;

    public PlaylistsController(
        IAuthenticatedUserContext authenticatedUserContext,
        PlaylistService playlistService,
        PlaylistSharingService sharingService)
    {
        _authenticatedUserContext = authenticatedUserContext;
        _playlistService = playlistService;
        _sharingService = sharingService;
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
            return CreatedAtAction(nameof(Get), new { playlistIdentifier = playlist.PlaylistUuid }, playlist);
        }
        catch (PlaylistOperationException ex)
        {
            return BadRequest(new PlaylistErrorResponse { Error = ex.Code });
        }
    }

    [AllowAnonymous]
    [HttpGet("{playlistIdentifier}")]
    [ProducesResponseType(typeof(PlaylistResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PlaylistResponse>> Get([FromRoute] string playlistIdentifier)
    {
        var access = await _sharingService.GetForViewer(
            OptionalAuthenticatedUserUuid(),
            playlistIdentifier,
            MobileGrantFromHeaders());
        return access == null ? NotFound() : PlaylistSharingService.ToResponse(access.Snapshot);
    }

    [HttpGet("{playlistUuid:guid}/viewer-state")]
    [ProducesResponseType(typeof(PlaylistViewerStateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PlaylistViewerStateResponse>> ViewerState([FromRoute] Guid playlistUuid)
    {
        var viewerState = await _sharingService.GetViewerState(
            _authenticatedUserContext.CurrentUser.UserUuid,
            playlistUuid);
        return viewerState == null ? NotFound() : viewerState;
    }

    [HttpPost("{playlistUuid:guid}/follow")]
    [ProducesResponseType(typeof(PlaylistViewerStateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PlaylistViewerStateResponse>> Follow([FromRoute] Guid playlistUuid)
    {
        var viewerState = await _sharingService.Follow(
            _authenticatedUserContext.CurrentUser.UserUuid,
            playlistUuid,
            MobileGrantFromHeaders());
        return viewerState == null ? NotFound() : viewerState;
    }

    [HttpPost("{playlistIdentifier}/clone")]
    [ProducesResponseType(typeof(PlaylistResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(PlaylistResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(PlaylistErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PlaylistResponse>> Clone(
        [FromRoute] string playlistIdentifier,
        [FromBody] ClonePlaylistRequest request)
    {
        try
        {
            var result = await _sharingService.ClonePlaylist(
                _authenticatedUserContext.CurrentUser.UserUuid,
                playlistIdentifier,
                MobileGrantFromHeaders(),
                request);
            if (result == null)
            {
                return NotFound();
            }

            return result.Created
                ? CreatedAtAction(nameof(Get), new { playlistIdentifier = result.Playlist.PlaylistUuid }, result.Playlist)
                : Ok(result.Playlist);
        }
        catch (PlaylistOperationException ex)
        {
            return PlaylistError(ex);
        }
    }

    [HttpPost("{playlistUuid:guid}/collaborators/invitations")]
    [ProducesResponseType(typeof(PlaylistCollaboratorResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(PlaylistErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PlaylistCollaboratorResponse>> InviteCollaborator(
        [FromRoute] Guid playlistUuid,
        [FromBody] CreatePlaylistCollaboratorInvitationRequest request)
    {
        try
        {
            var collaborator = await _sharingService.InviteCollaborator(
                _authenticatedUserContext.CurrentUser.UserUuid,
                playlistUuid,
                request);
            return collaborator == null
                ? NotFound()
                : CreatedAtAction(nameof(ViewerState), new { playlistUuid }, collaborator);
        }
        catch (PlaylistOperationException ex)
        {
            return PlaylistError(ex);
        }
    }

    [HttpPost("{playlistUuid:guid}/collaborators/invitations/accept")]
    [HttpPost("~/api/v3/library/invitations/{playlistUuid:guid}/accept")]
    [ProducesResponseType(typeof(PlaylistCollaboratorResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PlaylistCollaboratorResponse>> AcceptCollaboratorInvitation(
        [FromRoute] Guid playlistUuid)
    {
        var collaborator = await _sharingService.AcceptCollaboratorInvitation(
            _authenticatedUserContext.CurrentUser.UserUuid,
            playlistUuid);
        return collaborator == null ? NotFound() : collaborator;
    }

    [HttpDelete("{playlistUuid:guid}/collaborators/{collaboratorUserUuid:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeCollaborator(
        [FromRoute] Guid playlistUuid,
        [FromRoute] Guid collaboratorUserUuid)
    {
        return await _sharingService.RevokeCollaborator(
            _authenticatedUserContext.CurrentUser.UserUuid,
            playlistUuid,
            collaboratorUserUuid)
            ? NoContent()
            : NotFound();
    }

    [HttpPost("{playlistUuid:guid}/share-tokens")]
    [ProducesResponseType(typeof(PlaylistShareTokenResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(PlaylistErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PlaylistShareTokenResponse>> CreateShareToken(
        [FromRoute] Guid playlistUuid,
        [FromBody] CreatePlaylistShareTokenRequest request)
    {
        try
        {
            var token = await _sharingService.CreateShareToken(
                _authenticatedUserContext.CurrentUser.UserUuid,
                playlistUuid,
                request);
            return token == null
                ? NotFound()
                : CreatedAtAction(nameof(Get), new { playlistIdentifier = token.PlaylistUuid }, token);
        }
        catch (PlaylistOperationException ex)
        {
            return PlaylistError(ex);
        }
    }

    [HttpDelete("{playlistUuid:guid}/share-tokens/{shareTokenUuid:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeShareToken(
        [FromRoute] Guid playlistUuid,
        [FromRoute] Guid shareTokenUuid)
    {
        return await _sharingService.RevokeShareToken(
            _authenticatedUserContext.CurrentUser.UserUuid,
            playlistUuid,
            shareTokenUuid)
            ? NoContent()
            : NotFound();
    }

    [AllowAnonymous]
    [HttpPost("{playlistIdentifier}/share-tokens/exchange")]
    [ProducesResponseType(typeof(ExchangePlaylistShareTokenResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(PlaylistErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(PlaylistErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ExchangePlaylistShareTokenResponse>> ExchangeShareToken(
        [FromRoute] string playlistIdentifier,
        [FromBody] ExchangePlaylistShareTokenRequest request)
    {
        try
        {
            return await _sharingService.ExchangeShareToken(
                OptionalAuthenticatedUserUuid(),
                playlistIdentifier,
                request);
        }
        catch (PlaylistOperationException ex)
        {
            return PlaylistError(ex);
        }
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
            return PlaylistError(ex);
        }
    }

    private Guid? OptionalAuthenticatedUserUuid()
    {
        return Guid.TryParse(
            User.FindFirstValue(RelistenUserAuthenticationDefaults.ClaimTypes.UserUuid),
            out var userUuid)
            ? userUuid
            : null;
    }

    private PlaylistMobileGrantCredential? MobileGrantFromHeaders()
    {
        var grant = Request.Headers["X-Relisten-Mobile-Grant"].ToString();
        var deviceId = Request.Headers["X-Relisten-Device-Id"].ToString();
        return string.IsNullOrWhiteSpace(grant) || string.IsNullOrWhiteSpace(deviceId)
            ? null
            : new PlaylistMobileGrantCredential(grant.Trim(), deviceId.Trim());
    }

    private ObjectResult PlaylistError(PlaylistOperationException ex)
    {
        var response = new PlaylistErrorResponse { Error = ex.Code };
        return ex.Code == "sign_in_required" ? Unauthorized(response) : BadRequest(response);
    }
}
