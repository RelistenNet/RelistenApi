using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Relisten.Api.Models;
using Relisten.Ingestion;
using Relisten.Vendor;
using Relisten.Vendor.ArchiveOrg;
using Relisten.Vendor.ArchiveOrg.Metadata;
using InvalidDataException = System.IO.InvalidDataException;

namespace Relisten.Import;

// Full discovery is a verified rows=* snapshot. Item metadata lives only for one
// activity attempt; absence alone never authorizes a deletion.
public partial class ArchiveOrgImporter : IProviderIngestion
{
    /// <inheritdoc />
    public async Task<DiscoverySnapshot> DiscoverAsync(ProviderContext context, string? cursor)
    {
        var docs = await new ArchiveOrgSearchClient(http).FetchAllAsync(
            context.Source.upstream_identifier,
            context.Request.Year
        );
        await PreloadData(context.Artist);
        var cutoff = DateTime.UtcNow.AddDays(
            -CurrentImportOptions.RescrapeRecentDays.GetValueOrDefault(7)
        );
        var selected = docs.Where(doc =>
        {
            var source = existingSources.GetValue(doc.identifier);
            var review = existingSourceReviewInformation.GetValue(doc.identifier);
            var date = ArchiveOrgImporterUtils.FixDisplayDate(doc.raw_display_date, doc.identifier);
            return source == null
                || (review != null && doc.max_review_date > review.review_max_updated_at)
                || (date != null && source.display_date != date)
                || (CurrentImportOptions.RescrapeRecentDays.HasValue && doc.addeddate > cutoff);
        });

        return new DiscoverySnapshot(
            selected
                .Select(doc => new DiscoveredItem(doc.identifier, JsonConvert.SerializeObject(doc)))
                .ToArray(),
            ObservedIdentifiers: docs.Select(doc => doc.identifier).ToArray()
        );
    }

    /// <inheritdoc />
    public async Task<ItemImportResult> ImportItemAsync(
        ProviderContext context,
        DiscoveredItem item
    )
    {
        var doc = string.IsNullOrEmpty(item.Metadata)
            ? await new ArchiveOrgSearchClient(http).FetchItemAsync(
                context.Source.upstream_identifier,
                item.Identifier
            )
            : JsonConvert.DeserializeObject<SearchDoc>(item.Metadata)!;
        if (doc == null)
        {
            throw new InvalidDataException(
                "The requested Archive item was not found in its collection."
            );
        }

        using var response = await http.GetAsync(
            DetailsUrlForIdentifier(item.Identifier),
            context.CancellationToken
        );
        response.EnsureSuccessStatusCode();
        var details =
            JsonConvert.DeserializeObject<RootObject>(
                await response.Content.ReadAsStringAsync(context.CancellationToken),
                new TolerantStringConverter()
            ) ?? throw new InvalidDataException("Archive returned empty metadata.");

        if (details.is_dark == true)
        {
            return new ItemImportResult(new ImportStats());
        }

        if (
            !string.IsNullOrEmpty(details.error)
            || details.errcode.HasValue
            || details.metadata?.identifier != item.Identifier
        )
        {
            throw new InvalidDataException("Archive returned erroneous or mismatched metadata.");
        }

        var date =
            details.metadata.date == null
                ? null
                : ArchiveOrgImporterUtils.FixDisplayDate(details.metadata);
        if (date == null)
        {
            return new ItemImportResult(new ImportStats());
        }

        return await context.CommitAsync(async () =>
        {
            var source = await _sourceService.ForUpstreamIdentifierFromPrimary(
                context.Artist,
                item.Identifier
            );
            if (HasNoVbrMp3Files(details))
            {
                return new ItemImportResult(new ImportStats(), Ineligible: source != null);
            }

            var stats = await ImportSingleIdentifier(
                context.Artist,
                source,
                doc,
                details,
                context.Source,
                date,
                null
            );
            return new ItemImportResult(stats);
        });
    }

    /// <inheritdoc />
    public async Task<ProviderResult> ReconcileAsync(
        ProviderContext context,
        string[] ineligibleItems,
        bool hasFailures
    )
    {
        if (context.Request.Year != null || context.Request.Item != null)
        {
            return new ProviderResult(new ImportStats());
        }

        var snapshot =
            await context.Store.ReadSnapshotAsync<DiscoverySnapshot>(
                context.Run,
                IngestionActivities.DiscoveryName(Provider, null)
            ) ?? throw new InvalidOperationException("Archive discovery has not completed.");
        var observed =
            snapshot.ObservedIdentifiers
            ?? throw new InvalidOperationException(
                "Archive discovery did not record its observed identifiers."
            );
        var existing = await _sourceService.AllForArtistFromPrimary(context.Artist);
        var candidates = existing
            .Select(source => source.upstream_identifier)
            .Except(observed.Except(ineligibleItems))
            .ToArray();

        if (ExceedsDeletionLimit(candidates.Length) || ExceedsDeletionLimit(ineligibleItems.Length))
        {
            return new ProviderResult(
                new ImportStats(),
                [
                    new DeletionDecision(
                        "*",
                        "Guarded",
                        "More than five deletion candidates; no sources were deleted.",
                        false
                    ),
                ]
            );
        }

        var decisions = new List<DeletionDecision>();
        foreach (var identifier in candidates)
        {
            if (ineligibleItems.Contains(identifier))
            {
                decisions.Add(
                    new DeletionDecision(
                        identifier,
                        "Ineligible",
                        "Metadata contains no VBR MP3 files",
                        true
                    )
                );
                continue;
            }

            var metadata = await new ArchiveOrgMetadataClient(http).CheckAsync(identifier);
            decisions.Add(
                new DeletionDecision(
                    identifier,
                    metadata.Status.ToString(),
                    metadata.Reason,
                    metadata.Status == ArchiveOrgItemStatus.Deleted
                )
            );
        }

        return await context.CommitAsync(async () =>
        {
            var deleted = decisions
                .Where(decision => decision.Delete)
                .Select(decision => decision.Identifier)
                .ToArray();
            var removed =
                deleted.Length == 0
                    ? 0
                    : await _sourceService.RemoveSourcesWithUpstreamIdentifiers(
                        context.Artist.id,
                        deleted
                    );
            return new ProviderResult(new ImportStats { Removed = removed }, decisions.ToArray());
        });
    }
}
