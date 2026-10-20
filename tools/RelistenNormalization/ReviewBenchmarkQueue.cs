using System.Security.Cryptography;
using System.Text.Json;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Reads a completed, hash-pinned benchmark without rewriting its evidence. V2 pages
/// contain resolved identity disagreements; raw imported-ID differences stay inspectable
/// on each retained row and do not create review work after their identities agree.
/// </summary>
public sealed class ReviewBenchmarkQueue
{
    private const long MaximumQueueBytes = 128L * 1024 * 1024;

    /// <summary>
    /// Authenticates the entire bounded queue and its artist ownership before returning
    /// one page. Artist counts cover the domain; category counts cover the selected artist;
    /// total and paging also apply the category filter. Counts never describe a truncated scan.
    /// </summary>
    public async Task<ReviewBenchmarkQueueSnapshot> ReadAsync(
        string path,
        ReviewBenchmarkQueuePage page,
        CancellationToken ct = default
    )
    {
        if (page.Offset < 0 || page.Limit is < 0 or > 25)
        {
            throw new ArgumentException("Invalid queue page bound.");
        }
        path = Path.GetFullPath(path);
        var before = new FileInfo(path);
        if (before.Name != "errors.jsonl" || !before.Exists || before.Length > MaximumQueueBytes)
        {
            throw new ArgumentException(
                "The configured queue must be a retained errors.jsonl of at most 128 MiB."
            );
        }
        var originalLength = before.Length;
        var originalModified = before.LastWriteTimeUtc;
        var header = await ReadHeaderAsync(path, ct);
        await using var input = File.OpenRead(path);
        var sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct));
        if (
            header.Declared.GetProperty("bytes").GetInt64() != originalLength
            || Text(header.Declared, "sha256") != sha
        )
        {
            throw new NormalizationConflictException(
                "The benchmark queue differs from its completion manifest."
            );
        }

        var snapshot = await ReadPageAsync(input, header, sha, page, ct);
        var after = new FileInfo(path);
        if (after.Length != originalLength || after.LastWriteTimeUtc != originalModified)
        {
            throw new NormalizationConflictException("The benchmark queue changed during paging.");
        }
        return snapshot;
    }

    private static async Task<QueueHeader> ReadHeaderAsync(string path, CancellationToken ct)
    {
        var manifestPath = Path.Combine(Path.GetDirectoryName(path)!, "manifest.json");
        if (new FileInfo(manifestPath).Length > 4 * 1024 * 1024)
        {
            throw new ArgumentException("The benchmark completion manifest exceeds four MiB.");
        }
        using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(manifestPath, ct));
        var metadata = manifest.RootElement;
        var version = Text(metadata, "version");
        var resolved = version == "artist-setlist-reference-benchmark-v2";
        if (
            (!resolved && version != "artist-setlist-reference-benchmark-v1")
            || Text(metadata, "state") != "completed"
        )
        {
            throw new ArgumentException(
                "A completed artist-setlist benchmark is required for this queue."
            );
        }
        var declarations = metadata
            .GetProperty("outputs")
            .EnumerateArray()
            .Where(item => Path.GetFullPath(Text(item, "path") ?? "") == path)
            .ToArray();
        if (declarations.Length != 1)
        {
            throw new NormalizationConflictException(
                "The benchmark manifest must declare this queue exactly once."
            );
        }
        var artists = metadata
            .GetProperty("artists")
            .EnumerateArray()
            .Select(item => item.Clone())
            .ToArray();
        var owners = artists
            .Select(item => Text(item, "artistUuid")!)
            .ToHashSet(StringComparer.Ordinal);
        if (owners.Count != artists.Length || owners.Any(owner => !Guid.TryParse(owner, out _)))
        {
            throw new ArgumentException("Benchmark artists must have distinct UUIDs.");
        }
        if (
            resolved
            && artists.Any(item =>
                !item.TryGetProperty("resolvedIdentity", out var metrics)
                || metrics.ValueKind != JsonValueKind.Object
            )
        )
        {
            throw new ArgumentException("A v2 benchmark must supply resolved artist metrics.");
        }
        return new(
            version!,
            resolved,
            declarations[0].Clone(),
            artists,
            owners,
            metadata
                .GetProperty("limitations")
                .EnumerateArray()
                .Select(item => item.GetString())
                .ToArray()
        );
    }

    private static async Task<ReviewBenchmarkQueueSnapshot> ReadPageAsync(
        FileStream input,
        QueueHeader header,
        string sha,
        ReviewBenchmarkQueuePage page,
        CancellationToken ct
    )
    {
        var artistCounts = new Dictionary<string, int>();
        var categoryCounts = new Dictionary<string, int>();
        var selected = new List<ReviewBenchmarkQueueRow>();
        var total = 0;
        var rows = 0;
        input.Position = 0;
        using var reader = new StreamReader(input, leaveOpen: true);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length > 1024 * 1024)
            {
                throw new ArgumentException("A benchmark queue row exceeds one MiB.");
            }
            using var document = JsonDocument.Parse(line);
            var row = document.RootElement;
            var owner = Artist(row);
            ValidateOwner(row, owner, header.Owners);
            var comparison = CurrentComparison(row, header.UsesResolvedIdentity);
            var status = Text(comparison, "status")!;
            rows++;
            if (
                status == "compared"
                && Array(comparison, "missing").Length == 0
                && Array(comparison, "extra").Length == 0
            )
            {
                continue;
            }
            if (page.Domain.Length != 0 && page.Domain != "songs")
            {
                continue;
            }
            artistCounts[owner] = artistCounts.GetValueOrDefault(owner) + 1;
            if (page.Artist.Length != 0 && owner != page.Artist)
            {
                continue;
            }
            categoryCounts[status] = categoryCounts.GetValueOrDefault(status) + 1;
            if (page.Category.Length != 0 && status != page.Category)
            {
                continue;
            }
            if (total >= page.Offset && selected.Count < page.Limit)
            {
                selected.Add(
                    new(row.Clone(), comparison.Clone(), owner, status, header.UsesResolvedIdentity)
                );
            }
            total++;
        }
        return new(
            total,
            rows,
            sha,
            header.Version,
            header.UsesResolvedIdentity ? "resolvedIdentity" : "retainedIdentity",
            header.Artists,
            artistCounts,
            categoryCounts,
            selected.ToArray(),
            header.Limitations
        );
    }

    private static JsonElement CurrentComparison(JsonElement row, bool usesResolvedIdentity)
    {
        var hasResolvedComparison = row.TryGetProperty("resolvedIdentity", out var identity);
        var comparison = usesResolvedIdentity && hasResolvedComparison ? identity : row;
        // Unaligned references have no projected identity comparison. They remain
        // actionable coverage work; an aligned v2 row must supply its resolved result.
        var originalStatus =
            Text(row, "status") ?? throw new ArgumentException("A benchmark row has no status.");
        if (usesResolvedIdentity && originalStatus == "compared" && !hasResolvedComparison)
        {
            throw new ArgumentException(
                "A compared v2 benchmark row has no resolved identity result."
            );
        }
        var status =
            Text(comparison, "status")
            ?? throw new ArgumentException("A benchmark comparison has no status.");
        ValidateComparison(comparison, status);
        return comparison;
    }

    private sealed record QueueHeader(
        string Version,
        bool UsesResolvedIdentity,
        JsonElement Declared,
        JsonElement[] Artists,
        HashSet<string> Owners,
        string?[] Limitations
    );

    private static void ValidateComparison(JsonElement comparison, string status)
    {
        if (status != "compared")
        {
            return;
        }
        var values = new Dictionary<string, HashSet<string>>();
        foreach (var field in new[] { "expected", "predicted", "missing", "extra" })
        {
            if (
                !comparison.TryGetProperty(field, out var array)
                || array.ValueKind != JsonValueKind.Array
                || array
                    .EnumerateArray()
                    .Any(item =>
                        item.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(item.GetString())
                    )
            )
            {
                throw new ArgumentException(
                    "A scored benchmark comparison must supply its song identity sets."
                );
            }
            var set = array
                .EnumerateArray()
                .Select(item => item.GetString()!)
                .ToHashSet(StringComparer.Ordinal);
            if (set.Count != array.GetArrayLength())
            {
                throw new ArgumentException(
                    "A benchmark song identity set contains duplicate keys."
                );
            }
            values.Add(field, set);
        }
        if (
            !values["missing"].SetEquals(values["expected"].Except(values["predicted"]))
            || !values["extra"].SetEquals(values["predicted"].Except(values["expected"]))
        )
        {
            throw new NormalizationConflictException(
                "Benchmark disagreement sets do not match expected and predicted identities."
            );
        }
    }

    private static void ValidateOwner(JsonElement row, string owner, HashSet<string> owners)
    {
        var key = Array(row, "key");
        if (!owners.Contains(owner) || key.Length == 0 || key[0].GetString() != owner)
        {
            throw new NormalizationConflictException(
                "A benchmark row is not owned by its declared artist."
            );
        }
        var show = Text(row, "showUuid");
        if (show != null && (key.Length != 2 || key[1].GetString() != show))
        {
            throw new NormalizationConflictException(
                "A benchmark row does not match its declared show."
            );
        }
        var references = Array(row, "referenceAccounts");
        if (row.TryGetProperty("reference", out var reference))
        {
            references = [.. references, reference];
        }
        if (references.Any(item => Text(item, "artistUuid") != owner))
        {
            throw new NormalizationConflictException(
                "A benchmark reference belongs to a different artist."
            );
        }
        if (show != null && references.Any(item => Text(item, "showUuid") != show))
        {
            throw new NormalizationConflictException(
                "A benchmark reference belongs to a different show."
            );
        }
    }

    internal static string? Text(JsonElement value, string key) =>
        value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String
            ? item.GetString()
            : null;

    internal static JsonElement[] Array(JsonElement value, string key) =>
        value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.Array
            ? item.EnumerateArray().ToArray()
            : [];

    private static string Artist(JsonElement row) =>
        Text(row, "artistUuid")
        ?? (
            row.TryGetProperty("reference", out var reference)
                ? Text(reference, "artistUuid")
                : null
        )
        ?? "unknown";
}

