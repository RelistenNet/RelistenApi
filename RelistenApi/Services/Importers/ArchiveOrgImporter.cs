using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Transactions;
using Hangfire.Console;
using Hangfire.Server;
using Newtonsoft.Json;
using Relisten.Api.Models;
using Relisten.Data;
using Relisten.Vendor;
using Relisten.Vendor.ArchiveOrg;
using Relisten.Vendor.ArchiveOrg.Metadata;
using Sentry;

namespace Relisten.Import
{
    public class ArchiveOrgImporter : ImporterBase
    {
        public const string DataSourceName = "archive.org";


        private readonly LinkService linkService;

        private IDictionary<string, SourceReviewInformation> existingSourceReviewInformation =
            new Dictionary<string, SourceReviewInformation>();

        private IDictionary<string, Source> existingSources = new Dictionary<string, Source>();

        public ArchiveOrgImporter(
            DbService db,
            VenueService venueService,
            TourService tourService,
            SourceService sourceService,
            SourceSetService sourceSetService,
            SourceReviewService sourceReviewService,
            LinkService linkService,
            SourceTrackService sourceTrackService,
            RedisService redisService
        ) : base(db, redisService)
        {
            this.linkService = linkService;
            _sourceService = sourceService;
            _venueService = venueService;
            _tourService = tourService;

            _sourceReviewService = sourceReviewService;
            _sourceTrackService = sourceTrackService;
            _sourceSetService = sourceSetService;
        }

        protected SourceService _sourceService { get; set; }
        protected SourceSetService _sourceSetService { get; set; }
        protected SourceReviewService _sourceReviewService { get; set; }
        protected SourceTrackService _sourceTrackService { get; set; }
        protected VenueService _venueService { get; set; }
        protected TourService _tourService { get; set; }

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

        public override async Task<ImportStats> ImportDataForArtist(Artist artist, ArtistUpstreamSource src,
            PerformContext ctx)
        {
            return await ImportSpecificShowDataForArtist(artist, src, null, ctx);
        }

        public override async Task<ImportStats> ImportSpecificShowDataForArtist(Artist artist, ArtistUpstreamSource src,
            string showIdentifier, PerformContext ctx)
        {
            await PreloadData(artist);

            var url = SearchUrlForArtist(artist, src);
            ctx?.WriteLine($"All shows URL: {url}");

            return await ProcessIdentifiers(artist, await http.GetAsync(url), src, showIdentifier, ctx);
        }

        private string SearchUrlForArtist(Artist artist, ArtistUpstreamSource src)
        {
            return
                $"http://archive.org/advancedsearch.php?q=collection%3A{src.upstream_identifier}&fl%5B%5D=date&fl%5B%5D=identifier&fl%5B%5D=year&fl%5B%5D=addeddate&fl%5B%5D=reviewdate&fl%5B%5D=indexdate&fl%5B%5D=publicdate&fl%5B%5D=updatedate&sort%5B%5D=year+asc&sort%5B%5D=&sort%5B%5D=&rows=9999999&page=1&output=json&save=yes";
        }

        private static string DetailsUrlForIdentifier(string identifier)
        {
            return $"http://archive.org/metadata/{identifier}";
        }

