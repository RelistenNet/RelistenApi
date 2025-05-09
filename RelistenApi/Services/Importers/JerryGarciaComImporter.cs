using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dapper;
using Hangfire.Console;
using Hangfire.Server;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Relisten.Api.Models;
using Relisten.Data;
using Relisten.Vendor;

namespace Relisten.Import
{
    public class JerryGarciaComImporter : ImporterBase
    {
        public const string DataSourceName = "jerrygarcia.com";
        private IDictionary<string, SetlistShow> existingSetlistShows = new Dictionary<string, SetlistShow>();
        private IDictionary<string, SetlistSong> existingSetlistSongs = new Dictionary<string, SetlistSong>();
        private IDictionary<string, Tour> existingTours = new Dictionary<string, Tour>();

        private IDictionary<string, VenueWithShowCount> existingVenues = new Dictionary<string, VenueWithShowCount>();
        private IDictionary<string, DateTime> tourToEndDate = new Dictionary<string, DateTime>();

        private IDictionary<string, DateTime> tourToStartDate = new Dictionary<string, DateTime>();

        public JerryGarciaComImporter(
            DbService db,
            SetlistShowService setlistShowService,
            VenueService venueService,
            TourService tourService,
            SetlistSongService setlistSongService,
            ILogger<JerryGarciaComImporter> log,
            RedisService redisService
        ) : base(db, redisService)
        {
            _setlistShowService = setlistShowService;
            _venueService = venueService;
            _tourService = tourService;
            _setlistSongService = setlistSongService;
            _log = log;
        }

        protected SetlistShowService _setlistShowService { get; set; }
        protected SetlistSongService _setlistSongService { get; set; }
        protected VenueService _venueService { get; set; }
        protected TourService _tourService { get; set; }
        protected ILogger<JerryGarciaComImporter> _log { get; set; }

        public override string ImporterName => "jerrygarcia.com";

        public override ImportableData ImportableDataForArtist(Artist artist)
        {
            return ImportableData.Eras
                   | ImportableData.SetlistShowsAndSongs
                   | ImportableData.Tours
                   | ImportableData.Venues;
        }

        private string ShowPagesListingUrl(ArtistUpstreamSource src)
        {
            return
                $"https://relisten-grateful-dead-metadata-mirror.s3.amazonaws.com/{src.upstream_identifier}/show_pages.json";
        }

        private string ShowPageUrl(ArtistUpstreamSource src, string filename)
        {
            return
                $"https://relisten-grateful-dead-metadata-mirror.s3.amazonaws.com/{src.upstream_identifier}/show_pages/{filename}";
        }

        public override async Task<ImportStats> ImportDataForArtist(Artist artist, ArtistUpstreamSource src,
            PerformContext ctx)
        {
            var stats = new ImportStats();

            await PreloadData(artist);

            var resp = await http.GetAsync(ShowPagesListingUrl(src));
            var showFilesResponse = await resp.Content.ReadAsStringAsync();
            var showFiles = JsonConvert.DeserializeObject<List<string>>(showFilesResponse);

            var files = showFiles
                    .Select(f =>
                    {
                        var fileName = Path.GetFileName(f);
                        return new FileMetaObject
                        {
                            DisplayDate = fileName.Substring(0, 10),
                            Date = DateTime.Parse(fileName.Substring(0, 10)),
                            FilePath = f,
                            Identifier =
                                fileName.Remove(fileName.LastIndexOf(".html", StringComparison.OrdinalIgnoreCase))
                        };
                    })
                    .ToList()
                ;

            ctx?.WriteLine($"Checking {files.Count} html files");
            var prog = ctx?.WriteProgressBar();

            await files.AsyncForEachWithProgress(prog, async f =>
            {
                if (existingSetlistShows.ContainsKey(f.Identifier))
                {
                    return;
                }

                var url = ShowPageUrl(src, f.FilePath);
                var pageResp = await http.GetAsync(url);
                var pageContents = await pageResp.Content.ReadAsStringAsync();

                await ProcessPage(stats, artist, f, pageContents,
                    pageResp.Content.Headers.LastModified?.UtcDateTime ?? DateTime.UtcNow, ctx);
            });

            if (artist.features.tours)
            {
                await UpdateTourStartEndDates(artist);
            }

            ctx.WriteLine("Rebuilding shows and years");

            // update shows
            await RebuildShows(artist);

            // update years
            await RebuildYears(artist);

            return stats;
        }

