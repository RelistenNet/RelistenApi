using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Resolves each distinct retained file title once against the complete music
/// population. File ownership remains in the pinned export map. Completion is
/// published only after every exported title has an output; an interrupted run
/// leaves its partial output for inspection and can be rerun into a new folder.
/// This offline pass has no database, model or network dependency.
/// </summary>
public sealed class FileTitleReplayBatch
{
    public const string InputVersion = "retained-file-title-replay-input-v1";
    public const string OutputVersion = "retained-file-title-replay-output-v1";
    private const long MaximumTitles = 4_000_000;

    public async Task<FileTitleReplayOutput> RunAsync(
        string inputManifestPath,
        string outputDirectory,
        string inputManifestSha256,
        CancellationToken ct = default,
        string? knowledgePath = null,
        string? knowledgeSha256 = null
    )
    {
        inputManifestPath = Path.GetFullPath(inputManifestPath);
        outputDirectory = Path.GetFullPath(outputDirectory);
        var input = await AuthenticateAsync(inputManifestPath, inputManifestSha256, ct);
        if (Directory.Exists(outputDirectory))
        {
            throw new InvalidDataException("File title replay requires a new output directory.");
        }

        using var duration = CancellationTokenSource.CreateLinkedTokenSource(ct);
        duration.CancelAfter(TimeSpan.FromSeconds(input.MaximumSeconds));
        ct = duration.Token;
        var resolver = await new MusicRepertoireConsolidationBatch().LoadResolverAsync(
            input.MusicInputManifest.Path,
            input.MusicInputManifest.Sha256,
            ct,
            knowledgePath,
            knowledgeSha256,
            observeAdditional: (context, cancellation) =>
                ObserveTitlesAsync(input, context, cancellation)
        );
        var knowledge = await ReadKnowledgeAsync(knowledgePath, knowledgeSha256, ct);
        var replay = new RetainedFileTitleReplay(resolver);
        Directory.CreateDirectory(outputDirectory);
        CheckStorage(input, outputDirectory);

        var (titles, outcomes) = await ResolveTitlesAsync(input, replay, outputDirectory, ct);
        var inputPin = await PinAsync(inputManifestPath, 1, ct);
        if (inputPin.Sha256 != inputManifestSha256)
        {
            throw new InvalidDataException("The replay input manifest changed during processing.");
        }
        var output = new FileTitleReplayOutput(
            OutputVersion,
            "completed",
            inputManifestSha256,
            inputPin,
            input.MusicInputManifest,
            input.FileMap,
            titles,
            TrackTitleExtractor.Version,
            MusicRepertoireConsolidator.Version,
            RetainedFileTitleReplay.Version,
            outcomes,
            knowledge?.Snapshot.Sha256,
            knowledge?.Artifact
        );
        await WriteJsonAsync(Path.Combine(outputDirectory, "manifest.json"), output, ct);
        return output;
    }

