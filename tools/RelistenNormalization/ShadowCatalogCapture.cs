using System.Security.Cryptography;
using System.Text.Json;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Freezes the completed input files selected for one inline run. A growing capture
/// manifest is a discovery list; its later changes do not change a resumed run.
/// Every selected compressed file and capture receipt is verified before extraction.
/// </summary>
internal sealed class ShadowCatalogCapture
{
    private const string DescriptorVersion = "shadow-inline-inputs-v1";
    private const string DescriptorName = "input-descriptor.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly byte[] descriptorBytes;

    private ShadowCatalogCapture(InputDescriptor descriptor, byte[] descriptorBytes)
    {
        Descriptor = descriptor;
        this.descriptorBytes = descriptorBytes;
    }

    internal InputDescriptor Descriptor { get; }
    internal string Identity => Convert.ToHexStringLower(SHA256.HashData(descriptorBytes));

    internal static async Task<ShadowCatalogCapture> OpenAsync(
        string inputPath,
        string outputDirectory,
        bool resume,
        string? providerInputsPath,
        CancellationToken ct
    )
    {
        inputPath = Path.GetFullPath(inputPath);
        byte[] bytes;
        InputDescriptor descriptor;
        if (resume)
        {
            bytes = await File.ReadAllBytesAsync(Path.Combine(outputDirectory, DescriptorName), ct);
            descriptor = JsonSerializer.Deserialize<InputDescriptor>(bytes, Json)!;
            if (
                descriptor.Version != DescriptorVersion
                || descriptor.InputPath != inputPath
                || (
                    providerInputsPath != null
                    && Path.GetFullPath(providerInputsPath) != descriptor.ProviderInput?.Path
                )
            )
            {
                throw new InvalidOperationException(
                    "Resume must use the original frozen inline input descriptor."
                );
            }
        }
        else
        {
            descriptor = await SelectAsync(inputPath, providerInputsPath, ct);
            bytes = JsonSerializer.SerializeToUtf8Bytes(descriptor, Json);
        }

        var capture = new ShadowCatalogCapture(descriptor, bytes);
        await capture.VerifyAsync(ct);
        return capture;
    }

    internal async Task SaveAsync(string outputDirectory)
    {
        var path = Path.Combine(outputDirectory, DescriptorName);
        if (!File.Exists(path))
        {
            await File.WriteAllBytesAsync(path + ".writing", descriptorBytes);
            File.Move(path + ".writing", path);
        }
    }

    private static async Task<InputDescriptor> SelectAsync(
        string inputPath,
        string? providerInputsPath,
        CancellationToken ct
    )
    {
        InputFile[] files;
        string scope;
        if (Path.GetExtension(inputPath).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(inputPath, ct));
            var manifest = document.RootElement;
            if (
                manifest.GetProperty("version").GetString() != "shadow-raw-catalog-v1"
                || manifest.GetProperty("state").GetString()
                    is not ("partial" or "completed_declared_ranges")
            )
            {
                throw new InvalidDataException(
                    "Expected a shadow-raw-catalog-v1 capture with completed shards."
                );
            }

            var directory = Path.GetDirectoryName(inputPath)!;
            files = manifest
                .GetProperty("completedShards")
                .EnumerateArray()
                .Select(shard =>
                {
                    if (shard.GetProperty("state").GetString() != "completed")
                    {
                        throw new InvalidDataException(
                            "Only completed capture shards can enter a shadow run."
                        );
                    }

                    return new InputFile(
                        ChildPath(directory, shard.GetProperty("gzipPath").GetString()!),
                        shard.GetProperty("compressed").Deserialize<Fingerprint>(Json)!,
                        shard.GetProperty("shardId").GetString(),
                        shard.GetProperty("actualRows").GetInt64(),
                        ChildPath(directory, shard.GetProperty("receipt").GetString()!),
                        shard.GetProperty("receiptFingerprint").Deserialize<Fingerprint>(Json),
                        shard.Clone()
                    );
                })
                .ToArray();
            if (
                files.Length == 0
                || files.Select(file => file.Path).Distinct().Count() != files.Length
                || files.Select(file => file.ShardId).Distinct().Count() != files.Length
            )
            {
                throw new InvalidDataException(
                    "A capture selection requires distinct completed shards."
                );
            }

            scope =
                "Completed shards selected in manifest order; each retains its own read-only snapshot and footer.";
        }
        else
        {
            files = [new(inputPath, await ReadFingerprintAsync(inputPath, ct))];
            scope = "One retained snapshot file; its original snapshot and footer are preserved.";
        }

        var provider =
            providerInputsPath == null
                ? null
                : new InputFile(
                    Path.GetFullPath(providerInputsPath),
                    await ReadFingerprintAsync(providerInputsPath, ct)
                );
        return new(DescriptorVersion, inputPath, files, provider, scope);
    }

    private async Task VerifyAsync(CancellationToken ct)
    {
        foreach (
            var file in Descriptor.Files.Concat(
                Descriptor.ProviderInput is { } provider ? [provider] : []
            )
        )
        {
            await RequireFingerprintAsync(file.Path, file.Compressed, ct);
            if (file.ReceiptPath is not { } receiptPath)
            {
                continue;
            }

            await RequireFingerprintAsync(receiptPath, file.ReceiptFingerprint!, ct);
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(receiptPath, ct));
            var receipt = document.RootElement;
            if (
                receipt.GetProperty("state").GetString() != "completed"
                || receipt.GetProperty("gzipPath").GetString() != Path.GetFileName(file.Path)
                || receipt.GetProperty("compressed").Deserialize<Fingerprint>(Json)
                    != file.Compressed
                || receipt.GetProperty("shard").GetProperty("id").GetString() != file.ShardId
                || receipt.GetProperty("reconciliation").GetProperty("actualRows").GetInt64()
                    != file.ActualRows
            )
            {
                throw new InvalidDataException(
                    "Capture receipt does not describe its selected shard."
                );
            }
        }
    }

    private static string ChildPath(string directory, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name)
        {
            throw new InvalidDataException(
                "Capture files must be direct children of their manifest directory."
            );
        }
        return Path.Combine(directory, name);
    }

    private static async Task RequireFingerprintAsync(
        string path,
        Fingerprint expected,
        CancellationToken ct
    )
    {
        if (await ReadFingerprintAsync(path, ct) != expected)
        {
            throw new InvalidDataException("Frozen shadow input bytes changed: " + path);
        }
    }

    private static async Task<Fingerprint> ReadFingerprintAsync(string path, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        return new(file.Length, Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct)));
    }

    internal sealed record InputDescriptor(
        string Version,
        string InputPath,
        IReadOnlyList<InputFile> Files,
        InputFile? ProviderInput,
        string Scope
    );

    internal sealed record InputFile(
        string Path,
        Fingerprint Compressed,
        string? ShardId = null,
        long? ActualRows = null,
        string? ReceiptPath = null,
        Fingerprint? ReceiptFingerprint = null,
        JsonElement? CaptureEntry = null
    );

    internal sealed record Fingerprint(long Bytes, string Sha256);
}
