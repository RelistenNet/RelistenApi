using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Relisten.Search;

/// <summary>Uses a pooled HTTP connection for Typesense's JSON and JSONL endpoints.</summary>
internal sealed class TypesenseClient(HttpClient http)
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static void Configure(HttpClient http, string url, string apiKey)
    {
        http.BaseAddress = new Uri(url.TrimEnd('/') + "/");
        http.DefaultRequestHeaders.Add("X-TYPESENSE-API-KEY", apiKey);
        http.Timeout = TimeSpan.FromMinutes(5);
    }

    public async Task CreateCollection(object schema, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(
            "collections",
            schema,
            Json,
            cancellationToken
        );
        await EnsureSuccess(response, cancellationToken);
    }

    public async Task<ImportResult> Import(
        string collection,
        string jsonl,
        bool upsert,
        CancellationToken cancellationToken
    )
    {
        var action = upsert ? "upsert" : "create";
        using var body = new StringContent(jsonl, Encoding.UTF8, "text/plain");
        using var response = await http.PostAsync(
            $"collections/{collection}/documents/import?action={action}",
            body,
            cancellationToken
        );
        await EnsureSuccess(response, cancellationToken);

        // Typesense returns HTTP 200 even when individual documents were rejected.
        using var reader = new StringReader(
            await response.Content.ReadAsStringAsync(cancellationToken)
        );
        var accepted = 0;
        var rejected = 0;
        string? firstError = null;
        while (reader.ReadLine() is { Length: > 0 } line)
        {
            using var result = JsonDocument.Parse(line);
            if (result.RootElement.GetProperty("success").GetBoolean())
            {
                accepted++;
            }
            else
            {
                rejected++;
                firstError ??= result.RootElement.GetProperty("error").GetString();
            }
        }

        return new ImportResult(accepted, rejected, firstError);
    }

    public async Task<string?> ReadAlias(string alias, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"aliases/{alias}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccess(response, cancellationToken);
        using var result = await response.Content.ReadFromJsonAsync<JsonDocument>(
            cancellationToken: cancellationToken
        );
        return result!.RootElement.GetProperty("collection_name").GetString();
    }

    public async Task<string[]> ReadCollectionNames(CancellationToken cancellationToken)
    {
        using var result = await http.GetFromJsonAsync<JsonDocument>(
            "collections",
            cancellationToken
        );
        return result!
            .RootElement.EnumerateArray()
            .Select(collection => collection.GetProperty("name").GetString()!)
            .ToArray();
    }

    public async Task<Dictionary<string, string>> ReadAliases(CancellationToken cancellationToken)
    {
        using var result = await http.GetFromJsonAsync<JsonDocument>("aliases", cancellationToken);
        return result!
            .RootElement.GetProperty("aliases")
            .EnumerateArray()
            .ToDictionary(
                alias => alias.GetProperty("name").GetString()!,
                alias => alias.GetProperty("collection_name").GetString()!,
                StringComparer.Ordinal
            );
    }

    public async Task SetAlias(string alias, string collection, CancellationToken cancellationToken)
    {
        using var response = await http.PutAsJsonAsync(
            $"aliases/{alias}",
            new { collection_name = collection },
            cancellationToken
        );
        await EnsureSuccess(response, cancellationToken);
    }

    public Task DeleteCollection(string collection, CancellationToken cancellationToken) =>
        Delete($"collections/{collection}", cancellationToken);

    public Task DeleteAlias(string alias, CancellationToken cancellationToken) =>
        Delete($"aliases/{alias}", cancellationToken);

    public Task DeleteDocument(string collection, string id, CancellationToken cancellationToken) =>
        Delete($"collections/{collection}/documents/{Uri.EscapeDataString(id)}", cancellationToken);

    public async IAsyncEnumerable<IndexedDocument> ExportDocuments(
        string collection,
        string filter,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var path =
            $"collections/{collection}/documents/export"
            + $"?filter_by={Uri.EscapeDataString(filter)}&include_fields=id,venue_id,kind";
        using var response = await http.GetAsync(
            path,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken
        );
        await EnsureSuccess(response, cancellationToken);
        using var reader = new StreamReader(
            await response.Content.ReadAsStreamAsync(cancellationToken)
        );
        while (await reader.ReadLineAsync(cancellationToken) is { Length: > 0 } line)
        {
            yield return JsonSerializer.Deserialize<IndexedDocument>(line, Json)
                ?? throw new InvalidDataException(
                    "Typesense returned a null document in its export."
                );
        }
    }

    public async Task DeleteDocuments(
        string collection,
        IEnumerable<string> ids,
        CancellationToken cancellationToken
    )
    {
        // Keep the URL below normal request-line size limits.
        foreach (var batch in ids.Chunk(100))
        {
            var filter = Uri.EscapeDataString($"id:=[{string.Join(',', batch)}]");
            await Delete(
                $"collections/{collection}/documents?filter_by={filter}",
                cancellationToken
            );
        }
    }

    public async Task<SearchDocument?> ReadDocument(
        string collection,
        string id,
        CancellationToken cancellationToken
    )
    {
        using var response = await http.GetAsync(
            $"collections/{collection}/documents/{Uri.EscapeDataString(id)}",
            cancellationToken
        );
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccess(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<SearchDocument>(Json, cancellationToken);
    }

    public async Task<JsonDocument> MultiSearch(object request, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(
            "multi_search",
            request,
            Json,
            cancellationToken
        );
        await EnsureSuccess(response, cancellationToken);
        return (
            await response.Content.ReadFromJsonAsync<JsonDocument>(
                cancellationToken: cancellationToken
            )
        )!;
    }

    private async Task Delete(string path, CancellationToken cancellationToken)
    {
        using var response = await http.DeleteAsync(path, cancellationToken);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            await EnsureSuccess(response, cancellationToken);
        }
    }

    private static async Task EnsureSuccess(
        HttpResponseMessage response,
        CancellationToken cancellationToken
    )
    {
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Typesense returned HTTP {(int)response.StatusCode}: {error}",
                null,
                response.StatusCode
            );
        }
    }

    internal sealed record ImportResult(int Accepted, int Rejected, string? FirstError);

    internal sealed record IndexedDocument(string Id, string? VenueId, string? Kind);
}
