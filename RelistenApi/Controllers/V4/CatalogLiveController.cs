using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Npgsql;
using Relisten.Api.Models;
using Relisten.Api.Models.Api.V4;
using Relisten.Catalog;
using Relisten.Data;
using Relisten.Identifiers;
using Relisten.Services.Popularity;

namespace Relisten.Controllers.V4;

/// <summary>Anonymous play reporting and public activity. Neither route changes account history.</summary>
[Route("api/v4/live")]
[ApiExplorerSettings(GroupName = "v4")]
[Produces("application/json")]
public sealed class CatalogLiveController(
    CatalogLiveReader reader,
    SourceTrackPlaysService plays,
    PopularityRefreshRequest refresh,
    ILogger<CatalogLiveController> logger
) : CatalogControllerBase(logger, refresh)
{
    [HttpPost("play")]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(PlayReceiptResource), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 422)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 503)]
    public async Task<IActionResult> Play(
        [FromBody] PlayRequest? request,
        CancellationToken cancellationToken
    )
    {
        if (request is null || !ModelState.IsValid)
        {
            return Failure(
                422,
                "invalid_request",
                "Provide a track_id, supported app_type, and optional client_uuid as JSON."
            );
        }
        if (!TrackId.TryParse(request.TrackId, out var trackId))
        {
            return InvalidId(TrackId.Prefix);
        }

        try
        {
            var play = await plays.RecordPlayableTrack(
                new SourceTrackPlay
                {
                    source_track_uuid = trackId.Value,
                    app_type = (SourceTrackPlayAppType)request.AppType!.Value,
                    user_uuid = request.ClientUuid,
                },
                cancellationToken
            );
            if (play is null)
            {
                return Failure(404, "not_found", "The requested playable track does not exist.");
            }

            // The write is already committed. Do not enqueue popularity work here:
            // a queue failure must not turn an accepted play into a retryable error.
            return new CatalogJsonResult(
                new PlayReceiptResource(play.id, play.created_at, trackId, request.AppType.Value),
                cacheable: false
            );
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
        {
            logger.LogError(exception, "Play reporting failed");
            return Failure(
                503,
                "telemetry_unavailable",
                "Play reporting is temporarily unavailable."
            );
        }
    }

    /// <summary>One-hour activity window, ascending by sequence. Limits above 2000 are capped.</summary>
    [HttpGet("history")]
    [ProducesResponseType(typeof(LiveHistoryResource[]), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 422)]
    [ProducesResponseType(typeof(ProblemDetails), 503)]
    public Task<IActionResult> History(
        [FromQuery, Range(0, int.MaxValue)] int? after_sequence = null,
        [FromQuery, Range(1, int.MaxValue)] int limit = 20,
        CancellationToken cancellationToken = default
    ) =>
        Execute(
            async () =>
                (await reader.ReadHistory(after_sequence, Math.Min(limit, 2000), cancellationToken))
                    .Select(item => new LiveHistoryResource(
                        item.Play.id,
                        item.Play.created_at,
                        (PlayAppType)item.Play.app_type,
                        V4CatalogProjection.Track(item.Track),
                        V4CatalogProjection.ShowSummary(item.Show)
                    ))
                    .ToArray(),
            cacheable: false
        );
}
