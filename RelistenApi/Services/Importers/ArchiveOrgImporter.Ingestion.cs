using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Relisten.Ingestion;
using Relisten.Ingestion.Providers.Archive;
using Relisten.Vendor;
using Relisten.Vendor.ArchiveOrg;
using Temporalio.Exceptions;

namespace Relisten.Import;

// Selection and deletion evidence remain fixed across retries; item HTTP responses may be fetched again.
public partial class ArchiveOrgImporter
{
    public async Task<ArchiveSelection> DiscoverRecordingsAsync(ProviderContext context, int? year)
    {
        var docs = await new ArchiveOrgSearchClient(http).FetchAllAsync(
            context.Source.upstream_identifier,
            year
        );
        await PreloadData(context.Artist);
        var selected = new List<DiscoveredItem<ArchiveItemId, ArchiveSelectedItem>>();
        foreach (var doc in docs)
        {
            var reason = ImportReason(
                doc,
                existingSources.GetValue(doc.identifier),
                existingSourceReviewInformation.GetValue(doc.identifier),
                null
            );
            if (reason != null)
                selected.Add(new(new(doc.identifier), new(doc, reason)));
        }
        return new(
            selected.ToArray(),
            docs.Select(doc => new ArchiveItemId(doc.identifier)).ToArray()
        );
    }

    public async Task<ArchiveItemResult> ImportArchiveItemAsync(
        ProviderContext context,
        ArchiveItemInput input
    )
    {
        DiscoveredItem<ArchiveItemId, ArchiveSelectedItem> item;
        if (input is DiscoveredArchiveItem discovered)
            item = discovered.Item;
        else if (input is RequestedArchiveItem requested)
        {
            var doc =
                await new ArchiveOrgSearchClient(http).FetchItemAsync(
                    context.Source.upstream_identifier,
                    requested.Item.Value
                )
                ?? throw new ApplicationFailureException(
                    "The requested Archive item was not found in its collection.",
                    "ArchiveItemNotFound",
                    nonRetryable: true
                );
            item = new(requested.Item, new(doc, "targeted_show"));
        }
        else
            throw new ArgumentException("Unknown Archive item input.", nameof(input));
        ArchiveSkipReason? skip = null;
        string? diagnostic = null;
        var metadata = await FetchItemMetadata(
            context.Artist,
            item.Input.Document,
            item.Input.Reason,
            null,
            context.CancellationToken,
            (reason, detail) =>
            {
                skip = reason;
                diagnostic = detail;
            }
        );
        if (metadata == null)
            return new(item.Id, ItemDisposition.Skipped, null, skip, diagnostic);
        return await context.CommitAsync(async () =>
        {
            var source = await _sourceService.ForUpstreamIdentifierFromPrimary(
                context.Artist,
                item.Id.Value
            );
            if (HasNoVbrMp3Files(metadata.Details))
                return new ArchiveItemResult(
                    item.Id,
                    ItemDisposition.Skipped,
                    source == null ? null : new CatalogSourceId(source.id),
                    ArchiveSkipReason.NoVbrMp3,
                    "Metadata contains no VBR MP3 files"
                );
            await ImportSingleIdentifier(
                context.Artist,
                source,
                item.Input.Document,
                metadata.Details,
                context.Source,
                metadata.DisplayDate,
                null
            );
            var current =
                await _sourceService.ForUpstreamIdentifierFromPrimary(context.Artist, item.Id.Value)
                ?? throw new InvalidOperationException("Archive import did not produce a source.");
            return new ArchiveItemResult(
                item.Id,
                ItemDisposition.Applied,
                new CatalogSourceId(current.id)
            );
        });
    }

    public async Task<ArchiveDeletionPlan> VerifyDeletionsAsync(
        ProviderContext context,
        ArchiveSelection selection,
        ArchiveItemId[] noMp3
    )
    {
        var existing = await _sourceService.AllForArtistFromPrimary(context.Artist);
        var observed = selection.ObservedIdentifiers.Select(id => id.Value);
        var ineligible = noMp3.Select(id => id.Value).ToHashSet();
        var candidates = existing
            .Select(source => source.upstream_identifier)
            .Except(observed.Except(ineligible))
            .ToArray();
        if (ExceedsDeletionLimit(candidates.Length) || ExceedsDeletionLimit(noMp3.Length))
        {
            await ReportDeletionGuard(context.Artist, candidates, null);
            return new(ArchiveDeletionDisposition.Guarded, []);
        }
        var verification = await VerifyDeletionCandidatesAsync(
            candidates,
            ineligible,
            http,
            message => ImportProgress.WriteLine(log, null, message)
        );
        return new(
            ArchiveDeletionDisposition.Allowed,
            verification
                .Decisions.Select(d => new ArchiveDeletionDecision(
                    new(d.Identifier),
                    d.Status == "Ineligible"
                        ? ArchiveDeletionEvidence.NoVbrMp3
                        : Enum.Parse<ArchiveDeletionEvidence>(d.Status),
                    d.Delete ? ArchiveDeletionAction.Delete : ArchiveDeletionAction.Keep,
                    d.Reason
                ))
                .ToArray()
        );
    }

    public Task<ArchiveDeletionResult> ApplyDeletionsAsync(
        ProviderContext context,
        ArchiveDeletionPlan plan
    ) =>
        context.CommitAsync(async () =>
        {
            if (plan.Disposition != ArchiveDeletionDisposition.Allowed)
                return new ArchiveDeletionResult(plan.Decisions, []);
            var approved = plan
                .Decisions.Where(d => d.Action == ArchiveDeletionAction.Delete)
                .Select(d => d.Item.Value)
                .ToList();
            if (approved.Count > 0)
                await _sourceService.RemoveSourcesWithUpstreamIdentifiers(
                    context.Artist.id,
                    approved
                );
            var current = (await _sourceService.AllForArtistFromPrimary(context.Artist))
                .Select(s => s.upstream_identifier)
                .ToHashSet();
            return new ArchiveDeletionResult(
                plan.Decisions,
                approved
                    .Where(id => !current.Contains(id))
                    .Select(id => new ArchiveItemId(id))
                    .ToArray()
            );
        });
}

/// <summary>Evidence produced by the shared Archive deletion policy used by both ingestion engines.</summary>
public record DeletionDecision(string Identifier, string Status, string Reason, bool Delete);
