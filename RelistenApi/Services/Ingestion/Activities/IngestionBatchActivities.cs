using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Relisten.Data;
using Relisten.Services.Indexing;
using Relisten.Vendor.ArchiveOrg;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace Relisten.Ingestion;

/// <summary>Records batch membership and separates Archive collection fetches from atomic page writes.</summary>
public sealed class IngestionBatchActivities(
    ArtistService artists,
    ArchiveOrgArtistIndexer indexer,
    CatalogImportTransaction transactions,
    IngestionHttpClientFactory httpClients
)
{
    [Activity("Ingestion.SelectArtists")]
    public async Task<ArtistId[]> SelectArtists(
        CatalogBatchCommand request,
        IngestionExecution execution
    )
    {
        using var heartbeat = new ActivityHeartbeat();
        RequireActive(execution);

        var selected = await artists.UuidsForImport(request is RefreshRecentlyActiveArtists);
        RequireActive(execution);

        return selected.Select(value => new ArtistId(Guid.Parse(value))).ToArray();
    }

    [Activity("Archive.FetchCollectionPage")]
    public async Task<ArchiveCollectionPage> FetchCollectionPage(ArchiveDiscoveryProgress progress)
    {
        using var heartbeat = new ActivityHeartbeat();
        RequireActive(progress.Execution);
        var token = ActivityExecutionContext.Current.CancellationToken;
        using var http = httpClients.Create(token);
        var url =
            "https://archive.org/services/search/v1/scrape?q=collection:etree%20AND%20mediatype:collection&fields=identifier,title,item_count,month&count=10000";
        if (progress.Cursor != null)
        {
            url += "&cursor=" + Uri.EscapeDataString(progress.Cursor.Token);
        }

        using var response = await http.GetAsync(url, token);
        response.EnsureSuccessStatusCode();

        var page = ArchiveOrgCollectionIndexParser.Parse(
            await response.Content.ReadAsStringAsync(token)
        );
        if (page?.items == null)
        {
            throw new InvalidDataException("Archive returned an invalid collection page.");
        }

        // A missing or repeated cursor cannot establish completeness while the
        // provider still claims more results. Fail instead of accepting a partial index.
        var complete =
            page.total > 0
                ? progress.Processed + page.items.Count >= page.total
                : page.items.Count == 0 || string.IsNullOrWhiteSpace(page.cursor);
        if (
            !complete
            && (
                page.items.Count == 0
                || string.IsNullOrWhiteSpace(page.cursor)
                || page.cursor == progress.Cursor?.Token
            )
        )
        {
            throw new InvalidDataException(
                "Archive collection listing ended before its declared total or its cursor did not advance."
            );
        }

        RequireActive(progress.Execution);
        return new ArchiveCollectionPage(
            page.items.ToArray(),
            complete ? null : new ArchiveCollectionCursor(page.cursor)
        );
    }

    [Activity("Archive.ApplyCollectionPage")]
    public async Task<int> ApplyCollectionPage(
        IngestionExecution execution,
        ArchiveCollectionPage page
    )
    {
        using var heartbeat = new ActivityHeartbeat();
        RequireActive(execution);
        var token = ActivityExecutionContext.Current.CancellationToken;

        try
        {
            return await transactions.RunCollectionPageAsync(
                execution.Deadline,
                token,
                async () =>
                {
                    await indexer.ApplyCollectionsAsync(page.Items, cancellationToken: token);
                    return page.Items.Length;
                }
            );
        }
        catch (CatalogImportExpiredException)
        {
            throw new ApplicationFailureException(
                "This import has expired. Start a fresh import.",
                errorType: "ImportExpired",
                nonRetryable: true
            );
        }
    }

    private static void RequireActive(IngestionExecution execution)
    {
        ActivityExecutionContext.Current.CancellationToken.ThrowIfCancellationRequested();
        if (DateTimeOffset.UtcNow >= execution.Deadline)
        {
            throw new ApplicationFailureException(
                "This import has expired. Start a fresh import.",
                errorType: "ImportExpired",
                nonRetryable: true
            );
        }
    }
}
