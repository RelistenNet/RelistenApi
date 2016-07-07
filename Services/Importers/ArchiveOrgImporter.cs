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
        protected ILogger<SetlistFmImporter> _log { get; set; }

        public ArchiveOrgImporter(
            DbService db,
            VenueService venueService,
            TourService tourService,
            SourceService sourceService,
            SourceSetService sourceSetService,
            SourceReviewService sourceReviewService,
            SourceTrackService sourceTrackService,
            ILogger<SetlistFmImporter> log
        ) : base(db)
        {
            this._sourceService = sourceService;
            this._venueService = venueService;
            this._tourService = tourService;
            this._log = log;
            _sourceReviewService = sourceReviewService;
            _sourceTrackService = sourceTrackService;
            _sourceSetService = sourceSetService;
        }

        public override ImportableData ImportableDataForArtist(Artist artist)
        {
            if (!artist.data_source.Contains(DataSourceName)) return ImportableData.Nothing;

            var r = ImportableData.Sources | ImportableData.SourceReviews | ImportableData.SourceRatings;

            if (artist.features.per_source_venues)
            {
                r |= ImportableData.Venues;
            }

            return r;
        }

        public override async Task<ImportStats> ImportDataForArtist(Artist artist)
        {
            await PreloadData(artist);
            return await ProcessIdentifiers(artist, await this.http.GetAsync(SearchUrlForArtist(artist)));
        }

        private IDictionary<string, Source> existingSources = new Dictionary<string, Source>();

        private static string SearchUrlForArtist(Artist artist)
        {
            return $"http://archive.org/advancedsearch.php?q=collection%3A{artist.upstream_identifier}&fl%5B%5D=date&fl%5B%5D=identifier&fl%5B%5D=year&fl%5B%5D=oai_updatedate&sort%5B%5D=year+asc&sort%5B%5D=&sort%5B%5D=&rows=9999999&page=1&output=json&save=yes";
        }
        private static string DetailsUrlForIdentifier(string identifier)
        {
            return $"http://archive.org/metadata/{identifier}";
        }

        private async Task<ImportStats> ProcessIdentifiers(Artist artist, HttpResponseMessage res)
        {
            var stats = new ImportStats();

            var json = await res.Content.ReadAsStringAsync();
            var root = JsonConvert.DeserializeObject<Relisten.Vendor.ArchiveOrg.SearchRootObject>(
                json,
                new Relisten.Vendor.ArchiveOrg.TolerantArchiveDateTimeConverter() 
            );

            foreach (var doc in root.response.docs)
            {
//                _log.LogDebug("Checking {0}", doc.identifier);
                var dbShow = existingSources.GetValue(doc.identifier);
                if (dbShow == null
                || doc._iguana_updated_at > dbShow.updated_at)
                {
                    _log.LogDebug("Pulling https://archive.org/metadata/{0}", doc.identifier);
                    var detailRes = await http.GetAsync(DetailsUrlForIdentifier(doc.identifier));
                    var detailsJson = await detailRes.Content.ReadAsStringAsync();
                    var detailsRoot = JsonConvert.DeserializeObject<Relisten.Vendor.ArchiveOrg.Metadata.RootObject>(
                        detailsJson,
                        new Vendor.ArchiveOrg.TolerantStringConverter()
                    );
                    stats += await ImportSingleIdentifier(artist, dbShow, doc, detailsRoot);
                }
            }

            // update shows
            await RebuildShows(artist);

            // update years
            await RebuildYears(artist);

            return stats;
        }

        private async Task<ImportStats> ImportSingleIdentifier(
            Artist artist,
            Source dbSource,
            Relisten.Vendor.ArchiveOrg.SearchDoc searchDoc,
            Relisten.Vendor.ArchiveOrg.Metadata.RootObject detailsRoot
        )
        {
            var stats = new ImportStats();

            var upstream_identifier = searchDoc.identifier;
            var isUpdate = dbSource != null;

            var meta = detailsRoot.metadata;

            var mp3Files = detailsRoot.files.Where(file => file.format == "VBR MP3");

            if(mp3Files.Count() == 0) {
                _log.LogDebug("No VBR MP3 files found for {0}", searchDoc.identifier);

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
                    review = rev.reviewbody,
                    author = rev.reviewer,
                    updated_at = rev.reviewdate
                };
            }).ToList();

            if (isUpdate)
            {
                var src = CreateSourceForMetadata(artist, meta, searchDoc);
                src.id = dbSource.id;
                dbSource = await _sourceService.Save(src);

                stats.Updated++;
                stats.Created += (await ReplaceSourceReviews(dbSource, dbReviews)).Count();
            }
            else
            {
                Venue dbVenue = null;
                if (artist.features.per_source_venues)
                {
                    var venueName = meta.venue.Length > 0 ? meta.venue : meta.coverage;
                    var venueUpstreamId = venueName;
                    dbVenue = await _venueService.ForUpstreamIdentifier(artist, venueUpstreamId);

                    if(dbVenue == null)
                    {
                        dbVenue = await _venueService.Save(new Venue() {
                            artist_id = artist.id,
                            name = venueName,
                            location = meta.coverage,
                            upstream_identifier = venueUpstreamId,
                            slug = Slugify(venueName),
                            updated_at = searchDoc._iguana_updated_at
                        });
                    }
                }

                dbSource = await _sourceService.Save(CreateSourceForMetadata(artist, meta, searchDoc, dbVenue));                
                stats.Created++;
                
                var dbSet = (await _sourceSetService.InsertAll(new[] { CreateSetForSource(dbSource) })).First();
                stats.Created++;

                var trackNum = 0;
                var dbTracks = mp3Files.
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
                        var r = CreateSourceTrackForFile(artist, dbSource, meta, file, trackNum, dbSet);
                        trackNum = r.track_position;
                        return r;
                    })
                    ;

                stats.Created += (await ReplaceSourceReviews(dbSource, dbReviews)).Count();
                stats.Created += (await _sourceTrackService.InsertAll(dbTracks)).Count();
            }

            return stats;
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
            Relisten.Vendor.ArchiveOrg.Metadata.Metadata meta,
            Relisten.Vendor.ArchiveOrg.SearchDoc searchDoc,
            Venue dbVenue = null
        )
        {
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
                display_date = meta.date
            };
        }

        private SourceTrack CreateSourceTrackForFile(
            Artist artist,
            Source dbSource,
            Relisten.Vendor.ArchiveOrg.Metadata.Metadata meta,
            Relisten.Vendor.ArchiveOrg.Metadata.File file,
            int previousTrackNumber,
            SourceSet set = null
        )
        {
            int trackNum = previousTrackNumber + 1;

            var title = !String.IsNullOrEmpty(file.title) ? file.title : file.original;

            return new SourceTrack()
            {
                title = title,
                track_position = trackNum,
                artist_id = artist.id,
                source_id = dbSource.id,
                source_set_id = set == null ? -1 : set.id,
                duration = file.length.
                    Split(':').
                    Reverse().
                    Select((v, k) => (int)Math.Round(Math.Max(1, 60 * k) * double.Parse(v, NumberStyles.Any))).
                    Sum(),
                slug = Slugify(title),
                mp3_url = $"https://archive.org/download/{meta.identifier}/{file.name}",
                md5 = file.md5,
                updated_at = dbSource.updated_at
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