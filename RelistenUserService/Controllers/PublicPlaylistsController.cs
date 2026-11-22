using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Relisten.Accounts.Contracts.Playlists;
using RelistenUserService.Authentication;
using RelistenUserService.Authentication.Authorization;
using RelistenUserService.Http;
using RelistenUserService.Playlists;

namespace RelistenUserService.Controllers;

[ApiController]
[Route("v1/public-playlists")]
public sealed class PublicPlaylistsController(
    CurrentAccountContext account,
    PlaylistSharingService sharing,
    PlaylistWriteService writes,
    PublicPlaylistDirectory directory
) : ControllerBase
{
    /// <summary>
    /// Public playlists, newest first by default. <c>artist</c> keeps playlists with at
    /// least one of that artist's tracks.
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    [PublicPlaylistCache]
    [ServiceFilter(typeof(PlaylistHttpFilter))]
    public async Task<ActionResult<PublicPlaylistPage>> List(
        [FromQuery] string sort = "recent",
        [FromQuery] string? artist = null,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = PublicPlaylistDirectory.DefaultLimit,
        CancellationToken cancellationToken = default
    ) => Ok(await directory.ListAsync(sort, artist, cursor, limit, cancellationToken));

    [HttpGet("{publicCode}")]
    [AllowAnonymous]
    [PublicPlaylistCache]
    public async Task<ActionResult<PublicPlaylistSnapshot>> Snapshot(
        string publicCode,
        CancellationToken cancellationToken
    )
    {
        var snapshot = await sharing.SnapshotAsync(publicCode, cancellationToken);
        return snapshot is null ? NotFound() : Ok(snapshot);
    }

    [HttpPut("{publicCode}/follow")]
    [Authorize(Policy = AuthenticationConstants.LibraryAccessPolicy)]
    [ServiceFilter(typeof(PlaylistHttpFilter))]
    public async Task<IActionResult> Follow(string publicCode, CancellationToken cancellationToken)
    {
        await sharing.FollowAsync(account.User.Id, publicCode, cancellationToken);
        return NoContent();
    }

    [HttpPost("{publicCode}/copy")]
    [Authorize(Policy = AuthenticationConstants.LibraryAccessPolicy)]
    [ServiceFilter(typeof(PlaylistHttpFilter))]
    [RequestSizeLimit(4 * 1024)]
    public async Task<ActionResult<PlaylistCreated>> Copy(
        string publicCode,
        [FromBody] CopyPublicPlaylistRequest request,
        CancellationToken cancellationToken
    )
    {
        var result = await writes.CopyAsync(
            account.User.Id,
            publicCode,
            request,
            cancellationToken
        );
        return StatusCode(result.Replayed ? 200 : 201, result.Response);
    }

    [HttpPost("{publicCode}/reports")]
    [Authorize(Policy = AuthenticationConstants.LibraryAccessPolicy)]
    [ServiceFilter(typeof(PlaylistHttpFilter))]
    [RequestSizeLimit(4 * 1024)]
    public async Task<IActionResult> Report(
        string publicCode,
        [FromBody] ReportPublicPlaylistRequest request,
        CancellationToken cancellationToken
    )
    {
        await sharing.ReportAsync(account.User.Id, publicCode, request.Reason, cancellationToken);
        return NoContent();
    }
}
