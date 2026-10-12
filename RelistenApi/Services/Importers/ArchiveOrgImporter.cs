using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Transactions;
using Hangfire.Console;
using Hangfire.Server;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Relisten.Api.Models;
using Relisten.Data;
using Relisten.Ingestion;
using Relisten.Services.Notifications;
using Relisten.Vendor;
using Relisten.Vendor.ArchiveOrg;
using Relisten.Vendor.ArchiveOrg.Metadata;
using Sentry;
using InvalidDataException = System.IO.InvalidDataException;

namespace Relisten.Import
{
    public partial class ArchiveOrgImporter : IngestionImporter<ArchiveImportItem, ArchiveDiscovery>
    {
        private const int MaxSourcesDeletedPerSync = 5;

        private static readonly ActivitySource ActivitySource = new("Relisten.Import");

        private readonly LinkService linkService;
        private readonly IDiscordWebhookNotifier discordWebhookNotifier;

        private IDictionary<string, SourceReviewInformation?> existingSourceReviewInformation =
            new Dictionary<string, SourceReviewInformation?>();

        private IDictionary<string, Source?> existingSources = new Dictionary<string, Source?>();

        public ArchiveOrgImporter(
            CatalogRebuildService catalog,
            Microsoft.Extensions.Logging.ILogger<ArchiveOrgImporter> log,
            VenueService venueService,
            TourService tourService,
            SourceService sourceService,
            SourceSetService sourceSetService,
            SourceReviewService sourceReviewService,
            LinkService linkService,
            SourceTrackService sourceTrackService,
            IDiscordWebhookNotifier discordWebhookNotifier
        )
            : base(catalog, log)
        {
            this.linkService = linkService;
            this.discordWebhookNotifier = discordWebhookNotifier;
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

        public override UpstreamProvider Provider => UpstreamProvider.ArchiveOrg;

        public override ImportableData ImportableDataForArtist(Artist artist)
        {
            var r =
                ImportableData.Sources
                | ImportableData.SourceReviews
                | ImportableData.SourceRatings;

            if (artist.features.per_source_venues)
            {
                r |= ImportableData.Venues;
            }

            return r;
        }

        public override async Task<ImportStats> ImportDataForArtist(
            Artist artist,
            ArtistUpstreamSource src,
            PerformContext? ctx
        )
        {
            return await ImportSpecificShowDataForArtist(artist, src, null, ctx);
        }

        public override async Task<ImportStats> ImportSpecificShowDataForArtist(
            Artist artist,
            ArtistUpstreamSource src,
            string? showIdentifier,
            PerformContext? ctx
        )
        {
            await PreloadData(artist);

            // Require a complete upstream result before importing or reconciling any sources.
            var docs = await new ArchiveOrgSearchClient(http).FetchAllAsync(
                src.upstream_identifier,
                CurrentImportOptions.OnlyYear,
                message => ImportProgress.WriteLine(log, ctx, message)
            );

            return await ProcessIdentifiers(artist, docs, src, showIdentifier, ctx);
        }

        private static string DetailsUrlForIdentifier(string identifier)
        {
            return $"https://archive.org/metadata/{Uri.EscapeDataString(identifier)}";
        }

        private async Task<ImportStats> ProcessIdentifiers(
            Artist artist,
            IList<SearchDoc> docs,
            ArtistUpstreamSource src,
            string? showIdentifier,
            PerformContext? ctx
        )
        {
            using var artistActivity = ActivitySource.StartActivity($"import-artist:{artist.slug}");
            artistActivity?.SetTag("artist.id", artist.id);
            artistActivity?.SetTag("artist.name", artist.name);

            var stats = new ImportStats();

            artistActivity?.SetTag("source_count", docs.Count);
            ImportProgress.WriteLine(log, ctx, $"Checking {docs.Count} archive.org results");

            var prog = ctx?.WriteProgressBar();

            var identifiersWithoutMP3s = new HashSet<string>();

            async Task ProcessDocument(SearchDoc doc)
            {
                try
                {
                    var source = existingSources.GetValue(doc.identifier);
                    var reason = ImportReason(
                        doc,
                        source,
                        existingSourceReviewInformation.GetValue(doc.identifier),
                        showIdentifier
                    );
                    if (reason == null)
                    {
                        return;
                    }

                    var metadata = await FetchItemMetadata(artist, doc, reason, ctx);
                    if (metadata == null)
                    {
                        return;
                    }

                    using var scope = new TransactionScope(
                        TransactionScopeOption.Required,
                        new TransactionOptions { IsolationLevel = IsolationLevel.RepeatableRead },
                        TransactionScopeAsyncFlowOption.Enabled
                    );
                    try
                    {
                        stats += await ImportSingleIdentifier(
                            artist,
                            source,
                            doc,
                            metadata.Details,
                            src,
                            metadata.DisplayDate,
                            ctx
                        );
                    }
                    catch (NoVBRMp3FilesException)
                    {
                        identifiersWithoutMP3s.Add(doc.identifier);
                    }

                    scope.Complete();
                }
                catch (Exception error)
                {
                    ImportProgress.WriteLine(log, ctx, $"Error processing {doc.identifier}:");
                    ImportProgress.LogException(log, ctx, error);
                    error.Data["artist"] = artist.name;
                    error.Data["archive_org_identifier"] = doc.identifier;
                    SentrySdk.CaptureException(error);
                }
            }

            if (prog == null)
            {
                foreach (var doc in docs)
                {
                    await ProcessDocument(doc);
                }
            }
            else
            {
                await docs.AsyncForEachWithProgress(prog, ProcessDocument);
            }

            if (!CurrentImportOptions.IsThinScrape)
            {
                // we want to keep all the shows from this import--aside from ones that no longer have MP3s
                var sourcesToKeep = docs.Select(d => d.identifier).Except(identifiersWithoutMP3s);
                var deletedSourceUpstreamIdentifiers = existingSources
                    .Select(kvp => kvp.Key)
                    .Except(sourcesToKeep)
                    .ToList();

                var verification = await VerifyDeletionCandidatesAsync(
                    deletedSourceUpstreamIdentifiers,
                    identifiersWithoutMP3s,
                    http,
                    message =>
                    {
                        ImportProgress.WriteLine(log, ctx, message);
                    }
                );

                if (verification.Guarded)
                {
                    await ReportDeletionGuard(artist, deletedSourceUpstreamIdentifiers, ctx);
                }
                else if (verification.Identifiers.Count > 0)
                {
                    ImportProgress.WriteLine(
                        log,
                        ctx,
                        $"Removing {verification.Identifiers.Count} sources "
                            + $"confirmed deleted upstream or without VBR MP3 files: "
                            + string.Join(',', verification.Identifiers)
                    );
                    stats.Removed += await _sourceService.RemoveSourcesWithUpstreamIdentifiers(
                        artist.id,
                        verification.Identifiers
                    );
                }
            }

            ImportProgress.WriteLine(log, ctx, $"Import stats: {stats}");

            if (stats.Created > 0 || stats.Updated > 0 || stats.Removed > 0)
            {
                ImportProgress.WriteLine(log, ctx, "Rebuilding shows...");
                await RebuildShows(artist);

                ImportProgress.WriteLine(log, ctx, "--> rebuilt shows!");
                ImportProgress.WriteLine(log, ctx, "Rebuilding years...");

                await RebuildYears(artist);

                ImportProgress.WriteLine(log, ctx, "--> rebuilt years!");
            }
            else
            {
                ImportProgress.WriteLine(
                    log,
                    ctx,
                    "No changes detected, skipping show/year rebuild."
                );
            }

            return stats;
        }

        internal static async Task<ArchiveDeletionVerification> VerifyDeletionCandidatesAsync(
            IReadOnlyCollection<string> candidates,
            ISet<string> identifiersWithoutMP3s,
            HttpClient httpClient,
            Action<string> log
        )
        {
            if (ExceedsDeletionLimit(candidates.Count))
            {
                return new ArchiveDeletionVerification(true, []);
            }

            var decisions = new List<DeletionDecision>();
            var metadataClient = new ArchiveOrgMetadataClient(httpClient);
            foreach (var identifier in candidates)
            {
                DeletionDecision decision;
                if (identifiersWithoutMP3s.Contains(identifier))
                {
                    decision = new DeletionDecision(
                        identifier,
                        "Ineligible",
                        "Metadata contains no VBR MP3 files",
                        true
                    );
                }
                else
                {
                    var result = await metadataClient.CheckAsync(identifier);
                    decision = new DeletionDecision(
                        identifier,
                        result.Status.ToString(),
                        result.Reason,
                        result.Status == ArchiveOrgItemStatus.Deleted
                    );
                }

                decisions.Add(decision);
                log(
                    $"Archive.org deletion check {identifier}: {decision.Status} ({decision.Reason})"
                );
            }

            return new ArchiveDeletionVerification(false, decisions.ToArray());
        }

        internal record ArchiveDeletionVerification(bool Guarded, DeletionDecision[] Decisions)
        {
            internal List<string> Identifiers =>
                Decisions
                    .Where(decision => decision.Delete)
                    .Select(decision => decision.Identifier)
                    .ToList();
        }

        internal static bool HasNoVbrMp3Files(RootObject details)
        {
            if (details.files == null)
            {
                throw new InvalidDataException(
                    "Archive.org item metadata is missing its files array."
                );
            }

            return !details.files.Any(file => file?.format == "VBR MP3");
        }

        internal static bool ExceedsDeletionLimit(int sourceCount)
        {
            return sourceCount > MaxSourcesDeletedPerSync;
        }

        private async Task<ImportStats> ImportSingleIdentifier(
            Artist artist,
            Source? dbSource,
            SearchDoc searchDoc,
            RootObject detailsRoot,
            ArtistUpstreamSource upstreamSrc,
            string properDisplayDate,
            PerformContext? ctx
        )
        {
            var stats = new ImportStats();

            var upstream_identifier = searchDoc.identifier;
            var isUpdate = dbSource != null;

            var meta = detailsRoot.metadata!;

            var mp3Files =
                detailsRoot.files?.Where(file => file?.format == "VBR MP3")
                ?? Enumerable.Empty<File>();
            var flacFiles =
                detailsRoot.files?.Where(file =>
                    file?.format == "Flac" || file?.format == "24bit Flac"
                ) ?? Enumerable.Empty<File>();

            if (HasNoVbrMp3Files(detailsRoot))
            {
                ImportProgress.WriteLine(
                    log,
                    ctx,
                    "\tNo VBR MP3 files found for {0}",
                    searchDoc.identifier
                );

                throw new NoVBRMp3FilesException();
            }

            var dbReviews =
                detailsRoot.reviews == null
                    ? new List<SourceReview>()
                    : detailsRoot
                        .reviews.Select(rev =>
                        {
                            return new SourceReview
                            {
                                rating = rev.stars * 2, // scale to out of 10
                                title = rev.reviewtitle,
                                review = rev.reviewbody?.Replace("Â", "") ?? "",
                                author = rev.reviewer,
                                updated_at = rev.reviewdate,
                            };
                        })
                        .ToList();

            Venue? dbVenue = null;
            if (artist.features.per_source_venues)
            {
                var venueName = string.IsNullOrEmpty(meta.venue) ? meta.coverage : meta.venue;

                if (string.IsNullOrEmpty(venueName))
                {
                    venueName = "Unknown Venue";
                }

                var venueUpstreamId =
                    venueName
                    + (string.IsNullOrEmpty(meta.coverage) ? "blank coverage" : meta.coverage);
                dbVenue = await _venueService.ForUpstreamIdentifier(artist, venueUpstreamId);

                if (dbVenue == null)
                {
                    dbVenue = await _venueService.Save(
                        new Venue
                        {
                            artist_id = artist.id,
                            name = venueName,
                            location = string.IsNullOrEmpty(meta.coverage)
                                ? "Unknown Location"
                                : meta.coverage,
                            upstream_identifier = venueUpstreamId,
                            slug = Slugify(venueName),
                            updated_at = searchDoc.max_updated_at,
                        }
                    );
                }
            }

            if (isUpdate)
            {
                var src = CreateSourceForMetadata(
                    artist,
                    detailsRoot,
                    searchDoc,
                    properDisplayDate
                );
                src.id = dbSource!.id;
                src.venue_id = dbVenue?.id;

                dbSource = await _sourceService.Save(src);
                if (dbVenue != null)
                    dbSource.venue = dbVenue;

                stats.Updated++;
            }
            else
            {
                dbSource = await _sourceService.Save(
                    CreateSourceForMetadata(
                        artist,
                        detailsRoot,
                        searchDoc,
                        properDisplayDate,
                        dbVenue
                    )
                );
                stats.Created++;

                existingSources[dbSource.upstream_identifier] = dbSource;
            }

            stats.Created += (await ReplaceSourceReviews(dbSource, dbReviews)).Count();

            stats.Created += (
                await linkService.AddLinksForSource(
                    dbSource,
                    LinksForSource(artist, dbSource, upstreamSrc)
                )
            ).Count();

            var dbSet = (
                await _sourceSetService.UpdateAll(
                    dbSource,
                    new[] { CreateSetForSource(dbSource) },
                    SourceSetUuidVersion.V1
                )
            ).First();
            stats.Created++;

            var flacTracksByName = flacFiles
                .GroupBy(f => f.name)
                .ToDictionary(g => g.Key, g => (File?)g.First());

            var allFilesByName =
                detailsRoot
                    .files?.GroupBy(f => f.name)
                    .ToDictionary(g => g.Key, g => (File?)g.First())
                ?? new Dictionary<string, File?>();

            var dbTracks = CreateSourceTracksForFiles(
                artist,
                dbSource,
                meta,
                mp3Files,
                flacTracksByName,
                dbSet,
                allFilesByName
            );

            stats.Created += (await _sourceTrackService.InsertAll(dbSource, dbTracks)).Count();

            ResetTrackSlugCounts();

            return stats;
        }

        private IEnumerable<Link> LinksForSource(
            Artist artist,
            Source dbSource,
            ArtistUpstreamSource src
        )
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
                    label = "View on archive.org",
                },
            };

