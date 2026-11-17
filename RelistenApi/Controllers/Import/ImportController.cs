using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Relisten.Api;
using Relisten.Data;
using Relisten.Ingestion;

namespace Relisten.Controllers;

[Route("api/v2/import/")]
[ApiExplorerSettings(IgnoreApi = true)]
[ImportAdmissionErrors]
public class ImportController(
    RedisService redis,
    DbService db,
    ArtistService artistService,
    IConfiguration configuration,
    TemporalIngestionClient temporal
) : RelistenBaseController(redis, db, artistService)
{
    [HttpPost("{idOrSlug}")]
    [Authorize]
    public async Task<IActionResult> Post(
        string idOrSlug,
        [FromQuery] bool deleteOldContent = false,
        [FromQuery] int? archiveDeletionLimit = null
    )
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        if (deleteOldContent)
        {
            return Conflict(
                "Destructive reimports are unsupported. Use archiveDeletionLimit for verified cleanup."
            );
        }

        var artist = await _artistService.FindArtistForImport(idOrSlug);
        if (artist == null)
        {
            return JsonNotFound(false);
        }

        var handle = await temporal.RefreshAsync(
            new RefreshArtist(new ArtistId(artist.uuid), archiveDeletionLimit)
        );
        Response.Headers.Location = temporal.ExecutionUrl(handle);
        return StatusCode(201, $"Queued as job {handle.WorkflowId}!");
    }

    // Keep retired URLs explicit: old clients must not accidentally start a
    // synchronous writer or have "debug" interpreted as an Archive identifier.
    [HttpPost("{idOrSlug}/debug")]
    [HttpPost("{idOrSlug}/{showIdentifier}/debug")]
    [Authorize]
    public IActionResult DebugImport() =>
        Conflict(
            "Synchronous debug imports are unsupported. Use the queued import endpoint and Temporal execution history."
        );

    [HttpPost("phishin")]
    public async Task<IActionResult> JustRefreshPhishin()
    {
        if (
            !Request.Headers.TryGetValue("X-Relisten-Api-Key", out var key)
            || key != configuration["PANIC_KEY"]
        )
        {
            return Unauthorized();
        }

        var artist = await _artistService.FindArtistForImport("phish");
        if (artist == null)
        {
            return JsonNotFound(false);
        }

        var handle = await temporal.RefreshAsync(
            new RefreshPhishInArtist(new ArtistId(artist.uuid))
        );
        Response.Headers.Location = temporal.ExecutionUrl(handle);
        return StatusCode(201, handle.WorkflowId);
    }

    [HttpPost("all-years-and-shows")]
    [Authorize]
    public async Task<IActionResult> AllYearsAndShows()
    {
        var handle = await temporal.RebuildCatalogAsync(new RebuildCatalog());
        Response.Headers.Location = temporal.ExecutionUrl(handle);
        return StatusCode(201, handle.WorkflowId);
    }

    [HttpPost("{idOrSlug}/{showIdentifier}")]
    [Authorize]
    public async Task<IActionResult> PostShow(
        string idOrSlug,
        string showIdentifier,
        [FromQuery] bool deleteOldContent = false,
        [FromQuery] int? archiveDeletionLimit = null
    )
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        if (archiveDeletionLimit != null)
        {
            throw new InvalidImportRequest("Single-recording imports do not reconcile deletions.");
        }

        if (deleteOldContent)
        {
            return Conflict(
                "Destructive reimports are unsupported. Use a full refresh for verified cleanup."
            );
        }

        var artist = await _artistService.FindArtistForImport(idOrSlug);
        if (artist == null)
        {
            return JsonNotFound(false);
        }

        var handle = await temporal.ImportArchiveItemAsync(
            new ImportArchiveItem(new ArtistId(artist.uuid), new ArchiveItemId(showIdentifier))
        );
        Response.Headers.Location = temporal.ExecutionUrl(handle);
        return StatusCode(201, $"Queued as job {handle.WorkflowId}!");
    }
}
