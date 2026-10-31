using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Relisten.Api.Models.Api.V4;
using Relisten.Catalog;
using Relisten.Identifiers;
using Relisten.Services.Popularity;

namespace Relisten.Controllers.V4;

/// <summary>Artist tour lists and direct tour details with complete catalog show membership.</summary>
[Route("api/v4")]
[ApiExplorerSettings(GroupName = "v4")]
[Produces("application/json")]
[ProducesResponseType(typeof(ProblemDetails), 422)]
[ProducesResponseType(typeof(ProblemDetails), 404)]
[ProducesResponseType(typeof(ProblemDetails), 503)]
public sealed class CatalogToursController(
    CatalogTourReader reader,
    PopularityRefreshRequest refresh,
    ILogger<CatalogToursController> logger
) : CatalogControllerBase(logger, refresh)
{
    [HttpGet("artists/{artistId}/tours")]
    [ProducesResponseType(typeof(TourResource[]), 200)]
    public Task<IActionResult> Tours(string artistId, CancellationToken cancellationToken) =>
        Read(
            artistId,
            ArtistId.Prefix,
            async artist =>
                (await reader.ReadTours(artist, cancellationToken))
                    ?.Select(V4CatalogProjection.Tour)
                    .ToArray()
        );

    [HttpGet("tours/{tourId}")]
    [ProducesResponseType(typeof(TourWithShowsResource), 200)]
    public Task<IActionResult> Tour(string tourId, CancellationToken cancellationToken) =>
        Read(
            tourId,
            TourId.Prefix,
            async tour =>
            {
                var detail = await reader.ReadTour(tour, cancellationToken);
                return detail is null ? null : V4CatalogProjection.TourWithShows(detail);
            }
        );
}
