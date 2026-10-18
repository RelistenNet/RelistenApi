using System.Text.Json;
using System.Text.Json.Serialization;
using Relisten.Api.Models.Normalization;
using static Relisten.Normalization.Batch.RetainedHttpProviderInputs;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Admits original Local root/directory captures for offline normalization. Exact playable
/// URLs establish historical recording ownership. No catalog or normalization store is written.
/// </summary>
public sealed partial class RetainedLocalProviderInputs(TimeProvider? timeProvider = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Creates a new replayable input stream: one Local supporting root followed by directory
    /// inputs. Source/file shards are authenticated and streamed once; only matching URL rows
    /// are retained in memory. Failed or cancelled preparation leaves no completed descriptor.
    /// </summary>
    public async Task<RetainedLocalProviderInputsResult> PrepareAsync(
        RetainedLocalProviderInputsRequest request,
        string outputDirectory,
        CancellationToken ct = default
    )
    {
        ct.ThrowIfCancellationRequested();
        if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory))
        {
            throw new ArgumentException("Local admission requires a new output directory.");
        }

        var capture = await ReadCaptureAsync(request, ct);
        var decoder = new NormalizationProviderDecoder();
        var rootDecoded = NormalizationLocalRecordingSelection.DecodeRoot(
            capture.Root,
            decoder,
            cancellationToken: ct
        );
        var directories = capture
            .Directories.Select(directory => new DecodedDirectory(
                directory,
                decoder.Decode(directory.Input, ct)
            ))
            .ToArray();
        var urls = CollectMediaUrls(capture, directories);
        var catalog = await ReadCatalogAsync(
            request.CatalogManifestPath,
            urls,
            capture.Root.Resource.ProviderKey,
            ct
        );

        return await WriteAdmissionAsync(
            capture,
            directories,
            rootDecoded,
            catalog,
            outputDirectory,
            ct
        );
    }

    private static HashSet<string> CollectMediaUrls(
        LocalCapture capture,
        IReadOnlyList<DecodedDirectory> directories
    )
    {
        var urls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directory in directories)
        {
            if (directory.Decoded.Recordings.Single().Files.Count > 10_000)
            {
                throw new InvalidDataException("A Local directory exceeds 10,000 file assertions.");
            }
            foreach (var file in directory.Decoded.Recordings.Single().Files)
            {
                if (
                    MediaUrl(
                        capture.Root.Resource.ProviderKey,
                        directory.Capture.Directory,
                        file
                    ) is
                    { } url
                )
                {
                    urls.Add(url);
                }
            }
        }
        if (urls.Count > 100_000)
        {
            throw new InvalidDataException(
                "Local admission exceeds 100,000 distinct playable URL assertions."
            );
        }
        return urls;
    }

    private async Task<RetainedLocalProviderInputsResult> WriteAdmissionAsync(
        LocalCapture capture,
        IReadOnlyList<DecodedDirectory> directories,
        ProviderDecodeResult rootDecoded,
        LocalCatalog catalog,
        string outputDirectory,
        CancellationToken ct
    )
    {
        Directory.CreateDirectory(outputDirectory);
        var partial = Path.Combine(outputDirectory, "inputs.partial.jsonl");
        var entries = new List<RetainedLocalProviderProvenance>();
        await using (
            var writer = new StreamWriter(
                new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.Read)
            )
        )
        {
            var rootRow = JsonSerializer.SerializeToElement(
                new
                {
                    kind = "local_root_input",
                    provider = "local",
                    row = new { uuid = capture.Root.Uuid, artist_uuid = capture.ArtistUuid },
                    input = capture.Root,
                    retained_local_root = new { capture.RootManifest, capture.RootBody },
                },
                Json
            );
            await writer.WriteLineAsync(rootRow.GetRawText().AsMemory(), ct);
            foreach (var directory in directories)
            {
                ct.ThrowIfCancellationRequested();
                var (binding, source) = Bind(capture, directory, rootDecoded, catalog);
                var provenance = new RetainedLocalProviderProvenance(
                    directory.Capture.Input.Uuid,
                    capture.Root.Uuid,
                    clock.GetUtcNow(),
                    capture.Root.Resource.ProviderKey,
                    directory.Capture.Directory,
                    directory.Capture.Url,
                    capture.RootManifest,
                    capture.RootBody,
                    capture.Manifest,
                    capture.Plan,
                    capture.Receipts,
                    directory.Capture.Ordinal,
                    directory.Capture.Body,
                    binding
                );
                entries.Add(provenance);
                var artist = catalog.Artists.GetValueOrDefault(capture.ArtistUuid);
                var wrapper = JsonSerializer.SerializeToElement(
                    new
                    {
                        kind = "provider_input",
                        provider = "local",
                        row = new
                        {
                            uuid = directory.Capture.Input.Uuid,
                            artist_id = artist?.Id,
                            artist_uuid = capture.ArtistUuid,
                            artist_name = artist?.Name,
                        },
                        input = directory.Capture.Input,
                        retained_local = provenance,
                        source_context = binding.Status == "bound" ? source?.Row : null,
                        show_context = binding.ShowUuid != null ? source?.Show : null,
                    },
                    Json
                );
                await writer.WriteLineAsync(wrapper.GetRawText().AsMemory(), ct);
            }
        }

        ct.ThrowIfCancellationRequested();
        var inputs = Path.Combine(outputDirectory, "inputs.jsonl");
        File.Move(partial, inputs);
        var descriptor = Path.Combine(outputDirectory, "descriptor.json");
        var bound = entries.Where(entry => entry.CatalogBinding.Status == "bound").ToArray();
        var result = new RetainedLocalProviderInputsResult(
            inputs,
            descriptor,
            entries.Count,
            bound.Length,
            bound.Select(entry => entry.CatalogBinding.SourceUuid).Distinct().Count(),
            entries.Count - bound.Length
        );
        var pending = Path.Combine(outputDirectory, "descriptor.partial.json");
        await File.WriteAllTextAsync(
            pending,
            JsonSerializer.Serialize(
                new
                {
                    version = "retained-local-shadow-admission-v1",
                    state = "completed",
                    decoder = NormalizationProviderDecoder.Version,
                    selection = NormalizationLocalRecordingSelection.Version,
                    admission_is_persisted_provider_history = false,
                    current_generation_receipts_created = false,
                    inputs = await FingerprintAsync(inputs, ct),
                    result,
                    entries,
                },
                Json
            ),
            ct
        );
        ct.ThrowIfCancellationRequested();
        File.Move(pending, descriptor);
        return result;
    }

    internal static string? MediaUrl(string root, string directory, ProviderFileObservation file) =>
        string.IsNullOrWhiteSpace(file.PlaybackLocator?.Value)
            ? null
            : $"https://audio.relisten.net/{root}/{directory}/{file.PlaybackLocator.Value}";

    private sealed record DecodedDirectory(DirectoryCapture Capture, ProviderDecodeResult Decoded);
}
