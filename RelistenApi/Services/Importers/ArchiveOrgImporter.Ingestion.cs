using System;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Newtonsoft.Json;
using Relisten.Api.Models;
using Relisten.Ingestion;
using Relisten.Vendor;
using Relisten.Vendor.ArchiveOrg;
using Relisten.Vendor.ArchiveOrg.Metadata;
using InvalidDataException = System.IO.InvalidDataException;

namespace Relisten.Import;

// Temporal uses the existing metadata parser and catalog upserts, but keeps
// discovery, each fetch, and reconciliation independently resumable.
public partial class ArchiveOrgImporter : IProviderIngestion
{
    public async Task<ProviderPage> DiscoverAsync(ProviderContext context, string? cursor)
    {
        var docs = await new ArchiveOrgSearchClient(http).FetchAllAsync(
            context.Source.upstream_identifier, context.Request.Year);
        await PreloadData(context.Artist);
        var cutoff = DateTime.UtcNow.AddDays(-CurrentImportOptions.RescrapeRecentDays.GetValueOrDefault(7));
        var selected = docs.Where(doc =>
        {
            if (context.Request.Item != null) return doc.identifier == context.Request.Item;
            var source = existingSources.GetValue(doc.identifier);
            var review = existingSourceReviewInformation.GetValue(doc.identifier);
            var date = ArchiveOrgImporterUtils.FixDisplayDate(doc.raw_display_date, doc.identifier);
            return source == null || (review != null && doc.max_review_date > review.review_max_updated_at)
                || (date != null && source.display_date != date)
                || (CurrentImportOptions.RescrapeRecentDays.HasValue && doc.addeddate > cutoff);
        });
        return new ProviderPage(selected.Select(d => new ProviderItem(d.identifier, JsonConvert.SerializeObject(d))).ToArray(),
            ObservedKeys: docs.Select(d => d.identifier).ToArray());
    }

    public async Task<ProviderFetch> FetchAsync(ProviderContext context, ProviderItem item, int part)
    {
        using var response = await http.GetAsync(DetailsUrlForIdentifier(item.Key), context.CancellationToken);
        response.EnsureSuccessStatusCode();
        return new ProviderFetch(await response.Content.ReadAsStringAsync(context.CancellationToken));
    }

    public async Task<ImportStats> ApplyAsync(ProviderContext context, ProviderItem item, ProviderFetch[] fetched)
    {
        var doc = JsonConvert.DeserializeObject<SearchDoc>(item.Body)!;
        var details = JsonConvert.DeserializeObject<RootObject>(fetched[0].Body, new TolerantStringConverter())
            ?? throw new InvalidDataException("Archive returned empty metadata.");
        if (details.is_dark == true) return new ImportStats();
        if (!string.IsNullOrEmpty(details.error) || details.errcode.HasValue || details.metadata?.identifier != item.Key)
            throw new InvalidDataException("Archive returned erroneous or mismatched metadata.");
        var date = details.metadata.date == null ? null : ArchiveOrgImporterUtils.FixDisplayDate(details.metadata);
        if (date == null) return new ImportStats(); // Preserve existing unsupported-date skip behavior.
        if (HasNoVbrMp3Files(details))
        {
            await context.Store.SaveAsync(context.Request.RequestId, "archive.org/no-mp3/" + item.Key, new { confirmed = true });
            return new ImportStats();
        }
        // Fetch one source, rather than preloading the artist's entire catalog for every item.
        var source = await db.WithWriteConnection(c => c.QuerySingleOrDefaultAsync<Source>(
            "SELECT * FROM sources WHERE artist_id=@artistId AND upstream_identifier=@identifier",
            new { artistId = context.Artist.id, identifier = item.Key }));
        return await ImportSingleIdentifier(context.Artist, source, doc, details, context.Source, date, null);
    }

    internal async Task<string[]> IngestionDeletionCandidates(ProviderContext context)
    {
        var requestId = context.Request.RequestId;
        var saved = await context.Store.ReadAsync<string[]>(requestId, "archive.org/candidates");
        if (saved != null) return saved;
        var observed = await context.Store.ReadAsync<string[]>(requestId, "archive.org/observed")
            ?? throw new InvalidOperationException("Archive discovery has not completed.");
        var noMp3 = await context.Store.KeysAsync(requestId, "archive.org/no-mp3/");
        var existing = await _sourceService.AllForArtistFromPrimary(context.Artist);
        var candidates = existing.Select(s => s.upstream_identifier).Except(observed.Except(noMp3)).ToArray();
        if (ExceedsDeletionLimit(candidates.Length))
        {
            await discordWebhookNotifier.SendAsync($"Archive deletion guard: {context.Artist.name} has {candidates.Length} candidates; none were deleted.");
            candidates = Array.Empty<string>();
        }
        await context.Store.SaveAsync(requestId, "archive.org/candidates", candidates);
        return candidates;
    }

    internal async Task VerifyIngestionCandidate(ProviderContext context, string identifier)
    {
        var key = "archive.org/verification/" + identifier;
        if (await context.Store.ReadAsync<DeletionEvidence>(context.Request.RequestId, key) != null) return;
        var noMp3 = await context.Store.KeysAsync(context.Request.RequestId, "archive.org/no-mp3/");
        var confirmed = noMp3.Contains(identifier);
        if (!confirmed)
        {
            var evidence = await new ArchiveOrgMetadataClient(http).CheckAsync(identifier);
            confirmed = evidence.Status == ArchiveOrgItemStatus.Deleted;
        }
        await context.Store.SaveAsync(context.Request.RequestId, key, new DeletionEvidence(confirmed));
    }

    internal async Task<ImportStats> FinishIngestion(ProviderContext context)
    {
        if (context.Request.Year != null || context.Request.Item != null) return new ImportStats();
        var candidates = await context.Store.ReadAsync<string[]>(context.Request.RequestId, "archive.org/candidates")
            ?? Array.Empty<string>();
        var deleted = new System.Collections.Generic.List<string>();
        foreach (var identifier in candidates)
        {
            var evidence = await context.Store.ReadAsync<DeletionEvidence>(context.Request.RequestId,
                "archive.org/verification/" + identifier);
            if (evidence?.Confirmed == true) deleted.Add(identifier);
        }
        if (ExceedsDeletionLimit(deleted.Count)) return new ImportStats();
        return new ImportStats { Removed = deleted.Count == 0 ? 0 :
            await _sourceService.RemoveSourcesWithUpstreamIdentifiers(context.Artist.id, deleted) };
    }

    private record DeletionEvidence(bool Confirmed);
}
