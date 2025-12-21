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
}

public class ArchiveOrgCollectionIndexClient : IArchiveOrgCollectionIndexClient, IDisposable
{
    private readonly HttpClient httpClient;

    public ArchiveOrgCollectionIndexClient()
    {
        httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (iPhone; CPU iPhone OS 11_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/11.0 Mobile/15E148 Safari/604.1");
        httpClient.DefaultRequestHeaders.Add("Accept", "*/*");
    }

    public async Task<IReadOnlyList<ArchiveOrgCollectionIndexItem>> FetchCollectionsAsync(int count,
        CancellationToken cancellationToken)
    {
        var safeCount = Math.Max(100, count);
        var items = new List<ArchiveOrgCollectionIndexItem>();
        string cursor = null;
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);

        while (true)
        {
            var url =
                $"https://archive.org/services/search/v1/scrape?q=collection:etree%20AND%20mediatype:collection&fields=identifier,title,item_count,month&count={safeCount}";
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

            // Cursor behavior appears inconsistent for this endpoint. When we tested live requests, the cursor
            // repeated and the next page contained the same items (or the cursor was rejected when sorts were used).
            // Example tests:
            // - collection:etree AND mediatype:collection with fields=identifier,title,item_count,month, count=100
            // - collection:nasa with fields=identifier,title, count=100
            // In both cases, the cursor did not advance and the page results overlapped 100%.
            if (parsed.total > 0 && items.Count >= parsed.total)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(parsed.cursor))
            {
                break;
            }

            if (!seenCursors.Add(parsed.cursor))
            {
                if (parsed.total > items.Count)
                {
                    throw new InvalidOperationException(
                        $"archive.org scraping cursor did not advance for query; fetched {items.Count} of {parsed.total} items.");
                }

                break;
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
        return JsonConvert.DeserializeObject<ArchiveOrgCollectionIndexResponse>(json);
    }
}

public class ArchiveOrgCollectionIndexResponse
{
    public List<ArchiveOrgCollectionIndexItem> items { get; set; }
    public int count { get; set; }
    public string cursor { get; set; }
    public int total { get; set; }
}

public class ArchiveOrgCollectionIndexItem
{
    public string identifier { get; set; }
    public string title { get; set; }
    public int item_count { get; set; }
    public int month { get; set; }
}
