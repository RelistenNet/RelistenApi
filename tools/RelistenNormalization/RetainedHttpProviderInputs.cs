using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Validates original HTTP artifacts, associates their selected recording with captured
/// catalog coordinates, then writes frozen shadow provider inputs. Failed preparation
/// retains its prefix without a completed descriptor. A new destination is required.
/// </summary>
public sealed partial class RetainedHttpProviderInputs
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly TimeProvider clock;

    public RetainedHttpProviderInputs(TimeProvider? clock = null) =>
        this.clock = clock ?? TimeProvider.System;

    /// <summary>
    /// Consumes completed native-phish-credit-fields-v1/native-archive-credit-fields-v1
    /// capture directories and a completed raw catalog manifest. Repeated preparation
    /// requires a new destination and creates new admission UUIDs; reuse the returned
    /// inputs/descriptor for exact replay. Cancellation never publishes completion.
    /// </summary>
    public async Task<RetainedHttpProviderInputsResult> PrepareAsync(
        RetainedHttpProviderInputsRequest request,
        string outputDirectory,
        CancellationToken ct = default
    )
    {
        ct.ThrowIfCancellationRequested();
        if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory))
        {
            throw new ArgumentException("Offline admission requires a new output directory.");
        }
        if (request.HttpCaptureDirectories.Count == 0)
        {
            throw new ArgumentException("At least one retained HTTP capture is required.");
        }

        var captures = new List<HttpCapture>();
        foreach (var directory in request.HttpCaptureDirectories)
        {
            captures.Add(await ReadCaptureAsync(directory, ct));
        }
        var selected = captures.SelectMany(value => value.Items).ToArray();
        if (selected.Select(value => value.SourceUuid).Distinct().Count() != selected.Length)
        {
            throw new InvalidDataException(
                "Retained HTTP captures select duplicate source identities."
            );
        }
        var (catalog, catalogArtifact) = await ReadCatalogAsync(
            request.CatalogManifestPath,
            selected.Select(value => value.SourceUuid).ToHashSet(),
            ct
        );

        Directory.CreateDirectory(outputDirectory);
        var partialPath = Path.Combine(outputDirectory, "inputs.partial.jsonl");
        var provenance = new List<RetainedHttpProviderProvenance>();
        await using (
            var writer = new StreamWriter(
                new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)
            )
        )
        {
            foreach (var capture in captures)
            {
                foreach (var item in capture.Items)
                {
                    ct.ThrowIfCancellationRequested();
                    var prepared = await PrepareInputAsync(
                        capture,
                        item,
                        catalog,
                        catalogArtifact,
                        ct
                    );
                    await writer.WriteLineAsync(prepared.Wrapper.GetRawText().AsMemory(), ct);
                    provenance.Add(prepared.Provenance);
                }
            }
        }
        ct.ThrowIfCancellationRequested();
        var inputsPath = Path.Combine(outputDirectory, "inputs.jsonl");
        File.Move(partialPath, inputsPath);
        var descriptorPath = Path.Combine(outputDirectory, "descriptor.json");
        var descriptor = new
        {
            version = "retained-http-shadow-admission-v1",
            state = "completed",
            admission_is_persisted_provider_history = false,
            current_generation_receipts_created = false,
            decoder = NormalizationProviderDecoder.Version,
            inputRows = provenance.Count,
            boundSources = provenance.Count(value => value.CatalogBinding.Status == "bound"),
            inputs = await FingerprintAsync(inputsPath, ct),
            catalogManifest = catalogArtifact,
            entries = provenance,
        };
        var pendingDescriptor = Path.Combine(outputDirectory, "descriptor.partial.json");
        await File.WriteAllTextAsync(
            pendingDescriptor,
            JsonSerializer.Serialize(descriptor, Json),
            ct
        );
        ct.ThrowIfCancellationRequested();
        File.Move(pendingDescriptor, descriptorPath);
        return new(
            inputsPath,
            descriptorPath,
            provenance.Count,
            descriptor.boundSources,
            provenance.Count - descriptor.boundSources
        );
    }

    private async Task<(
        JsonElement Wrapper,
        RetainedHttpProviderProvenance Provenance
    )> PrepareInputAsync(
        HttpCapture capture,
        HttpItem item,
        CatalogIndex catalog,
        RetainedHttpArtifact catalogArtifact,
        CancellationToken ct
    )
    {
        var bodyPath = ContainedPath(capture.Directory, item.ResponsePath);
        var body = await ReadVerifiedAsync(bodyPath, item.Bytes, item.Sha256, ct);
        using var document = JsonDocument.Parse(body);
        var resource = VerifyResource(capture.Provider, item, document.RootElement);
        var binding = BindSource(item, resource, catalog, catalogArtifact, out var source);
        catalog.Artists.TryGetValue(item.ArtistUuid, out var artist);
        var admittedAt = clock.GetUtcNow();
        var revision = new NormalizationInputRevision
        {
            Uuid = Guid.NewGuid(),
            Resource = resource,
            Payload = body,
            Sha256 = item.Sha256,
            MediaType = item.MediaType,
            CaptureVersion =
                capture.Provider == "phish.in"
                    ? "phishin-show-response-v1"
                    : "archive-item-response-v1",
            CapturedAt = admittedAt.UtcDateTime,
        };
        var original = new RetainedHttpProviderProvenance(
            revision.Uuid,
            admittedAt,
            item.StartedAt,
            item.Url,
            200,
            capture.Manifest,
            capture.Plan,
            capture.Receipts,
            item.ReceiptOrdinal,
            new(Path.GetFullPath(bodyPath), body.LongLength, item.Sha256),
            binding
        );
        var wrapper = JsonSerializer.SerializeToElement(
            new
            {
                kind = "provider_input",
                provider = capture.Provider,
                row = new
                {
                    uuid = revision.Uuid,
                    artist_id = artist?.Id,
                    artist_uuid = item.ArtistUuid,
                    artist_name = artist?.Name,
                },
                input = revision,
                retained_http = original,
                catalog_binding = binding,
                source_context = binding.Status == "bound" ? source?.Row : null,
                show_context = binding.ShowUuid != null ? source?.Show : null,
            },
            Json
        );
        return (wrapper, original);
    }

    private static NormalizationResourceKey VerifyResource(
        string provider,
        HttpItem item,
        JsonElement root
    )
    {
        var uri = new Uri(item.Url);
        var expectedPath =
            provider == "phish.in"
                ? "/api/v1/shows/" + item.UpstreamIdentifier + ".json"
                : "/metadata/" + item.UpstreamIdentifier;
        if (
            uri.Scheme != "https"
            || uri.Host != provider
            || uri.UserInfo.Length != 0
            || uri.Fragment.Length != 0
            || Uri.UnescapeDataString(uri.AbsolutePath) != expectedPath
        )
        {
            throw new InvalidDataException(
                "HTTP receipt URL differs from its selected provider resource."
            );
        }
        if (provider == "phish.in")
        {
            if (
                root.GetProperty("success").ValueKind != JsonValueKind.True
                || !root.GetProperty("data").GetProperty("id").TryGetInt32(out var id)
                || id <= 0
                || id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    != item.UpstreamIdentifier
            )
            {
                throw new InvalidDataException(
                    "Phish response differs from its selected show identity."
                );
            }
            return new(provider, "show", item.UpstreamIdentifier);
        }
        if (
            root.GetProperty("metadata").GetProperty("identifier").GetString()
            != item.UpstreamIdentifier
        )
        {
            throw new InvalidDataException(
                "Archive response differs from its selected item identity."
            );
        }
        return new(provider, "item_metadata", item.UpstreamIdentifier);
    }

    private sealed record HttpCapture(
        string Directory,
        string Provider,
        RetainedHttpArtifact Manifest,
        RetainedHttpArtifact Plan,
        RetainedHttpArtifact Receipts,
        IReadOnlyList<HttpItem> Items
    );

    private sealed record HttpItem(
        Guid SourceUuid,
        Guid ArtistUuid,
        string UpstreamIdentifier,
        string Url,
        DateTimeOffset StartedAt,
        string MediaType,
        int ReceiptOrdinal,
        string ResponsePath,
        long Bytes,
        string Sha256
    );
}