        private async Task<ImportStats> ProcessIdentifiers(Artist artist, HttpResponseMessage res,
            ArtistUpstreamSource src, string showIdentifier, PerformContext ctx)
        {
            var stats = new ImportStats();

            var json = await res.Content.ReadAsStringAsync();
            var root = JsonConvert.DeserializeObject<SearchRootObject>(
                json.Replace("\"0000-01-01T00:00:00Z\"", "null") /* serious...wtf archive */,
                new TolerantArchiveDateTimeConverter()
            );

            if (root?.response?.docs == null)
            {
                ctx?.WriteLine($"No results found. json={json}");
                return stats;
            }

            ctx?.WriteLine($"Checking {root.response.docs.Count} archive.org results");

            var prog = ctx?.WriteProgressBar();

            var identifiersWithoutMP3s = new HashSet<string>();

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

                    var maxSourceInformation = existingSourceReviewInformation.GetValue(doc.identifier);
                    var isNew = dbShow == null;
                    var needsToUpdateReviews = maxSourceInformation != null &&
                                               doc._iguana_index_date > maxSourceInformation.review_max_updated_at;
                    var needsDateUpdate = dbShow?.display_date?.Contains("XX");

                    if (currentIsTargetedShow || isNew || needsToUpdateReviews || needsDateUpdate)
                    {
                        ctx?.WriteLine("Pulling https://archive.org/metadata/{0}", doc.identifier);

                        var detailRes = await http.GetAsync(DetailsUrlForIdentifier(doc.identifier));
                        var detailsJson = await detailRes.Content.ReadAsStringAsync();
                        var detailsRoot = JsonConvert.DeserializeObject<RootObject>(
                            detailsJson,
                            new TolerantStringConverter()
                        );

                        if (detailsRoot.is_dark ?? false)
                        {
                            ctx?.WriteLine("\tis_dark == true, skipping...");
                            return;
                        }

                        // in the future it might be better to retry intead of skipping
                        if (detailsRoot.metadata?.date == null)
                        {
                            ctx?.WriteLine("\tSkipping {0} because it has an invalid, unrecoverable metadata: {1}",
                                doc.identifier, detailsRoot.metadata);

                            var e = new ArgumentException("Invalid, unrecoverable metadata")
                            {
                                Data =
                                {
                                    ["artist"] = artist.name,
                                    ["archive_identifier"] = doc.identifier,
                                    ["background_job_id"] = ctx?.BackgroundJob.Id
                                }
                            };

                            SentrySdk.CaptureException(e);

                            return;
                        }

                        var properDate = ArchiveOrgImporterUtils.FixDisplayDate(detailsRoot.metadata);

                        if (properDate == null)
                        {
                            ctx?.WriteLine("\tSkipping {0} because it has an invalid, unrecoverable date: {1}",
                                doc.identifier, detailsRoot.metadata.date);

                            var e = new ArgumentException("Skipping doc because it has an invalid, unrecoverable date")
                            {
                                Data =
                                {
                                    ["artist"] = artist.name,
                                    ["archive_identifier"] = doc.identifier,
                                    ["date"] = detailsRoot.metadata.date
                                }
                            };

                            SentrySdk.CaptureException(e);

                            return;
                        }

                        using var scope = new TransactionScope(TransactionScopeOption.Required,
                            new TransactionOptions() { IsolationLevel = IsolationLevel.RepeatableRead },
                            TransactionScopeAsyncFlowOption.Enabled);

                        try
                        {
                            stats += await ImportSingleIdentifier(artist, dbShow, doc, detailsRoot, src,
                                properDate, ctx);
                        }
                        catch (NoVBRMp3FilesException)
                        {
                            identifiersWithoutMP3s.Add(doc.identifier);
                        }

                        scope.Complete();
                    }
                }
                catch (Exception e)
                {
                    ctx?.WriteLine($"Error processing {doc.identifier}:");
                    ctx?.LogException(e);

                    e.Data["artist"] = artist.name;
                    e.Data["archive_org_identifier"] = doc.identifier;

                    SentrySdk.CaptureException(e);
                }
            });

            // we want to keep all the shows from this import--aside from ones that no longer have MP3s
            var showsToKeep = root.response.docs
                    .Select(d => d.identifier)
                    .Except(identifiersWithoutMP3s)
                ;

            // find sources that no longer exist
            var deletedSourceUpstreamIdentifiers = existingSources
                    .Select(kvp => kvp.Key)
                    .Except(showsToKeep)
                    .ToList()
                ;

            ctx?.WriteLine($"Removing {deletedSourceUpstreamIdentifiers.Count} sources " +
                           $"that are in the database but no longer on Archive.org: {string.Join(',', deletedSourceUpstreamIdentifiers)}");
            stats.Removed +=
                await _sourceService.RemoveSourcesWithUpstreamIdentifiers(deletedSourceUpstreamIdentifiers);

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
            SearchDoc searchDoc,
            RootObject detailsRoot,
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

                throw new NoVBRMp3FilesException();
            }

            var dbReviews = detailsRoot.reviews == null
                ? new List<SourceReview>()
                : detailsRoot.reviews.Select(rev =>
                {
                    return new SourceReview
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
                var venueName = string.IsNullOrEmpty(meta.venue) ? meta.coverage : meta.venue;

                if (string.IsNullOrEmpty(venueName))
                {
                    venueName = "Unknown Venue";
                }

                var venueUpstreamId =
                    venueName + (string.IsNullOrEmpty(meta.coverage) ? "blank coverage" : meta.coverage);
                dbVenue = await _venueService.ForUpstreamIdentifier(artist, venueUpstreamId);

                if (dbVenue == null)
                {
                    dbVenue = await _venueService.Save(new Venue
                    {
                        artist_id = artist.id,
                        name = venueName,
                        location = string.IsNullOrEmpty(meta.coverage) ? "Unknown Location" : meta.coverage,
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
            }
            else
            {
                dbSource = await _sourceService.Save(CreateSourceForMetadata(artist, detailsRoot, searchDoc,
                    properDisplayDate, dbVenue));
                stats.Created++;

                existingSources[dbSource.upstream_identifier] = dbSource;

                stats.Created += (await ReplaceSourceReviews(dbSource, dbReviews)).Count();
            }

            stats.Created +=
                (await linkService.AddLinksForSource(dbSource, LinksForSource(artist, dbSource, upstreamSrc)))
                .Count();

            var dbSet = (await _sourceSetService.UpdateAll(dbSource, new[] { CreateSetForSource(dbSource) }))
                .First();
            stats.Created++;

            var flacTracksByName = flacFiles.GroupBy(f => f.name).ToDictionary(g => g.Key, g => g.First());

            var allFilesByName = detailsRoot.files?.GroupBy(f => f.name).ToDictionary(g => g.Key, g => g.First());

            var dbTracks = CreateSourceTracksForFiles(artist, dbSource, meta, mp3Files, flacTracksByName, dbSet,
                allFilesByName);

            stats.Created += (await _sourceTrackService.InsertAll(dbSource, dbTracks)).Count();

            ResetTrackSlugCounts();

            return stats;
        }

        private IEnumerable<Link> LinksForSource(Artist artist, Source dbSource, ArtistUpstreamSource src)
        {
            var links = new List<Link>
            {
                new()
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
                links.Add(new Link
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

        private async Task<IEnumerable<SourceReview>> ReplaceSourceReviews(Source source,
            IEnumerable<SourceReview> reviews)
        {
            foreach (var review in reviews)
            {
                review.source_id = source.id;
            }

            return await _sourceReviewService.UpdateAll(source, reviews);
        }

        private SourceSet CreateSetForSource(
            Source source
        )
        {
            return new SourceSet
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
            RootObject detailsRoot,
            SearchDoc searchDoc,
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

            return new Source
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


        private IEnumerable<SourceTrack> CreateSourceTracksForFiles(
            Artist artist,
            Source dbSource,
            Metadata meta,
            IEnumerable<File> mp3Files,
            IDictionary<string, File> flacFiles,
            SourceSet set,
            IDictionary<string, File> allFilesByName)
        {
            var trackNum = 0;

            return mp3Files.Where(file =>
                {
                    return !(
                        (file.title == null && file.original == null && file.name == null)
                        || file.length == null
                        || file.name == null
                    );
                }).OrderBy(file => file.name).Select(file =>
                {
                    var r = CreateSourceTrackForFile(artist, dbSource, meta, file, trackNum, flacFiles, set,
                        allFilesByName);
                    trackNum = r.track_position;

                    return r;
                })
                ;
        }

        private SourceTrack CreateSourceTrackForFile(
            Artist artist,
            Source dbSource,
            Metadata meta,
            File file,
            int previousTrackNumber,
            IDictionary<string, File> flacFiles,
            SourceSet set,
            IDictionary<string, File> allFilesByName)
        {
            var trackNum = previousTrackNumber + 1;

            var title = file.name;

            if (!string.IsNullOrEmpty(file.title))
            {
                title = file.title;
            }
            else if (
                !string.IsNullOrEmpty(file.original)
                && allFilesByName.TryGetValue(file.original, out var original)
                && !string.IsNullOrWhiteSpace(original.title)
            )
            {
                // sometimes if file.title is null the original file.title will be null as well
                title = original.title;
            }

            var flac = file.original == null ? null : flacFiles.GetValue(file.original);

            return new SourceTrack
            {
                title = title,
                track_position = trackNum,
                source_set_id = set?.id ?? -1,
                source_id = dbSource.id,
                duration =
                    file.length.Split(':').Reverse().Select((v, k) =>
                        (int)Math.Round(Math.Max(1, 60 * k) * double.Parse(v, NumberStyles.Any))).Sum(),
                slug = SlugifyTrack(title),
                mp3_url = $"https://archive.org/download/{meta.identifier}/{file.name}",
                mp3_md5 = file.md5,
                flac_url = flac == null ? null : $"https://archive.org/download/{meta.identifier}/{flac.name}",
                flac_md5 = flac?.md5,
                updated_at = dbSource.updated_at,
                artist_id = artist.id
            };
        }

        private async Task PreloadData(Artist artist)
        {
            existingSources = (await _sourceService.AllForArtist(artist))
                .GroupBy(source => source.upstream_identifier).ToDictionary(grp => grp.Key, grp => grp.First());

            existingSourceReviewInformation =
                (await _sourceService.AllSourceReviewInformationForArtist(artist))
                .GroupBy(source => source.upstream_identifier).ToDictionary(grp => grp.Key, grp => grp.First());
        }

        private class NoVBRMp3FilesException : Exception
        {
        }
    }
}
