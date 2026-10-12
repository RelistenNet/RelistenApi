using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hangfire.Console;
using Hangfire.Server;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Relisten.Api.Models;
using Relisten.Vendor;
using Relisten.Vendor.ArchiveOrg;
using Relisten.Vendor.ArchiveOrg.Metadata;
using Sentry;
using InvalidDataException = System.IO.InvalidDataException;

namespace Relisten.Import;

// Both ingestion engines use these selection, metadata-validation, and diagnostic
// rules. Hangfire owns its loop; Temporal owns activity retries and commit receipts.
public partial class ArchiveOrgImporter
{
    private record ItemMetadata(RootObject Details, string DisplayDate);

    private string? ImportReason(
        SearchDoc doc,
        Source? source,
        SourceReviewInformation? review,
        string? targetedIdentifier
    )
    {
        if (targetedIdentifier != null && targetedIdentifier != doc.identifier)
        {
            return null;
        }

        if (source == null)
        {
            return "new_source";
        }

        if (review != null && doc.max_review_date > review.review_max_updated_at)
        {
            return "reviews_updated";
        }

        var date = ArchiveOrgImporterUtils.FixDisplayDate(doc.raw_display_date, doc.identifier);
        if (date != null && source.display_date != date)
        {
            return "date_updated";
        }

        if (
            CurrentImportOptions.RescrapeRecentDays is { } days
            && doc.addeddate > DateTime.UtcNow.AddDays(-days)
        )
        {
            return "recently_added";
        }

        return targetedIdentifier == doc.identifier ? "targeted_show" : null;
    }

    private async Task<ItemMetadata?> FetchItemMetadata(
        Artist artist,
        SearchDoc doc,
        string reason,
        PerformContext? ctx,
        CancellationToken cancellationToken = default
    )
    {
        using var sourceActivity = ActivitySource.StartActivity($"import-source:{doc.identifier}");
        sourceActivity?.SetTag("import.reason", reason);
        sourceActivity?.SetTag("archive.identifier because", doc.identifier);

        log.LogInformation(
            ImportEvents.ArchiveItemSelected,
            "Fetching Archive item {ItemIdentifier} because {Reason}",
            doc.identifier,
            reason
        );
        ctx?.WriteLine(
            "Pulling https://archive.org/metadata/{0} because '{1}'",
            doc.identifier,
            reason
        );

        using var detailRes = await http.GetAsync(
            DetailsUrlForIdentifier(doc.identifier),
            cancellationToken
        );
        detailRes.EnsureSuccessStatusCode();
        var detailsJson = await detailRes.Content.ReadAsStringAsync(cancellationToken);
        var detailsRoot = JsonConvert.DeserializeObject<RootObject>(
            detailsJson,
            new TolerantStringConverter()
        );

        if (detailsRoot == null)
        {
            throw new InvalidDataException("Archive.org returned empty item metadata.");
        }

        if (detailsRoot.is_dark ?? false)
        {
            log.LogInformation(
                ImportEvents.ArchiveItemSkipped,
                "Skipping Archive item {ItemIdentifier}: {Reason}",
                doc.identifier,
                "dark"
            );
            ImportProgress.WriteLine(log, ctx, "\tis_dark == true, skipping...");
            return null;
        }

        if (!string.IsNullOrEmpty(detailsRoot.error) || detailsRoot.errcode.HasValue)
        {
            throw new InvalidDataException("Archive.org returned an item metadata error.");
        }

        if (detailsRoot.metadata?.identifier != doc.identifier)
        {
            throw new InvalidDataException(
                "Archive.org returned missing or mismatched item metadata."
            );
        }

        if (detailsRoot.metadata?.date == null)
        {
            log.LogWarning(
                ImportEvents.ArchiveItemSkipped,
                "Skipping Archive item {ItemIdentifier}: {Reason}",
                doc.identifier,
                "missing_date"
            );
            ImportProgress.WriteLine(
                log,
                ctx,
                "\tSkipping {0} because it has an invalid, unrecoverable metadata (e.g. date missing): {1}",
                doc.identifier,
                detailsRoot.metadata
            );

            var e = new ArgumentException("Invalid, unrecoverable metadata")
            {
                Data =
                {
                    ["artist"] = artist.name,
                    ["archive_identifier"] = doc.identifier,
                    ["background_job_id"] = ctx?.BackgroundJob.Id,
                },
            };

            SentrySdk.CaptureException(e);

            return null;
        }

        var properDate = ArchiveOrgImporterUtils.FixDisplayDate(detailsRoot.metadata);

        // Log if date was remapped (invalid month/day converted to XX)
        var originalDate = detailsRoot.metadata?.date;
        if (properDate != null && originalDate != null && properDate != originalDate)
        {
            log.LogInformation(
                ImportEvents.ArchiveDateRemapped,
                "Remapped date for {ItemIdentifier}: {OriginalDate} to {DisplayDate}",
                doc.identifier,
                originalDate,
                properDate
            );
            ImportProgress.WriteLine(
                log,
                ctx,
                $"[REMAP_DATE] {doc.identifier}: '{originalDate}' → '{properDate}'"
            );
        }

        if (properDate == null)
        {
            log.LogWarning(
                ImportEvents.ArchiveItemSkipped,
                "Skipping Archive item {ItemIdentifier}: {Reason}",
                doc.identifier,
                "invalid_date"
            );
            ImportProgress.WriteLine(
                log,
                ctx,
                "\tSkipping {0} because it has an invalid, unrecoverable date: {1}",
                doc.identifier,
                detailsRoot.metadata?.date
            );

            var e = new ArgumentException(
                "Skipping doc because it has an invalid, unrecoverable date"
            )
            {
                Data =
                {
                    ["artist"] = artist.name,
                    ["archive_identifier"] = doc.identifier,
                    ["date"] = detailsRoot.metadata?.date,
                },
            };

            SentrySdk.CaptureException(e);

            return null;
        }

        return new ItemMetadata(detailsRoot, properDate);
    }

    private async Task ReportDeletionGuard(
        Artist artist,
        IReadOnlyCollection<string> candidates,
        PerformContext? ctx
    )
    {
        foreach (var identifier in candidates)
        {
            log.LogWarning(
                "Archive.org deletion guard: would have deleted source "
                    + "{Identifier} (https://archive.org/details/{Identifier}) for {ArtistName} ({ArtistSlug})",
                identifier,
                identifier,
                artist.name,
                artist.slug
            );
        }

        var message =
            $"🛑 Archive.org deletion guard blocked {candidates.Count} source "
            + $"deletions for {artist.name} ({artist.slug}); threshold is "
            + $"{MaxSourcesDeletedPerSync}. No sources were deleted.";

        var jobId = ctx?.BackgroundJob.Id;
        if (jobId != null)
        {
            message += $"\n\nHangfire job: /relisten-admin/hangfire/jobs/details/{jobId}";
        }

        ImportProgress.WriteLine(log, ctx, message);
        log.LogWarning(
            ImportEvents.ArchiveDeletionGuard,
            "Archive.org deletion guard blocked {SourceDeletionCount} source deletions for "
                + "{ArtistName} ({ArtistSlug}); threshold is {DeletionThreshold}",
            candidates.Count,
            artist.name,
            artist.slug,
            MaxSourcesDeletedPerSync
        );
        await discordWebhookNotifier.SendAsync(message);
    }
}
