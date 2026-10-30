using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Logging;
using Relisten.Api.Models.Api.V4;
using Relisten.Catalog;
using Relisten.Identifiers;

namespace Relisten.Controllers.V4;

/// <summary>Date, recent and rating-based show discovery. Lists contain the usual show summaries.</summary>
[Route("api/v4")]
[ApiExplorerSettings(GroupName = "v4")]
[ProducesResponseType(typeof(ProblemDetails), 422, "application/problem+json")]
[ProducesResponseType(typeof(ProblemDetails), 404, "application/problem+json")]
[ProducesResponseType(typeof(ProblemDetails), 503, "application/problem+json")]
public sealed class CatalogShowDiscoveryController(
    CatalogShowDiscoveryReader discovery,
    CatalogReader recordings,
    ILogger<CatalogShowDiscoveryController> logger
) : CatalogControllerBase(logger)
{
    /// <summary>Month/day across all years. Missing components use the database session date.</summary>
    [HttpGet("shows/today")]
    [ProducesResponseType(typeof(ShowSummaryResource[]), 200, "application/json")]
    public Task<IActionResult> Today(
        [FromQuery, Range(1, 12)] int? month = null,
        [FromQuery, Range(1, 31)] int? day = null,
        CancellationToken cancellationToken = default
    ) => Anniversary(null, month, day, cancellationToken);

    [HttpGet("artists/{artistId}/shows/today")]
    [ProducesResponseType(typeof(ShowSummaryResource[]), 200, "application/json")]
    public Task<IActionResult> ArtistToday(
        string artistId,
        [FromQuery, Range(1, 12)] int? month = null,
        [FromQuery, Range(1, 31)] int? day = null,
        CancellationToken cancellationToken = default
    ) => Anniversary(artistId, month, day, cancellationToken);

    /// <summary>Required month/day across all years; this is not an exact-date lookup.</summary>
    [HttpGet("shows/on-date")]
    [ProducesResponseType(typeof(ShowSummaryResource[]), 200, "application/json")]
    public Task<IActionResult> OnDate(
        [FromQuery, BindRequired, Range(1, 12)] int month,
        [FromQuery, BindRequired, Range(1, 31)] int day,
        CancellationToken cancellationToken
    ) => Anniversary(null, month, day, cancellationToken);

    [HttpGet("artists/{artistId}/shows/on-date")]
    [ProducesResponseType(typeof(ShowSummaryResource[]), 200, "application/json")]
    public Task<IActionResult> ArtistOnDate(
        string artistId,
        [FromQuery, BindRequired, Range(1, 12)] int month,
        [FromQuery, BindRequired, Range(1, 31)] int day,
        CancellationToken cancellationToken
    ) => Anniversary(artistId, month, day, cancellationToken);

    private Task<IActionResult> Anniversary(
        string? artistId,
        int? month,
        int? day,
        CancellationToken cancellationToken
    )
    {
        // Validate the pair in a leap year so February 29 remains browsable.
        if (
            month is >= 1 and <= 12
            && day.HasValue
            && day > DateTime.DaysInMonth(2000, month.Value)
        )
        {
            return Task.FromResult<IActionResult>(
                Failure(422, "invalid_query", "The month and day do not form a calendar date.")
            );
        }
        return Summaries(
            artistId,
            artist => discovery.ReadAnniversary(artist, month, day, cancellationToken)
        );
    }

    /// <summary>Defaults to 25 shows; days (at most 90) overrides shows (at most 250).</summary>
    [HttpGet("shows/recently-performed")]
    [ProducesResponseType(typeof(ShowSummaryResource[]), 200, "application/json")]
    public Task<IActionResult> RecentlyPerformed(
        [FromQuery(Name = "artist_ids")] string[]? artistIds = null,
        [FromQuery, Range(1, int.MaxValue)] int? shows = null,
        [FromQuery, Range(1, int.MaxValue)] int? days = null,
        CancellationToken cancellationToken = default
    ) => Recent(null, artistIds, true, shows, days, cancellationToken);

    [HttpGet("artists/{artistId}/shows/recently-performed")]
    [ProducesResponseType(typeof(ShowSummaryResource[]), 200, "application/json")]
    public Task<IActionResult> ArtistRecentlyPerformed(
        string artistId,
        [FromQuery, Range(1, int.MaxValue)] int? shows = null,
        [FromQuery, Range(1, int.MaxValue)] int? days = null,
        CancellationToken cancellationToken = default
    ) => Recent(artistId, null, true, shows, days, cancellationToken);

    /// <summary>Imported show update order; normalization-only corrections do not move a show here.</summary>
    [HttpGet("shows/recently-updated")]
    [ProducesResponseType(typeof(ShowSummaryResource[]), 200, "application/json")]
    public Task<IActionResult> RecentlyUpdated(
        [FromQuery(Name = "artist_ids")] string[]? artistIds = null,
        [FromQuery, Range(1, int.MaxValue)] int? shows = null,
        [FromQuery, Range(1, int.MaxValue)] int? days = null,
        CancellationToken cancellationToken = default
    ) => Recent(null, artistIds, false, shows, days, cancellationToken);

    [HttpGet("artists/{artistId}/shows/recently-updated")]
    [ProducesResponseType(typeof(ShowSummaryResource[]), 200, "application/json")]
    public Task<IActionResult> ArtistRecentlyUpdated(
        string artistId,
        [FromQuery, Range(1, int.MaxValue)] int? shows = null,
        [FromQuery, Range(1, int.MaxValue)] int? days = null,
        CancellationToken cancellationToken = default
    ) => Recent(artistId, null, false, shows, days, cancellationToken);

    private Task<IActionResult> Recent(
        string? artistId,
        string[]? artistIds,
        bool performed,
        int? shows,
        int? days,
        CancellationToken cancellationToken
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
        return Summaries(
            artistId,
            artist =>
                discovery.ReadRecent(
                    artist,
                    artists.Distinct().ToArray(),
                    performed,
                    shows,
                    days,
                    cancellationToken
                )
        );
    }

    /// <summary>Newest source timestamp first; defaults to 20 shows, capped at 200.</summary>
    [HttpGet("shows/recently-added")]
    [ProducesResponseType(typeof(ShowSummaryResource[]), 200, "application/json")]
    public Task<IActionResult> RecentlyAdded(
        [FromQuery, Range(1, int.MaxValue)] int limit = 20,
        [FromQuery(Name = "previous_days"), Range(0, int.MaxValue)] int? previousDays = null,
        [FromQuery(Name = "sort_by"), AllowedValues("created_at", "updated_at")]
            string sortBy = "created_at",
        CancellationToken cancellationToken = default
    ) => Added(null, limit, previousDays, sortBy, cancellationToken);

    [HttpGet("artists/{artistId}/shows/recently-added")]
    [ProducesResponseType(typeof(ShowSummaryResource[]), 200, "application/json")]
    public Task<IActionResult> ArtistRecentlyAdded(
        string artistId,
        [FromQuery, Range(1, int.MaxValue)] int limit = 20,
        [FromQuery(Name = "previous_days"), Range(0, int.MaxValue)] int? previousDays = null,
        [FromQuery(Name = "sort_by"), AllowedValues("created_at", "updated_at")]
            string sortBy = "created_at",
        CancellationToken cancellationToken = default
    ) => Added(artistId, limit, previousDays, sortBy, cancellationToken);

    private Task<IActionResult> Added(
        string? artistId,
        int limit,
        int? previousDays,
        string sortBy,
        CancellationToken cancellationToken
    ) =>
        Summaries(
            artistId,
            artist =>
                discovery.ReadRecentlyAdded(
                    artist,
                    limit,
                    previousDays,
                    sortBy == "updated_at",
                    cancellationToken
                )
        );

    [HttpGet("artists/{artistId}/shows/top")]
    [ProducesResponseType(typeof(ShowSummaryResource[]), 200, "application/json")]
    public Task<IActionResult> Top(
        string artistId,
        [FromQuery, Range(1, int.MaxValue)] int limit = 25,
        CancellationToken cancellationToken = default
    ) => Summaries(artistId, artist => discovery.ReadTop(artist!.Value, limit, cancellationToken));

    /// <summary>Full recording hierarchy for the exact imported display_date, including partial dates.</summary>
    [HttpGet("artists/{artistId}/shows/by-date/{showDate}")]
    [ProducesResponseType(typeof(ShowResource), 200, "application/json")]
    public Task<IActionResult> ByDate(
        string artistId,
        string showDate,
        CancellationToken cancellationToken
    ) =>
        Read(
            artistId,
            ArtistId.Prefix,
            async artist =>
            {
                var show = await recordings.ReadShowByDate(artist, showDate, cancellationToken);
                return show is null ? null : V4CatalogProjection.Show(show);
            }
        );

    /// <summary>Full random recording hierarchy. Unknown or empty artists return 404. No cache validation.</summary>
    [HttpGet("artists/{artistId}/shows/random")]
    [ProducesResponseType(typeof(ShowResource), 200, "application/json")]
    public Task<IActionResult> Random(string artistId, CancellationToken cancellationToken) =>
        Read(
            artistId,
            ArtistId.Prefix,
            async artist =>
            {
                var show = await recordings.ReadRandomShow(artist, cancellationToken);
                return show is null ? null : V4CatalogProjection.Show(show);
            },
            cacheable: false
        );

    private Task<IActionResult> Summaries(
        string? artistId,
        Func<Guid?, Task<IReadOnlyList<CatalogShow>?>> read
    )
    {
        Guid? artist = null;
        if (artistId is not null)
        {
            if (!ArtistId.TryParse(artistId, out var parsed))
            {
                return Task.FromResult<IActionResult>(InvalidId(ArtistId.Prefix));
            }
            artist = parsed.Value;
        }
        return Execute(async () =>
            (await read(artist))?.Select(V4CatalogProjection.ShowSummary).ToArray()
        );
    }
}
