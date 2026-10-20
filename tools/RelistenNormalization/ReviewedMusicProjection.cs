using System.Text.Json;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Reinterprets only artists touched by saved song decisions using the same complete
/// observation and resolution phases as the batch. Frozen files own evidence; this
/// service owns no decisions and never calls a reader that consumes its projection.
/// </summary>
public sealed class ReviewedMusicProjection
{
    private readonly MusicConsolidationInput input;
    private readonly ExpertKnowledgeSnapshot? baselineKnowledge;
    private readonly Dictionary<Guid, ArtistContext> artists = new();
    private readonly SemaphoreSlim gate = new(1);
    private readonly Dictionary<string, FileStamp> files;
    private string? currentKnowledge;
    private ReviewedMusicChange[] changes = [];

    private ReviewedMusicProjection(
        MusicConsolidationInput input,
        ExpertKnowledgeSnapshot? baselineKnowledge,
        string inputSha256,
        string outputSha256,
        Dictionary<string, FileStamp> files
    )
    {
        this.input = input;
        this.baselineKnowledge = baselineKnowledge;
        InputManifestSha256 = inputSha256;
        OutputManifestSha256 = outputSha256;
        this.files = files;
    }

    public string InputManifestSha256 { get; }
    public string OutputManifestSha256 { get; }

    /// <summary>Authenticate the completed source/output pair before serving any corrections.</summary>
    public static async Task<ReviewedMusicProjection> OpenAsync(
        string inputPath,
        string inputSha256,
        string outputPath,
        string outputSha256,
        CancellationToken ct = default
    )
    {
        inputPath = Path.GetFullPath(inputPath);
        outputPath = Path.GetFullPath(outputPath);
        var input = await MusicRepertoireConsolidationBatch.AuthenticateInputAsync(
            inputPath,
            inputSha256,
            ct
        );
        await MusicRepertoireConsolidationBatch.RequireFileAsync(
            outputPath,
            new FileInfo(outputPath).Length,
            outputSha256,
            ct
        );
        var output =
            JsonSerializer.Deserialize<MusicConsolidationOutput>(
                await File.ReadAllBytesAsync(outputPath, ct),
                ShadowCatalogRun.Json
            ) ?? throw new InvalidDataException("A completed music output is required.");
        if (
            !output.Complete
            || output.InputManifestSha256 != inputSha256
            || output.Version != MusicRepertoireConsolidationBatch.Version
            || output.ExtractorVersion != TrackTitleExtractor.Version
            || output.ResolverVersion != MusicRepertoireConsolidator.Version
            || output.LogicalRows != input.Descriptors.Rows
        )
        {
            throw new InvalidDataException(
                "Live song corrections require matching completed input/output and resolver versions."
            );
        }

        var paths = new List<string>
        {
            inputPath,
            outputPath,
            input.Descriptors.Path,
            input.DictionaryNames.Path,
            input.Catalogue.Path,
        };
        foreach (var artifact in output.Chunks.Append(output.Progress))
        {
            var path = Path.GetFullPath(artifact.Path, Path.GetDirectoryName(outputPath)!);
            await MusicRepertoireConsolidationBatch.RequireFileAsync(
                path,
                artifact.Bytes,
                artifact.Sha256,
                ct
            );
            paths.Add(path);
        }
        ExpertKnowledgeSnapshot? baseline = null;
        if (output.KnowledgeSnapshot is { } retained)
        {
            var path = Path.GetFullPath(retained.Path, Path.GetDirectoryName(outputPath)!);
            await MusicRepertoireConsolidationBatch.RequireFileAsync(
                path,
                retained.Bytes,
                retained.Sha256,
                ct
            );
            baseline =
                JsonSerializer.Deserialize<ExpertKnowledgeSnapshot>(
                    await File.ReadAllBytesAsync(path, ct),
                    ShadowCatalogRun.Json
                ) ?? throw new InvalidDataException("The baseline knowledge snapshot is missing.");
            _ = new ExpertKnowledgeResolver(baseline);
            if (baseline.Sha256 != output.KnowledgeSha256)
            {
                throw new InvalidDataException(
                    "The output knowledge snapshot differs from its manifest."
                );
            }
            paths.Add(path);
        }
        else if (output.KnowledgeSha256 != null)
        {
            throw new InvalidDataException(
                "Completed music output must retain its baseline knowledge."
            );
        }
        return new(
            input,
            baseline,
            inputSha256,
            outputSha256,
            paths.Distinct().ToDictionary(path => path, FileStamp.Read)
        );
    }

    /// <summary>
    /// Returns a pinned page of changed original accounts. Retractions compare against
    /// the frozen baseline too, so withdrawing a rule restores the original interpretation.
    /// </summary>
    public async Task<ReviewedMusicPage> ReadAsync(
        ExpertKnowledgeSnapshot snapshot,
        int offset = 0,
        CancellationToken ct = default
    )
    {
        if (offset < 0)
        {
            throw new ArgumentException("A nonnegative projection offset is required.");
        }
        _ = new ExpertKnowledgeResolver(snapshot);
        await gate.WaitAsync(ct);
        try
        {
            foreach (var (path, expected) in files)
            {
                if (FileStamp.Read(path) != expected)
                {
                    throw new IOException(
                        "Frozen music evidence changed. Reload a matching completed bundle."
                    );
                }
            }
            if (currentKnowledge != snapshot.Sha256)
            {
                var affected = snapshot
                    .Rules.Concat(baselineKnowledge?.Rules ?? [])
                    .Where(rule => rule.Definition.Domain == "songs")
                    .Select(rule =>
                        rule.Definition.Selector.ArtistUuid
                        ?? throw new InvalidDataException(
                            "Song review requires an exact artist scope."
                        )
                    )
                    .Distinct()
                    .Order()
                    .ToArray();
                var next = new List<ReviewedMusicChange>();
                foreach (var artist in affected)
                {
                    if (!artists.TryGetValue(artist, out var context))
                    {
                        context = await ReadArtistAsync(artist, ct);
                        artists[artist] = context;
                    }
                    var resolver = Observe(context, snapshot);
                    foreach (var descriptor in context.Descriptors)
                    {
                        ct.ThrowIfCancellationRequested();
                        var change = ReviewedMusicChange.From(resolver.Resolve(descriptor));
                        if (
                            JsonSerializer.Serialize(change, ShadowCatalogRun.Json)
                            != context.Baseline[
                                ReviewedRepertoireCorrespondences.StableKey(
                                    descriptor.Scope,
                                    descriptor.OriginalKey
                                )
                            ]
                        )
                        {
                            next.Add(change);
                        }
                    }
                }
                changes = next.ToArray();
                currentKnowledge = snapshot.Sha256;
            }
            return Page(snapshot.Sha256, offset);
        }
        finally
        {
            gate.Release();
        }
    }

