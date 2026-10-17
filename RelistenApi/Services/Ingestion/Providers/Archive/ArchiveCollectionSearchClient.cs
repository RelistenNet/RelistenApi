using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Relisten.Ingestion.Providers.TapersSection;

namespace Relisten.Ingestion.Providers.Archive;

/// <summary>A received page must be persisted before its cursor becomes a durable checkpoint.</summary>
public sealed record ArchiveCollectionSearchPage(ArchiveItemId[] Items, string? Cursor, int Total);

/// <summary>
/// Reads identifier pages from one Archive audio collection. Search hints cannot authorize publication;
/// publication requires a separate current item metadata observation.
/// </summary>
public sealed class ArchiveCollectionSearchClient(HttpClient http, string collectionIdentifier)
{
    /// <summary>Reads and validates a page for the configured collection; a cursor alone does not authorize its items.</summary>
    public async Task<ArchiveCollectionSearchPage> FetchPageAsync(
        string? cursor = null,
        CancellationToken cancellationToken = default
    )
    {
        if (
            !System.Text.RegularExpressions.Regex.IsMatch(
                collectionIdentifier,
                @"^[A-Za-z0-9_.-]+$"
            )
        )
        {
            throw new ArgumentException(
                "An Archive collection identifier is required.",
                nameof(collectionIdentifier)
            );
        }

        var url =
            "https://archive.org/services/search/v1/scrape?"
            + "q="
            + Uri.EscapeDataString("collection:" + collectionIdentifier + " AND mediatype:audio")
            + "&fields=identifier,collection,mediatype";
        // Identifier-only field selection has returned unrelated results from Archive.
        // Request membership fields and validate each row before retaining its identifier.
        // Archive repeats the first page if count is retained on continuation requests.
        url += cursor == null ? "&count=100" : "&cursor=" + Uri.EscapeDataString(cursor);
        using var response = await http.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken
        );
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(
            ArchiveObservationProjector.MaximumResponseBytes,
            cancellationToken
        );
        var root = JObject.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (
            root["error"] != null
            || root["items"] is not JArray items
            || root["total"]?.Type != JTokenType.Integer
            || (int)root["total"]! < 0
            || items.Count > 5000
        )
        {
            throw new InvalidDataException("Archive item discovery returned an invalid page.");
        }

        if (
            root["cursor"] != null
            && root["cursor"]!.Type is not (JTokenType.String or JTokenType.Null)
        )
        {
            throw new InvalidDataException("Archive item discovery returned an invalid cursor.");
        }

        var next = (string?)root["cursor"];
        if (string.IsNullOrEmpty(next))
        {
            next = null;
        }

        if (next != null && next == cursor)
        {
            throw new InvalidDataException(
                "Archive item discovery repeated its continuation cursor."
            );
        }

        if (
            items.Any(item =>
                item is not JObject
                || item["identifier"]?.Type != JTokenType.String
                || string.IsNullOrWhiteSpace((string?)item["identifier"])
            )
        )
        {
            throw new InvalidDataException(
                "Archive item discovery returned an invalid identifier."
            );
        }

        if (items.Any(item => !MatchesCollectionQuery((JObject)item)))
        {
            throw new InvalidDataException(
                "Archive item discovery returned a row outside the requested audio collection."
            );
        }

        if (next == null && (int)root["total"]! > items.Count)
        {
            throw new InvalidDataException("Archive item discovery ended with unreturned items.");
        }

        return new ArchiveCollectionSearchPage(
            items
                .Select(item => (string)item["identifier"]!)
                .Distinct(StringComparer.Ordinal)
                .Select(identifier => new ArchiveItemId(identifier))
                .ToArray(),
            next,
            (int)root["total"]!
        );
    }

    private bool MatchesCollectionQuery(JObject item) =>
        item["mediatype"]?.Type == JTokenType.String
        && (string?)item["mediatype"] == "audio"
        && (
            item["collection"] is JArray collections
                ? collections.Any(value =>
                    value.Type == JTokenType.String && (string?)value == collectionIdentifier
                )
                : item["collection"]?.Type == JTokenType.String
                    && (string?)item["collection"] == collectionIdentifier
        );
}
