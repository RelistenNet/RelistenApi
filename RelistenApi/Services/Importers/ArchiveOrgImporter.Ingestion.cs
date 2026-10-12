using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Relisten.Api.Models;
using Relisten.Ingestion;
using Relisten.Vendor;
using Relisten.Vendor.ArchiveOrg;
using Relisten.Vendor.ArchiveOrg.Metadata;
using InvalidDataException = System.IO.InvalidDataException;

namespace Relisten.Import;

// Full discovery is a verified rows=* snapshot. Item metadata lives only for one
// activity attempt; absence alone never authorizes a deletion.
public partial class ArchiveOrgImporter
{
    /// <inheritdoc />
    public override async Task<ArchiveDiscovery> DiscoverAsync(
        ProviderContext context,
        string? cursor
    )
    {
        var docs = await new ArchiveOrgSearchClient(http).FetchAllAsync(
            context.Source.upstream_identifier,
            context.Request.Year
        );
        await PreloadData(context.Artist);
        var selected = new List<DiscoveredItem<ArchiveImportItem>>();
        foreach (var doc in docs)
        {
            var reason = ImportReason(
                doc,
                existingSources.GetValue(doc.identifier),
                existingSourceReviewInformation.GetValue(doc.identifier),
                null
            );
            if (reason != null)
            {
                selected.Add(
                    new DiscoveredItem<ArchiveImportItem>(
                        doc.identifier,
                        new ArchiveImportItem(doc, reason)
                    )
                );
            }
        }

        return new ArchiveDiscovery(
            selected.ToArray(),
            docs.Select(doc => doc.identifier).ToArray()
        );
    }

    /// <inheritdoc />
    public override async Task<ItemImportResult> ReimportItemAsync(
        ProviderContext context,
        string identifier
    )
    {
        var doc =
            await new ArchiveOrgSearchClient(http).FetchItemAsync(
                context.Source.upstream_identifier,
                identifier
            )
            ?? throw new InvalidDataException(
                "The requested Archive item was not found in its collection."
            );
        return await ImportItemAsync(
            context,
            new DiscoveredItem<ArchiveImportItem>(
                identifier,
                new ArchiveImportItem(doc, "targeted_show")
            )
        );
    }

    /// <inheritdoc />
    public override async Task<ItemImportResult> ImportItemAsync(
        ProviderContext context,
        DiscoveredItem<ArchiveImportItem> item
    )
    {
        var doc = item.Metadata.Document;
        var metadata = await FetchItemMetadata(
            context.Artist,
            doc,
            item.Metadata.Reason,
            null,
            context.CancellationToken
        );
        if (metadata == null)
        {
            return new ItemImportResult(new ImportStats());
        }

        var details = metadata.Details;
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
                metadata.DisplayDate,
                null
            );
            return new ItemImportResult(stats);
        });
    }

    /// <inheritdoc />
    public override async Task<ProviderResult> ReconcileAsync(
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
            await context.Store.ReadSnapshotAsync<ArchiveDiscovery>(
                context.Run,
                DiscoveryName(null)
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
            await ReportDeletionGuard(context.Artist, candidates, null);
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

        var verification = await VerifyDeletionCandidatesAsync(
            candidates,
            ineligibleItems.ToHashSet(),
            http,
            message => ImportProgress.WriteLine(log, null, message)
        );

        return await context.CommitAsync(async () =>
        {
            var deleted = verification.Identifiers;
            var removed =
                deleted.Count == 0
                    ? 0
                    : await _sourceService.RemoveSourcesWithUpstreamIdentifiers(
                        context.Artist.id,
                        deleted
                    );
            return new ProviderResult(
                new ImportStats { Removed = removed },
                verification.Decisions
            );
        });
    }
}

/// <summary>Archive listing evidence used to verify absence independently of selected imports.</summary>
public record ArchiveDiscovery(
    DiscoveredItem<ArchiveImportItem>[] Items,
    string[] ObservedIdentifiers
) : DiscoverySnapshot<ArchiveImportItem>(Items);

/// <summary>Archive search metadata and the reason it was selected for import.</summary>
public record ArchiveImportItem(SearchDoc Document, string Reason);
