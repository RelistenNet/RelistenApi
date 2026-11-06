using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Relisten.Api.Models.Api.V4;
using Relisten.Catalog;
using Relisten.Identifiers;
using Relisten.Normalization;
using Relisten.Services.Popularity;

namespace Relisten.Controllers.V4;

/// <summary>Artist venue groups, imported observations, and global canonical venue reads.</summary>
[Route("api/v4")]
[ApiExplorerSettings(GroupName = "v4")]
[Produces("application/json")]
[ProducesResponseType(typeof(ProblemDetails), 422)]
[ProducesResponseType(typeof(ProblemDetails), 404)]
[ProducesResponseType(typeof(ProblemDetails), 410)]
[ProducesResponseType(typeof(ProblemDetails), 503)]
public sealed class CatalogVenuesController(
    CatalogVenueReader reader,
    PopularityRefreshRequest refresh,
    ILogger<CatalogVenuesController> logger
) : CatalogControllerBase(logger, refresh)
{
    [HttpGet("artists/{artistId}/venues")]
    [ProducesResponseType(typeof(ArtistVenueEntryResource[]), 200)]
    public Task<IActionResult> ArtistVenues(string artistId, CancellationToken cancellationToken) =>
        Read(
            artistId,
            ArtistId.Prefix,
            async artist =>
                (await reader.ReadArtistVenues(artist, cancellationToken))
                    ?.Select(V4CatalogProjection.VenueEntry)
                    .ToArray()
        );

    [HttpGet("artists/{artistId}/venues/{venueId}")]
    [ProducesResponseType(typeof(ArtistVenueWithShowsResource), 200)]
    public Task<IActionResult> ArtistVenue(
        string artistId,
        string venueId,
        CancellationToken cancellationToken
    ) =>
        ReadVenue(
            venueId,
            () =>
                Read(
                    artistId,
                    ArtistId.Prefix,
                    async artist =>
                    {
                        var entry = await reader.ReadArtistVenue(
                            artist,
                            venueId,
                            cancellationToken
                        );
                        return entry is null
                            ? null
                            : V4CatalogProjection.ArtistVenueWithShows(entry);
                    }
                )
        );

    /// <summary>Finds an artist's canonical venue collection by its shared venue slug.</summary>
    /// <remarks>Imported artist-specific venue slugs identify separate collections.</remarks>
    [HttpGet("artists/{artistId}/venues/by-slug/{slug}")]
    [ProducesResponseType(typeof(ArtistVenueWithShowsResource), 200)]
    public Task<IActionResult> ArtistVenueBySlug(
        string artistId,
        string slug,
        CancellationToken cancellationToken
    ) =>
        Read(
            artistId,
            ArtistId.Prefix,
            async artist =>
            {
                var detail = await reader.ReadArtistVenueBySlug(artist, slug, cancellationToken);
                return detail is null ? null : V4CatalogProjection.ArtistVenueWithShows(detail);
            }
        );

    [HttpGet("artist-venues/{artistVenueId}")]
    [ProducesResponseType(typeof(ArtistVenueWithShowsResource), 200)]
    public Task<IActionResult> ArtistVenueById(
        string artistVenueId,
        CancellationToken cancellationToken
    )
    {
        try
        {
            ArtistVenueId.Parse(artistVenueId);
        }
        catch (FormatException)
        {
            return Task.FromResult<IActionResult>(InvalidId(ArtistVenueId.Prefix));
        }

        return Execute(async () =>
        {
            var detail = await reader.ReadArtistVenue(artistVenueId, cancellationToken);
            return detail is null ? null : V4CatalogProjection.ArtistVenueWithShows(detail);
        });
    }

    /// <summary>Lists canonical venues once across all artists, ordered by name and then ID.</summary>
    /// <remarks>Includes zero-show venues. Counts include only artists with normalization enabled; shows are not embedded.</remarks>
    [HttpGet("venues")]
    [ProducesResponseType(typeof(VenueResource[]), 200)]
    public Task<IActionResult> Venues(CancellationToken cancellationToken) =>
        Execute(async () =>
            (await reader.ReadVenues(cancellationToken)).Select(V4CatalogProjection.Venue).ToArray()
        );

    [HttpGet("venues/{venueId}")]
    [ProducesResponseType(typeof(VenueWithShowsResource), 200)]
    public Task<IActionResult> Venue(string venueId, CancellationToken cancellationToken) =>
        ReadVenue(
            venueId,
            () =>
                Execute(async () =>
                {
                    var venue = await reader.ReadVenue(venueId, cancellationToken);
                    return venue is null ? null : V4CatalogProjection.VenueWithShows(venue);
                })
        );

    /// <summary>Finds a canonical venue and its shows by its stored slug.</summary>
    /// <remarks>A merged venue's slug returns the survivor, including its current ID and slug.</remarks>
    [HttpGet("venues/by-slug/{slug}")]
    [ProducesResponseType(typeof(VenueWithShowsResource), 200)]
    public Task<IActionResult> VenueBySlug(string slug, CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            var detail = await reader.ReadVenueBySlug(slug, cancellationToken);
            return detail is null ? null : V4CatalogProjection.VenueWithShows(detail);
        });

    [HttpGet("artists/{artistId}/imported-venues")]
    [ProducesResponseType(typeof(ImportedVenueResource[]), 200)]
    public Task<IActionResult> ImportedVenues(
        string artistId,
        CancellationToken cancellationToken
    ) =>
        Read(
            artistId,
            ArtistId.Prefix,
            async artist =>
                (await reader.ReadImportedVenues(artist, cancellationToken))
                    ?.Select(V4CatalogProjection.ImportedVenue)
                    .ToArray()
        );

    [HttpGet("imported-venues/{importedVenueId}")]
    [ProducesResponseType(typeof(ImportedVenueWithShowsResource), 200)]
    public Task<IActionResult> ImportedVenue(
        string importedVenueId,
        CancellationToken cancellationToken
    ) =>
        Read(
            importedVenueId,
            ImportedVenueId.Prefix,
            async venue =>
            {
                var detail = await reader.ReadImportedVenue(venue, cancellationToken);
                return detail is null ? null : V4CatalogProjection.ImportedVenueWithShows(detail);
            }
        );

    // Normalization's short IDs have their own codec; UUID-backed kinds use the base reader.
    private Task<IActionResult> ReadVenue(string id, Func<Task<IActionResult>> read)
    {
        try
        {
            VenueId.Parse(id);
        }
        catch (FormatException)
        {
            return Task.FromResult<IActionResult>(InvalidId(VenueId.Prefix));
        }
        return read();
    }
}
