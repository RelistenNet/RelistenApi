using System.Threading.Tasks;
using Hangfire;
using Hangfire.RecurringJobExtensions;
using Hangfire.Server;
using Relisten.Api.Models;
using Relisten.Data;
using Relisten.Import;

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
		public async Task RefreshAllArtists(PerformContext context)
		{
			foreach(var artist in await _artistService.All()) {
				EnqueueArtistRefresh(artist);
			}
		}

		public static void EnqueueArtistRefresh(Artist artist)
		{
			BackgroundJob.Enqueue<ScheduledService>(s => s.RefreshArtistById(artist.id));
		}

		public async Task<ImportStats> RefreshArtistById(int id)
		{
			var artist = await _artistService.FindArtistById(id);

			return await _importerService.Import(artist);
		}
	}
}
