using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    public partial class ArchiveOrgImporter : ImporterBase
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
                        new TransactionOptions { IsolationLevel = IsolationLevel.ReadCommitted },
                        TransactionScopeAsyncFlowOption.Enabled
                    );
                    if (await _sourceService.IsArchiveItemIntakeOwnedForUpdate(doc.identifier))
                    {
                        ImportProgress.WriteLine(
                            log,
                            ctx,
                            "Skipping {0}: owned_by_taperssection",
                            doc.identifier
                        );
                        scope.Complete();
                        return;
                    }

                    source = await _sourceService.ForUpstreamIdentifierFromPrimary(
                        artist,
                        doc.identifier
                    );
                    try
                    {
                        stats += await WriteRecordingAsync(
                            artist,
                            source,
                            doc,
                            metadata.Details,
                            src.upstream_source_id,
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
                var deletedSourceUpstreamIdentifiers = (
                    await _sourceService.ArchiveDeletionCandidatesFromPrimary(artist)
                )
                    .Select(source => source.upstream_identifier)
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
                    stats.Removed +=
                        await _sourceService.RemoveUnownedArchiveSourcesWithUpstreamIdentifiers(
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
