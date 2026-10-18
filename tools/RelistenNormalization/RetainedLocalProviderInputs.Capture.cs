using System.Text.Json;
using Relisten.Api.Models.Normalization;
using static Relisten.Normalization.Batch.RetainedHttpProviderInputs;

namespace Relisten.Normalization.Batch;

public sealed partial class RetainedLocalProviderInputs
{
    // These are the original capture formats. The joined native-fields replay supplies
    // sampling hints only; normalization always decodes the separately retained HTTP bodies.
    private async Task<LocalCapture> ReadCaptureAsync(
        RetainedLocalProviderInputsRequest request,
        CancellationToken ct
    )
    {
        var rootDirectory = Path.GetFullPath(request.RootCaptureDirectory);
        var directory = Path.GetFullPath(request.DirectoryCaptureDirectory);
        var (rootManifest, rootManifestArtifact) = await ReadJsonAsync(
            Path.Combine(rootDirectory, "manifest.json"),
            ct
        );
        if (
            rootManifest.GetProperty("version").GetString()
                != "native-credit-metadata-inventory-result-v1"
            || rootManifest.GetProperty("state").GetString() != "completed_metadata_root"
            || rootManifest.GetProperty("httpStatus").GetInt32() != 200
        )
        {
            throw new InvalidDataException("A completed retained Local root capture is required.");
        }
        var (rootPlanBytes, rootPlanArtifact) = await DeclaredFileAsync(
            rootDirectory,
            rootManifest,
            "inventory-plan.json",
            ct
        );
        using var rootPlanDocument = JsonDocument.Parse(rootPlanBytes);
        if (
            !StringComparer.OrdinalIgnoreCase.Equals(
                rootManifest.GetProperty("planSha256").GetString(),
                rootPlanArtifact.Sha256
            )
        )
        {
            throw new InvalidDataException(
                "Local root manifest differs from its original inventory plan."
            );
        }
        var rootPlan = rootPlanDocument.RootElement;
        var root = rootPlan.GetProperty("metadataRoot").GetString()!;
        var artist = rootPlan.GetProperty("artistUuid").GetGuid();
        if (rootPlan.GetProperty("provider").GetString() != "local")
        {
            throw new InvalidDataException("Root capture is not Local metadata.");
        }
        RequireUrl(rootPlan.GetProperty("url").GetString()!, root, null);
        var (rootBytes, rootBody) = await DeclaredFileAsync(
            rootDirectory,
            rootManifest,
            "local-root-response.json",
            ct
        );

        var (manifest, manifestArtifact) = await ReadJsonAsync(
            Path.Combine(directory, "manifest.json"),
            ct
        );
        if (
            manifest.GetProperty("version").GetString() != "native-local-credit-fields-result-v1"
            || manifest.GetProperty("state").GetString()
                != "completed_capture_with_explicit_outcomes"
        )
        {
            throw new InvalidDataException(
                "A completed retained Local directory capture is required."
            );
        }
        var (planBytes, planArtifact) = await DeclaredFileAsync(
            directory,
            manifest,
            "selection-plan.json",
            ct
        );
        using var planDocument = JsonDocument.Parse(planBytes);
        var plan = planDocument.RootElement;
        if (
            plan.GetProperty("version").GetString() != "native-local-credit-fields-plan-v1"
            || plan.GetProperty("provider").GetString() != "local"
            || plan.GetProperty("metadataRoot").GetString() != root
            || plan.GetProperty("artistUuid").GetGuid() != artist
            || !StringComparer.OrdinalIgnoreCase.Equals(
                plan.GetProperty("inputManifestSha256").GetString(),
                rootManifestArtifact.Sha256
            )
        )
        {
            throw new InvalidDataException("Local root and directory capture identities disagree.");
        }
        var (receiptBytes, receiptArtifact) = await DeclaredFileAsync(
            directory,
            manifest,
            "request-outcomes.json",
            ct
        );
        using var receiptDocument = JsonDocument.Parse(receiptBytes);
        var requests = plan.GetProperty("requests")
            .EnumerateArray()
            .ToDictionary(value => value.GetProperty("index").GetInt32());
        if (requests.Count is < 1 or > 10_000)
        {
            throw new InvalidDataException(
                "Local admission requires between one and 10,000 directory responses."
            );
        }
        var declarations = manifest
            .GetProperty("responseArtifacts")
            .EnumerateArray()
            .ToDictionary(value => value.GetProperty("path").GetString()!, StringComparer.Ordinal);
        var expectedSources = await ReadSelectionHintsAsync(directory, manifest, ct);
        var admittedAt = clock.GetUtcNow().UtcDateTime;
        var rootInput = Revision(
            new("local", "metadata_root", root),
            rootBytes,
            rootBody.Sha256,
            admittedAt
        );
        var inputs = new List<DirectoryCapture>();
        var seen = new HashSet<int>();
        var directories = new HashSet<string>(StringComparer.Ordinal);
        long totalBytes = rootBytes.LongLength;
        foreach (var receipt in receiptDocument.RootElement.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            var index = receipt.GetProperty("index").GetInt32();
            if (
                !seen.Add(index)
                || !requests.TryGetValue(index, out var selected)
                || receipt.GetProperty("httpStatus").GetInt32() != 200
                || receipt.GetProperty("state").GetString() != "captured"
            )
            {
                throw new InvalidDataException(
                    "Local directory receipts must reconcile with successful selected requests."
                );
            }
            var name = selected.GetProperty("directory").GetString()!;
            var url = selected.GetProperty("url").GetString()!;
            if (
                !directories.Add(name)
                || receipt.GetProperty("directory").GetString() != name
                || receipt.GetProperty("url").GetString() != url
            )
            {
                throw new InvalidDataException(
                    "Local directory receipt identity differs from selection."
                );
            }
            RequireUrl(url, root, name);
            var response = receipt.GetProperty("response");
            var path = response.GetProperty("path").GetString()!;
            if (
                !declarations.TryGetValue(path, out var declaration)
                || !JsonElement.DeepEquals(response, declaration)
            )
            {
                throw new InvalidDataException(
                    "Local response declaration differs from its manifest."
                );
            }
            var bytes = response.GetProperty("bytes").GetInt64();
            totalBytes = checked(totalBytes + bytes);
            if (totalBytes > 128 * 1024 * 1024)
            {
                throw new InvalidDataException("Local retained inputs exceed 128 MiB.");
            }
            var bodyPath = ContainedPath(Path.Combine(directory, "responses"), path);
            var hash = response.GetProperty("sha256").GetString()!;
            var payload = await ReadVerifiedAsync(bodyPath, bytes, hash, ct);
            var input = Revision(
                NormalizationProviderResources.LocalDirectory(root, name),
                payload,
                hash,
                admittedAt
            );
            inputs.Add(
                new(
                    name,
                    url,
                    index,
                    expectedSources.GetValueOrDefault(path),
                    input,
                    new(bodyPath, bytes, hash)
                )
            );
        }
        if (
            seen.Count != requests.Count
            || seen.Count != declarations.Count
            || seen.Count != manifest.GetProperty("requestCount").GetInt32()
        )
        {
            throw new InvalidDataException("Local directory receipt counts do not reconcile.");
        }
        return new(
            rootInput,
            artist,
            rootManifestArtifact,
            rootBody,
            manifestArtifact,
            planArtifact,
            receiptArtifact,
            inputs
        );
    }

