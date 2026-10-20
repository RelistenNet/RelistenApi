using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Resolves a frozen complete descriptor population using the shared dictionary
/// policy. The input exporter owns original witness validation; its externally
/// pinned manifest binds those witnesses and both metadata streams. Checkpoints
/// belong to this exact input/version tuple, never to a refreshed catalogue.
/// </summary>
public sealed class MusicRepertoireConsolidationBatch
{
    public const string Version = "music-consolidation-output-v1";
    private const int MaximumInputLineCharacters = 1024 * 1024;
    private const long MaximumRows = 2_000_000;
    private const long MaximumMetadataBytes = 2L * 1024 * 1024 * 1024;
    private const long MaximumOutputBytes = 4L * 1024 * 1024 * 1024;
    private const long MinimumFreeBytes = 11L * 1024 * 1024 * 1024 / 2;
    private const int CheckpointRows = 2000;

    /// <summary>
    /// Opens the same complete, authenticated title population used by the batch.
    /// File replay and reviewed refreshes must not build a smaller dictionary from
    /// whichever files happen to be selected for a request.
    /// </summary>
    public async Task<MusicRepertoireConsolidator> LoadResolverAsync(
        string manifestPath,
        string manifestSha256,
        CancellationToken ct = default,
        string? knowledgePath = null,
        string? knowledgeSha256 = null
    )
    {
        var input = await AuthenticateInputAsync(
            Path.GetFullPath(manifestPath),
            manifestSha256,
            ct
        );
        var knowledge = await ReadKnowledgeAsync(knowledgePath, knowledgeSha256, ct);
        return await ObserveCompletePopulationAsync(input, knowledge?.Snapshot, ct);
    }

