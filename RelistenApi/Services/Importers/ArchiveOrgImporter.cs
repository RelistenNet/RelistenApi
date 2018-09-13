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
using System.Globalization;
using Hangfire.Server;
using Hangfire.Console;
using Npgsql;
using System.Transactions;
using Microsoft.ApplicationInsights;

namespace Relisten.Import
{

    public class ArchiveOrgImporter : ImporterBase
    {
        public const string DataSourceName = "archive.org";

        protected SourceService _sourceService { get; set; }
        protected SourceSetService _sourceSetService { get; set; }
        protected SourceReviewService _sourceReviewService { get; set; }
        protected SourceTrackService _sourceTrackService { get; set; }
        protected VenueService _venueService { get; set; }
        protected TourService _tourService { get; set; }

        readonly LinkService linkService;

        public ArchiveOrgImporter(
            DbService db,
            VenueService venueService,
            TourService tourService,
            SourceService sourceService,
            SourceSetService sourceSetService,
            SourceReviewService sourceReviewService,
            LinkService linkService,
            SourceTrackService sourceTrackService
        ) : base(db)
        {
            this.linkService = linkService;
            this._sourceService = sourceService;
            this._venueService = venueService;
            this._tourService = tourService;

            _sourceReviewService = sourceReviewService;
            _sourceTrackService = sourceTrackService;
            _sourceSetService = sourceSetService;
        }

        public override string ImporterName => "archive.org";

        public override ImportableData ImportableDataForArtist(Artist artist)
        {
            var r = ImportableData.Sources | ImportableData.SourceReviews | ImportableData.SourceRatings;

            if (artist.features.per_source_venues)
            {
                r |= ImportableData.Venues;
            }

            return r;
        }

        public override async Task<ImportStats> ImportDataForArtist(Artist artist, ArtistUpstreamSource src, PerformContext ctx)
        {
            return await ImportSpecificShowDataForArtist(artist, src, null, ctx);
        }

        public override async Task<ImportStats> ImportSpecificShowDataForArtist(Artist artist, ArtistUpstreamSource src, string showIdentifier, PerformContext ctx)
        {
            await PreloadData(artist);

			var url = SearchUrlForArtist(artist, src);
			ctx?.WriteLine($"All shows URL: {url}");

            return await ProcessIdentifiers(artist, await this.http.GetAsync(url), src, showIdentifier, ctx);
        }

        private IDictionary<string, Source> existingSources = new Dictionary<string, Source>();

        private string SearchUrlForArtist(Artist artist, ArtistUpstreamSource src)
        {
            return $"http://archive.org/advancedsearch.php?q=collection%3A{src.upstream_identifier}&fl%5B%5D=date&fl%5B%5D=identifier&fl%5B%5D=year&fl%5B%5D=addeddate&fl%5B%5D=reviewdate&fl%5B%5D=indexdate&fl%5B%5D=publicdate&sort%5B%5D=year+asc&sort%5B%5D=&sort%5B%5D=&rows=9999999&page=1&output=json&save=yes";
        }
        private static string DetailsUrlForIdentifier(string identifier)
        {
            return $"http://archive.org/metadata/{identifier}";
        }

