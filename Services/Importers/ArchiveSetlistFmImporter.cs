using System;
using System.Collections.Generic;
using System.Data;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Relisten.Api.Models;
using Relisten.Vendor;

namespace Relisten.Import
{
    public interface IErasImporter
    {
        Task<ImportStats> updateEras(Artist artist);
    }

    public interface IToursImporter
    {
        Task<ImportStats> updateTours(Artist artist);
    }

    public interface IVenuesImporter
    {
        Task<ImportStats> updateVenues(Artist artist);
    }

    public interface ISetlistImporter
    {
        // called first
        Task<ImportStats> updateSetlistSongs(Artist artist);
        Task<ImportStats> updateSetlistShows(Artist artist);
    }

    public interface ISourceReviewImporter
    {
        Task<ImportStats> updateSourceReviews(Artist artist);
    }

    public interface ISourceRatingImporter
    {
        Task<ImportStats> updateSourceRatings(Artist artist);
    }

    public interface ISourceImporter
    {
        Task<ImportStats> updateSources(Artist artist);
    }

    public class ImportStats
    {
        public static readonly ImportStats None = new ImportStats();

        public int Updated { get; set; } = 0;
        public int Created { get; set; } = 0;

        public static ImportStats operator +(ImportStats c1, ImportStats c2)
        {
            return new ImportStats() {
                Updated = c1.Updated + c2.Updated,
                Created = c1.Created + c2.Created
            };
        } 
    }

    public abstract class ImporterBase : IDisposable
    {
        protected IDbConnection db { get; set; }
        protected HttpClient http { get; set; }

        public ImporterBase(DbService db)
        {
            this.db = db.connection;
            this.http = new HttpClient();
        }

        public void Dispose()
        {
            this.http.Dispose();
        }

        public async Task<ImportStats> rebuildYears()
        {
            return ImportStats.None;
        }
        public async Task<ImportStats> rebuildShows()
        {
            return ImportStats.None;
        }
    }

    public class ArchiveOrgImporter
    {

    }
    
    public class SetlistFmImporter : ImporterBase, ISetlistImporter, IVenuesImporter
    {
        private Artist artist { get; set; }

        public SetlistFmImporter(DbService db) : base(db) { }

        string setlistUrlForArtist(Artist artist, int page = 1)
        {
            return $"http://api.setlist.fm/rest/0.1/artist/{artist.musicbrainz_id}/setlists.json?p={page}";
        }

        async Task<Tuple<bool, ImportStats>> processSetlistPage(HttpResponseMessage res)
        {
            var root = JsonConvert.DeserializeObject<Relisten.Vendor.SetlistFm.SetlistsRootObject>(await res.Content.ReadAsStringAsync());

            var stats = new ImportStats();

            foreach(var setlist in root.setlists.setlist) {
                
            }

            var hasMorePages = root.setlists.page < Math.Ceiling(1.0 * root.setlists.total / root.setlists.itemsPerPage);

            return new Tuple<bool, ImportStats>(hasMorePages, stats); 
        }

        public async Task<ImportStats> updateSetlistShows(Artist artist)
        {
            int page = 1;
            Tuple<bool, ImportStats> result = null;
            var stats = ImportStats.None;

            do {
                result = await processSetlistPage(await this.http.GetAsync(setlistUrlForArtist(artist, page)));

                stats += result.Item2;
            } while(result != null && result.Item1);

            return stats;
        }

        public async Task<ImportStats> updateSetlistSongs(Artist artist)
        {
            return null;
        }
    }
    public class ArchiveOrgSetlistFmImporter
    {

    }
}
