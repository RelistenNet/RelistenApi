using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Relisten.Accounts.Contracts.Playlists;
using RelistenUserService.Authentication;
using RelistenUserService.Authentication.Authorization;
using RelistenUserService.Playlists;

namespace RelistenUserService.Controllers;

[ApiController]
[Route("v1/playlists")]
[Authorize(Policy = AuthenticationConstants.LibraryAccessPolicy)]
[ServiceFilter(typeof(PlaylistHttpFilter))]
public sealed class PlaylistsController(
    CurrentAccountContext account,
    PlaylistWriteService writes,
    PlaylistReadService reads
) : ControllerBase
{
    [HttpPost]
    [RequestSizeLimit(2 * 1024 * 1024)]
    public async Task<ActionResult<PlaylistCreated>> Create(
        [FromBody] CreatePlaylistRequest request,
        CancellationToken cancellationToken
    )
    {
        var result = await writes.CreateAsync(account.User.Id, request, cancellationToken);
        return StatusCode(result.Replayed ? 200 : 201, result.Response);
    }

    [HttpGet]
    public async Task<ActionResult<PlaylistList>> List(
        [FromQuery] string view = "active",
        CancellationToken cancellationToken = default
    ) => Ok(await reads.ListAsync(account.User.Id, view, cancellationToken));

    [HttpGet("{playlistId}/snapshot")]
    public async Task<ActionResult<PlaylistSnapshot>> Snapshot(
        string playlistId,
        CancellationToken cancellationToken
    ) => Ok(await reads.SnapshotAsync(account.User.Id, ParseId(playlistId), cancellationToken));

    [HttpGet("{playlistId}/changes")]
    public async Task<ActionResult<PlaylistChanges>> Changes(
        string playlistId,
        [FromQuery(Name = "after_revision")] long? afterRevision,
        CancellationToken cancellationToken
    )
    {
        if (afterRevision is null)
        {
            throw new PlaylistException(
                422,
                "invalid_playlist_operation",
                "after_revision is required."
            );
        }
        return Ok(
            await reads.ChangesAsync(
                account.User.Id,
                ParseId(playlistId),
                afterRevision.Value,
                cancellationToken
            )
        );
    }

    [HttpPost("{playlistId}/operations:batch")]
    [RequestSizeLimit(2 * 1024 * 1024)]
    public async Task<ActionResult<PlaylistBatchResponse>> Batch(
        string playlistId,
        [FromBody] PlaylistBatchRequest request,
        CancellationToken cancellationToken
    ) =>
        Ok(
            await writes.ApplyAsync(
                account.User.Id,
                ParseId(playlistId),
                request,
                cancellationToken
            )
        );

    [HttpPut("{playlistId}/archive-state")]
    public async Task<ActionResult<PlaylistArchived>> Archive(
        string playlistId,
        [FromBody] ArchivePlaylistRequest request,
        CancellationToken cancellationToken
    ) =>
        Ok(
            await writes.ArchiveAsync(
                account.User.Id,
                ParseId(playlistId),
                request,
                cancellationToken
            )
        );

    private static PlaylistId ParseId(string value) =>
        PlaylistId.TryParse(value, out var id)
            ? id
            : throw new PlaylistException(404, "playlist_not_found", "Playlist not found.");
}
