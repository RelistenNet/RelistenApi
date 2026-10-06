using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Relisten.Vendor.ArchiveOrg;

public sealed class ArchiveOrgSearchClient
{
    private readonly HttpClient httpClient;

    public ArchiveOrgSearchClient(HttpClient httpClient)
    {
        this.httpClient = httpClient;
    }

    public async Task<IList<SearchDoc>> FetchAllAsync(string? collection, int? year,
        Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        var query = $"collection:{collection}";
        if (year.HasValue)
        {
            query += $" AND year:{year.Value}";
        }

        // Fetch current large collections in one call, with at most one expansion if needed.
        var rows = 30000;
        var result = await FetchAsync(query, rows, log);
        if (result.docs.Count == rows && result.numFound > rows)
        {
            result = await FetchAsync(query, result.numFound, log);
        }

        if (result.docs.Count != result.numFound)
        {
            throw new InvalidDataException(
                $"Archive.org search was incomplete: fetched {result.docs.Count} of {result.numFound} results.");
        }

        return result.docs;
    }

    private async Task<SearchResponse> FetchAsync(string query, int rows, Action<string>? log)
    {
        // Omitting page and sort permits results beyond the 10,000 deep-paging limit.
        var url = $"https://archive.org/advancedsearch.php?q={Uri.EscapeDataString(query)}" +
                  "&fl[]=date&fl[]=identifier&fl[]=year&fl[]=addeddate&fl[]=reviewdate" +
                  "&fl[]=indexdate&fl[]=publicdate&fl[]=updatedate" +
                  $"&rows={rows}&output=json";
        log?.Invoke($"All shows URL: {url}");

        using var response = await httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var root = JsonConvert.DeserializeObject<SearchRootObject>(
            json.Replace("\"0000-01-01T00:00:00Z\"", "null"),
            new TolerantArchiveDateTimeConverter());

        if (!string.IsNullOrEmpty(root?.error))
        {
            throw new InvalidDataException($"Archive.org search failed: {root.error}");
        }

        if (root?.responseHeader?.status != 0 || root.response?.docs == null)
        {
            throw new InvalidDataException("Archive.org search returned an invalid response.");
        }

        var result = root.response;
        if (result.start != 0 || result.numFound < 0 || result.docs.Count > result.numFound ||
            result.docs.Count > rows ||
            result.docs.Any(doc => string.IsNullOrWhiteSpace(doc?.identifier)) ||
            result.docs.Select(doc => doc.identifier).Distinct(StringComparer.Ordinal).Count() != result.docs.Count)
        {
            throw new InvalidDataException("Archive.org search returned invalid counts or identifiers.");
        }

        log?.Invoke($"Fetched {result.docs.Count} of {result.numFound} archive.org results");
        return result;
    }
}
