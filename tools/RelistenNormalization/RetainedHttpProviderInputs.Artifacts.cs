using System.Security.Cryptography;
using System.Text.Json;

namespace Relisten.Normalization.Batch;

public sealed partial class RetainedHttpProviderInputs
{
    private static async Task<HttpCapture> ReadCaptureAsync(string directory, CancellationToken ct)
    {
        directory = Path.GetFullPath(directory);
        var manifestPath = Path.Combine(directory, "manifest.json");
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, ct);
        var manifest = new RetainedHttpArtifact(
            manifestPath,
            manifestBytes.LongLength,
            Convert.ToHexStringLower(SHA256.HashData(manifestBytes))
        );
        using var document = JsonDocument.Parse(manifestBytes);
        var root = document.RootElement;
        var version = root.TryGetProperty("schema_version", out var schema)
            ? schema.GetString()
            : root.GetProperty("version").GetString();
        var provider = version switch
        {
            "native-phish-credit-fields-v1" => "phish.in",
            "native-archive-credit-fields-v1" => "archive.org",
            _ => throw new NotSupportedException("Unsupported retained HTTP capture format."),
        };
        if (root.GetProperty("state").GetString() != "completed")
        {
            throw new InvalidDataException("Retained HTTP capture is not complete.");
        }
        var plan = await ReadDeclaredJsonAsync(directory, root.GetProperty("plan"), ct);
        var receipts = await ReadDeclaredJsonAsync(
            directory,
            root.GetProperty("http_receipts"),
            ct
        );
        var selections = plan
            .Json.GetProperty("items")
            .EnumerateArray()
            .ToDictionary(value => value.GetProperty("source_uuid").GetGuid(), value => value);
        var items = new List<HttpItem>();
        var ordinal = 0;
        foreach (var receipt in receipts.Json.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            if (
                receipt.GetProperty("status").GetInt32() != 200
                || receipt.GetProperty("failure").ValueKind != JsonValueKind.Null
            )
            {
                throw new InvalidDataException(
                    "Offline admission requires successful retained HTTP receipts."
                );
            }
            var selected = selections[receipt.GetProperty("source_uuid").GetGuid()];
            var response = receipt.GetProperty("response");
            var mediaType = "application/json";
            if (
                receipt.TryGetProperty("response_headers", out var headers)
                && headers.TryGetProperty("Content-Type", out var contentType)
            )
            {
                mediaType = contentType.GetString()!;
            }
            items.Add(
                new(
                    selected.GetProperty("source_uuid").GetGuid(),
                    selected.GetProperty("artist_uuid").GetGuid(),
                    selected.GetProperty("upstream_identifier").GetString()!,
                    receipt.GetProperty("url").GetString()!,
                    receipt.GetProperty("started_at_utc").GetDateTimeOffset(),
                    mediaType,
                    ordinal++,
                    response.GetProperty("path").GetString()!,
                    response.GetProperty("bytes").GetInt64(),
                    response.GetProperty("sha256").GetString()!
                )
            );
        }
        if (
            items.Count != selections.Count
            || items.Count != root.GetProperty("captured").GetInt32()
            || items.Select(value => value.SourceUuid).Distinct().Count() != items.Count
        )
        {
            throw new InvalidDataException(
                "Retained HTTP selections and receipts do not reconcile."
            );
        }
        return new(directory, provider, manifest, plan.Artifact, receipts.Artifact, items);
    }

    private static async Task<(
        JsonElement Json,
        RetainedHttpArtifact Artifact
    )> ReadDeclaredJsonAsync(string directory, JsonElement declaration, CancellationToken ct)
    {
        var path = ContainedPath(directory, declaration.GetProperty("path").GetString()!);
        var expectedBytes = declaration.GetProperty("bytes").GetInt64();
        var sha = declaration.GetProperty("sha256").GetString()!;
        using var document = JsonDocument.Parse(
            await ReadVerifiedAsync(path, expectedBytes, sha, ct)
        );
        return (document.RootElement.Clone(), new(path, expectedBytes, sha));
    }

    private static async Task<byte[]> ReadVerifiedAsync(
        string path,
        long bytes,
        string sha,
        CancellationToken ct
    )
    {
        if (bytes is < 0 or > 64 * 1024 * 1024 || new FileInfo(path).Length != bytes)
        {
            throw new InvalidDataException(
                "Retained HTTP artifact length differs or exceeds 64 MiB."
            );
        }
        var raw = await File.ReadAllBytesAsync(path, ct);
        if (
            raw.LongLength != bytes
            || !StringComparer.OrdinalIgnoreCase.Equals(
                Convert.ToHexStringLower(SHA256.HashData(raw)),
                sha
            )
        )
        {
            throw new InvalidDataException("Retained HTTP artifact hash differs.");
        }
        return raw;
    }

    private static async Task<RetainedHttpArtifact> FingerprintAsync(
        string path,
        CancellationToken ct
    )
    {
        path = Path.GetFullPath(path);
        await using var stream = File.OpenRead(path);
        var sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
        return new(path, stream.Length, sha);
    }

    private static string ContainedPath(string directory, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(directory, relative));
        var relation = Path.GetRelativePath(directory, path);
        if (
            Path.IsPathRooted(relative)
            || relation == ".."
            || relation.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
        )
        {
            throw new InvalidDataException("Retained artifact path leaves its capture directory.");
        }
        return path;
    }
}
