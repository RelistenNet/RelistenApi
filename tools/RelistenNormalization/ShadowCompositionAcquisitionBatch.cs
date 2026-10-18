using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>A local file pinned before any provider acquisition can begin.</summary>
public sealed record ShadowCompositionAcquisitionFile(string Path, string Sha256);

/// <summary>
/// Operator input for a bounded composition acquisition. Paths are relative to this
/// request file. Retained input files contain complete NormalizationInputRevision
/// values; original command files contain arrays of exact durable commands.
/// </summary>
public sealed record ShadowCompositionAcquisitionRequest(
    IReadOnlyList<ShadowCompositionAcquisitionFile> CatalogSnapshots,
    IReadOnlyList<ShadowCompositionAcquisitionFile> RetainedInputs,
    ShadowCompositionAcquisitionLimits Limits,
    ShadowCompositionAcquisitionFile? OriginalCommands = null
);

/// <summary>
/// Authenticates operator files and reads catalog seeds before handing transport
/// to the durable acquisition runner. This class does not interpret search results.
/// </summary>
public sealed class ShadowCompositionAcquisitionBatch(IShadowCompositionAcquisitionClient client)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public async Task<ShadowCompositionAcquisitionCheckpoint> RunAsync(
        string requestPath,
        string outputDirectory,
        CancellationToken ct = default
    )
    {
        requestPath = System.IO.Path.GetFullPath(requestPath);
        var request =
            JsonSerializer.Deserialize<ShadowCompositionAcquisitionRequest>(
                await File.ReadAllBytesAsync(requestPath, ct),
                Json
            ) ?? throw new InvalidDataException("A composition acquisition request is required.");
        var directory = System.IO.Path.GetDirectoryName(requestPath)!;

        // Verify the complete selection before even consulting durable provider state.
        // A modified last file must not permit requests for an earlier valid prefix.
        var files = request
            .CatalogSnapshots.Concat(request.RetainedInputs)
            .Concat(request.OriginalCommands is { } original ? [original] : []);
        foreach (var file in files)
        {
            await VerifyAsync(file, directory, ct);
        }

        var seeds = ShadowCompositionAcquisition.ReadCatalogSeeds(
            request.CatalogSnapshots.SelectMany(file =>
                ReadSnapshot(Resolve(directory, file.Path), ct)
            )
        );
        var inputs = new List<NormalizationInputRevision>();
        foreach (var file in request.RetainedInputs)
        {
            inputs.Add(
                JsonSerializer.Deserialize<NormalizationInputRevision>(
                    await File.ReadAllBytesAsync(Resolve(directory, file.Path), ct),
                    Json
                ) ?? throw new InvalidDataException("A retained input revision is required.")
            );
        }
        var commands = request.OriginalCommands is { } commandsFile
            ? JsonSerializer.Deserialize<ShadowCompositionAcquisitionCommand[]>(
                await File.ReadAllBytesAsync(Resolve(directory, commandsFile.Path), ct),
                Json
            ) ?? throw new InvalidDataException("Original commands must be an array.")
            : null;

        return await new ShadowCompositionAcquisitionRunner(client).RunAsync(
            seeds,
            inputs,
            outputDirectory,
            request.Limits,
            commands,
            ct
        );
    }

    private static async Task VerifyAsync(
        ShadowCompositionAcquisitionFile file,
        string directory,
        CancellationToken ct
    )
    {
        await using var stream = File.OpenRead(Resolve(directory, file.Path));
        var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
        if (!string.Equals(actual, file.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Pinned composition acquisition file changed: " + file.Path
            );
        }
    }

    private static IEnumerable<JsonElement> ReadSnapshot(string path, CancellationToken ct)
    {
        using var file = File.OpenRead(path);
        using Stream stream = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? new GZipStream(file, CompressionMode.Decompress)
            : file;
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            ct.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(line);
            yield return document.RootElement;
        }
    }

    private static string Resolve(string directory, string path) =>
        System.IO.Path.GetFullPath(path, directory);
}