/// <summary>Optional filters and a bounded page within an authenticated benchmark.</summary>
public sealed record ReviewBenchmarkQueuePage(
    int Offset = 0,
    int Limit = 10,
    string Artist = "",
    string Category = "",
    string Domain = "songs"
);

/// <summary>Complete scan denominators, original artist metrics and one retained page.</summary>
public sealed record ReviewBenchmarkQueueSnapshot(
    int Total,
    int RowCount,
    string Sha256,
    string Version,
    string ComparisonBasis,
    JsonElement[] Artists,
    IReadOnlyDictionary<string, int> ArtistCounts,
    IReadOnlyDictionary<string, int> CategoryCounts,
    ReviewBenchmarkQueueRow[] SelectedRows,
    string?[] Limitations
);

/// <summary>
/// A current disagreement and its untouched evidence. Suggested actions use only the
/// comparison's unresolved keys, never unrelated file predictions or superseded raw IDs.
/// </summary>
public sealed record ReviewBenchmarkQueueRow(
    JsonElement Original,
    JsonElement Comparison,
    string ArtistUuid,
    string Status,
    bool UsesResolvedIdentity
)
{
    public string[] ActionKeys =>
        Status != "compared"
            ? []
            : ReviewBenchmarkQueue
                .Array(Comparison, "extra")
                .Concat(ReviewBenchmarkQueue.Array(Comparison, "missing"))
                .Select(item => item.GetString()!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

    /// <summary>Only files contributing current extra identities, mapped through the retained alias evidence.</summary>
    public string[] RecordedTitles
    {
        get
        {
            var extra = ReviewBenchmarkQueue
                .Array(Comparison, "extra")
                .Select(item => item.GetString()!)
                .ToHashSet(StringComparer.Ordinal);
            var aliases = UsesResolvedIdentity
                ? ReviewBenchmarkQueue
                    .Array(Original, "identityResolutions")
                    .ToDictionary(
                        item => ReviewBenchmarkQueue.Text(item, "originalKey")!,
                        item => ReviewBenchmarkQueue.Text(item, "targetKey")!
                    )
                : [];
            return ReviewBenchmarkQueue
                .Array(Original, "filesForReview")
                .Where(file =>
                    ReviewBenchmarkQueue
                        .Array(file, "predicted")
                        .Any(item =>
                        {
                            var key = item.GetString()!;
                            return extra.Contains(aliases.GetValueOrDefault(key, key));
                        })
                )
                .Select(file => ReviewBenchmarkQueue.Text(file, "title"))
                .Where(title => !string.IsNullOrWhiteSpace(title))
                .Select(title => title!)
                .Distinct()
                .Take(8)
                .ToArray();
        }
    }
}
