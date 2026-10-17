using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Relisten.Data;

namespace Relisten.Controllers;

/// <summary>Reads accepted repertoire, works and performance accounts without changing legacy playback routes.</summary>
[ApiController]
[AllowAnonymous]
[Route("api/v3/normalization")]
[Produces("application/json")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[NormalizationReadValidation]
public sealed class NormalizationMusicController(NormalizationReadService reads)
    : NormalizationReadControllerBase
{
    [HttpGet("artists/{artist}/songs")]
    public Task<IActionResult> Songs(
        string artist,
        [FromQuery] string? q = null,
        [FromQuery] string? basis = null,
        [FromQuery] int limit = 50,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default
    ) =>
        ReadAsync(async () =>
            await reads.ListRepertoireAsync(
                artist,
                QueryValue("q", q),
                QueryValue("basis", basis),
                limit,
                QueryValue("cursor", cursor),
                cancellationToken
            )
        );

    [HttpGet("songs/{uuid}")]
    public Task<IActionResult> Song(string uuid, CancellationToken cancellationToken = default) =>
        ReadAsync(() => reads.GetSongAsync(ParseUuid(uuid), cancellationToken));

    [HttpGet("songs/{uuid}/shows")]
    public Task<IActionResult> SongShows(
        string uuid,
        [FromQuery] int limit = 50,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default
    ) =>
        ReadAsync(() =>
            reads.ListSongShowsAsync(
                ParseUuid(uuid),
                limit,
                QueryValue("cursor", cursor),
                cancellationToken
            )
        );

    [HttpGet("compositions")]
    public Task<IActionResult> Compositions(
        [FromQuery] string? q = null,
        [FromQuery] int limit = 50,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default
    ) =>
        ReadAsync(async () =>
            await reads.ListCompositionsAsync(
                QueryValue("q", q),
                limit,
                QueryValue("cursor", cursor),
                cancellationToken
            )
        );

    [HttpGet("compositions/{uuid}")]
    public Task<IActionResult> Composition(
        string uuid,
        CancellationToken cancellationToken = default
    ) => ReadAsync(() => reads.GetCompositionAsync(ParseUuid(uuid), cancellationToken));

    [HttpGet("compositions/{uuid}/shows")]
    public Task<IActionResult> CompositionShows(
        string uuid,
        [FromQuery(Name = "artist_uuid")] string? artistUuid = null,
        [FromQuery] int limit = 50,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default
    ) =>
        ReadAsync(() =>
            reads.ListCompositionShowsAsync(
                ParseUuid(uuid),
                ParseOptionalUuid(QueryValue("artist_uuid", artistUuid)),
                limit,
                QueryValue("cursor", cursor),
                cancellationToken
            )
        );

    [HttpGet("artists/{artist}/setlists")]
    public Task<IActionResult> ArtistSetlists(
        string artist,
        [FromQuery] int limit = 50,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default
    ) =>
        ReadAsync(async () =>
            await reads.ListArtistSetlistsAsync(
                artist,
                limit,
                QueryValue("cursor", cursor),
                cancellationToken
            )
        );

    [HttpGet("shows/{uuid}/setlists")]
    public Task<IActionResult> ShowSetlists(
        string uuid,
        [FromQuery] int limit = 50,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default
    ) =>
        ReadAsync(() =>
            reads.ListShowSetlistsAsync(
                ParseUuid(uuid),
                limit,
                QueryValue("cursor", cursor),
                cancellationToken
            )
        );

    [HttpGet("setlists/{uuid}/sets")]
    public Task<IActionResult> Sets(
        string uuid,
        [FromQuery] int limit = 50,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default
    ) =>
        ReadAsync(() =>
            reads.ListSetlistSetsAsync(
                ParseUuid(uuid),
                limit,
                QueryValue("cursor", cursor),
                cancellationToken
            )
        );

    [HttpGet("setlists/{uuid}/occurrences")]
    public Task<IActionResult> Occurrences(
        string uuid,
        [FromQuery] int limit = 50,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default
    ) =>
        ReadAsync(() =>
            reads.ListOccurrencesAsync(
                ParseUuid(uuid),
                limit,
                QueryValue("cursor", cursor),
                cancellationToken
            )
        );

    [HttpGet("tracks/{uuid}/contents")]
    public Task<IActionResult> TrackContents(
        string uuid,
        [FromQuery] int limit = 50,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default
    ) =>
        ReadAsync(() =>
            reads.ListTrackContentsAsync(
                ParseUuid(uuid),
                limit,
                QueryValue("cursor", cursor),
                cancellationToken
            )
        );
}
