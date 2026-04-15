using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Relisten.Vendor.ArchiveOrg;

public interface IArchiveOrgCollectionIndexClient
{
    Task<IReadOnlyList<ArchiveOrgCollectionIndexItem>> FetchCollectionsAsync(int count, CancellationToken cancellationToken);
    Task<IReadOnlyList<ArchiveOrgCollectionIndexItem>> FetchCollectionItemsAsync(string collectionIdentifier,
        int count, CancellationToken cancellationToken);
}

public class ArchiveOrgCollectionIndexClient : IArchiveOrgCollectionIndexClient, IDisposable
{
    private readonly HttpClient httpClient;

    public ArchiveOrgCollectionIndexClient()
        : this(new HttpClient())
    {
    }

    public ArchiveOrgCollectionIndexClient(HttpClient httpClient)
    {
        this.httpClient = httpClient;

        if (!this.httpClient.DefaultRequestHeaders.Contains("User-Agent"))
        {
            this.httpClient.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (iPhone; CPU iPhone OS 11_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/11.0 Mobile/15E148 Safari/604.1");
        }

        if (!this.httpClient.DefaultRequestHeaders.Contains("Accept"))
        {
            this.httpClient.DefaultRequestHeaders.Add("Accept", "*/*");
        }
    }

    public async Task<IReadOnlyList<ArchiveOrgCollectionIndexItem>> FetchCollectionsAsync(int count,
        CancellationToken cancellationToken)
    {
        return await FetchScrapeItemsAsync("collection:etree AND mediatype:collection",
            "identifier,title,item_count,month", count, cancellationToken);
    }

    public async Task<IReadOnlyList<ArchiveOrgCollectionIndexItem>> FetchCollectionItemsAsync(
        string collectionIdentifier, int count, CancellationToken cancellationToken)
    {
        return await FetchScrapeItemsAsync($"collection:{collectionIdentifier}",
            "identifier,title,creator,date,year", count, cancellationToken);
    }

    private async Task<IReadOnlyList<ArchiveOrgCollectionIndexItem>> FetchScrapeItemsAsync(string query,
        string fields, int count, CancellationToken cancellationToken)
    {
        var safeCount = Math.Max(100, count);
        var items = new List<ArchiveOrgCollectionIndexItem>();
        string? cursor = null;
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);

        while (true)
        {
            var url =
                $"https://archive.org/services/search/v1/scrape?q={Uri.EscapeDataString(query)}&fields={Uri.EscapeDataString(fields)}&count={safeCount}";
            if (!string.IsNullOrWhiteSpace(cursor))
            {
                url += $"&cursor={Uri.EscapeDataString(cursor)}";
            }

            using var response = await httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var parsed = ArchiveOrgCollectionIndexParser.Parse(json);
            var pageItems = parsed.items ?? new List<ArchiveOrgCollectionIndexItem>();

            if (pageItems.Count == 0)
            {
                break;
            }

            items.AddRange(pageItems);

            if (string.IsNullOrWhiteSpace(parsed.cursor))
            {
                break;
            }

            if (!seenCursors.Add(parsed.cursor))
            {
                throw new InvalidOperationException(
                    $"archive.org scraping cursor did not advance for query after fetching {items.Count} items.");
            }

            cursor = parsed.cursor;
        }

        return items;
    }

    public void Dispose()
    {
        httpClient.Dispose();
    }
}

public static class ArchiveOrgCollectionIndexParser
{
    public static ArchiveOrgCollectionIndexResponse Parse(string json)
    {
        return JsonConvert.DeserializeObject<ArchiveOrgCollectionIndexResponse>(json)!;
    }
}

public class ArchiveOrgCollectionIndexResponse
{
    public List<ArchiveOrgCollectionIndexItem> items { get; set; } = null!;
    public int count { get; set; }
    public string cursor { get; set; } = null!;
    public int total { get; set; }
}

public class ArchiveOrgCollectionIndexItem
{
    public string identifier { get; set; } = null!;
    public string title { get; set; } = null!;
    public string creator { get; set; } = null!;
    public string date { get; set; } = null!;
    public int? year { get; set; }
    public int item_count { get; set; }
    public int month { get; set; }
}
