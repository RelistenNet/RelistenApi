using System;
using System.Collections.Generic;
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
using Relisten.Services.Live;
using Relisten.Services.Popularity;

namespace Relisten.Controllers.V4;

/// <summary>Anonymous play reporting and public activity. These routes do not change account history.</summary>
[Route("api/v4/live")]
[ApiExplorerSettings(GroupName = "v4")]
[Produces("application/json")]
public sealed class CatalogLiveController(
    CatalogLiveReader reader,
    CatalogLiveSummaryReader summary,
    SourceTrackPlaysService plays,
    PopularityRefreshRequest refresh,
    ILogger<CatalogLiveController> logger
) : CatalogControllerBase(logger, refresh)
{
    /// <summary>
    /// Plays grouped by show and artist over the last completed minute window.
    /// Minutes clamp to 5–60; list limits and ranking options match live discovery.
    /// Listened time counts each started track's duration, with 420 seconds for unknown duration.
    /// </summary>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(LiveSummaryResource), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 422)]
    [ProducesResponseType(typeof(ProblemDetails), 503)]
    public Task<IActionResult> Summary(
        [FromQuery] int minutes = LiveSummaryRequest.DefaultMinutes,
        [FromQuery] int shows_limit = LiveSummaryRequest.DefaultShowsLimit,
        [FromQuery] int artists_limit = LiveSummaryRequest.DefaultArtistsLimit,
        [FromQuery] string? month_day = null,
        [FromQuery, AllowedValues("surprise", "plays")] string sort = "surprise",
        [FromQuery] int max_per_artist = LiveSummaryRequest.DefaultMaxPerArtist,
        [FromQuery] int fresh_days = LiveSummaryRequest.DefaultFreshDays,
        CancellationToken cancellationToken = default
    )
    {
        var now = DateTime.UtcNow;
        if (
            !LiveSummaryRequest.TryCreate(
                minutes,
                shows_limit,
                artists_limit,
                month_day,
                sort,
                max_per_artist,
                fresh_days,
                now,
                out var request,
                out var error
            )
        )
        {
            return Task.FromResult<IActionResult>(Failure(422, "invalid_query", error));
        }

        var window = LiveWindow.EndingBefore(now, request.Minutes);
        // This public window can be shared until the next minute boundary. Cloudflare
        // owns response caching; the origin does not keep another serialized copy.
        var maxAge = Math.Max(1, (int)Math.Ceiling((window.NextRefreshAt - now).TotalSeconds));
        Response.Headers.CacheControl = $"public, max-age={maxAge}, stale-while-revalidate=60";
        return Execute(async () => await summary.Read(request, window, cancellationToken));
    }

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

    /// <summary>
    /// One-hour activity window, ascending by sequence. Repeat artist_ids to select
    /// any of those artists before the limit; omit it for all artists. Limits cap at 2000.
    /// </summary>
    [HttpGet("history")]
    [ProducesResponseType(typeof(LiveHistoryResource[]), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 422)]
    [ProducesResponseType(typeof(ProblemDetails), 503)]
    public Task<IActionResult> History(
        [FromQuery(Name = "artist_ids")] string[]? artistIds = null,
        [FromQuery, Range(0, int.MaxValue)] int? after_sequence = null,
        [FromQuery, Range(1, int.MaxValue)] int limit = 20,
        CancellationToken cancellationToken = default
    )
    {
        var artists = new List<Guid>();
        foreach (var id in artistIds ?? [])
        {
            if (!ArtistId.TryParse(id, out var artist))
            {
                return Task.FromResult<IActionResult>(InvalidId(ArtistId.Prefix));
            }
            artists.Add(artist.Value);
        }

        return Execute(
            async () =>
                (
                    await reader.ReadHistory(
                        after_sequence,
                        Math.Min(limit, 2000),
                        artists.Distinct().ToArray(),
                        cancellationToken
                    )
                )
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
}
