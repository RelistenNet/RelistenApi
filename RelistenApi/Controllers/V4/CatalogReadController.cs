using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Npgsql;
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
) : ControllerBase
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

    private async Task<IActionResult> Read<T>(string id, string prefix, Func<Guid, Task<T?>> read)
        where T : class
    {
        if (!UuidTypeId.TryParse(id, prefix, out var uuid))
        {
            return Failure(422, "invalid_id", "Expected a canonical " + prefix + " ID.");
        }

        try
        {
            var resource = await read(uuid);
            return resource is null
                ? Failure(404, "not_found", "The requested resource does not exist.")
                : new CatalogJsonResult(resource);
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
        {
            logger.LogError(exception, "Catalog read failed for {ResourceId}", id);
            return Failure(503, "catalog_unavailable", "The catalog is temporarily unavailable.");
        }
    }

    private ObjectResult Failure(int status, string code, string title)
    {
        Response.Headers.CacheControl = "no-store";
        var problem = new ProblemDetails { Status = status, Title = title };
        problem.Extensions["code"] = code;
        return new ObjectResult(problem)
        {
            StatusCode = status,
            ContentTypes = { "application/problem+json" },
        };
    }
}
