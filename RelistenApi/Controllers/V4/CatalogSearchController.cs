using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
    public Task<IActionResult> Search(
        [FromQuery] CatalogSearchParameters request,
        CancellationToken cancellationToken
    ) => SearchCore(request, exchange: false, cancellationToken);

    [HttpGet(CatalogSearchExchange.Path)]
    [ApiExplorerSettings(IgnoreApi = true)]
    public Task<IActionResult> Candidates(
        [FromQuery] CatalogSearchParameters request,
        CancellationToken cancellationToken
    ) =>
        AuthorizedService()
            ? SearchCore(request, exchange: true, cancellationToken)
            : Task.FromResult<IActionResult>(NotFound());

    private async Task<IActionResult> SearchCore(
        CatalogSearchParameters request,
        bool exchange,
        CancellationToken cancellationToken
    )
    {
        var artistId = request.ArtistId;
        var country = request.Country;
        if (artistId != null && !ArtistId.TryParse(artistId, out _))
        {
            return InvalidId(ArtistId.Prefix);
        }

        var boosts = SplitValues(request.BoostArtistIds).ToArray();
        if (
            !ModelState.IsValid
            || !TryScope(request.Scope, out var resultScope)
            || request.Mode is not ("explore" or "add_tracks")
            || (request.Q?.Length ?? 0) > 256
            || (request.RequestId?.Length ?? 0) > 128
            || (request.ReadingId?.Length ?? 0) > 192
            || request.Page is < 1 or > 100
            || country != null && (country.Length != 2 || !country.All(char.IsAsciiLetter))
            || boosts.Length > 32
            || boosts.Any(value => !ArtistId.TryParse(value, out _))
        )
        {
            return Failure(422, "invalid_query", "The query contains an invalid search value.");
        }

        var input = new SearchInput(
            request.Q ?? "",
            resultScope,
            request.Mode == "add_tracks",
            request.Typing,
            artistId,
            country?.ToUpperInvariant(),
            boosts,
            SplitValues(request.Ignore).ToHashSet(StringComparer.Ordinal),
            request.Page,
            request.RequestId,
            request.ReadingId
        );
        Response.Headers.CacheControl = "no-store";
        var result = await coordinator.Search(input, cancellationToken);
        var json = exchange
            ? System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(result, SearchJson.Options)
            : System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                SearchResponse.From(
                    result.Page,
                    result.Candidates.Select(candidate => candidate.Result).ToArray()
                ),
                SearchJson.Options
            );
        return File(json, "application/json");
    }

    private bool AuthorizedService()
    {
        var services = HttpContext.RequestServices;
        var key = services.GetRequiredService<IConfiguration>()[CatalogSearchExchange.KeySetting];
        if (
            string.IsNullOrEmpty(key)
            && services.GetRequiredService<IWebHostEnvironment>().IsDevelopment()
        )
        {
            key = CatalogSearchExchange.DevelopmentKey;
        }
        return !string.IsNullOrEmpty(key)
            && Request.Headers.TryGetValue(CatalogSearchExchange.Header, out var supplied)
            && supplied.Count == 1
            && CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(key)),
                SHA256.HashData(Encoding.UTF8.GetBytes(supplied.ToString()))
            );
    }

    private static bool TryScope(string scope, out SearchScope value) =>
        Enum.TryParse(scope, ignoreCase: true, out value)
        && string.Equals(value.ToString(), scope, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> SplitValues(string[]? values) =>
        (values ?? [])
            .OfType<string>()
            .SelectMany(value =>
                value.Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                )
            );
}

public sealed record CatalogSearchParameters
{
    public string? Q { get; init; }
    public string Scope { get; init; } = "all";
    public string Mode { get; init; } = "explore";

    [FromQuery(Name = "artist_id")]
    public string? ArtistId { get; init; }
    public string? Country { get; init; }

    [FromQuery(Name = "boost_artist_ids")]
    public string[]? BoostArtistIds { get; init; }
    public string[]? Ignore { get; init; }
    public bool Typing { get; init; }
    public int Page { get; init; } = 1;

    [FromQuery(Name = "request_id")]
    public string? RequestId { get; init; }

    [FromQuery(Name = "reading_id")]
    public string? ReadingId { get; init; }
}
