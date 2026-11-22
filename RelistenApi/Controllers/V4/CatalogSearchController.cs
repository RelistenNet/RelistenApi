using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Relisten.Identifiers;
using Relisten.Search;
using Relisten.Services.Popularity;

namespace Relisten.Controllers.V4;

/// <summary>Searches the catalog for things to browse and exact files to play.</summary>
[Route("api/v4/search")]
[ApiExplorerSettings(GroupName = "v4")]
[ProducesResponseType(typeof(ProblemDetails), 422, "application/problem+json")]
public sealed class CatalogSearchController(
    CatalogSearchCoordinator coordinator,
    PopularityRefreshRequest refresh,
    ILogger<CatalogSearchController> logger
) : CatalogControllerBase(logger, refresh)
{
    [HttpGet]
    [ProducesResponseType(typeof(SearchResponse), 200, "application/json")]
    public async Task<IActionResult> Search(
        [FromQuery] string? q = null,
        [FromQuery] string scope = "all",
        [FromQuery] string mode = "explore",
        [FromQuery(Name = "artist_id")] string? artistId = null,
        [FromQuery] string? country = null,
        [FromQuery(Name = "boost_artist_ids")] string[]? boostArtistIds = null,
        [FromQuery] string[]? ignore = null,
        [FromQuery] bool typing = false,
        [FromQuery] int page = 1,
        [FromQuery(Name = "request_id")] string? requestId = null,
        CancellationToken cancellationToken = default
    )
    {
        if (artistId != null && !ArtistId.TryParse(artistId, out _))
        {
            return InvalidId(ArtistId.Prefix);
        }

        var boosts = SplitValues(boostArtistIds).ToArray();
        if (
            !ModelState.IsValid
            || !TryScope(scope, out var resultScope)
            || mode is not ("explore" or "add_tracks")
            || (q?.Length ?? 0) > 256
            || (requestId?.Length ?? 0) > 128
            || page is < 1 or > 100
            || country != null && (country.Length != 2 || !country.All(char.IsAsciiLetter))
            || boosts.Length > 32
            || boosts.Any(value => !ArtistId.TryParse(value, out _))
        )
        {
            return Failure(422, "invalid_query", "The query contains an invalid search value.");
        }

        var input = new SearchInput(
            q ?? "",
            resultScope,
            mode == "add_tracks",
            typing,
            artistId,
            country?.ToUpperInvariant(),
            boosts,
            SplitValues(ignore).ToHashSet(StringComparer.Ordinal),
            page,
            requestId
        );
        return new CatalogJsonResult(
            await coordinator.Search(input, cancellationToken),
            cacheable: false
        );
    }

    [HttpGet("performance")]
    [ProducesResponseType(typeof(SearchPerformance), 200, "application/json")]
    [ProducesResponseType(typeof(ProblemDetails), 404, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), 503, "application/problem+json")]
    public async Task<IActionResult> Performance(
        [FromQuery] string? key,
        CancellationToken cancellationToken
    )
    {
        if (
            key == null
            || key.Length != 29
            || !key.StartsWith("perf_", StringComparison.Ordinal)
            || !key.Skip(5).All(char.IsAsciiHexDigitLower)
        )
        {
            return Failure(
                422,
                "invalid_key",
                "Expected a performance key returned by catalog search."
            );
        }

        try
        {
            var result = await coordinator.Files(key, cancellationToken);
            return result == null
                ? Failure(404, "not_found", "The requested performance does not exist.")
                : new CatalogJsonResult(result, cacheable: false);
        }
        catch (Exception error)
            when (!cancellationToken.IsCancellationRequested
                && error
                    is HttpRequestException
                        or OperationCanceledException
                        or InvalidDataException
                        or System.Text.Json.JsonException
            )
        {
            logger.LogWarning(error, "Catalog performance lookup is unavailable");
            return Failure(503, "search_unavailable", "Catalog search is temporarily unavailable.");
        }
    }

    private static bool TryScope(string scope, out SearchScope value) =>
        Enum.TryParse(scope, ignoreCase: true, out value)
        && string.Equals(value.ToString(), scope, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> SplitValues(string[]? values) =>
        (values ?? []).SelectMany(value =>
            value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        );
}
