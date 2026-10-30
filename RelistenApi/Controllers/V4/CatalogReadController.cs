using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Relisten.Api.Models.Api.V4;
using Relisten.Catalog;
using Relisten.Identifiers;

namespace Relisten.Controllers.V4;

/// <summary>Typed catalog reads. Imported files remain playback identities.</summary>
[Route("api/v4")]
[ApiExplorerSettings(GroupName = "v4")]
[Produces("application/json")]
public sealed class CatalogReadController(
    CatalogReader reader,
    ILogger<CatalogReadController> logger
) : CatalogControllerBase(logger)
{
    [HttpGet("shows/{showId}")]
    [ProducesResponseType(typeof(ShowResource), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 422)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 503)]
    public Task<IActionResult> Show(string showId, CancellationToken cancellationToken) =>
        Read(
            showId,
            ShowId.Prefix,
            async uuid =>
            {
                var show = await reader.ReadShow(uuid, cancellationToken);
                return show is null ? null : V4CatalogProjection.Show(show);
            }
        );

    [HttpGet("sources/{sourceId}")]
    [ProducesResponseType(typeof(SourceResource), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 422)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 503)]
    public Task<IActionResult> Source(string sourceId, CancellationToken cancellationToken) =>
        Read(
            sourceId,
            SourceId.Prefix,
            async uuid =>
            {
                var source = await reader.ReadSource(uuid, cancellationToken);
                return source is null ? null : V4CatalogProjection.Source(source);
            }
        );

    [HttpGet("source-tracks/{sourceTrackId}")]
    [ProducesResponseType(typeof(TrackResource), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 422)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 503)]
    public Task<IActionResult> Track(string sourceTrackId, CancellationToken cancellationToken) =>
        Read(
            sourceTrackId,
            TrackId.Prefix,
            async uuid =>
            {
                var track = await reader.ReadTrack(uuid, cancellationToken);
                return track is null ? null : V4CatalogProjection.Track(track);
            }
        );
}
