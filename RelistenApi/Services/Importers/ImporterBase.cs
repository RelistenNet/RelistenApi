using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Hangfire.Console;
using Hangfire.Server;
using Microsoft.Extensions.Logging;
using Relisten.Api.Models;
using Relisten.Data;

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
        Sources = 1 << 6,
    }

    public class ImportStats
    {
        public static readonly ImportStats None = new();

        public int Updated { get; set; }
        public int Created { get; set; }
        public int Removed { get; set; }

        public static ImportStats operator +(ImportStats c1, ImportStats c2)
        {
            return new ImportStats
            {
                Updated = c1.Updated + c2.Updated,
                Removed = c1.Removed + c2.Removed,
                Created = c1.Created + c2.Created,
            };
        }

        public override string ToString()
        {
            return $"Created: {Created}; Updated: {Updated}; Removed: {Removed}";
        }
    }

    public class ImporterService
    {
        public ImporterService(
            ArchiveOrgImporter _archiveOrg,
            SetlistFmImporter _setlistFm,
            PhishinImporter _phishin,
            PhishNetImporter _phishnet,
            JerryGarciaComImporter _jerry,
            PhantasyTourImporter _phantasy,
            LocalImporter _local,
            ILogger<ImporterService> log
        )
        {
            this.log = log;
            var imps = new ImporterBase[]
            {
                _setlistFm,
                _archiveOrg,
                _jerry,
                _phishin,
                _phishnet,
                _phantasy,
                _local,
            };

            importers = new Dictionary<UpstreamProvider, ImporterBase>();

            foreach (var i in imps)
            {
                importers[i.Provider] = i;
            }
        }

        private readonly ILogger log;

        private IDictionary<UpstreamProvider, ImporterBase> importers { get; }

        public IEnumerable<ImporterBase> ImportersForArtist(Artist art)
        {
            return art
                .upstream_sources.Select(source =>
                    ImporterForUpstreamSource(source.upstream_source)
                )
                .OfType<ImporterBase>();
        }

        public ImporterBase? ImporterForUpstreamSource(UpstreamSource source)
        {
            return
                source.Provider is { } provider && importers.TryGetValue(provider, out var importer)
                ? importer
                : null;
        }

        public Task<ImportStats> Import(
            Artist artist,
            Func<ArtistUpstreamSource, bool>? filterUpstreamSources,
            PerformContext? ctx
        )
        {
            return Import(artist, filterUpstreamSources, (string?)null, ctx);
        }

        public Task<ImportStats> Import(
            Artist artist,
            Func<ArtistUpstreamSource, bool>? filterUpstreamSources,
            ImportOptions? options,
            PerformContext? ctx
        )
        {
            return Import(artist, filterUpstreamSources, null, options, ctx);
        }

        public async Task<ImportStats> Rebuild(Artist artist, PerformContext? ctx)
        {
            var importer = ImportersForArtist(artist).FirstOrDefault();
            if (importer == null)
                return new ImportStats();

            ImportProgress.WriteLine(log, ctx, "Rebuilding Shows");
            var stats = await importer.RebuildShows(artist);

            ImportProgress.WriteLine(log, ctx, "Rebuilding Years");
            stats += await importer.RebuildYears(artist);

            ImportProgress.WriteLine(log, ctx, "Done!");

            return stats;
        }

        public Task<ImportStats> Import(
            Artist artist,
            Func<ArtistUpstreamSource, bool>? filterUpstreamSources,
            string? showIdentifier,
            PerformContext? ctx
        )
        {
            return Import(artist, filterUpstreamSources, showIdentifier, null, ctx);
        }

        public async Task<ImportStats> Import(
            Artist artist,
            Func<ArtistUpstreamSource, bool>? filterUpstreamSources,
            string? showIdentifier,
            ImportOptions? options,
            PerformContext? ctx
        )
        {
            var stats = new ImportStats();
            var importOptions = options ?? ImportOptions.Default;

            var srcs = artist
                .upstream_sources.Where(s => s.upstream_source.importer != null)
                .ToList();

            ImportProgress.WriteLine(
                log,
                ctx,
                $"Found {srcs.Count} valid importers.{(importOptions.IsThinScrape ? $" (thin scrape, year={importOptions.OnlyYear})" : "")}"
            );
            var prog = ctx?.WriteProgressBar();

            Func<ArtistUpstreamSource, Task> processSource = async item =>
            {
                if (filterUpstreamSources != null && !filterUpstreamSources(item))
                {
                    ImportProgress.WriteLine(
                        log,
                        ctx,
                        $"Skipping (rejected by filter): {item.upstream_source_id}, {item.upstream_identifier}"
                    );
                    return;
                }

                ImportProgress.WriteLine(
                    log,
                    ctx,
                    $"Importing with {item.upstream_source_id}, {item.upstream_identifier}"
                );

                item.upstream_source.importer!.CurrentImportOptions = importOptions;

                if (showIdentifier != null)
                {
                    stats += await item.upstream_source.importer!.ImportSpecificShowDataForArtist(
                        artist,
                        item,
                        showIdentifier,
                        ctx
                    );
                }
                else
                {
                    stats += await item.upstream_source.importer!.ImportDataForArtist(
                        artist,
                        item,
                        ctx
                    );
                }
            };

            if (prog == null)
            {
                foreach (var item in srcs)
                {
                    await processSource(item);
                }
            }
            else
            {
                await srcs.AsyncForEachWithProgress(prog, processSource);
            }

            return stats;
        }
    }

    public abstract class ImporterBase : IDisposable
    {
        private readonly CatalogRebuildService catalog;

        public ImportOptions CurrentImportOptions { get; set; } = ImportOptions.Default;

        public ImporterBase(CatalogRebuildService catalog, ILogger log)
        {
            this.catalog = catalog;
            this.log = log;
            http = new HttpClient();

            // iPhone on iOS 11.4
            http.DefaultRequestHeaders.Add(
                "User-Agent",
                "Mozilla/5.0 (iPhone; CPU iPhone OS 11_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/11.0 Mobile/15E148 Safari/604.1"
            );
            http.DefaultRequestHeaders.Add("Accept", "*/*");
        }

        protected readonly ILogger log;

        protected HttpClient http { get; set; }

        /// <summary>Installs an activity-scoped HTTP client while retaining the provider's existing headers.</summary>
        public virtual void UseIngestionHttpClient(HttpClient client)
        {
            foreach (var header in http.DefaultRequestHeaders)
                client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
            http.Dispose();
            http = client;
        }

        public abstract UpstreamProvider Provider { get; }

        private IDictionary<string, int> trackSlugCounts { get; } = new Dictionary<string, int>();

        public void Dispose()
        {
            http.Dispose();
        }

        public abstract ImportableData ImportableDataForArtist(Artist artist);

        public abstract Task<ImportStats> ImportDataForArtist(
            Artist artist,
            ArtistUpstreamSource src,
            PerformContext? ctx
        );

        public abstract Task<ImportStats> ImportSpecificShowDataForArtist(
            Artist artist,
            ArtistUpstreamSource src,
            string? showIdentifier,
            PerformContext? ctx
        );

        /// <summary>
        ///     Resets the slug counts. This needs to be called after each source is imported otherwise you'll get thing like
        ///     you-enjoy-myself-624
        /// </summary>
        public void ResetTrackSlugCounts()
        {
            trackSlugCounts.Clear();
        }

        public string Slugify(string full)
        {
            return SlugUtils.Slugify(full);
        }

        public string SlugifyTrack(string full)
        {
            var slug = Slugify(full);

            if (trackSlugCounts.ContainsKey(slug))
            {
                var count = 0;
                do
                {
                    count = trackSlugCounts[slug];

                    count++;
                    trackSlugCounts[slug] = count;

                    // keep incrementing until we find a slug that isn't taken
                } while (trackSlugCounts.ContainsKey(slug + $"-{count}"));

                slug += $"-{count}";

                trackSlugCounts[slug] = 1;
            }
            else
            {
                trackSlugCounts[slug] = 1;
            }

            return slug;
        }

        public async Task<ImportStats> RebuildYears(Artist artist)
        {
            await catalog.RebuildYears(artist);
            return ImportStats.None;
        }

        public async Task<ImportStats> RebuildShows(Artist artist)
        {
            await catalog.RebuildShows(artist);
            return ImportStats.None;
        }
    }
}
