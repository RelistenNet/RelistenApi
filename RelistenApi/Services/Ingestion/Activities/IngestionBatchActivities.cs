using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Relisten.Data;
using Relisten.Services.Indexing;
using Relisten.Vendor.ArchiveOrg;
using Temporalio.Activities;

namespace Relisten.Ingestion;

/// <summary>Snapshots batch membership and indexes Archive artist collections one page at a time.</summary>
public sealed class IngestionBatchActivities(
    ArtistService artistService,
    IngestionStore store,
    ArchiveOrgArtistIndexer indexer,
    IngestionHttpClientFactory httpClients
)
{
    /// <summary>Returns a bounded slice of the artist selection made when this batch began.</summary>
    [Activity("Ingestion.ListArtists")]
    public async Task<ArtistBatchPage> ListArtists(ImportBatchRequest request)
    {
        using var heartbeat = new ActivityHeartbeat();
        var artists = await store.SnapshotAsync(
            request.Run!,
            "artists",
            () => artistService.UuidsForImport(request.Mode == "thin")
        );
        var page = artists.Skip(request.Offset).Take(250).ToArray();
        var nextOffset = request.Offset + page.Length;
        return new ArtistBatchPage(page, nextOffset < artists.Length ? nextOffset : null);
    }

    /// <summary>Fetches, validates, and applies a collection page; retries return the committed result.</summary>
    [Activity("Ingestion.DiscoverArtists")]
    public async Task<DiscoveryPage> DiscoverArtists(IngestionRun run, string? cursor, int fetched)
    {
        using var heartbeat = new ActivityHeartbeat();
        var operation = "collections/" + (cursor ?? "first");
        var previous = await store.ReadReceiptAsync<DiscoveryPage>(run, operation);
        if (previous != null)
        {
            return previous;
        }

        var token = ActivityExecutionContext.Current.CancellationToken;
        var page = await FetchCollections(cursor, token);
        var nextCursor = NextCursor(page, cursor, fetched);
        token.ThrowIfCancellationRequested();

        return await store.CommitAsync(
            run,
            operation,
            async () =>
            {
                await indexer.ApplyCollectionsAsync(page.items, cancellationToken: token);
                token.ThrowIfCancellationRequested();
                return new DiscoveryPage(nextCursor, page.items.Count);
            }
        );
    }

    private async Task<ArchiveOrgCollectionIndexResponse> FetchCollections(
        string? cursor,
        CancellationToken token
    )
    {
        using var http = httpClients.Create(token);
        var url =
            "https://archive.org/services/search/v1/scrape?q=collection:etree%20AND%20mediatype:collection&fields=identifier,title,item_count,month&count=10000";
        if (cursor != null)
        {
            url += "&cursor=" + Uri.EscapeDataString(cursor);
        }

        using var response = await http.GetAsync(url, token);
        response.EnsureSuccessStatusCode();
        return ArchiveOrgCollectionIndexParser.Parse(
            await response.Content.ReadAsStringAsync(token)
        );
    }

    private static string? NextCursor(
        ArchiveOrgCollectionIndexResponse page,
        string? current,
        int fetched
    )
    {
        if (page?.items == null)
        {
            throw new InvalidDataException("Archive returned an invalid collection page.");
        }

        var complete =
            page.items.Count == 0 || (page.total > 0 && fetched + page.items.Count >= page.total);
        if (complete || string.IsNullOrWhiteSpace(page.cursor))
        {
            return null;
        }

        if (page.cursor == current)
        {
            throw new InvalidDataException("Archive collection cursor did not advance.");
        }

        return page.cursor;
    }
}