    private static async Task<Dictionary<string, Guid?>> ReadSelectionHintsAsync(
        string directory,
        JsonElement manifest,
        CancellationToken ct
    )
    {
        var (bytes, _) = await DeclaredFileAsync(directory, manifest, "native-fields.jsonl", ct);
        var hints = new Dictionary<string, Guid?>(StringComparer.Ordinal);
        using var reader = new StringReader(System.Text.Encoding.UTF8.GetString(bytes));
        while (reader.ReadLine() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var row = document.RootElement;
            var source = row.GetProperty("source_uuid").GetGuid();
            foreach (var response in row.GetProperty("native_responses").EnumerateArray())
            {
                var path = response.GetProperty("path").GetString()!;
                hints[path] =
                    hints.TryGetValue(path, out var prior) && prior != source ? null : source;
            }
        }
        return hints;
    }

    private static NormalizationInputRevision Revision(
        NormalizationResourceKey resource,
        byte[] bytes,
        string sha256,
        DateTime admittedAt
    ) =>
        new()
        {
            Uuid = Guid.NewGuid(),
            Resource = resource,
            Payload = bytes,
            Sha256 = sha256,
            MediaType = "application/json",
            CaptureVersion = "local-http-json-v1",
            CapturedAt = admittedAt,
        };

    private static void RequireUrl(string url, string root, string? directory)
    {
        var uri = new Uri(url);
        var expected = "/" + root + (directory == null ? "" : "/" + directory) + "/metadata.json";
        if (
            uri.Scheme != "https"
            || uri.Host != "audio.relisten.net"
            || uri.UserInfo.Length != 0
            || uri.Query.Length != 0
            || uri.Fragment.Length != 0
            || Uri.UnescapeDataString(uri.AbsolutePath) != expected
        )
        {
            throw new InvalidDataException(
                "Local receipt URL differs from its root/directory resource."
            );
        }
    }

    private static async Task<(JsonElement Json, RetainedHttpArtifact Artifact)> ReadJsonAsync(
        string path,
        CancellationToken ct
    )
    {
        var artifact = await FingerprintAsync(path, ct);
        using var document = JsonDocument.Parse(
            await ReadVerifiedAsync(path, artifact.Bytes, artifact.Sha256, ct)
        );
        return (document.RootElement.Clone(), artifact);
    }

    private static async Task<(byte[] Bytes, RetainedHttpArtifact Artifact)> DeclaredFileAsync(
        string directory,
        JsonElement manifest,
        string name,
        CancellationToken ct
    )
    {
        var declaration = manifest
            .GetProperty("files")
            .EnumerateArray()
            .Single(value => value.GetProperty("path").GetString() == name);
        var path = ContainedPath(directory, name);
        var bytes = declaration.GetProperty("bytes").GetInt64();
        var hash = declaration.GetProperty("sha256").GetString()!;
        return (await ReadVerifiedAsync(path, bytes, hash, ct), new(path, bytes, hash));
    }

    private sealed record LocalCapture(
        NormalizationInputRevision Root,
        Guid ArtistUuid,
        RetainedHttpArtifact RootManifest,
        RetainedHttpArtifact RootBody,
        RetainedHttpArtifact Manifest,
        RetainedHttpArtifact Plan,
        RetainedHttpArtifact Receipts,
        IReadOnlyList<DirectoryCapture> Directories
    );

    private sealed record DirectoryCapture(
        string Directory,
        string Url,
        int Ordinal,
        Guid? ExpectedSourceUuid,
        NormalizationInputRevision Input,
        RetainedHttpArtifact Body
    );
}
