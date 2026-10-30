using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Relisten.Api.Models.Api.V4;
using Relisten.Catalog;
using Relisten.Identifiers;

namespace Relisten.Controllers.V4;

/// <summary>Literal substring search. Every category is present, including disabled or empty ones.</summary>
[Route("api/v4/search")]
[ApiExplorerSettings(GroupName = "v4")]
[ProducesResponseType(typeof(ProblemDetails), 422, "application/problem+json")]
[ProducesResponseType(typeof(ProblemDetails), 503, "application/problem+json")]
public sealed class CatalogSearchController(
    CatalogSearchReader reader,
    ILogger<CatalogSearchController> logger
) : CatalogControllerBase(logger)
{
    [HttpGet]
    [ProducesResponseType(typeof(CatalogSearchResource), 200, "application/json")]
    public Task<IActionResult> Search(
        [FromQuery] string? q = null,
        [FromQuery(Name = "artist_id")] string? artistId = null,
        [FromQuery] bool artists = true,
        [FromQuery] bool shows = true,
        [FromQuery] bool songs = true,
        [FromQuery] bool sources = true,
        [FromQuery] bool tours = true,
        [FromQuery] bool venues = true,
        CancellationToken cancellationToken = default
    )
    {
        if (artistId is not null && !ArtistId.TryParse(artistId, out _))
        {
            return Task.FromResult<IActionResult>(InvalidId(ArtistId.Prefix));
        }
        return Execute(async () =>
            CatalogSearchResource.From(
                await reader.Read(
                    q,
                    artistId is null ? null : ArtistId.Parse(artistId).Value,
                    new(artists, shows, songs, sources, tours, venues),
                    cancellationToken
                )
            )
        );
    }
}
