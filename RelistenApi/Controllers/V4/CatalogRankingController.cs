using System;
using System.ComponentModel.DataAnnotations;
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

/// <summary>Existing listening ranks with current catalog metadata and typed identities.</summary>
[Route("api/v4")]
[ApiExplorerSettings(GroupName = "v4")]
[ProducesResponseType(typeof(ProblemDetails), 422, "application/problem+json")]
[ProducesResponseType(typeof(ProblemDetails), 503, "application/problem+json")]
public sealed class CatalogRankingController(
    CatalogRankingReader reader,
    PopularityRefreshRequest refresh,
    ILogger<CatalogRankingController> logger
) : CatalogControllerBase(logger, refresh)
{
    [HttpGet("popular/artists")]
    [ProducesResponseType(typeof(RankedArtistResource[]), 200, "application/json")]
    public Task<IActionResult> PopularArtists(
        [FromQuery, Range(1, int.MaxValue)] int limit = 50,
        CancellationToken cancellationToken = default
    ) => Artists(false, limit, cancellationToken);

    [HttpGet("trending/artists")]
    [ProducesResponseType(typeof(RankedArtistResource[]), 200, "application/json")]
    public Task<IActionResult> TrendingArtists(
        [FromQuery, Range(1, int.MaxValue)] int limit = 50,
        CancellationToken cancellationToken = default
    ) => Artists(true, limit, cancellationToken);

    private Task<IActionResult> Artists(bool trending, int limit, CancellationToken token) =>
        Execute(async () =>
            (await reader.ReadArtists(trending, limit, token))
                .Select((item, index) => V4CatalogProjection.RankedArtist(item, index + 1))
                .ToArray()
        );

    [HttpGet("popular/years")]
    [ProducesResponseType(typeof(RankedYearResource[]), 200, "application/json")]
    public Task<IActionResult> PopularYears(
        [FromQuery, Range(1, int.MaxValue)] int limit = 50,
        CancellationToken cancellationToken = default
    ) => Years(false, limit, cancellationToken);

    [HttpGet("trending/years")]
    [ProducesResponseType(typeof(RankedYearResource[]), 200, "application/json")]
    public Task<IActionResult> TrendingYears(
        [FromQuery, Range(1, int.MaxValue)] int limit = 50,
        CancellationToken cancellationToken = default
    ) => Years(true, limit, cancellationToken);

    private Task<IActionResult> Years(bool trending, int limit, CancellationToken token) =>
        Execute(async () =>
            (await reader.ReadYears(trending, limit, token))
                .Select((item, index) => V4CatalogProjection.RankedYear(item, index + 1))
                .ToArray()
        );

    [HttpGet("popular/shows")]
    [ProducesResponseType(typeof(RankedShowResource[]), 200, "application/json")]
    public Task<IActionResult> PopularShows(
        [FromQuery] int limit = 25,
        [FromQuery, AllowedValues("48h", "7d", "30d")] string window = "30d",
        CancellationToken cancellationToken = default
    ) => Shows(false, limit, window, cancellationToken);

    [HttpGet("trending/shows")]
    [ProducesResponseType(typeof(RankedShowResource[]), 200, "application/json")]
    public Task<IActionResult> TrendingShows(
        [FromQuery] int limit = 25,
        [FromQuery, AllowedValues("48h", "7d", "30d")] string window = "30d",
        CancellationToken cancellationToken = default
    ) => Shows(true, limit, window, cancellationToken);

    private Task<IActionResult> Shows(
        bool trending,
        int limit,
        string window,
        CancellationToken token
    ) =>
        Execute(async () =>
            (await reader.ReadShows(trending, Limit(limit), Window(window), token))
                .Select((item, index) => V4CatalogProjection.RankedShow(item, index + 1))
                .ToArray()
        );

    [HttpGet("artists/{artistId}/shows/popular-trending")]
    [ProducesResponseType(typeof(ArtistRankingResource), 200, "application/json")]
    [ProducesResponseType(typeof(ProblemDetails), 404, "application/problem+json")]
    public Task<IActionResult> ArtistShows(
        string artistId,
        [FromQuery] int limit = 25,
        [FromQuery, AllowedValues("48h", "7d", "30d")] string window = "30d",
        CancellationToken cancellationToken = default
    ) =>
        Read(
            artistId,
            ArtistId.Prefix,
            async artist =>
            {
                var groups = await reader.ReadArtistGroups(
                    [artist],
                    Limit(limit),
                    Window(window),
                    cancellationToken
                );
                return groups.Count == 0 ? null : V4CatalogProjection.RankingGroup(groups[0]);
            }
        );

    /// <summary>One group per requested existing artist; empty artist_ids means no groups.</summary>
    [HttpGet("shows/popular-trending")]
    [ProducesResponseType(typeof(ArtistRankingResource[]), 200, "application/json")]
    public Task<IActionResult> ArtistGroups(
        [FromQuery(Name = "artist_ids")] string[]? artistIds = null,
        [FromQuery] int limit = 10,
        [FromQuery, AllowedValues("48h", "7d", "30d")] string window = "30d",
        CancellationToken cancellationToken = default
    ) =>
        WithArtists(
            artistIds,
            async artists =>
                (
                    await reader.ReadArtistGroups(
                        artists,
                        Limit(limit, 10),
                        Window(window),
                        cancellationToken
                    )
                )
                    .Select(V4CatalogProjection.RankingGroup)
                    .ToArray()
        );

    [HttpGet("shows/momentum")]
    [ProducesResponseType(typeof(RankedShowResource[]), 200, "application/json")]
    public Task<IActionResult> Momentum(
        [FromQuery(Name = "artist_ids")] string[]? artistIds = null,
        [FromQuery] int limit = 25,
        CancellationToken cancellationToken = default
    ) =>
        WithArtists(
            artistIds,
            async artists =>
                (await reader.ReadMomentum(artists, Limit(limit), cancellationToken))
                    .Select((item, index) => V4CatalogProjection.RankedShow(item, index + 1))
                    .ToArray()
        );

    private Task<IActionResult> WithArtists<T>(string[]? artistIds, Func<Guid[], Task<T?>> read)
        where T : class
    {
        var artists = new Guid[artistIds?.Length ?? 0];
        for (var i = 0; i < artists.Length; i++)
        {
            if (!ArtistId.TryParse(artistIds![i], out var id))
            {
                return Task.FromResult<IActionResult>(InvalidId(ArtistId.Prefix));
            }
            artists[i] = id.Value;
        }
        return Execute(() => read(artists.Distinct().ToArray()));
    }

    private static int Limit(int limit, int max = 25) => limit <= 0 ? max : Math.Min(limit, max);

    private static PopularitySortWindow Window(string window) =>
        window switch
        {
            "48h" => PopularitySortWindow.Hours48,
            "7d" => PopularitySortWindow.Days7,
            _ => PopularitySortWindow.Days30,
        };
}