    private ReviewedMusicPage Page(string knowledgeSha256, int offset)
    {
        var page = new ReviewedMusicPage(
            InputManifestSha256,
            OutputManifestSha256,
            knowledgeSha256,
            changes.Length,
            offset,
            []
        );
        var remaining =
            7 * 1024 * 1024
            - JsonSerializer.SerializeToUtf8Bytes(page, ShadowCatalogRun.Json).Length;
        var selected = new List<ReviewedMusicChange>();
        foreach (var change in changes.Skip(offset).Take(100))
        {
            var bytes =
                JsonSerializer.SerializeToUtf8Bytes(change, ShadowCatalogRun.Json).Length
                + (selected.Count == 0 ? 0 : 1);
            if (bytes > remaining)
            {
                if (selected.Count == 0)
                {
                    throw new IOException(
                        "One reviewed song exceeds the complete projection response limit."
                    );
                }
                break;
            }
            selected.Add(change);
            remaining -= bytes;
        }
        return page with { Items = selected };
    }

    private async Task<ArtistContext> ReadArtistAsync(Guid artist, CancellationToken ct)
    {
        var names = new List<MusicDictionaryName>();
        await foreach (
            var name in MusicRepertoireConsolidationBatch.RowsAsync<MusicDictionaryName>(
                input.DictionaryNames.Path,
                ct
            )
        )
        {
            if (name.ArtistUuid == artist)
            {
                names.Add(name);
            }
        }
        var descriptors = new List<MusicRepertoireDescriptor>();
        await foreach (
            var descriptor in MusicRepertoireConsolidationBatch.RowsAsync<MusicRepertoireDescriptor>(
                input.Descriptors.Path,
                ct
            )
        )
        {
            if (descriptor.ArtistUuid == artist)
            {
                descriptors.Add(descriptor);
            }
        }
        var context = new ArtistContext(names, descriptors, new());
        var resolver = Observe(context, baselineKnowledge);
        foreach (var descriptor in descriptors)
        {
            context.Baseline.Add(
                ReviewedRepertoireCorrespondences.StableKey(
                    descriptor.Scope,
                    descriptor.OriginalKey
                ),
                JsonSerializer.Serialize(
                    ReviewedMusicChange.From(resolver.Resolve(descriptor)),
                    ShadowCatalogRun.Json
                )
            );
        }
        return context;
    }

    private static MusicRepertoireConsolidator Observe(
        ArtistContext context,
        ExpertKnowledgeSnapshot? knowledge
    )
    {
        var resolver = new MusicRepertoireConsolidator(knowledge);
        foreach (var name in context.Names)
        {
            resolver.ObserveDictionary(name);
        }
        foreach (var descriptor in context.Descriptors)
        {
            resolver.ObserveDescriptor(descriptor);
        }
        resolver.CompleteObservation();
        return resolver;
    }

    private sealed record ArtistContext(
        IReadOnlyList<MusicDictionaryName> Names,
        IReadOnlyList<MusicRepertoireDescriptor> Descriptors,
        Dictionary<string, string> Baseline
    );

    private sealed record FileStamp(
        long Length,
        DateTime LastWriteTimeUtc,
        DateTime CreationTimeUtc
    )
    {
        internal static FileStamp Read(string path)
        {
            var info = new FileInfo(path);
            return new(info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc);
        }
    }
}

/// <summary>Sparse display/membership projection; exact original evidence stays in the frozen bundle.</summary>
public sealed record ReviewedMusicChange(
    string Scope,
    Guid ArtistUuid,
    string OriginalKey,
    string Disposition,
    string Reason,
    string SequenceBasis,
    IReadOnlyList<ReviewedMusicTarget> Targets
)
{
    internal static ReviewedMusicChange From(MusicRepertoireCorrespondence value) =>
        new(
            value.Scope,
            value.ArtistUuid,
            value.OriginalKey,
            value.Disposition,
            value.Reason,
            value.SequenceBasis,
            value
                .Targets.Select(target => new ReviewedMusicTarget(
                    target.Song,
                    target.Relation,
                    target.Disposition,
                    target.Reason,
                    target.Qualifiers.Select(qualifier => qualifier.Kind).ToArray()
                ))
                .ToArray()
        );
}

public sealed record ReviewedMusicTarget(
    ShadowRepertoireSong Song,
    MusicRepertoireRelation Relation,
    string Disposition,
    string Reason,
    IReadOnlyList<string> Qualifiers
);

public sealed record ReviewedMusicPage(
    string InputManifestSha256,
    string OutputManifestSha256,
    string KnowledgeSha256,
    int Total,
    int Offset,
    IReadOnlyList<ReviewedMusicChange> Items
);