    public async Task<MusicConsolidationOutput> RunAsync(
        string manifestPath,
        string outputDirectory,
        string manifestSha256,
        bool resume = false,
        CancellationToken ct = default,
        string? knowledgePath = null,
        string? knowledgeSha256 = null
    )
    {
        manifestPath = Path.GetFullPath(manifestPath);
        outputDirectory = Path.GetFullPath(outputDirectory);
        var input = await AuthenticateInputAsync(manifestPath, manifestSha256, ct);
        var knowledge = await ReadKnowledgeAsync(knowledgePath, knowledgeSha256, ct);
        var methods = new Dictionary<string, string>
        {
            [nameof(MusicRepertoireConsolidator)] = MusicRepertoireConsolidator.Version,
            [nameof(TrackTitleExtractor)] = TrackTitleExtractor.Version,
            [nameof(MusicRepertoireConsolidationBatch)] = Version,
        };
        if (knowledge != null)
        {
            methods[nameof(ReviewedRepertoireCorrespondences)] =
                ReviewedRepertoireCorrespondences.Version + "/" + knowledge.Snapshot.Sha256;
            methods["reviewedKnowledgeFileSha256"] = knowledge.Artifact.Sha256;
        }
        var completionPath = Path.Combine(outputDirectory, "manifest.json");
        if (resume && File.Exists(completionPath))
        {
            return await ReadCompletedAsync(completionPath, manifestSha256, knowledge, ct);
        }

        // Rebuild only the compact deterministic context on resume. Dictionary
        // and preferred spelling decisions must see the same complete population
        // before even the first resumed descriptor is interpreted.
        var resolver = await ObserveCompletePopulationAsync(input, knowledge?.Snapshot, ct);
        CheckResources(Path.GetDirectoryName(outputDirectory)!, outputDirectory);
        await using var run = new ShadowCatalogRun(
            outputDirectory,
            manifestSha256,
            methods,
            resume
        );
        var completed = run.CompletedLine;
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        var acknowledged = new HashSet<string>(StringComparer.Ordinal);
        if (completed > input.Descriptors.Rows)
        {
            throw new InvalidDataException(
                "The checkpoint exceeds the frozen descriptor population."
            );
        }
        if (resume)
        {
            long observed = 0;
            await foreach (
                var descriptor in RowsAsync<MusicRepertoireDescriptor>(input.Descriptors.Path, ct)
            )
            {
                var result = Result(descriptor, resolver.Resolve(descriptor));
                expected.Add(result.InputKey, MeasureResult(result, out _));
                if (++observed <= completed)
                {
                    acknowledged.Add(result.InputKey);
                }
            }
            await RequireOutputCoverageAsync(outputDirectory, expected, acknowledged, ct);
        }
        try
        {
            long line = 0;
            await foreach (
                var descriptor in RowsAsync<MusicRepertoireDescriptor>(input.Descriptors.Path, ct)
            )
            {
                line++;
                if (line <= completed)
                {
                    continue;
                }
                var correspondence = resolver.Resolve(descriptor);
                var result = Result(descriptor, correspondence);
                var resultHash = MeasureResult(result, out var encodedBytes);
                RequireFreeSpace(outputDirectory, encodedBytes);
                if (!resume)
                {
                    expected.Add(result.InputKey, resultHash);
                }
                await run.WriteInputAsync([result]);
                completed = line;
                if (line % CheckpointRows == 0)
                {
                    await run.SaveAsync(line, "running", Population(input), ct);
                    CheckResources(outputDirectory, outputDirectory);
                }
            }
            await AuthenticateInputAsync(manifestPath, manifestSha256, ct);
            await ReadKnowledgeAsync(knowledgePath, knowledgeSha256, ct);
            await run.SaveAsync(completed, "completed", Population(input), ct);
            CheckResources(outputDirectory, outputDirectory);
            await RequireOutputCoverageAsync(
                outputDirectory,
                expected,
                expected.Keys.ToHashSet(StringComparer.Ordinal),
                ct
            );
            var output = await DescribeOutputAsync(outputDirectory, manifestSha256, completed, ct);
            if (knowledge != null)
            {
                output = output with
                {
                    KnowledgeSha256 = knowledge.Snapshot.Sha256,
                    KnowledgeSnapshot = knowledge.Artifact,
                };
            }
            await WriteCompletionAsync(completionPath, output, ct);
            return output;
        }
        catch
        {
            // Flush completed rows only. A failure before WriteInputAsync returns
            // leaves that descriptor unacknowledged, so exact resume repeats it.
            // No completion manifest is written on cancellation or capacity failure.
            using var audit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await run.SaveAsync(completed, "interrupted", Population(input), audit.Token);
            }
            catch { }
            throw;
        }
    }

    private sealed record ReviewedKnowledge(
        ExpertKnowledgeSnapshot Snapshot,
        MusicConsolidationArtifact Artifact
    );

    private static async Task<ReviewedKnowledge?> ReadKnowledgeAsync(
        string? path,
        string? sha256,
        CancellationToken ct
    )
    {
        if (path == null && sha256 == null)
        {
            return null;
        }
        if (path == null || sha256 == null)
        {
            throw new InvalidDataException(
                "Reviewed knowledge requires both a snapshot path and an external SHA256."
            );
        }
        path = Path.GetFullPath(path);
        var length = new FileInfo(path).Length;
        if (length > 16 * 1024 * 1024)
        {
            throw new InvalidDataException("The reviewed knowledge snapshot exceeds 16 MiB.");
        }
        await RequireFileAsync(path, length, sha256, ct);
        var snapshot =
            JsonSerializer.Deserialize<ExpertKnowledgeSnapshot>(
                await File.ReadAllBytesAsync(path, ct),
                ShadowCatalogRun.Json
            ) ?? throw new InvalidDataException("A reviewed knowledge snapshot is required.");
        // The shared resolver authenticates the policy version and complete rule hash.
        _ = new ExpertKnowledgeResolver(snapshot);
        await RequireFileAsync(path, length, sha256, ct);
        return new(snapshot, new(path, length, sha256, 1));
    }

    internal static async Task<MusicConsolidationInput> AuthenticateInputAsync(
        string manifestPath,
        string expectedSha,
        CancellationToken ct
    )
    {
        if (new FileInfo(manifestPath).Length > 16 * 1024 * 1024)
        {
            throw new InvalidDataException("The music input manifest exceeds 16 MiB.");
        }
        var bytes = await File.ReadAllBytesAsync(manifestPath, ct);
        if (
            !string.Equals(
                Convert.ToHexStringLower(SHA256.HashData(bytes)),
                expectedSha,
                StringComparison.Ordinal
            )
        )
        {
            throw new InvalidDataException(
                "The music input manifest differs from its external pin."
            );
        }
        var input =
            JsonSerializer.Deserialize<MusicConsolidationInput>(bytes, ShadowCatalogRun.Json)
            ?? throw new InvalidDataException("A music input manifest is required.");
        if (input.Version != "music-consolidation-input-v1" || input.State != "completed")
        {
            throw new InvalidDataException("A completed music descriptor export is required.");
        }
        if (
            input.Descriptors.Rows is < 0 or > MaximumRows
            || input.DictionaryNames.Rows is < 0 or > MaximumRows
        )
        {
            throw new InvalidDataException("The complete music population exceeds the row bound.");
        }
        var directory = Path.GetDirectoryName(manifestPath)!;
        input = input with
        {
            Descriptors = input.Descriptors with
            {
                Path = Path.GetFullPath(input.Descriptors.Path, directory),
            },
            DictionaryNames = input.DictionaryNames with
            {
                Path = Path.GetFullPath(input.DictionaryNames.Path, directory),
            },
        };
        foreach (var artifact in new[] { input.Descriptors, input.DictionaryNames })
        {
            if (artifact.Bytes is < 0 or > MaximumMetadataBytes)
            {
                throw new InvalidDataException("A music metadata stream exceeds 2 GiB.");
            }
            await RequireFileAsync(artifact.Path, artifact.Bytes, artifact.Sha256, ct);
        }
        await RequireFileAsync(
            input.Catalogue.Path,
            input.Catalogue.Bytes,
            input.Catalogue.Sha256,
            ct
        );
        return input;
    }

    private static async Task<MusicRepertoireConsolidator> ObserveCompletePopulationAsync(
        MusicConsolidationInput input,
        ExpertKnowledgeSnapshot? knowledge,
        CancellationToken ct
    )
    {
        var resolver = new MusicRepertoireConsolidator(knowledge);
        var sources = input
            .Sources.Select(source => (source.Scope, Path.GetFullPath(source.Run)))
            .ToHashSet();
        var chunks = input.WitnessChunks.ToDictionary(
            chunk => (Path.GetFullPath(chunk.Run), chunk.Chunk),
            chunk => chunk.Sha256
        );
        long names = 0;
        await foreach (var name in RowsAsync<MusicDictionaryName>(input.DictionaryNames.Path, ct))
        {
            ValidateWitness(name.Scope, name.ArtistUuid, name.Witness, sources, chunks, true);
            resolver.ObserveDictionary(name);
            if (++names > input.DictionaryNames.Rows)
            {
                throw new InvalidDataException(
                    "The dictionary stream exceeds its declared population."
                );
            }
        }
        if (names != input.DictionaryNames.Rows)
        {
            throw new InvalidDataException("The dictionary stream is incomplete.");
        }
        await foreach (
            var descriptor in RowsAsync<MusicRepertoireDescriptor>(input.Descriptors.Path, ct)
        )
        {
            ValidateWitness(
                descriptor.Scope,
                descriptor.ArtistUuid,
                descriptor.Witness,
                sources,
                chunks,
                false
            );
            if ((descriptor.Title != null) != (descriptor.Witness.ValuePointer != null))
            {
                throw new InvalidDataException(
                    "A descriptor title must retain its exact value pointer."
                );
            }
            resolver.ObserveDescriptor(descriptor);
            if (resolver.DescriptorCount > input.Descriptors.Rows)
            {
                throw new InvalidDataException(
                    "The descriptor stream exceeds its declared population."
                );
            }
        }
        if (resolver.DescriptorCount != input.Descriptors.Rows)
        {
            throw new InvalidDataException("The descriptor stream is incomplete.");
        }
        resolver.CompleteObservation();
        return resolver;
    }

    private static void ValidateWitness(
        string scope,
        Guid artist,
        MusicTitleWitness witness,
        HashSet<(string Scope, string Run)> sources,
        Dictionary<(string Run, string Chunk), string> chunks,
        bool dictionary
    )
    {
        if (
            artist == Guid.Empty
            || !sources.Contains((scope, Path.GetFullPath(witness.Run)))
            || witness.Line < 1
            || !chunks.TryGetValue((Path.GetFullPath(witness.Run), witness.Chunk), out var sha)
            || sha != witness.ChunkSha256
        )
        {
            throw new InvalidDataException("A title witness is outside the frozen source scope.");
        }
        if (dictionary && witness.ValuePointer == null)
        {
            throw new InvalidDataException(
                "A dictionary spelling must retain its exact value pointer."
            );
        }
        if (witness.DocumentKind == "catalog_context")
        {
            if (!dictionary || witness.RecordId != 0 || witness.ValuePointer == null)
            {
                throw new InvalidDataException(
                    "Only literal dictionary context may use a raw context witness."
                );
            }
        }
        else if (witness.DocumentKind != "retained_result" || witness.RecordId < 1)
        {
            throw new InvalidDataException(
                "A retained title witness lacks its original result coordinate."
            );
        }
    }

    internal static async IAsyncEnumerable<T> RowsAsync<T>(
        string path,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        await foreach (
            var line in ShadowJsonLines.ReadTextAsync(
                path,
                MaximumInputLineCharacters,
                MaximumMetadataBytes,
                ct
            )
        )
        {
            yield return JsonSerializer.Deserialize<T>(line, ShadowCatalogRun.Json)
                ?? throw new InvalidDataException("A music metadata row is null.");
        }
    }

    private static ShadowCatalogResult Result(
        MusicRepertoireDescriptor descriptor,
        MusicRepertoireCorrespondence correspondence
    ) =>
        new(
            "repertoire_correspondences",
            JsonSerializer.Serialize(new[] { descriptor.Scope, descriptor.OriginalKey }),
            "repertoire_descriptor",
            descriptor.LegacySongUuid ?? Guid.Empty,
            descriptor.ArtistUuid,
            "",
            "retained_music_descriptor",
            correspondence.Disposition,
            correspondence.Reason,
            MusicRepertoireConsolidator.Version + "/" + TrackTitleExtractor.Version,
            correspondence
                .Targets.Select(target => target.Song.EntityKey)
                .Prepend(descriptor.OriginalKey)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            correspondence,
            descriptor.Witness
        );

    private static string MeasureResult(ShadowCatalogResult result, out long encodedBytes)
    {
        using var measured = new ResultSizeStream();
        JsonSerializer.Serialize(measured, result, ShadowCatalogRun.Json);
        encodedBytes = measured.Length;
        return measured.Hash();
    }

    private static async Task RequireOutputCoverageAsync(
        string directory,
        Dictionary<string, string> expected,
        HashSet<string> required,
        CancellationToken ct
    )
    {
        var domain = Path.Combine(directory, "repertoire_correspondences");
        var paths = Directory.Exists(domain) ? Directory.EnumerateFiles(domain, "*.jsonl.gz") : [];
        foreach (var path in paths)
        {
            // Hash the original serialized envelope, rather than deserializing and
            // silently dropping unknown/tampered properties. Exact repeated chunks
            // from a crash are valid; missing acknowledged keys are never skipped.
            await foreach (
                var line in ShadowJsonLines.ReadTextAsync(
                    path,
                    32 * 1024 * 1024,
                    MaximumOutputBytes * 16,
                    ct
                )
            )
            {
                using var document = JsonDocument.Parse(line);
                var key = document.RootElement.GetProperty("inputKey").GetString()!;
                var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(line)));
                if (!expected.TryGetValue(key, out var wanted) || wanted != hash)
                {
                    throw new InvalidDataException(
                        "A retained music result differs from its frozen correspondence."
                    );
                }
                required.Remove(key);
            }
        }
        if (required.Count != 0)
        {
            throw new InvalidDataException(
                "Retained music output is missing acknowledged descriptor results."
            );
        }
    }

    private static object Population(MusicConsolidationInput input) =>
        new
        {
            descriptors = input.Descriptors.Rows,
            dictionaryNames = input.DictionaryNames.Rows,
            catalogue = input.Catalogue,
            sourceScopes = input.Sources,
            acceptedIdentity = false,
        };

    private static void CheckResources(string volumePath, string outputDirectory)
    {
        RequireFreeSpace(volumePath, 0);
        if (
            Directory.Exists(outputDirectory)
            && Directory
                .EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length) > MaximumOutputBytes
        )
        {
            throw new IOException("Music consolidation reached its 4 GiB retained-output bound.");
        }
    }

    private static void RequireFreeSpace(string volumePath, long nextRowBytes)
    {
        // The next gzip write cannot require more than its complete uncompressed
        // envelope plus framing. Reserve that before writing, not after a chunk.
        if (new DriveInfo(volumePath).AvailableFreeSpace < MinimumFreeBytes + nextRowBytes + 65536)
        {
            throw new IOException("Music consolidation paused at the 5.5 GiB free-space floor.");
        }
    }

    internal static async Task RequireFileAsync(
        string path,
        long bytes,
        string sha,
        CancellationToken ct
    )
    {
        if (new FileInfo(path).Length != bytes || await HashAsync(path, ct) != sha)
        {
            throw new InvalidDataException("A frozen music artifact changed.");
        }
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }

    private static async Task<MusicConsolidationOutput> DescribeOutputAsync(
        string directory,
        string inputSha,
        long rows,
        CancellationToken ct
    )
    {
        var chunks = new List<MusicConsolidationArtifact>();
        var domain = Path.Combine(directory, "repertoire_correspondences");
        var paths = Directory.Exists(domain)
            ? Directory.EnumerateFiles(domain, "*.jsonl.gz").Order(StringComparer.Ordinal)
            : Enumerable.Empty<string>();
        foreach (var path in paths)
        {
            await using var file = File.OpenRead(path);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            var buffer = new byte[65536];
            long count = 0;
            int read;
            while ((read = await gzip.ReadAsync(buffer, ct)) > 0)
            {
                count += buffer.AsSpan(0, read).Count((byte)'\n');
            }
            chunks.Add(
                new(
                    Path.GetRelativePath(directory, path),
                    new FileInfo(path).Length,
                    await HashAsync(path, ct),
                    count
                )
            );
        }
        var progress = Path.Combine(directory, "progress.json");
        return new(
            Version,
            true,
            inputSha,
            TrackTitleExtractor.Version,
            MusicRepertoireConsolidator.Version,
            rows,
            chunks,
            new("progress.json", new FileInfo(progress).Length, await HashAsync(progress, ct), 1)
        );
    }

    private static async Task<MusicConsolidationOutput> ReadCompletedAsync(
        string path,
        string inputSha,
        ReviewedKnowledge? knowledge,
        CancellationToken ct
    )
    {
        var output =
            JsonSerializer.Deserialize<MusicConsolidationOutput>(
                await File.ReadAllBytesAsync(path, ct),
                ShadowCatalogRun.Json
            ) ?? throw new InvalidDataException("A completed music output is required.");
        if (
            !output.Complete
            || output.Version != Version
            || output.InputManifestSha256 != inputSha
            || output.ExtractorVersion != TrackTitleExtractor.Version
            || output.ResolverVersion != MusicRepertoireConsolidator.Version
            || output.KnowledgeSha256 != knowledge?.Snapshot.Sha256
            || output.KnowledgeSnapshot?.Sha256 != knowledge?.Artifact.Sha256
        )
        {
            throw new InvalidDataException(
                "Completed replay requires the exact input and resolver versions."
            );
        }
        foreach (var artifact in output.Chunks.Append(output.Progress))
        {
            await RequireFileAsync(
                Path.Combine(Path.GetDirectoryName(path)!, artifact.Path),
                artifact.Bytes,
                artifact.Sha256,
                ct
            );
        }
        return output;
    }

    private static async Task WriteCompletionAsync(
        string path,
        MusicConsolidationOutput output,
        CancellationToken ct
    )
    {
        await File.WriteAllBytesAsync(
            path + ".writing",
            JsonSerializer.SerializeToUtf8Bytes(output, ShadowCatalogRun.Json),
            ct
        );
        File.Move(path + ".writing", path);
    }

    private sealed class ResultSizeStream : Stream
    {
        private long size;
        private readonly IncrementalHash digest = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256
        );

        public string Hash() => Convert.ToHexStringLower(digest.GetHashAndReset());

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            digest.AppendData(buffer);
            size += buffer.Length;
            if (size > 32 * 1024 * 1024)
            {
                throw new InvalidDataException("A complete music correspondence exceeds 32 MiB.");
            }
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => size;
        public override long Position
        {
            get => size;
            set => throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                digest.Dispose();
            }
            base.Dispose(disposing);
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
