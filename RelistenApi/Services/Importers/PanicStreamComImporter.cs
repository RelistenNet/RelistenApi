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
using System.IO;
using HtmlAgilityPack;
using System.Globalization;
using System.Net;
using Hangfire.Server;
using Hangfire.Console;
using Microsoft.Extensions.Configuration;

namespace Relisten.Import
{
    public class PanicStreamComImporter : ImporterBase
    {
        public const string DataSourceName = "panicstream.com";

        protected SourceService _sourceService { get; set; }
        protected SourceSetService _sourceSetService { get; set; }
        protected SourceReviewService _sourceReviewService { get; set; }
        protected SourceTrackService _sourceTrackService { get; set; }
        protected VenueService _venueService { get; set; }
        protected TourService _tourService { get; set; }
        protected ILogger<PanicStreamComImporter> _log { get; set; }
        public IConfiguration _configuration { get; }

        readonly LinkService linkService;

        public PanicStreamComImporter(
            DbService db,
            VenueService venueService,
            TourService tourService,
            SourceService sourceService,
            SourceSetService sourceSetService,
            SourceReviewService sourceReviewService,
            SourceTrackService sourceTrackService,
            LinkService linkService,
            ILogger<PanicStreamComImporter> log,
            IConfiguration configuration
        ) : base(db)
        {
            this.linkService = linkService;
            this._sourceService = sourceService;
            this._venueService = venueService;
            this._tourService = tourService;
            this._log = log;
            _configuration = configuration;
            _sourceReviewService = sourceReviewService;
            _sourceTrackService = sourceTrackService;
            _sourceSetService = sourceSetService;
        }

        public override string ImporterName => "panicstream.com";

        public override ImportableData ImportableDataForArtist(Artist artist)
        {
            return ImportableData.Sources;
        }

        private static Regex ShowDirMatcher = new Regex(@"href=""([0-9_]+)([a-z]?)\/"">[0-9_]+[a-z]?\/<\/a>\s+([0-9A-Za-z: -]+)\s+-");
        private static Regex TrackPrefixFinder = new Regex(@"^((?:\d{2,3} )|(?:[Ww][sS]?[pP][0-9-]+(?:[.]|EQ)?(?:[dD]\d+)?[tT]\d+\.(?!mp3|m4a|MP3|M4A))|(?:(?:d\d+)?t\d+) )");
        private static Regex Mp3FileFinder = new Regex(@" href=""(.*\.(?:mp3|m4a|MP3|M4A))""");
        private static Regex TxtFileFinder = new Regex(@" href=""(.*\.txt)""");

        private async Task<string> FetchUrl(string url, PerformContext ctx)
        {
            url = url.Replace("&amp;", "&");
            ctx?.WriteLine("Fetching URL: " + url);
            var page = await http.GetAsync(url);

            if (page.StatusCode != HttpStatusCode.OK)
            {
                var e = new Exception("URL fetch failed: " + url);
                e.Data["url"] = url;
                e.Data["StatusCode"] = page.StatusCode;
                throw e;
            }

            return await page.Content.ReadAsStringAsync();
        }

        private string PanicIndexUrl() { return "https://www.panicstream.com/streams/wsp/relisten__/slim-metadata.json?key=" + _configuration["PANIC_KEY"]; }
        private static string PanicShowUrl(string panicDate)
        {
            return "https://www.panicstream.com/streams/wsp/" + panicDate + "/";
        }
        private static string PanicShowFileUrl(string panicDate, string fileName)
        {
            return PanicShowUrl(panicDate) + fileName;
        }