    private static async Task<(
        MusicConsolidationArtifact Titles,
        IReadOnlyDictionary<string, long> Outcomes
    )> ResolveTitlesAsync(
        FileTitleReplayInput input,
        RetainedFileTitleReplay replay,
        string outputDirectory,
        CancellationToken ct
    )
    {
        var outputPath = Path.Combine(outputDirectory, "titles.jsonl.gz");
        var partial = outputPath + ".writing";
        long processed = 0;
        var outcomes = new Dictionary<string, long>();
        await using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write))
        {
            await using var compressed = new GZipStream(
                file,
                CompressionLevel.Optimal,
                leaveOpen: true
            );
            await using var writer = new StreamWriter(compressed, new UTF8Encoding(false));
            await foreach (var title in ReadTitlesAsync(input, ct))
            {
                var correspondence = replay.ResolveTitle(
                    title.Scope,
                    title.ArtistUuid,
                    title.OriginalTitle,
                    title.Witness
                );
                await writer.WriteLineAsync(
                    JsonSerializer
                        .Serialize(
                            new FileTitleReplayCorrespondence(title.TitleId, correspondence),
                            ShadowCatalogRun.Json
                        )
                        .AsMemory(),
                    ct
                );
                processed++;
                outcomes[correspondence.Disposition] =
                    outcomes.GetValueOrDefault(correspondence.Disposition) + 1;
                if (processed % 2000 == 0)
                {
                    await writer.FlushAsync(ct);
                    CheckStorage(input, outputDirectory);
                    await WriteJsonAsync(
                        Path.Combine(outputDirectory, "progress.json"),
                        new
                        {
                            state = "running",
                            processedTitles = processed,
                            expectedTitles = input.Titles.Rows,
                            outcomes,
                        },
                        ct
                    );
                }
            }
            await writer.FlushAsync(ct);
        }
        CheckStorage(input, outputDirectory);
        File.Move(partial, outputPath);
        await MusicRepertoireConsolidationBatch.RequireFileAsync(
            input.Titles.Path,
            input.Titles.Bytes,
            input.Titles.Sha256,
            ct
        );
        return (await PinAsync(outputPath, processed, ct), outcomes);
    }

    private static async Task ObserveTitlesAsync(
        FileTitleReplayInput input,
        MusicRepertoireConsolidator resolver,
        CancellationToken ct
    )
    {
        await foreach (var title in ReadTitlesAsync(input, ct))
        {
            resolver.ObserveRetainedTitle(title.Scope, title.ArtistUuid, title.OriginalTitle);
        }
    }

    private static async IAsyncEnumerable<FileTitleReplayTitle> ReadTitlesAsync(
        FileTitleReplayInput input,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        var scopes = input
            .Readers.Select(source => (source.Scope, Path.GetFullPath(source.Run)))
            .ToHashSet();
        var chunks = input.WitnessChunks.ToDictionary(
            chunk => (Path.GetFullPath(chunk.Run), chunk.Chunk),
            chunk => chunk.Sha256
        );
        long processed = 0;
        await foreach (
            var title in MusicRepertoireConsolidationBatch.RowsAsync<FileTitleReplayTitle>(
                input.Titles.Path,
                ct
            )
        )
        {
            // The two passes authenticate the same complete ordinal sequence. Title
            // IDs remain artifact coordinates, never song or performance identities.
            if (title.TitleId != ++processed || title.TitleId > input.Titles.Rows)
            {
                throw new InvalidDataException(
                    "The retained file title population is not a complete sequence."
                );
            }
            RequireWitness(title, scopes, chunks);
            yield return title;
        }
        if (processed != input.Titles.Rows)
        {
            throw new InvalidDataException("The retained file title stream is incomplete.");
        }
    }

    private static async Task<(
        ExpertKnowledgeSnapshot Snapshot,
        MusicConsolidationArtifact Artifact
    )?> ReadKnowledgeAsync(string? path, string? sha256, CancellationToken ct)
    {
        if (path == null)
        {
            return null;
        }
        path = Path.GetFullPath(path);
        var bytes = await File.ReadAllBytesAsync(path, ct);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != sha256)
        {
            throw new InvalidDataException(
                "The replay knowledge snapshot differs from its file pin."
            );
        }
        var snapshot = JsonSerializer.Deserialize<ExpertKnowledgeSnapshot>(
            bytes,
            ShadowCatalogRun.Json
        )!;
        return (snapshot, new(path, bytes.LongLength, sha256!, 1));
    }

    private static async Task<FileTitleReplayInput> AuthenticateAsync(
        string path,
        string sha256,
        CancellationToken ct
    )
    {
        if (new FileInfo(path).Length > 16 * 1024 * 1024)
        {
            throw new InvalidDataException("A file title replay manifest exceeds 16 MiB.");
        }
        var bytes = await File.ReadAllBytesAsync(path, ct);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != sha256)
        {
            throw new InvalidDataException(
                "The file title replay input manifest differs from its external pin."
            );
        }
        var input =
            JsonSerializer.Deserialize<FileTitleReplayInput>(bytes, ShadowCatalogRun.Json)
            ?? throw new InvalidDataException("A file title replay input manifest is required.");
        if (
            input.Version != InputVersion
            || input.State != "completed"
            || input.Titles.Rows is < 0 or > MaximumTitles
            || input.MaximumSeconds is < 1 or > 86400
            || input.MaximumOutputBytes is < 1 or > 4L * 1024 * 1024 * 1024
            || input.MinimumFreeBytes < 0
        )
        {
            throw new InvalidDataException(
                "A completed, bounded retained file title export is required."
            );
        }
        foreach (var artifact in new[] { input.MusicInputManifest, input.FileMap, input.Titles })
        {
            await MusicRepertoireConsolidationBatch.RequireFileAsync(
                artifact.Path,
                artifact.Bytes,
                artifact.Sha256,
                ct
            );
        }
        return input;
    }

    private static void RequireWitness(
        FileTitleReplayTitle title,
        HashSet<(string Scope, string Run)> scopes,
        Dictionary<(string Run, string Chunk), string> chunks
    )
    {
        var witness = title.Witness;
        if (
            title.ArtistUuid == Guid.Empty
            || !scopes.Contains((title.Scope, Path.GetFullPath(witness.Run)))
            || !chunks.TryGetValue((Path.GetFullPath(witness.Run), witness.Chunk), out var sha256)
            || witness.ChunkSha256 != sha256
            || witness.Line < 1
            || witness.RecordId < 1
            || witness.DocumentKind != "retained_result"
            || witness.ValuePointer == null
        )
        {
            throw new InvalidDataException(
                "A replayed title witness is outside the retained source population."
            );
        }
    }

    private static void CheckStorage(FileTitleReplayInput input, string directory)
    {
        if (new DriveInfo(Path.GetPathRoot(directory)!).AvailableFreeSpace < input.MinimumFreeBytes)
        {
            throw new IOException("File title replay reached its free-space reserve.");
        }
        if (
            Directory.EnumerateFiles(directory).Sum(path => new FileInfo(path).Length)
            > input.MaximumOutputBytes
        )
        {
            throw new IOException("File title replay reached its output byte bound.");
        }
    }

    private static async Task<MusicConsolidationArtifact> PinAsync(
        string path,
        long rows,
        CancellationToken ct
    )
    {
        await using var source = File.OpenRead(path);
        return new(
            path,
            source.Length,
            Convert.ToHexStringLower(await SHA256.HashDataAsync(source, ct)),
            rows
        );
    }

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken ct)
    {
        await File.WriteAllTextAsync(
            path + ".writing",
            JsonSerializer.Serialize(value, ShadowCatalogRun.Json),
            ct
        );
        File.Move(path + ".writing", path, overwrite: true);
    }
}

