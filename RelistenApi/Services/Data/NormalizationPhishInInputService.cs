using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

/// <summary>
/// Retains Phish.in catalogs and complete show objects before legacy projection. A show keeps one
/// resource identity when pagination changes; its generation is always read before the page request.
/// </summary>
public sealed class NormalizationPhishInInputService(
    NormalizationEvidenceService evidence,
    NormalizationProviderInputService provider
)
{
    /// <summary>Captures a fixed catalog response using its stable route as the resource key.</summary>
    public Task<T> FetchCatalogAsync<T>(
        string route,
        Func<CancellationToken, Task<NormalizationProviderResponse<T>>> fetch,
        CancellationToken ct = default
    ) => provider.FetchAsync(new("phish.in", "catalog", route), "raw-http:1", fetch, ct);

    /// <summary>
    /// Fetches one page once, then retains every complete show object before returning the value.
    /// Unknown show IDs use generation zero. Partial retention survives failure; retry the whole
    /// fetch with a fresh snapshot. The caller must not advance page progress after an exception.
    /// </summary>
    public async Task<T> FetchShowsAsync<T>(
        Func<CancellationToken, Task<NormalizationProviderResponse<T>>> fetch,
        CancellationToken ct = default,
        int maxResources = 100000
    )
    {
        ct.ThrowIfCancellationRequested();
        if (!provider.Enabled)
        {
            return (await fetch(ct)).Value;
        }

        if (Transaction.Current != null)
        {
            throw new InvalidOperationException(
                "Fetch provider input outside the catalog transaction so retained evidence survives catalog rollback."
            );
        }

        var generations = await evidence.GetResourceGenerationsAsync(
            "phish.in",
            "show",
            maxResources,
            ct
        );
        var response = await fetch(ct);
        ct.ThrowIfCancellationRequested();

        // Match HttpContent's charset and BOM handling used by the legacy DTO parser. Selected
        // objects retain their original JSON text, re-encoded as UTF-8 for this capture version.
        using var content = new ByteArrayContent(response.Payload.ToArray());
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(response.MediaType);
        using var document = JsonDocument.Parse(await content.ReadAsStringAsync(ct));
        var shows = ValidateShows(document.RootElement);
        var observedAt = DateTimeOffset.UtcNow;

        foreach (var show in shows)
        {
            var key = show.GetProperty("id").GetInt32().ToString(CultureInfo.InvariantCulture);
            var resource = new NormalizationResourceKey("phish.in", "show", key);
            var capture = new NormalizationInputCapture(
                Guid.NewGuid(),
                resource,
                Encoding.UTF8.GetBytes(show.GetRawText()),
                "application/json",
                "phishin-show-object-v1",
                generations.GetValueOrDefault(key),
                observedAt,
                response.ProviderRevision
            );
            var retained = await evidence.CaptureInputAsync(capture, ct);
            if (!retained.Fetch.AcceptedAsCurrent)
            {
                throw new NormalizationStaleInputException(resource, retained);
            }
        }

        ct.ThrowIfCancellationRequested();
        return response.Value;
    }

    private static JsonElement[] ValidateShows(JsonElement root)
    {
        if (
            root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("success", out var success)
            || success.ValueKind != JsonValueKind.True
            || !root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array
        )
        {
            throw new InvalidDataException("Phish.in returned an invalid show page.");
        }

        var shows = data.EnumerateArray().ToArray();
        var identities = new HashSet<int>();
        foreach (var show in shows)
        {
            if (
                show.ValueKind != JsonValueKind.Object
                || !show.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.Number
                || !id.TryGetInt32(out var value)
                || value <= 0
                || !identities.Add(value)
            )
            {
                throw new InvalidDataException(
                    "Phish.in returned an invalid or repeated show identity."
                );
            }
        }

        return shows;
    }
}
