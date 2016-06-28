using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Relisten.Api.Models;
using Dapper;
using Relisten.Vendor;
using Relisten.Data;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Relisten.Import
{
    [Flags]
    public enum ImportableData
    {
        Nothing = 0,
        Eras = 1 << 0,
        Tours = 1 << 1,
        Venues = 1 << 2,
        SetlistShowsAndSongs = 1 << 3,
        SourceReviews = 1 << 4,
        SourceRatings = 1 << 5,
        Sources = 1 << 6
    }

    public class ImportStats
    {
        public static readonly ImportStats None = new ImportStats();

        public int Updated { get; set; } = 0;
        public int Created { get; set; } = 0;
        public int Removed { get; set; } = 0;

        public static ImportStats operator +(ImportStats c1, ImportStats c2)
        {
            return new ImportStats()
            {
                Updated = c1.Updated + c2.Updated,
                Removed = c1.Removed + c2.Removed,
                Created = c1.Created + c2.Created
            };
        }

        public override string ToString()
        {
            return $"Created: {Created}; Updated: {Updated}; Removed: {Removed}";
        }
    }

    public abstract class ImporterBase : IDisposable
    {
        protected DbService db { get; set; }
        protected HttpClient http { get; set; }

        public ImporterBase(DbService db)
        {
            this.db = db;
            this.http = new HttpClient();
        }

        public abstract ImportableData ImportableDataForArtist(Artist artist);
        public abstract Task<ImportStats> ImportDataForArtist(Artist artist);

        public void Dispose()
        {
            this.http.Dispose();
        }

        public string Slugify(string full)
        {
            var slug = Regex.Replace(full.ToLower().Normalize(), @"['.]", "");
            slug = Regex.Replace(slug, @"[^a-z0-9\s-]", " ");

            return Regex.Replace(slug, @"\s+", " ").
                Trim().
                Replace(" ", "-");
        }

        public async Task<ImportStats> RebuildYears()
        {
            return ImportStats.None;
        }
        public async Task<ImportStats> RebuildShows()
        {
            return ImportStats.None;
        }
    }

    public class ArchiveOrgSetlistFmImporter
    {

    }
}
