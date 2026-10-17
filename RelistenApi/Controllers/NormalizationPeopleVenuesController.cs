using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Relisten.Data;

namespace Relisten.Controllers;

/// <summary>Browses accepted global people and venue identities without changing legacy playback responses.</summary>
[ApiController]
[AllowAnonymous]
[Route("api/v3/normalization")]
[Produces("application/json")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[NormalizationReadValidation]
public sealed class NormalizationPeopleVenuesController(NormalizationReadService reads)
    : NormalizationReadControllerBase
{
    [HttpGet("people")]
    public Task<IActionResult> People(
        [FromQuery] string? q = null,
        [FromQuery] int limit = 50,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default
    ) =>
        ReadAsync(async () =>
            await reads.ListPeopleAsync(
                QueryValue("q", q),
                limit,
                QueryValue("cursor", cursor),
                cancellationToken
            )
        );

    [HttpGet("people/{uuid}")]
    public Task<IActionResult> Person(string uuid, CancellationToken cancellationToken = default) =>
        ReadAsync(() => reads.GetPersonAsync(ParseUuid(uuid), cancellationToken));

    [HttpGet("people/{uuid}/recordings")]
    public Task<IActionResult> PersonRecordings(
        string uuid,
        [FromQuery] string? role = null,
        [FromQuery(Name = "artist_uuid")] string? artistUuid = null,
        [FromQuery] int limit = 50,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default
    ) =>
        ReadAsync(() =>
            reads.ListPersonRecordingsAsync(
                ParseUuid(uuid),
                QueryValue("role", role),
                ParseOptionalUuid(QueryValue("artist_uuid", artistUuid)),
                limit,
                QueryValue("cursor", cursor),
                cancellationToken
            )
        );

    [HttpGet("venues")]
    public Task<IActionResult> Venues(
        [FromQuery] string? q = null,
        [FromQuery] int limit = 50,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default
    ) =>
        ReadAsync(async () =>
            await reads.ListVenuesAsync(
                QueryValue("q", q),
                limit,
                QueryValue("cursor", cursor),
                cancellationToken
            )
        );

    [HttpGet("venues/{uuid}")]
    public Task<IActionResult> Venue(string uuid, CancellationToken cancellationToken = default) =>
        ReadAsync(() => reads.GetVenueAsync(ParseUuid(uuid), cancellationToken));

    [HttpGet("venues/{uuid}/shows")]
    public Task<IActionResult> VenueShows(
        string uuid,
        [FromQuery(Name = "artist_uuid")] string? artistUuid = null,
        [FromQuery] int limit = 50,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default
    ) =>
        ReadAsync(() =>
            reads.ListVenueShowsAsync(
                ParseUuid(uuid),
                ParseOptionalUuid(QueryValue("artist_uuid", artistUuid)),
                limit,
                QueryValue("cursor", cursor),
                cancellationToken
            )
        );

    [HttpGet("recordings/{uuid}/credits")]
    public Task<IActionResult> Credits(
        string uuid,
        [FromQuery] int limit = 50,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default
    ) =>
        ReadAsync(() =>
            reads.ListRecordingCreditsAsync(
                ParseUuid(uuid),
                limit,
                QueryValue("cursor", cursor),
                cancellationToken
            )
        );
}
