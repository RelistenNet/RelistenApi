using System.Text.Json;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Applies saved knowledge to a trusted completed file-title replay. The music
/// projection owns the complete shared resolver context; retained replay titles
/// supply additional observations without changing files, source bodies or IDs.
/// </summary>
public sealed class ReviewedFileTitleProjection
{
    private readonly ReviewedMusicProjection music;
    private readonly FileTitleReplayInput input;
    private readonly ExpertKnowledgeSnapshot? baseline;
    private readonly Dictionary<Guid, ArtistTitles> artists = new();
    private readonly SemaphoreSlim gate = new(1);
    private readonly Dictionary<string, (long Bytes, DateTime Modified)> stamps;
    private string? currentKnowledge;
    private ReviewedFileTitleChange[] changes = [];

    private ReviewedFileTitleProjection(
        ReviewedMusicProjection music,
        FileTitleReplayInput input,
        ExpertKnowledgeSnapshot? baseline,
        string inputSha256,
        string outputSha256,
        IEnumerable<string> files
    )
    {
        this.music = music;
        this.input = input;
        this.baseline = baseline;
        InputManifestSha256 = inputSha256;
        OutputManifestSha256 = outputSha256;
        stamps = files.Distinct().ToDictionary(path => path, Stamp);
    }

    public string InputManifestSha256 { get; }
    public string OutputManifestSha256 { get; }

    public static async Task<ReviewedFileTitleProjection> OpenAsync(
        ReviewedMusicProjection music,
        string inputPath,
        string inputSha256,
        string outputPath,
        string outputSha256,
        CancellationToken ct = default
    )
    {
        inputPath = Path.GetFullPath(inputPath);
        outputPath = Path.GetFullPath(outputPath);
        foreach (var (path, sha) in new[] { (inputPath, inputSha256), (outputPath, outputSha256) })
        {
            if (new FileInfo(path).Length > 16 * 1024 * 1024)
            {
                throw new InvalidDataException("A retained replay manifest exceeds 16 MiB.");
            }
            await MusicRepertoireConsolidationBatch.RequireFileAsync(
                path,
                new FileInfo(path).Length,
                sha,
                ct
            );
        }
        var input =
            JsonSerializer.Deserialize<FileTitleReplayInput>(
                await File.ReadAllBytesAsync(inputPath, ct),
                ShadowCatalogRun.Json
            ) ?? throw new InvalidDataException("A retained replay input is required.");
        var output =
            JsonSerializer.Deserialize<FileTitleReplayOutput>(
                await File.ReadAllBytesAsync(outputPath, ct),
                ShadowCatalogRun.Json
            ) ?? throw new InvalidDataException("A retained replay output is required.");
        if (
            input.Version != FileTitleReplayBatch.InputVersion
            || input.State != "completed"
            || output.Version != FileTitleReplayBatch.OutputVersion
            || output.State != "completed"
            || output.InputManifestSha256 != inputSha256
            || output.InputManifest.Sha256 != inputSha256
            || output.FileReplayVersion != RetainedFileTitleReplay.Version
            || input.MusicInputManifest.Sha256 != music.InputManifestSha256
            || output.MusicInputManifest.Sha256 != music.InputManifestSha256
            || output.ExtractorVersion != TrackTitleExtractor.Version
            || output.ResolverVersion != MusicRepertoireConsolidator.Version
            || output.Titles.Rows != input.Titles.Rows
            || output.FileMap != input.FileMap
        )
        {
            throw new InvalidDataException(
                "Live replay corrections require matching completed music and file-title bundles."
            );
        }
        var files = new List<string> { inputPath, outputPath };
        foreach (
            var artifact in new[]
            {
                input.MusicInputManifest,
                input.Titles,
                input.FileMap,
                output.Titles,
            }
        )
        {
            await MusicRepertoireConsolidationBatch.RequireFileAsync(
                artifact.Path,
                artifact.Bytes,
                artifact.Sha256,
                ct
            );
            files.Add(artifact.Path);
        }
        ExpertKnowledgeSnapshot? baseline = null;
        if (output.KnowledgeSnapshot is { } retained)
        {
            await MusicRepertoireConsolidationBatch.RequireFileAsync(
                retained.Path,
                retained.Bytes,
                retained.Sha256,
                ct
            );
            baseline =
                JsonSerializer.Deserialize<ExpertKnowledgeSnapshot>(
                    await File.ReadAllBytesAsync(retained.Path, ct),
                    ShadowCatalogRun.Json
                ) ?? throw new InvalidDataException("The replay baseline knowledge is missing.");
            _ = new ExpertKnowledgeResolver(baseline);
            if (baseline.Sha256 != output.KnowledgeSha256)
            {
                throw new InvalidDataException(
                    "The replay baseline knowledge differs from its pin."
                );
            }
            files.Add(retained.Path);
        }
        else if (output.KnowledgeSha256 != null)
        {
            throw new InvalidDataException(
                "The replay must retain its baseline knowledge snapshot."
            );
        }
        return new(music, input, baseline, inputSha256, outputSha256, files);
    }