        private async Task<ImportStats> ProcessIdentifiers(Artist artist, HttpResponseMessage res, ArtistUpstreamSource src, string showIdentifier, PerformContext ctx)
        {
            var stats = new ImportStats();

            var json = await res.Content.ReadAsStringAsync();
            var root = JsonConvert.DeserializeObject<Relisten.Vendor.ArchiveOrg.SearchRootObject>(
                json.Replace("\"0000-01-01T00:00:00Z\"", "null") /* serious...wtf archive */,
                new Relisten.Vendor.ArchiveOrg.TolerantArchiveDateTimeConverter()
            );

            ctx?.WriteLine($"Checking {root.response.docs.Count} archive.org results");

            var prog = ctx?.WriteProgressBar();

            await root.response.docs.AsyncForEachWithProgress(prog, async doc =>
            {
                try
                {
                    var currentIsTargetedShow = doc.identifier == showIdentifier;

                    if (showIdentifier != null && !currentIsTargetedShow)
                    {
                        return;
                    }

                    var dbShow = existingSources.GetValue(doc.identifier);

                    if (currentIsTargetedShow || dbShow == null || doc._iguana_index_date > dbShow.created_at)
                    {
                        ctx?.WriteLine("Pulling https://archive.org/metadata/{0}", doc.identifier);

                        var detailRes = await http.GetAsync(DetailsUrlForIdentifier(doc.identifier));
                        var detailsJson = await detailRes.Content.ReadAsStringAsync();
                        var detailsRoot = JsonConvert.DeserializeObject<Relisten.Vendor.ArchiveOrg.Metadata.RootObject>(
                            detailsJson,
                            new Vendor.ArchiveOrg.TolerantStringConverter()
                        );

						if (detailsRoot.is_dark ?? false) {
							ctx?.WriteLine("\tis_dark == true, skipping...");
							return;
						}

                        var properDate = FixDisplayDate(detailsRoot.metadata);

                        if (properDate != null)
                        {
                            using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
                            {
                                stats += await ImportSingleIdentifier(artist, dbShow, doc, detailsRoot, src, properDate, ctx);

                                scope.Complete();
                            }
                        }
                        else
                        {
                            ctx?.WriteLine("\tSkipped {0} because it has an invalid, unrecoverable date: {1}", doc.identifier, detailsRoot.metadata.date);
                        }
                    }
                }
                catch (Exception e)
                {
                    ctx?.WriteLine($"Error processing {doc.identifier}:");
                    ctx?.LogException(e);

                    var telementry = new TelemetryClient();

                    telementry.TrackException(e, new Dictionary<string, string> {
						{ "upstream_identifier", doc.identifier }
					});
                }
            });

            ctx?.WriteLine("Rebuilding shows...");

            // update shows
            await RebuildShows(artist);

            ctx?.WriteLine("--> rebuilt shows!");
            ctx?.WriteLine("Rebuilding years...");

            // update years
            await RebuildYears(artist);

            ctx?.WriteLine("--> rebuilt years!");

            return stats;
        }

        private async Task<ImportStats> ImportSingleIdentifier(
            Artist artist,
            Source dbSource,
            Relisten.Vendor.ArchiveOrg.SearchDoc searchDoc,
            Relisten.Vendor.ArchiveOrg.Metadata.RootObject detailsRoot,
            ArtistUpstreamSource upstreamSrc,
            string properDisplayDate,
            PerformContext ctx
        )
        {
            var stats = new ImportStats();

            var upstream_identifier = searchDoc.identifier;
            var isUpdate = dbSource != null;

            var meta = detailsRoot.metadata;

            var mp3Files = detailsRoot.files?.Where(file => file?.format == "VBR MP3");
            var flacFiles = detailsRoot.files?.Where(file => file?.format == "Flac" || file?.format == "24bit Flac");

            if (mp3Files == null || mp3Files.Count() == 0)
            {
                ctx?.WriteLine("\tNo VBR MP3 files found for {0}", searchDoc.identifier);

                return stats;
            }

            var dbReviews = detailsRoot.reviews == null
                ? new List<SourceReview>()
                : detailsRoot.reviews.Select(rev =>
            {
                return new SourceReview()
                {
                    rating = rev.stars * 2, // scale to out of 10
                    title = rev.reviewtitle,
                    review = rev.reviewbody?.Replace("Â", "") ?? "",
                    author = rev.reviewer,
                    updated_at = rev.reviewdate
                };
            }).ToList();

            Venue dbVenue = null;
            if (artist.features.per_source_venues)
            {
                var venueName = String.IsNullOrEmpty(meta.venue) ? meta.coverage : meta.venue;

                if (String.IsNullOrEmpty(venueName))
                {
                    venueName = "Unknown Venue";
                }

                var venueUpstreamId = venueName + (String.IsNullOrEmpty(meta.coverage) ? "blank coverage" : meta.coverage);
                dbVenue = await _venueService.ForUpstreamIdentifier(artist, venueUpstreamId);

                if (dbVenue == null)
                {
                    dbVenue = await _venueService.Save(new Venue()
                    {
                        artist_id = artist.id,
                        name = venueName,
                        location = String.IsNullOrEmpty(meta.coverage) ? "Unknown Location" : meta.coverage,
                        upstream_identifier = venueUpstreamId,
                        slug = Slugify(venueName),
                        updated_at = searchDoc._iguana_updated_at
                    });
                }
            }

            if (isUpdate)
            {
                var src = CreateSourceForMetadata(artist, detailsRoot, searchDoc, properDisplayDate);
                src.id = dbSource.id;
                src.venue_id = dbVenue.id;

                dbSource = await _sourceService.Save(src);
                dbSource.venue = dbVenue;

                stats.Updated++;
                stats.Created += (await ReplaceSourceReviews(dbSource, dbReviews)).Count();
                stats.Removed += await _sourceService.DropAllSetsAndTracksForSource(dbSource);
            }
            else
            {


                dbSource = await _sourceService.Save(CreateSourceForMetadata(artist, detailsRoot, searchDoc, properDisplayDate, dbVenue));
                stats.Created++;

                existingSources[dbSource.upstream_identifier] = dbSource;

                stats.Created += (await ReplaceSourceReviews(dbSource, dbReviews)).Count();
            }

            stats.Created += (await linkService.AddLinksForSource(dbSource, LinksForSource(artist, dbSource, upstreamSrc))).Count();

            var dbSet = (await _sourceSetService.InsertAll(new[] { CreateSetForSource(dbSource) })).First();
            stats.Created++;

            var flacTracksByName = flacFiles.GroupBy(f => f.name).ToDictionary(g => g.Key, g => g.First());

            var dbTracks = CreateSourceTracksForFiles(artist, dbSource, meta, mp3Files, flacTracksByName, dbSet);

            stats.Created += (await _sourceTrackService.InsertAll(dbTracks)).Count();

            ResetTrackSlugCounts();

            return stats;
        }