public sealed record FileTitleReplayInput(
    string Version,
    string State,
    MusicConsolidationArtifact MusicInputManifest,
    MusicConsolidationArtifact FileMap,
    MusicConsolidationArtifact Titles,
    int MaximumSeconds,
    long MaximumOutputBytes,
    long MinimumFreeBytes
)
{
    public IReadOnlyList<MusicConsolidationSource> Readers { get; init; } = [];
    public IReadOnlyList<MusicConsolidationWitnessChunk> WitnessChunks { get; init; } = [];
}

public sealed record FileTitleReplayTitle(
    long TitleId,
    string Scope,
    Guid ArtistUuid,
    string OriginalTitle,
    MusicTitleWitness Witness
);

public sealed record FileTitleReplayCorrespondence(
    long TitleId,
    MusicRepertoireCorrespondence Correspondence
);

public sealed record FileTitleReplayOutput(
    string Version,
    string State,
    string InputManifestSha256,
    MusicConsolidationArtifact InputManifest,
    MusicConsolidationArtifact MusicInputManifest,
    MusicConsolidationArtifact FileMap,
    MusicConsolidationArtifact Titles,
    string ExtractorVersion,
    string ResolverVersion,
    string FileReplayVersion,
    IReadOnlyDictionary<string, long> Outcomes,
    string? KnowledgeSha256,
    MusicConsolidationArtifact? KnowledgeSnapshot
);
