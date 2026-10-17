using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Ingestion.Providers.TapersSection;

namespace Relisten.Ingestion.Providers.Archive;

/// <summary>
/// Retains the complete Archive item response before legacy DTO conversion or intake projection.
/// Native imports, artist intake and collection discovery share one provider resource and history.
/// </summary>
public static class ArchiveNormalizationInput
{
    public const string CaptureVersion = "archive-item-response-v1";

    public static NormalizationResourceKey Resource(ArchiveItemId item) =>
        new("archive.org", "item_metadata", item.Value);

    /// <summary>
    /// Fetches current metadata with the caller's HTTP policy. Enabled capture rejects invalid
    /// envelopes before retention; disabled capture preserves the importer or projector's validation.
    /// </summary>
    public static Task<string> FetchAsync(
        HttpClient http,
        NormalizationProviderInputService inputs,
        ArchiveItemId item,
        CancellationToken ct = default
    ) =>
        inputs.FetchAsync(
            Resource(item),
            CaptureVersion,
            async token =>
            {
                using var response = await http.GetAsync(
                    "https://archive.org/metadata/" + System.Uri.EscapeDataString(item.Value),
                    token
                );
                response.EnsureSuccessStatusCode();
                var bytes = await response.Content.ReadAsByteArrayAsync(token);
                var raw = await response.Content.ReadAsStringAsync(token);
                if (inputs.Enabled)
                {
                    Validate(item, raw, bytes.Length);
                }

                return new NormalizationProviderResponse<string>(
                    raw,
                    bytes,
                    response.Content.Headers.ContentType?.ToString() ?? "application/json",
                    response.Headers.ETag?.ToString()
                );
            },
            ct
        );

    private static void Validate(ArchiveItemId item, string raw, int byteCount)
    {
        if (byteCount > ArchiveObservationProjector.MaximumResponseBytes)
        {
            throw new InvalidDataException("Archive metadata exceeded the 64 MiB response limit.");
        }

        var root = ArchiveObservationProjector.ParseObject(raw);
        if (
            root["errcode"]?.Type is not null and not JTokenType.Null
            || !string.IsNullOrEmpty((string?)root["error"])
        )
        {
            throw new InvalidDataException("Archive.org returned an item metadata error.");
        }

        var metadata = root["metadata"] as JObject;
        var identifier = metadata?["identifier"];
        var dark = ArchiveObservationProjector.IsTrue(root["is_dark"]);
        // Dark responses may omit metadata. The successful request identifies that negative
        // response; a supplied identity must still agree with the requested item.
        if (
            (
                identifier is not null
                && (identifier.Type != JTokenType.String || (string?)identifier != item.Value)
            )
            || (!dark && identifier is null)
            || (root["metadata"]?.Type is not null and not JTokenType.Null && metadata is null)
        )
        {
            throw new InvalidDataException("Archive metadata did not identify the requested item.");
        }

        if (
            root.TryGetValue("files", out var files)
            && (files is not JArray array || array.Any(file => file is not JObject))
        )
        {
            throw new InvalidDataException("Archive metadata contained an invalid file list.");
        }
    }
}
