using System.Threading.Tasks;
using Hangfire;
using Hangfire.RecurringJobExtensions;
using Hangfire.Server;
using Relisten.Api.Models;
using Relisten.Data;
using Relisten.Import;
using Hangfire.Console;
using System.Linq;
using Microsoft.Extensions.Configuration;
using System.ComponentModel;

namespace Relisten
{
	public class ScheduledService
	{
		ImporterService _importerService { get; set; }
		ArtistService _artistService { get; set; }
		IConfigurationRoot _config { get; set; }

		public ScheduledService(
			ImporterService importerService,
			ArtistService artistService,
			IConfigurationRoot config
		)
		{
			_importerService = importerService;
			_artistService = artistService;
			_config = config;
		}

		// run every day at 3AM
		[RecurringJob("0 3 * * *", Enabled = true)]
		[AutomaticRetry(Attempts = 0)]
		[Queue("artist_import")]
		[DisplayName("Refresh All Artists")]
		public async Task RefreshAllArtists(PerformContext context)
		{
			if (_config["ASPNETCORE_ENVIRONMENT"] != "Production")
			{
				context.WriteLine($"Not running in {_config["ASPNETCORE_ENVIRONMENT"]}");
//				return;
			}

			var artists = (await _artistService.All()).ToList();

			context.WriteLine($"--> Updating all {artists.Count} artists");

			foreach (var artist in artists)
			{
				context.WriteLine($"--> Queueing {artist.name} ({artist.slug})");
				BackgroundJob.Enqueue(() => RefreshArtist(artist.slug, null));
			}

			context.WriteLine("--> Queued updates for all artists! ");
		}

		// this actually runs all of them without enqueing anything else
		[Queue("artist_import")]
		[AutomaticRetry(Attempts = 0)]
		private async Task __old(PerformContext context)
		{
			var artists = (await _artistService.All()).ToList();

			context.WriteLine($"--> Updating all {artists.Count} artists");

			var progress = context.WriteProgressBar();

			var stats = new ImportStats();

			await artists.AsyncForEachWithProgress(progress, async artist =>
			{
				context.WriteLine($"--> Importing {artist.name}");

				var artistStats = await _importerService.Import(artist, context);

				context.WriteLine($"--> Imported {artist.name}! " + artistStats);

				stats += artistStats;
			});

			context.WriteLine("--> Imported all artists! " + stats);
		}

		[Queue("artist_import")]
		[DisplayName("Refresh Artist: {0}")]
		[AutomaticRetry(Attempts = 0)]
		public async Task RefreshArtist(string idOrSlug, PerformContext ctx)
		{
			var artist = await _artistService.FindArtistWithIdOrSlug(idOrSlug);

			var artistStats = await _importerService.Import(artist, ctx);

			ctx?.WriteLine($"--> Imported {artist.name}! " + artistStats);
		}
	}
}