        public override Task<ImportStats> ImportSpecificShowDataForArtist(Artist artist, ArtistUpstreamSource src,
            string showIdentifier, PerformContext ctx)
        {
            return Task.FromResult(new ImportStats());
        }

        private async Task PreloadData(Artist artist)
        {
            existingVenues = (await _venueService.AllIncludingUnusedForArtist(artist))
                .GroupBy(venue => venue.upstream_identifier).ToDictionary(grp => grp.Key, grp => grp.First());

            existingTours = (await _tourService.AllForArtist(artist))
                .GroupBy(tour => tour.upstream_identifier).ToDictionary(grp => grp.Key, grp => grp.First());

            existingSetlistShows = (await _setlistShowService.AllForArtist(artist))
                .GroupBy(show => show.upstream_identifier).ToDictionary(grp => grp.Key, grp => grp.First());

            existingSetlistSongs = (await _setlistSongService.AllForArtist(artist))
                .GroupBy(song => song.upstream_identifier).ToDictionary(grp => grp.Key, grp => grp.First());
        }

        private async Task ProcessPage(ImportStats stats, Artist artist, FileMetaObject meta, string pageContents,
            DateTime updated_at, PerformContext ctx)
        {
            var dbShow = existingSetlistShows.GetValue(meta.Identifier);

            if (dbShow != null)
            {
                return;
            }

            ctx.WriteLine($"Processing: {meta.FilePath}");

            var html = new HtmlDocument();
            html.LoadHtml(pageContents);

            var root = html.DocumentNode;

            var ps = root.DescendantsWithClass("venue-name").ToList();

            var bandName = ps[1].InnerText.CollapseSpacesAndTrim();

            var venueName = ps[0].InnerText.CollapseSpacesAndTrim();
            var venueCityOrCityState = root.DescendantsWithClass("venue-address").Single().InnerText
                .CollapseSpacesAndTrim().TrimEnd(',');
            var venueCountry = root.DescendantsWithClass("venue-country").Single().InnerText.CollapseSpacesAndTrim();

            var tourName = ps.Count > 2 ? ps[2].InnerText.CollapseSpacesAndTrim() : "Not Part of a Tour";

            var venueUpstreamId = "jerrygarcia.com_" + venueName;

            Venue dbVenue = existingVenues.GetValue(venueUpstreamId);

            if (dbVenue == null)
            {
                var sc = new VenueWithShowCount
                {
                    artist_id = artist.id,
                    name = venueName,
                    location = venueCityOrCityState + ", " + venueCountry,
                    upstream_identifier = venueUpstreamId,
                    slug = Slugify(venueName),
                    updated_at = updated_at
                };

                dbVenue = await _venueService.Save(sc);

                sc.id = dbVenue.id;

                existingVenues[dbVenue.upstream_identifier] = sc;

                stats.Created++;
            }

            var dbTour = existingTours.GetValue(tourName);

            if (dbTour == null && artist.features.tours)
            {
                dbTour = await _tourService.Save(new Tour
                {
                    artist_id = artist.id,
                    name = tourName,
                    slug = Slugify(tourName),
                    upstream_identifier = tourName,
                    updated_at = updated_at
                });

                existingTours[dbTour.upstream_identifier] = dbTour;

                stats.Created++;
            }

            dbShow = await _setlistShowService.Save(new SetlistShow
            {
                artist_id = artist.id,
                tour_id = dbTour?.id,
                venue_id = dbVenue.id,
                date = meta.Date,
                upstream_identifier = meta.Identifier,
                updated_at = updated_at
            });

            existingSetlistShows[dbShow.upstream_identifier] = dbShow;

            stats.Created++;

            var dbSongs = root.Descendants("ol")
                    .SelectMany(node => node.Descendants("li"))
                    .Select(node =>
                    {
                        var trackName = node.InnerText.Trim().TrimEnd('>', '*', ' ');
                        var slug = SlugifyTrack(trackName);

                        return new SetlistSong
                        {
                            artist_id = artist.id,
                            name = trackName,
                            slug = slug,
                            upstream_identifier = slug,
                            updated_at = updated_at
                        };
                    })
                    .GroupBy(s => s.upstream_identifier)
                    .Select(g => g.First())
                    .ToList()
                ;

            ResetTrackSlugCounts();

            var dbSongsToAdd = dbSongs.Where(song => !existingSetlistSongs.ContainsKey(song.upstream_identifier));

            dbSongs = dbSongs.Where(song => existingSetlistSongs.ContainsKey(song.upstream_identifier))
                    .Select(song => existingSetlistSongs[song.upstream_identifier])
                    .ToList()
                ;

            var added = await _setlistSongService.InsertAll(artist, dbSongsToAdd);

            foreach (var s in added)
            {
                existingSetlistSongs[s.upstream_identifier] = s;
            }

            stats.Created += added.Count();
            dbSongs.AddRange(added);

            if (artist.features.tours &&
                (dbTour.start_date == null
                 || dbTour.end_date == null
                 || dbShow.date < dbTour.start_date
                 || dbShow.date > dbTour.end_date))
            {
                if (!tourToStartDate.ContainsKey(dbTour.upstream_identifier)
                    || dbShow.date < tourToStartDate[dbTour.upstream_identifier])
                {
                    tourToStartDate[dbTour.upstream_identifier] = dbShow.date;
                }

                if (!tourToEndDate.ContainsKey(dbTour.upstream_identifier)
                    || dbShow.date > tourToEndDate[dbTour.upstream_identifier])
                {
                    tourToEndDate[dbTour.upstream_identifier] = dbShow.date;
                }
            }

            await _setlistShowService.UpdateSongPlays(dbShow, dbSongs);
        }

