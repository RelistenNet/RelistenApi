using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Relisten.Api.Models.Api.V4;
using Relisten.Catalog;
using Relisten.Identifiers;

namespace Relisten.Controllers.V4;

/// <summary>Catalog navigation from artist discovery through years and show summaries.</summary>
[Route("api/v4/artists")]
[ApiExplorerSettings(GroupName = "v4")]
[Produces("application/json")]
[ProducesResponseType(typeof(ProblemDetails), 422)]
[ProducesResponseType(typeof(ProblemDetails), 404)]
[ProducesResponseType(typeof(ProblemDetails), 503)]
public sealed class CatalogBrowseController(
    CatalogBrowseReader reader,
    ILogger<CatalogBrowseController> logger
) : CatalogControllerBase(logger)
{
    [HttpGet]
    [ProducesResponseType(typeof(ArtistResource[]), 200)]
    public Task<IActionResult> Artists(
        [FromQuery(Name = "include_autocreated")] bool includeAutoCreated = false,
        CancellationToken cancellationToken = default
    ) =>
        Execute(async () =>
            (await reader.ReadArtists(includeAutoCreated, cancellationToken))
                .Select(CatalogBrowseProjection.Artist)
                .ToArray()
        );

    [HttpGet("{artistId}")]
    [ProducesResponseType(typeof(ArtistResource), 200)]
    public Task<IActionResult> Artist(string artistId, CancellationToken cancellationToken) =>
        Read(
            artistId,
            ArtistId.Prefix,
            async uuid =>
            {
                var artist = await reader.ReadArtist(uuid, cancellationToken);
                return artist is null ? null : CatalogBrowseProjection.Artist(artist);
            }
        );

    [HttpGet("{artistId}/years")]
    [ProducesResponseType(typeof(YearResource[]), 200)]
    public Task<IActionResult> Years(string artistId, CancellationToken cancellationToken) =>
        Read(
            artistId,
            ArtistId.Prefix,
            async uuid =>
            {
                var years = await reader.ReadYears(uuid, cancellationToken);
                return years?.Select(V4CatalogProjection.Year).ToArray();
            }
        );

    [HttpGet("{artistId}/shows")]
    [ProducesResponseType(typeof(ShowSummaryResource[]), 200)]
    public Task<IActionResult> Shows(string artistId, CancellationToken cancellationToken) =>
        Read(
            artistId,
            ArtistId.Prefix,
            async uuid =>
            {
                var shows = await reader.ReadShows(uuid, cancellationToken);
                return shows?.Select(V4CatalogProjection.ShowSummary).ToArray();
            }
        );

    [HttpGet("{artistId}/years/{yearId}")]
    [ProducesResponseType(typeof(YearWithShowsResource), 200)]
    public async Task<IActionResult> Year(
        string artistId,
        string yearId,
        CancellationToken cancellationToken
    )
    {
        if (!YearId.TryParse(yearId, out var year))
        {
            return InvalidId(YearId.Prefix);
        }
        return await Read(
            artistId,
            ArtistId.Prefix,
            async artist =>
            {
                var result = await reader.ReadYear(artist, year.Value, null, cancellationToken);
                return result is null ? null : V4CatalogProjection.YearWithShows(result);
            }
        );
    }

    [HttpGet("{artistId}/years/by-number/{year}")]
    [ProducesResponseType(typeof(YearWithShowsResource), 200)]
    public Task<IActionResult> YearByNumber(
        string artistId,
        [Range(1, 9999)] int year,
        CancellationToken cancellationToken
    ) =>
        Read(
            artistId,
            ArtistId.Prefix,
            async artist =>
            {
                var result = await reader.ReadYear(artist, null, year, cancellationToken);
                return result is null ? null : V4CatalogProjection.YearWithShows(result);
            }
        );
}
