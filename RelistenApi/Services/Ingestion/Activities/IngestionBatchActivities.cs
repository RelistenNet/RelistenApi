using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Configuration;
using Relisten.Services.Indexing;
using Relisten.Vendor.ArchiveOrg;
using Temporalio.Activities;

namespace Relisten.Ingestion;

/// <summary>Snapshots batch selection and indexes one Archive collection page per activity.</summary>
public sealed class IngestionBatchActivities(
    DbService db,
    ImportRunRepository store,
    ArchiveOrgArtistIndexer indexer,
    IConfiguration configuration
)
{
    /// <summary>Returns a page from the first persisted full/thin artist selection.</summary>
    [Activity("Ingestion.ListArtists")]
    public async Task<ArtistBatchPage> ListArtists(ImportBatchRequest request)
    {
        var artists = await store.ReadAsync<string[]>(request.RequestId!, "artists");
        if (artists == null)
        {
            artists = (
                await db.WithWriteConnection(c =>
                    c.QueryAsync<string>(
                        @"
                SELECT a.uuid::text FROM artists a
                WHERE @thin = false OR EXISTS (
                    SELECT 1 FROM shows s WHERE s.artist_id=a.id AND s.date > CURRENT_DATE - INTERVAL '7 days')
                ORDER BY a.id",
                        new { thin = request.Mode == "thin" }
                    )
                )
            ).ToArray();
            await store.SaveAsync(request.RequestId!, "artists", artists);
            artists = (await store.ReadAsync<string[]>(request.RequestId!, "artists"))!;
        }
        var page = artists.Skip(request.Offset).Take(250).ToArray();
        return new ArtistBatchPage(
            page,
            request.Offset + page.Length < artists.Length ? request.Offset + page.Length : null
        );
    }

    /// <summary>Fetches and commits one Archive collection page with its completion receipt.</summary>
    [Activity("Ingestion.DiscoverArtists")]
    public async Task<DiscoveryPage> DiscoverArtists(string requestId, string? cursor, int fetched)
    {
        var key = "discovery/" + (cursor ?? "first");
        var previous = await store.ReadAsync<DiscoveryPage>(requestId, key);
        if (previous != null)
            return previous;
        var token = ActivityExecutionContext.Current.CancellationToken;
        var endpoint = configuration["Ingestion:UpstreamBaseUrl"];
        using var http = new HttpClient(
            new IngestionHttpHandler(token, endpoint == null ? null : new Uri(endpoint))
        );
        var url =
            "https://archive.org/services/search/v1/scrape?q=collection:etree%20AND%20mediatype:collection&fields=identifier,title,item_count,month&count=10000";
        if (cursor != null)
            url += "&cursor=" + Uri.EscapeDataString(cursor);
        using var response = await http.GetAsync(url, token);
        response.EnsureSuccessStatusCode();
        var page = ArchiveOrgCollectionIndexParser.Parse(
            await response.Content.ReadAsStringAsync(token)
        );
        if (page?.items == null)
            throw new System.IO.InvalidDataException(
                "Archive returned an invalid collection page."
            );
        var next =
            page.items.Count == 0
            || (page.total > 0 && fetched + page.items.Count >= page.total)
            || string.IsNullOrWhiteSpace(page.cursor)
                ? null
                : page.cursor;
        if (
            next != null
            && (
                next == cursor
                || await store.ReadAsync<DiscoveryPage>(requestId, "discovery/" + next) != null
            )
        )
            throw new System.IO.InvalidDataException("Archive collection cursor did not advance.");
        return await store.ApplyOnceAsync(
            requestId,
            key,
            async () =>
            {
                await indexer.ApplyCollectionsAsync(page.items, cancellationToken: token);
                return new DiscoveryPage(next, page.items.Count);
            }
        );
    }
}