        private async Task UpdateTourStartEndDates(Artist artist)
        {
            await db.WithWriteConnection(con => con.ExecuteAsync(@"
                UPDATE
                    tours
                SET
                    start_date = @startDate,
                    end_date = @endDate
                WHERE
                    artist_id = @artistId
                    AND upstream_identifier = @upstream_identifier
            ", tourToStartDate.Keys.Select(tourUpstreamId =>
            {
                return new
                {
                    startDate = tourToStartDate[tourUpstreamId],
                    endDate = tourToEndDate[tourUpstreamId],
                    artistId = artist.id,
                    upstream_identifier = tourUpstreamId
                };
            })));

            tourToStartDate = new Dictionary<string, DateTime>();
            tourToEndDate = new Dictionary<string, DateTime>();
        }

        private class FileMetaObject
        {
            public string DisplayDate { get; set; }
            public DateTime Date { get; set; }
            public string FilePath { get; set; }
            public string Identifier { get; set; }
        }
    }

    public static class HtmlExtensions
    {
        public static string CollapseSpacesAndTrim(this string str)
        {
            return Regex.Replace(str.Trim(), @"\s+", " ");
        }

        public static IEnumerable<HtmlNode> WhereHasClass(this IEnumerable<HtmlNode> nodes, string cls)
        {
            return nodes.Where(node => node.GetAttributeValue("class", "")
                .Split(' ')
                .Contains(cls));
        }

        public static IEnumerable<HtmlNode> DescendantsWithClass(this HtmlNode node, string cls)
        {
            return node.Descendants().WhereHasClass(cls);
        }
    }
}