        public override async Task<ImportStats> ImportDataForArtist(Artist artist, ArtistUpstreamSource src, PerformContext ctx)
        {
            var stats = new ImportStats();

            await PreloadData(artist);

            var contents = await FetchUrl(PanicIndexUrl(), ctx);
            var tracks = JsonConvert.DeserializeObject<List<PanicStream.PanicStreamTrack>>(contents);

            var tracksByShow = tracks
                .Where(t => t.SourceName != null)
                .GroupBy(t => t.ShowDate)
                .Select(g => new
                {
                    ShowDate = g.Key,
                    Sources = g
                        .GroupBy(subg => subg.SourceName)
                        .Select(subg => new
                        {
                            SourceName = subg.Key,
                            Tracks = subg.ToList()
                        })
                })
                .ToList();

            ctx?.WriteLine($"Found {tracksByShow.Count} shows");

            var prog = ctx?.WriteProgressBar();

            await tracksByShow.AsyncForEachWithProgress(prog, async grp =>
            {
                foreach (var source in grp.Sources)
                {
                    try {
                        await ProcessShow(stats, artist, src, grp.ShowDate, source.SourceName, source.Tracks, ctx);
                    }
                    catch(Exception e) {
                        ctx?.WriteLine("EXCEPTION: " + e.Message);
                        ctx?.WriteLine("Source name: " + source.SourceName);
                        ctx?.WriteLine(e.ToString());
                        ctx?.WriteLine(JsonConvert.SerializeObject(source));
                    }
                }
            });

            await RebuildShows(artist);
            await RebuildYears(artist);

            return stats;
        }

        public override Task<ImportStats> ImportSpecificShowDataForArtist(Artist artist, ArtistUpstreamSource src, string showIdentifier, PerformContext ctx)
        {
            return Task.FromResult(new ImportStats());
        }

        private IDictionary<string, Source> existingSources = new Dictionary<string, Source>();

        async Task PreloadData(Artist artist)
        {
            existingSources = (await _sourceService.AllForArtist(artist)).
                GroupBy(src => src.upstream_identifier).
                ToDictionary(grp => grp.Key, grp => grp.First());
        }

        private async Task ProcessShow(ImportStats stats, Artist artist, ArtistUpstreamSource upstreamSrc, string showDate, string sourceName, IList<PanicStream.PanicStreamTrack> sourceTracks, PerformContext ctx)
        {
            var upstreamId = sourceName;
            var dbSource = existingSources.GetValue(upstreamId);

            var panicUpdatedAt = sourceTracks
                .Where(t => t.System.ParsedModificationTime.HasValue)
                .Max(t => t.System.ParsedModificationTime.Value);

            if (dbSource != null && dbSource.updated_at <= panicUpdatedAt)
            {
                return;
            }

            var isUpdate = dbSource != null;

            var src = new Source
            {
                artist_id = artist.id,
                display_date = showDate,
                is_soundboard = false,
                is_remaster = false,
                has_jamcharts = false,
                avg_rating = 0,
                num_reviews = 0,
                avg_rating_weighted = 0,
                upstream_identifier = upstreamId,
                taper_notes = "",
                updated_at = panicUpdatedAt
            };

            if (isUpdate)
            {
                src.id = dbSource.id;
            }

            dbSource = await _sourceService.Save(src);

            existingSources[dbSource.upstream_identifier] = dbSource;

            if (isUpdate)
            {
                stats.Updated++;
                stats.Removed += await _sourceService.DropAllSetsAndTracksForSource(dbSource);
            }
            else
            {
                stats.Created++;
                stats.Created += (await linkService.AddLinksForSource(dbSource, new[]
                {
                    new Link
                    {
                        source_id = dbSource.id,
                        for_ratings = false,
                        for_source = true,
                        for_reviews = false,
                        upstream_source_id = upstreamSrc.upstream_source_id,
                        url = $"https://www.panicstream.com/vault/widespread-panic/{dbSource.display_date.Substring(0, 4)}-streams/",
                        label = "View show page on panicstream.com"
                    }
                })).Count();
            }

            var dbSet = await _sourceSetService.Insert(new SourceSet
            {
                source_id = dbSource.id,
                index = 0,
                is_encore = false,
                name = "Default Set",
                updated_at = panicUpdatedAt
            });
            stats.Created++;

            var trackIndex = 0;
            var mp3s = sourceTracks
                .OrderBy(t => t.FileName)
                .Select(t =>
                {
                    var trackName = t.FileName
                        .Replace(".mp3", "")
                        .Replace(".MP3", "")
                        .Replace(".M4A", "")
                        .Replace(".m4a", "")
                        .Trim();

                    var cleanedTrackName = Regex.Replace(trackName, @"(wsp[0-9-]+d\d+t\d+\.)|(^\d+ ?-? ?)", "").Trim();

                    if(cleanedTrackName.Length != 0) {
                        trackName = cleanedTrackName;
                    }

                    trackIndex++;

                    return new SourceTrack
                    {
                        source_id = dbSource.id,
                        source_set_id = dbSet.id,
                        track_position = trackIndex,
                        duration = ((int?)t.Composite?.CalculatedDuration.TotalSeconds ?? (int?)0).Value,
                        title = trackName,
                        slug = SlugifyTrack(trackName),
                        mp3_url = t.AbsoluteUrl(_configuration["PANIC_KEY"]),
                        updated_at = panicUpdatedAt,
                        artist_id = artist.id
                    };
                });

            ResetTrackSlugCounts();

            await _sourceTrackService.InsertAll(mp3s);
            stats.Created += mp3s.Count();
        }
    }
}