    public async Task<ReviewedFileTitlePage> ReadAsync(
        ExpertKnowledgeSnapshot snapshot,
        int offset = 0,
        CancellationToken ct = default
    )
    {
        if (offset < 0)
        {
            throw new ArgumentException("A nonnegative replay projection offset is required.");
        }
        _ = new ExpertKnowledgeResolver(snapshot);
        await gate.WaitAsync(ct);
        try
        {
            foreach (var (path, expected) in stamps)
            {
                if (Stamp(path) != expected)
                {
                    throw new IOException(
                        "The retained replay changed. Reload a matching completed bundle."
                    );
                }
            }
            if (currentKnowledge != snapshot.Sha256)
            {
                var affected = snapshot
                    .Rules.Concat(baseline?.Rules ?? [])
                    .Where(rule => rule.Definition.Domain == "songs" && rule.State != "retracted")
                    .Select(rule =>
                        rule.Definition.Selector.ArtistUuid
                        ?? throw new InvalidDataException(
                            "A song correction needs an exact artist scope."
                        )
                    )
                    .Distinct()
                    .Order()
                    .ToArray();
                var next = new List<ReviewedFileTitleChange>();
                foreach (var artist in affected)
                {
                    if (!artists.TryGetValue(artist, out var context))
                    {
                        context = await ReadArtistAsync(artist, ct);
                        artists[artist] = context;
                    }
                    var resolved = await music.ResolveFileTitlesAsync(
                        artist,
                        snapshot,
                        context.Titles,
                        ct
                    );
                    for (var index = 0; index < resolved.Count; index++)
                    {
                        var json = JsonSerializer.Serialize(resolved[index], ShadowCatalogRun.Json);
                        if (json != context.Baseline[index])
                        {
                            next.Add(new(context.Titles[index].TitleId, resolved[index]));
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

    private async Task<ArtistTitles> ReadArtistAsync(Guid artist, CancellationToken ct)
    {
        var titles = new List<FileTitleReplayTitle>();
        await foreach (
            var title in MusicRepertoireConsolidationBatch.RowsAsync<FileTitleReplayTitle>(
                input.Titles.Path,
                ct
            )
        )
        {
            if (title.ArtistUuid == artist)
            {
                titles.Add(title);
            }
        }
        var resolved = await music.ResolveFileTitlesAsync(artist, baseline, titles, ct);
        return new(
            titles,
            resolved
                .Select(value => JsonSerializer.Serialize(value, ShadowCatalogRun.Json))
                .ToArray()
        );
    }

    private ReviewedFileTitlePage Page(string knowledgeSha256, int offset)
    {
        var page = new ReviewedFileTitlePage(
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
        var items = new List<ReviewedFileTitleChange>();
        foreach (var change in changes.Skip(offset).Take(100))
        {
            var bytes =
                JsonSerializer.SerializeToUtf8Bytes(change, ShadowCatalogRun.Json).Length
                + (items.Count == 0 ? 0 : 1);
            if (bytes > remaining)
            {
                if (items.Count == 0)
                {
                    throw new IOException(
                        "One replayed title exceeds the complete response limit."
                    );
                }
                break;
            }
            items.Add(change);
            remaining -= bytes;
        }
        return page with { Items = items };
    }

    private static (long Bytes, DateTime Modified) Stamp(string path)
    {
        var info = new FileInfo(path);
        return (info.Length, info.LastWriteTimeUtc);
    }

    private sealed record ArtistTitles(
        IReadOnlyList<FileTitleReplayTitle> Titles,
        IReadOnlyList<string> Baseline
    );
}

/// <summary>A changed interpretation of one retained title; original file ownership is unchanged.</summary>
public sealed record ReviewedFileTitleChange(long TitleId, ReviewedMusicChange Correspondence);

public sealed record ReviewedFileTitlePage(
    string InputManifestSha256,
    string OutputManifestSha256,
    string KnowledgeSha256,
    int Total,
    int Offset,
    IReadOnlyList<ReviewedFileTitleChange> Items
);
