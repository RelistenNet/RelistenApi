using System;
using System.Threading.Tasks;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Relisten.Api;
using Relisten.Api.Models;
using Relisten.Data;
using Relisten.Import;
using Relisten.Ingestion;

namespace Relisten.Controllers
{
    [Route("api/v2/import/")]
    [ApiExplorerSettings(IgnoreApi = true)]
    [ImportAdmissionErrors]
    public class ImportController : RelistenBaseController
    {
        public ImportController(
            RedisService redis,
            DbService db,
            ArtistService artistService,
            ImporterService importer,
            ScheduledService scheduledService,
            IConfiguration configuration,
            TemporalIngestionClient temporal
        )
            : base(redis, db, artistService)
        {
            _configuration = configuration;
            _temporal = temporal;
            _scheduledService = scheduledService;
            _importer = importer;
        }

        private readonly TemporalIngestionClient _temporal;
        private readonly ImporterService _importer;
        private readonly ScheduledService _scheduledService;
        private readonly IConfiguration _configuration;

        [HttpPost("{idOrSlug}")]
        [Authorize]
        public async Task<IActionResult> Post(
            string idOrSlug,
            [FromQuery] bool deleteOldContent = false
        )
        {
            var art = await _artistService.FindArtistWithIdOrSlug(idOrSlug);
            if (art != null)
            {
                if (_temporal.Enabled && deleteOldContent)
                    return Conflict(
                        "Stop Temporal ingestion and select Hangfire before a destructive reimport."
                    );
                var jobId = _temporal.Enabled
                    ? (
                        await _temporal.RefreshArtistAsync(
                            new RefreshArtist(new ArtistId(art.uuid))
                        )
                    ).WorkflowId
                    : BackgroundJob.Enqueue(() =>
                        _scheduledService.RefreshArtist(idOrSlug, deleteOldContent, null)
                    );

                return StatusCode(201, $"Queued as job {jobId}!");
            }

            return JsonNotFound(false);
        }

        [HttpPost("{idOrSlug}/debug")]
        [Authorize]
        public async Task<IActionResult> PostDebug(
            string idOrSlug,
            [FromQuery] bool deleteOldContent = false
        )
        {
            var art = await _artistService.FindArtistWithIdOrSlug(idOrSlug);
            if (art != null)
            {
                if (_temporal.Enabled)
                    return Conflict(
                        "Stop Temporal ingestion and select Hangfire before a synchronous debug import."
                    );
                await _scheduledService.RefreshArtist(idOrSlug, deleteOldContent, null);

                return StatusCode(201, "done!");
            }

            return JsonNotFound(false);
        }

        [HttpPost("phishin")]
        public async Task<IActionResult> JustRefreshPhishin()
        {
            if (
                !Request.Headers.TryGetValue("X-Relisten-Api-Key", out var key)
                || key != _configuration["PANIC_KEY"]
            )
            {
                return Unauthorized();
            }

            if (_temporal.Enabled)
            {
                var artist = await _artistService.FindArtistWithIdOrSlug("phish");
                if (artist == null)
                    return JsonNotFound(false);
                return StatusCode(
                    201,
                    (
                        await _temporal.PhishIn.RefreshAsync(
                            new RefreshPhishInArtist(new ArtistId(artist.uuid))
                        )
                    ).WorkflowId
                );
            }
            return StatusCode(
                201,
                BackgroundJob.Enqueue(() => _scheduledService.RefreshPhishFromPhishinOnly(null))
            );
        }

        [HttpPost("all-years-and-shows")]
        [Authorize]
        public async Task<IActionResult> AllYearsAndShows()
        {
            return StatusCode(
                201,
                _temporal.Enabled
                    ? (await _temporal.RebuildCatalogAsync(new RebuildCatalog())).WorkflowId
                    : BackgroundJob.Enqueue(() => _scheduledService.RebuildAllArtists(null))
            );
        }

        [HttpPost("{idOrSlug}/{showIdentifier}")]
        [Authorize]
        public async Task<IActionResult> PostShow(
            string idOrSlug,
            string showIdentifier,
            [FromQuery] bool deleteOldContent = false
        )
        {
            var art = await _artistService.FindArtistWithIdOrSlug(idOrSlug);
            if (art != null)
            {
                if (_temporal.Enabled && deleteOldContent)
                    return Conflict(
                        "Stop Temporal ingestion and select Hangfire before a destructive reimport."
                    );
                var jobId = _temporal.Enabled
                    ? (
                        await _temporal.Archive.ImportItemAsync(
                            new ImportArchiveItem(
                                new ArtistId(art.uuid),
                                new ArchiveItemId(showIdentifier)
                            )
                        )
                    ).WorkflowId
                    : BackgroundJob.Enqueue(() =>
                        _scheduledService.RefreshArtist(
                            idOrSlug,
                            showIdentifier,
                            deleteOldContent,
                            null
                        )
                    );

                return StatusCode(201, $"Queued as job {jobId}!");
            }

            return JsonNotFound(false);
        }

        [HttpPost("{idOrSlug}/{showIdentifier}/debug")]
        [Authorize]
        public async Task<IActionResult> PostShowDebug(
            string idOrSlug,
            string showIdentifier,
            [FromQuery] bool deleteOldContent = false
        )
        {
            var art = await _artistService.FindArtistWithIdOrSlug(idOrSlug);
            if (art != null)
            {
                if (_temporal.Enabled)
                    return Conflict(
                        "Stop Temporal ingestion and select Hangfire before a synchronous debug import."
                    );
                await _scheduledService.RefreshArtist(
                    idOrSlug,
                    showIdentifier,
                    deleteOldContent,
                    null,
                    null
                );

                return StatusCode(201, "done!");
            }

            return JsonNotFound(false);
        }

        // private void update()
        // {
        //     var updateDates = @"
        //     update shows s set updatedat = COALESCE((select MAX(updatedat) from tracks t WHERE t.showid = s.id), s.createdat);
        //     update years y set updatedat = COALESCE((select MAX(updatedat) from shows s WHERE s.year = y.year), y.createdat);
        //     update artists a set updatedat = COALESCE((select MAX(updatedat) from years y WHERE y.artistid = a.id), a.createdat);
        //     ";
        // }
    }
}
