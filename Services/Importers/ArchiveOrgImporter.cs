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

    public class ArchiveOrgImporter : ImporterBase
    {
        public const string DataSourceName = "archive.org";

        protected SourceService _sourceService { get; set; }
        protected VenueService _venueService { get; set; }
        protected TourService _tourService { get; set; }
        protected ILogger<SetlistFmImporter> _log { get; set; }

        public ArchiveOrgImporter(
            DbService db,
            VenueService venueService,
            TourService tourService,
            SourceService sourceService,
            ILogger<SetlistFmImporter> log
        ) : base(db)
        {
            this._sourceService = sourceService;
            this._venueService = venueService;
            this._tourService = tourService;
            this._log = log;
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
            var root = JsonConvert.DeserializeObject<Relisten.Vendor.ArchiveOrg.SearchRootObject>(json);

            foreach (var doc in root.response.docs)
            {
                var dbShow = existingSources.GetValue(doc.identifier);
                if (dbShow == null
                || doc._iguana_updated_at > dbShow.updated_at)
                {
                    var detailRes = await http.GetAsync(DetailsUrlForIdentifier(doc.identifier));
                    var detailsJson = await detailRes.Content.ReadAsStringAsync();
                    var detailsRoot = JsonConvert.DeserializeObject<Relisten.Vendor.ArchiveOrg.Metadata.RootObject>(detailsJson);
                    stats += await ImportSingleIdentifier(artist, dbShow, doc, detailsRoot);
                }
            }


            // update years
            // update shows
            // update avg_rating, num_reviews, avg_rating_weighted, duration on sources
            // update MAX(avg_rating) as avg_rating_weighted, MAX(duration) as avg_duration

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

            var dbReviews = detailsRoot.reviews.Select(rev =>
            {
                return new SourceReview()
                {
                    rating = rev.stars * 2, // scale to out of 10
                    title = rev.reviewtitle,
                    review = rev.reviewbody,
                    author = rev.reviewer,
                    updated_at = rev.createdate
                };
            }).ToList();

            if (isUpdate)
            {
                var src = CreateSourceForMetadata(artist, meta);
                src.id = dbSource.id;
                dbSource = await _sourceService.Save(src);
            }
            else
            {
                dbSource = await _sourceService.Save(CreateSourceForMetadata(artist, meta));
            }

            var trackNum = 0;
            var dbTracks = detailsRoot.files.
                Where(file => {
                    if(file.format != "VBR MP3")
                    {
                        return false;
                    }

                    if((file.title == null && file.original == null) || file.length == null || file.name == null)
                    {
                        return false;
                    }

                    return true;
                }).
                OrderBy(file => file.name).
                Select(file => {
                    var r = CreateSourceTrackForFile(artist, dbSource, meta, file, trackNum);
                    trackNum = r.track_position;
                    return r;
                })
                ;

            // associate sources with shows/create if necessary

            return stats;
        }

        private Source CreateSourceForMetadata(
            Artist artist,
            Relisten.Vendor.ArchiveOrg.Metadata.Metadata meta
        )
        {
            var sbd = meta.identifier.ContainsInsensitive("sbd")
                || meta.title.ContainsInsensitive("sbd")
                || meta.source.ContainsInsensitive("sbd")
                || meta.lineage.ContainsInsensitive("sbd")
                ;

            var remaster = meta.identifier.ContainsInsensitive("remast")
                || meta.title.ContainsInsensitive("remast")
                || meta.source.ContainsInsensitive("remast")
                || meta.lineage.ContainsInsensitive("remast")
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
                description = meta.description,
                taper_notes = meta.notes,
                source = meta.source,
                taper = meta.taper,
                transferrer = meta.transferer,
                lineage = meta.lineage
            };
        }

        private SourceTrack CreateSourceTrackForFile(
            Artist artist,
            Source dbSource,
            Relisten.Vendor.ArchiveOrg.Metadata.Metadata meta,
            Relisten.Vendor.ArchiveOrg.Metadata.File file,
            int previousTrackNumber
        )
        {
            int trackNum = previousTrackNumber + 1;

            var title = String.IsNullOrEmpty(file.title) ? file.title : file.original;

            return new SourceTrack()
            {
                title = title,
                track_position = trackNum,
                artist_id = artist.id,
                source_id = dbSource.id,
                source_set_id = 0,
                duration = (int)Math.Round(TimeSpan.ParseExact(file.length, new[] {
                            "mm:ss",
                            "m:ss",
                            "hh:mm:ss",
                            "h:mm:ss",
                        }, null).TotalSeconds),
                slug = Slugify(title),
                mp3_url = $"https://archive.org/download/{meta.identifier}/{file.name}",
                md5 = file.md5
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