namespace Relisten.Import.PanicStream
{
    public class PanicStreamShowSystem
    {
        public string FileModifyDate { get; set; }

        public DateTime? ParsedModificationTime
        {
            get
            {
                if (DateTime.TryParseExact(FileModifyDate, "yyyy:MM:dd HH:mm:sszzz", null, DateTimeStyles.AssumeUniversal, out var res))
                {
                    return res;
                }
                
                return null;
            }
        }
    }

    public class PanicStreamShowComposite
    {
        public string Duration { get; set; }

        private TimeSpan? _calculatedDuration = null;
        public TimeSpan CalculatedDuration
        {
            get
            {
                if (_calculatedDuration == null)
                {
                    if(Duration.EndsWith(" s (approx)")) {
                        _calculatedDuration = TimeSpan.FromSeconds(double.Parse(Duration.Replace(" s (approx)", "")));
                    }
                    else {
                        var parts = Duration
                            .Replace(" (approx)", "")
                            .Split(':')
                            .Select(i => i.Length == 1 ? i : i.TrimStart('0'))
                            .Select(i => i.Length == 0 ? "0" : i)
                            .Select(i => int.Parse(i))
                            .ToList();

                        _calculatedDuration = TimeSpan.FromSeconds((parts[0] * 60 * 60) + (parts[1] * 60) + parts[2]);
                    }
                }

                return _calculatedDuration.Value;
            }
        }
    }

    public class PanicStreamTrack
    {
        public string SourceFile { get; set; }

        private string _fileName = null;

        public string FileName => SourceName != null ? _fileName : null;

        private string _sourceName = null;
        public string SourceName
        {
            get
            {
                if (_sourceName == null)
                {
                    var parts = SourceFile.Split('/');

                    if (parts.Length == 3)
                    {
                        _sourceName = parts[1];
                        _fileName = parts[2];
                    }
                }

                return _sourceName;
            }
        }

        private string _showDate = null;
        public string ShowDate
        {
            get
            {
                if (_showDate == null && SourceName != null)
                {
                    _showDate = Regex.Replace(SourceName.Replace('_', '-'), @"(\d)([^0-9-]+)", @"$1");
                }

                return _showDate;
            }
        }

        public string AbsoluteUrl(string key) => "https://www.panicstream.com/streams/wsp/" + SourceFile.Substring(3) + "?key=" + key;

        public PanicStreamShowSystem System { get; set; }
        public PanicStreamShowComposite Composite { get; set; }
    }
}