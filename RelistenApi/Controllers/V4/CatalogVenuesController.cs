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

namespace Relisten.Controllers.V4;

/// <summary>Artist venue groups, imported observations, and global canonical venue reads.</summary>
[Route("api/v4")]
[ApiExplorerSettings(GroupName = "v4")]
[Produces("application/json")]
[ProducesResponseType(typeof(ProblemDetails), 422)]
[ProducesResponseType(typeof(ProblemDetails), 404)]
[ProducesResponseType(typeof(ProblemDetails), 503)]
public sealed class CatalogVenuesController(
    CatalogVenueReader reader,
    ILogger<CatalogVenuesController> logger
) : CatalogControllerBase(logger)
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
    [ProducesResponseType(typeof(ArtistVenueResource), 200)]
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
                        return entry is null ? null : V4CatalogProjection.ArtistVenue(entry);
                    }
                )
        );

    [HttpGet("artists/{artistId}/venues/{venueId}/shows")]
    [ProducesResponseType(typeof(ShowSummaryResource[]), 200)]
    public Task<IActionResult> ArtistVenueShows(
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
                        (await reader.ReadVenueShows(venueId, artist, cancellationToken))
                            ?.Select(V4CatalogProjection.ShowSummary)
                            .ToArray()
                )
        );

    [HttpGet("venues/{venueId}")]
    [ProducesResponseType(typeof(VenueResource), 200)]
    public Task<IActionResult> Venue(string venueId, CancellationToken cancellationToken) =>
        ReadVenue(
            venueId,
            () =>
                Execute(async () =>
                {
                    var venue = await reader.ReadVenue(venueId, cancellationToken);
                    return venue is null ? null : V4CatalogProjection.Venue(venue);
                })
        );

    [HttpGet("venues/{venueId}/shows")]
    [ProducesResponseType(typeof(ShowSummaryResource[]), 200)]
    public Task<IActionResult> VenueShows(
        string venueId,
        [FromQuery(Name = "artist_id")] string? artistId,
        CancellationToken cancellationToken
    )
    {
        Guid? artist = null;
        // MVC binds an empty value as null; an invalid filter must not broaden the result.
        if (Request.Query.ContainsKey("artist_id"))
        {
            if (!ArtistId.TryParse(artistId, out var parsed))
            {
                return Task.FromResult<IActionResult>(InvalidId(ArtistId.Prefix));
            }
            artist = parsed.Value;
        }
        return ReadVenue(
            venueId,
            () =>
                Execute(async () =>
                    (await reader.ReadVenueShows(venueId, artist, cancellationToken))
                        ?.Select(V4CatalogProjection.ShowSummary)
                        .ToArray()
                )
        );
    }

    [HttpGet("artists/{artistId}/imported-venues")]
    [ProducesResponseType(typeof(ImportedVenueWithShowCountResource[]), 200)]
    public Task<IActionResult> ImportedVenues(
        string artistId,
        CancellationToken cancellationToken
    ) =>
        Read(
            artistId,
            ArtistId.Prefix,
            async artist =>
                (await reader.ReadImportedVenues(artist, cancellationToken))
                    ?.Select(V4CatalogProjection.ImportedVenueWithShowCount)
                    .ToArray()
        );

    [HttpGet("artists/{artistId}/imported-venues/{importedVenueId}")]
    [ProducesResponseType(typeof(ImportedVenueWithShowsResource), 200)]
    public Task<IActionResult> ImportedVenue(
        string artistId,
        string importedVenueId,
        CancellationToken cancellationToken
    )
    {
        if (!ImportedVenueId.TryParse(importedVenueId, out var venue))
        {
            return Task.FromResult<IActionResult>(InvalidId(ImportedVenueId.Prefix));
        }
        return Read(
            artistId,
            ArtistId.Prefix,
            async artist =>
            {
                var detail = await reader.ReadImportedVenue(artist, venue.Value, cancellationToken);
                return detail is null ? null : V4CatalogProjection.ImportedVenueWithShows(detail);
            }
        );
    }

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
