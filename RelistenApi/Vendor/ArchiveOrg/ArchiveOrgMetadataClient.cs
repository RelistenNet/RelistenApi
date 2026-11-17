using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Relisten.Vendor.ArchiveOrg;

public enum ArchiveOrgItemStatus
{
    Unknown,
    Present,
    Deleted,
    Dark,
}

public sealed class ArchiveOrgMetadataClient(HttpClient httpClient)
{
    public async Task<(ArchiveOrgItemStatus Status, string Reason)> CheckAsync(string identifier)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var url =
                $"https://archive.org/metadata/{Uri.EscapeDataString(identifier)}?extended_err=1";
            using var response = await httpClient.GetAsync(url, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return (ArchiveOrgItemStatus.Unknown, $"HTTP {(int)response.StatusCode}");
            }

            var json = await response.Content.ReadAsStringAsync(timeout.Token);
            if (JToken.Parse(json) is not JObject root)
            {
                return (ArchiveOrgItemStatus.Unknown, "missing metadata response object");
            }

            var metadata = root["metadata"];
            if (
                metadata is JObject item
                && item["identifier"]?.Type == JTokenType.String
                && (string?)item["identifier"] == identifier
            )
            {
                // Positive existence evidence wins even if an error is also present.
                return (ArchiveOrgItemStatus.Present, "matching item metadata");
            }

            if (
                metadata != null
                && metadata.Type != JTokenType.Null
                && (metadata is not JObject || metadata.HasValues)
            )
            {
                return (ArchiveOrgItemStatus.Unknown, "invalid or mismatched item metadata");
            }

            // Dark items are not publicly playable. Remove their catalog metadata
            // through the normal cleanup path, which retains track rows and play events.
            if (root["is_dark"]?.Type == JTokenType.Boolean && (bool)root["is_dark"]!)
            {
                return (
                    ArchiveOrgItemStatus.Dark,
                    "Archive item is dark (not publicly accessible)"
                );
            }

            // Only this documented code establishes upstream deletion; empty responses and 404s do not.
            // https://archive.org/developers/md-read.html#extended-errors
            if (root["errcode"]?.Type == JTokenType.Integer && root["errcode"]!.ToString() == "104")
            {
                return (ArchiveOrgItemStatus.Deleted, "Archive errcode 104 (item deleted)");
            }

            return (ArchiveOrgItemStatus.Unknown, "no definitive item state");
        }
        catch (Exception error)
            when (error
                    is HttpRequestException
                        or OperationCanceledException
                        or JsonException
                        or IOException
            )
        {
            return (
                ArchiveOrgItemStatus.Unknown,
                $"metadata request failed ({error.GetType().Name})"
            );
        }
    }
}