        IEnumerable<Link> LinksForSource(Artist artist, Source dbSource, ArtistUpstreamSource src)
        {
            var links = new List<Link>
            {
                new Link
                {
                    source_id = dbSource.id,
                    for_ratings = true,
                    for_source = true,
                    for_reviews = true,
                    upstream_source_id = src.upstream_source_id,
                    url = "https://archive.org/details/" + dbSource.upstream_identifier,
                    label = "View on archive.org"
                }
            };

            if (artist.upstream_sources.Any(s => s.upstream_source_id == 6 /* setlist.fm */))
            {
                links.Add(new Link()
                {
                    source_id = dbSource.id,
                    for_ratings = false,
                    for_source = false,
                    for_reviews = false,
                    upstream_source_id = 6 /* setlist.fm */,
                    url = "https://www.setlist.fm/",
                    label = "Setlist Information from setlist.fm"
                });
            }

            return links;
        }

        private async Task<IEnumerable<SourceReview>> ReplaceSourceReviews(Source source, IEnumerable<SourceReview> reviews)
        {
            await _sourceReviewService.RemoveAllForSource(source);

            foreach (var review in reviews)
            {
                review.source_id = source.id;
            }

            return await _sourceReviewService.InsertAll(reviews);
        }

        private SourceSet CreateSetForSource(
            Source source
        )
        {
            return new SourceSet()
            {
                source_id = source.id,
                index = 0,
                is_encore = false,
                name = "Set",
                updated_at = source.updated_at
            };
        }

        private Source CreateSourceForMetadata(
            Artist artist,
            Relisten.Vendor.ArchiveOrg.Metadata.RootObject detailsRoot,
            Relisten.Vendor.ArchiveOrg.SearchDoc searchDoc,
            string properDisplayDate,
            Venue dbVenue = null
        )
        {
            var meta = detailsRoot.metadata;

            var sbd = meta.identifier.EmptyIfNull().ContainsInsensitive("sbd")
                || meta.title.EmptyIfNull().ContainsInsensitive("sbd")
                || meta.source.EmptyIfNull().ContainsInsensitive("sbd")
                || meta.lineage.EmptyIfNull().ContainsInsensitive("sbd")
                ;

            var remaster = meta.identifier.EmptyIfNull().ContainsInsensitive("remast")
                || meta.title.EmptyIfNull().ContainsInsensitive("remast")
                || meta.source.EmptyIfNull().ContainsInsensitive("remast")
                || meta.lineage.EmptyIfNull().ContainsInsensitive("remast")
                ;

            var flac_type = FlacType.NoFlac;

            if (detailsRoot.files.Any(f => f.format == "24bit Flac"))
            {
                flac_type = FlacType.Flac24Bit;
            }
            else if (detailsRoot.files.Any(f => f.format == "Flac"))
            {
                flac_type = FlacType.Flac16Bit;
            }

            return new Source()
            {
                artist_id = artist.id,
                is_soundboard = sbd,
                is_remaster = remaster,
                has_jamcharts = false,
                avg_rating = 0, // dbReviews.Average(rev => 1.0 * rev.rating),
                num_reviews = 0, // dbReviews.Count,
                upstream_identifier = meta.identifier,
                description = meta.description.EmptyIfNull(),
                taper_notes = meta.notes.EmptyIfNull(),
                source = meta.source.EmptyIfNull(),
                taper = meta.taper.EmptyIfNull(),
                transferrer = meta.transferer.EmptyIfNull(),
                lineage = meta.lineage.EmptyIfNull(),
                updated_at = searchDoc._iguana_updated_at,
                venue_id = dbVenue?.id,
                display_date = properDisplayDate,
                flac_type = flac_type
            };
        }

