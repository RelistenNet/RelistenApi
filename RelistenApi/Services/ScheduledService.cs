using System.Threading.Tasks;
using Hangfire;
using Hangfire.RecurringJobExtensions;
using Hangfire.Server;
using Relisten.Api.Models;
using Relisten.Data;
using Relisten.Import;
using Hangfire.Console;
using System.Linq;

namespace Relisten
{
	public class ScheduledService
	{
		ImporterService _importerService { get; set; }
		ArtistService _artistService { get; set; }

		public ScheduledService(
			ImporterService importerService,
			ArtistService artistService
		)
		{
			_importerService = importerService;
			_artistService = artistService;
		}

		// run every day at 3AM
		[RecurringJob("0 3 * * *")]
		[AutomaticRetry(Attempts = 0)]
		public async Task RefreshAllArtists(PerformContext context)
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

		[RecurringJob("0 6,9,12,15,18 * * *")]
		[AutomaticRetry(Attempts = 0)]
		public async Task RefreshPhish(PerformContext ctx)
		{
			var artist = await _artistService.FindArtistWithIdOrSlug("phish");

			var artistStats = await _importerService.Import(artist, ctx);

			ctx.WriteLine($"--> Imported {artist.name}! " + artistStats);
		}
	
		[RecurringJob("0 */5 * * *")]
		[AutomaticRetry(Attempts = 0)]
		public async Task RefreshWSP(PerformContext ctx)
		{
			var artist = await _artistService.FindArtistWithIdOrSlug("wsp");

			var artistStats = await _importerService.Import(artist, ctx);

			ctx.WriteLine($"--> Imported {artist.name}! " + artistStats);
		}
}
}
