using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Relisten.UserApi.Auth;
using Relisten.UserApi.Models;
using Relisten.UserApi.Services;

namespace Relisten.UserApi.Controllers;

[ApiController]
[Authorize]
[Route("api/v3/library/history")]
[Produces("application/json")]
public sealed class HistoryController : ControllerBase
{
    private readonly IAuthenticatedUserContext _authenticatedUserContext;
    private readonly PlaybackHistoryService _historyService;

    public HistoryController(
        IAuthenticatedUserContext authenticatedUserContext,
        PlaybackHistoryService historyService)
    {
        _authenticatedUserContext = authenticatedUserContext;
        _historyService = historyService;
    }

    [HttpGet("recent")]
    [ProducesResponseType(typeof(PlaybackHistoryRecentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(PlaybackHistoryErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<PlaybackHistoryRecentResponse>> Recent([FromQuery] string? limit = null)
    {
        try
        {
            return await _historyService.GetRecent(
                _authenticatedUserContext.CurrentUser.UserUuid,
                limit);
        }
        catch (PlaybackHistoryException ex)
        {
            return BadRequest(new PlaybackHistoryErrorResponse { Error = ex.Code });
        }
    }

    [HttpPost("batch")]
    [ProducesResponseType(typeof(PlaybackHistoryBatchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(PlaybackHistoryErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<PlaybackHistoryBatchResponse>> Batch([FromBody] PlaybackHistoryBatchRequest request)
    {
        if (request == null)
        {
            return BadRequest(new PlaybackHistoryErrorResponse { Error = "invalid_history_batch" });
        }

        try
        {
            return await _historyService.IngestBatch(
                _authenticatedUserContext.CurrentUser.UserUuid,
                request);
        }
        catch (PlaybackHistoryException ex)
        {
            return BadRequest(new PlaybackHistoryErrorResponse { Error = ex.Code });
        }
    }
}