        private static Regex ExtractDateFromIdentifier = new Regex(@"(\d{4}-\d{2}-\d{2})");

        // thanks to this trouble child: https://archive.org/metadata/lotus2011-16-07.lotus2011-16-07_Neumann
        private string FixDisplayDate(Relisten.Vendor.ArchiveOrg.Metadata.Metadata meta)
        {
            // 1970-03-XX or 1970-XX-XX which is okay because it is handled by the rebuild
            if (meta.date.Contains('X'))
            {
                return meta.date;
            }

            // happy case
            if (TestDate(meta.date))
            {
                return meta.date;
            }

            var d = TryFlippingMonthAndDate(meta.date);

            if (d != null)
            {
                return d;
            }

            // try to parse it out of the identifier
            var matches = ExtractDateFromIdentifier.Match(meta.identifier);

            if (matches.Success)
            {
                var tdate = matches.Groups[1].Value;

                if (TestDate(tdate))
                {
                    return tdate;
                }

                var flipped = TryFlippingMonthAndDate(tdate);

                if (flipped != null)
                {
                    return flipped;
                }
            }

            return null;
        }

        private bool TestDate(string date)
        {
            return DateTime.TryParseExact(date, "yyyy-MM-dd", DateTimeFormatInfo.InvariantInfo, DateTimeStyles.AssumeUniversal, out var _);
        }

        private string TryFlippingMonthAndDate(string date)
        {
            // not a valid date
            var parts = date.Split('-');

            // try to see if it is YYYY-DD-MM instead
            if (parts.Length > 2 && int.TryParse(parts[1], out int month))
            {
                if (month > 12)
                {

                    // rearrange to YYYY-MM-DD
                    var dateStr = parts[0] + "-" + parts[2] + "-" + parts[1];

                    if (TestDate(dateStr))
                    {
                        return dateStr;
                    }
                }
            }

            return null;
        }

        private IEnumerable<SourceTrack> CreateSourceTracksForFiles(
            Artist artist,
            Source dbSource,
            Vendor.ArchiveOrg.Metadata.Metadata meta,
            IEnumerable<Vendor.ArchiveOrg.Metadata.File> mp3Files,
            IDictionary<string, Vendor.ArchiveOrg.Metadata.File> flacFiles,
            SourceSet set = null
        )
        {
            var trackNum = 0;

            return mp3Files.
                Where(file =>
                {
                    return !(
                        (file.title == null && file.original == null)
                        || file.length == null
                        || file.name == null
                    );
                }).
                OrderBy(file => file.name).
                Select(file =>
                {
                    var r = CreateSourceTrackForFile(artist, dbSource, meta, file, trackNum, flacFiles, set);
                    trackNum = r.track_position;

                    return r;
                })
                ;
        }

        private SourceTrack CreateSourceTrackForFile(
            Artist artist,
            Source dbSource,
            Vendor.ArchiveOrg.Metadata.Metadata meta,
            Vendor.ArchiveOrg.Metadata.File file,
            int previousTrackNumber,
            IDictionary<string, Vendor.ArchiveOrg.Metadata.File> flacFiles,
            SourceSet set = null
        )
        {
            var trackNum = previousTrackNumber + 1;

            var title = !string.IsNullOrEmpty(file.title) ? file.title : file.original;

            var flac = file.original == null ? null : flacFiles.GetValue(file.original);

            return new SourceTrack()
            {
                title = title,
                track_position = trackNum,
                source_set_id = set?.id ?? -1,
                source_id = dbSource.id,
                duration = file.length.
                    Split(':').
                    Reverse().
                    Select((v, k) => (int)Math.Round(Math.Max(1, 60 * k) * double.Parse(v, NumberStyles.Any))).
                    Sum(),
                slug = SlugifyTrack(title),
                mp3_url = $"https://archive.org/download/{meta.identifier}/{file.name}",
                mp3_md5 = file.md5,
                flac_url = flac == null ? null : $"https://archive.org/download/{meta.identifier}/{flac.name}",
                flac_md5 = flac?.md5,
                updated_at = dbSource.updated_at,
                artist_id = artist.id
            };
        }

        async Task PreloadData(Artist artist)
        {
            existingSources = (await _sourceService.AllForArtist(artist)).
                GroupBy(venue => venue.upstream_identifier).
                ToDictionary(grp => grp.Key, grp => grp.First());
        }
    }

}
