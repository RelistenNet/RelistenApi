using System;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Npgsql;
using Relisten.Catalog;
using Relisten.Identifiers;
using Relisten.Normalization;
using Relisten.Services.Popularity;

namespace Relisten.Controllers.V4;

[Route("api/v4/artists/{artistId}")]
[ApiExplorerSettings(GroupName = "v4")]
[Produces("application/json")]
public sealed class CatalogPerformanceController(
    CatalogReviewSearch reviews,
    CatalogPerformances performances,
    PopularityRefreshRequest refresh,
    ILogger<CatalogPerformanceController> logger
) : CatalogControllerBase(logger, refresh)
{
    [HttpGet("reviews/search")]
    [ServiceFilter(typeof(ReviewSearchRateLimit))]
    [ProducesResponseType(typeof(CatalogReviewHit[]), 200)]
    public async Task<IActionResult> SearchReviews(
        string artistId,
        string? q,
        string? pattern,
        [FromQuery(Name = "song_id")] string? songId,
        [FromQuery(Name = "show_id")] string? showId,
        string? years,
        [Range(1, 25)] int limit = 10,
        CancellationToken cancellationToken = default
    )
    {
        if (
            !TryYears(years, out var parsedYears)
            || (showId is not null && !UuidTypeId.TryParse(showId, ShowId.Prefix, out _))
            || (songId is not null && !IsSong(songId))
            || new[] { q, pattern, songId }.Count(value => value is not null) > 1
            || q is { Length: > 200 }
            || (pattern is not null && (pattern.Length > 100 || Regex.IsMatch(pattern, @"\\[0-9]")))
        )
        {
            return Failure(
                422,
                "invalid_review_search",
                "Use one literal query, song ID or pattern (up to 100 characters with no backreferences), and valid show and year filters."
            );
        }

        var query = new CatalogReviewQuery(
            q,
            pattern,
            songId,
            showId is null ? null : ShowId.Parse(showId).Value,
            parsedYears,
            limit
        );
        try
        {
            return await Read(
                artistId,
                ArtistId.Prefix,
                async uuid =>
                {
                    try
                    {
                        return await reviews.Read(uuid, query, cancellationToken);
                    }
                    catch (PostgresException exception)
                        when (exception.SqlState == PostgresErrorCodes.QueryCanceled
                            && !cancellationToken.IsCancellationRequested
                        )
                    {
                        throw new ReviewQueryRejected(
                            "review_search_timeout",
                            "Narrow the review search with a show, year, or more specific expression."
                        );
                    }
                    catch (PostgresException exception)
                        when (exception.SqlState == PostgresErrorCodes.InvalidRegularExpression)
                    {
                        throw new ReviewQueryRejected(
                            "invalid_review_pattern",
                            "Use a valid PostgreSQL regular expression."
                        );
                    }
                },
                cacheable: false
            );
        }
        catch (ReviewQueryRejected rejection)
        {
            return Failure(422, rejection.Code, rejection.Message);
        }
    }

    private sealed class ReviewQueryRejected(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }

    [HttpGet("performances")]
    [ProducesResponseType(typeof(CatalogPerformancePage), 200)]
    public Task<IActionResult> List(
        string artistId,
        [FromQuery(Name = "song_id")] string? songId,
        string? years,
        [FromQuery(Name = "tour_id")] string? tourId,
        [FromQuery(Name = "venue_id")] string? venueId,
        [FromQuery(Name = "min_minutes")] double? minMinutes,
        [FromQuery(Name = "max_minutes")] double? maxMinutes,
        [FromQuery(Name = "soundboard_only")] bool soundboardOnly,
        [FromQuery(Name = "segued_from")] string? seguedFrom,
        [FromQuery(Name = "segued_into")] string? seguedInto,
        string order = "date",
        string? seed = null,
        string? exclude = null,
        [Range(1, 50)] int limit = 20,
        string? cursor = null,
        CancellationToken cancellationToken = default
    )
    {
        var excluded = exclude?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (
            !TryYears(years, out var parsedYears)
            || new[] { songId, seguedFrom, seguedInto }.Any(value =>
                value is not null && !IsSong(value)
            )
            || (tourId is not null && !UuidTypeId.TryParse(tourId, TourId.Prefix, out _))
            || (venueId is not null && !IsVenue(venueId))
            || !ValidMinutes(minMinutes)
            || !ValidMinutes(maxMinutes)
            || minMinutes > maxMinutes
            || order is not ("date" or "random" or "duration" or "rating" or "review_count")
            || seed is { Length: > 100 }
            || (order == "random" && string.IsNullOrWhiteSpace(seed))
            || excluded.Length > 100
            || excluded.Any(id =>
                !UuidTypeId.TryParse(id, ShowId.Prefix, out _)
                && !UuidTypeId.TryParse(id, TrackId.Prefix, out _)
            )
            || !CatalogPerformanceCursor.TryRead(cursor, out var after)
        )
        {
            return Task.FromResult<IActionResult>(
                Failure(
                    422,
                    "invalid_performance_query",
                    "Check the performance filters, sort order, exclusions and cursor. Random order requires a seed."
                )
            );
        }

        var query = new CatalogPerformanceQuery(
            songId,
            parsedYears,
            tourId is null ? null : TourId.Parse(tourId).Value,
            venueId,
            minMinutes * 60,
            maxMinutes * 60,
            soundboardOnly,
            seguedFrom,
            seguedInto,
            order,
            seed,
            excluded
                .Where(id => id.StartsWith(ShowId.Prefix, StringComparison.Ordinal))
                .Select(id => ShowId.Parse(id).Value)
                .ToArray(),
            excluded
                .Where(id => id.StartsWith(TrackId.Prefix, StringComparison.Ordinal))
                .Select(id => TrackId.Parse(id).Value)
                .ToArray(),
            limit,
            after
        );
        return Read(
            artistId,
            ArtistId.Prefix,
            uuid => performances.Read(uuid, query, cancellationToken),
            cacheable: false
        );
    }

    private static bool ValidMinutes(double? minutes) =>
        minutes is null || (double.IsFinite(minutes.Value) && minutes >= 0);

    private static bool IsSong(string id)
    {
        try
        {
            SongId.Parse(id);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsVenue(string id)
    {
        if (UuidTypeId.TryParse(id, ImportedVenueId.Prefix, out _))
        {
            return true;
        }
        try
        {
            if (id.StartsWith(ArtistVenueId.Prefix, StringComparison.Ordinal))
            {
                ArtistVenueId.Parse(id);
            }
            else
            {
                VenueId.Parse(id);
            }
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryYears(string? years, out int[]? values)
    {
        values = null;
        if (years is null)
        {
            return true;
        }
        var parts = years.Split(',');
        var parsed = new int[parts.Length];
        if (parts.Length > 100)
        {
            return false;
        }
        for (var index = 0; index < parts.Length; index++)
        {
            if (
                !int.TryParse(
                    parts[index],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out parsed[index]
                ) || parsed[index] is < 1 or > 9999
            )
            {
                return false;
            }
        }
        values = parsed;
        return true;
    }
}