            if (
                artist.upstream_sources.Any(s =>
                    s.upstream_source_id == 6 /* setlist.fm */
                )
            )
            {
                links.Add(
                    new Link
                    {
                        source_id = dbSource.id,
                        for_ratings = false,
                        for_source = false,
                        for_reviews = false,
                        upstream_source_id =
                            6 /* setlist.fm */
                        ,
                        url = "https://www.setlist.fm/",
                        label = "Setlist Information from setlist.fm",
                    }
                );
            }

            return links;
        }

        private async Task<IEnumerable<SourceReview>> ReplaceSourceReviews(
            Source source,
            IEnumerable<SourceReview> reviews
        )
        {
            foreach (var review in reviews)
            {
                review.source_id = source.id;
            }

            return await _sourceReviewService.UpdateAll(source, reviews);
        }

        private SourceSet CreateSetForSource(Source source)
        {
            return new SourceSet
            {
                source_id = source.id,
                index = 0,
                is_encore = false,
                name = "Set",
                updated_at = source.updated_at,
            };
        }

        private Source CreateSourceForMetadata(
            Artist artist,
            RootObject detailsRoot,
            SearchDoc searchDoc,
            string properDisplayDate,
            Venue? dbVenue = null
        )
        {
            var meta = detailsRoot.metadata;

            var sbd =
                meta.identifier.EmptyIfNull().ContainsInsensitive("sbd")
                || meta.title.EmptyIfNull().ContainsInsensitive("sbd")
                || meta.source.EmptyIfNull().ContainsInsensitive("sbd")
                || meta.lineage.EmptyIfNull().ContainsInsensitive("sbd");

            var remaster =
                meta.identifier.EmptyIfNull().ContainsInsensitive("remast")
                || meta.title.EmptyIfNull().ContainsInsensitive("remast")
                || meta.source.EmptyIfNull().ContainsInsensitive("remast")
                || meta.lineage.EmptyIfNull().ContainsInsensitive("remast");

            var flac_type = FlacType.NoFlac;

            if (detailsRoot.files?.Any(f => f.format == "24bit Flac") ?? false)
            {
                flac_type = FlacType.Flac24Bit;
            }
            else if (detailsRoot.files?.Any(f => f.format == "Flac") ?? false)
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
                updated_at = searchDoc.max_updated_at,
                venue_id = dbVenue?.id,
                display_date = properDisplayDate,
                flac_type = flac_type,
            };
        }

        private IEnumerable<SourceTrack> CreateSourceTracksForFiles(
            Artist artist,
            Source dbSource,
            Metadata meta,
            IEnumerable<File> mp3Files,
            IDictionary<string, File?> flacFiles,
            SourceSet set,
            IDictionary<string, File?> allFilesByName
        )
        {
            var trackNum = 0;

            return mp3Files
                .Where(file =>
                {
                    return !(
                        (file.title == null && file.original == null && file.name == null)
                        || file.length == null
                        || file.name == null
                    );
                })
                .OrderBy(file => file.name)
                .Select(file =>
                {
                    var r = CreateSourceTrackForFile(
                        artist,
                        dbSource,
                        meta,
                        file,
                        trackNum,
                        flacFiles,
                        set,
                        allFilesByName
                    );
                    trackNum = r.track_position;

                    return r;
                });
        }

        private SourceTrack CreateSourceTrackForFile(
            Artist artist,
            Source dbSource,
            Metadata meta,
            File file,
            int previousTrackNumber,
            IDictionary<string, File?> flacFiles,
            SourceSet set,
            IDictionary<string, File?> allFilesByName
        )
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
                && original != null
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
                duration = file.length!.Split(':')
                    .Reverse()
                    .Select(
                        (v, k) =>
                            (int)Math.Round(Math.Max(1, 60 * k) * double.Parse(v, NumberStyles.Any))
                    )
                    .Sum(),
                slug = SlugifyTrack(title),
                mp3_url = $"https://archive.org/download/{meta.identifier}/{file.name}",
                mp3_md5 = file.md5,
                flac_url =
                    flac == null
                        ? null
                        : $"https://archive.org/download/{meta.identifier}/{flac.name}",
                flac_md5 = flac?.md5,
                updated_at = dbSource.updated_at,
                artist_id = artist.id,
            };
        }

        private async Task PreloadData(Artist artist)
        {
            // Use primary database methods to ensure read-after-write consistency.
            // This is important when content has been deleted before import (full refresh),
            // as the read replica may have replication lag.
            existingSources = (await _sourceService.AllForArtistFromPrimary(artist))
                .GroupBy(source => source.upstream_identifier)
                .ToDictionary(grp => grp.Key, grp => (Source?)grp.First());

            existingSourceReviewInformation = (
                await _sourceService.AllSourceReviewInformationForArtistFromPrimary(artist)
            )
                .GroupBy(source => source.upstream_identifier)
                .ToDictionary(grp => grp.Key, grp => (SourceReviewInformation?)grp.First());
        }

        private class NoVBRMp3FilesException : Exception { }
    }
}
