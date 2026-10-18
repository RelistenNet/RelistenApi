using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Owns append-only shadow results and logical outcome counts. Checkpoints follow
/// flushed output. A restart can repeat a small uncheckpointed prefix; consumers
/// use (domain,inputKey) as the logical key and keep the last result. Raw inputs
/// and previously written proposals are never truncated or rewritten.
/// </summary>
internal sealed class ShadowCatalogRun : IAsyncDisposable
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
    private readonly string directory;
    private readonly Dictionary<string, OutputChunk> writers = new();
    private readonly Dictionary<string, ShadowOutcomeCounts> counts;
    private readonly string inputIdentity;
    private readonly IReadOnlyDictionary<string, string> methods;
    private long lastLine;
    private long physicalRows;

    internal ShadowCatalogRun(
        string directory,
        string inputIdentity,
        IReadOnlyDictionary<string, string> methods,
        bool resume
    )
    {
        this.directory = directory;
        this.inputIdentity = inputIdentity;
        this.methods = methods;
        var checkpointPath = Path.Combine(directory, "checkpoint.json");
        if (resume)
        {
            var saved = JsonSerializer.Deserialize<Checkpoint>(
                File.ReadAllText(checkpointPath),
                Json
            )!;
            if (
                saved.InputIdentity != inputIdentity
                || !saved
                    .Methods.OrderBy(pair => pair.Key)
                    .SequenceEqual(methods.OrderBy(pair => pair.Key))
            )
            {
                throw new InvalidDataException(
                    "Resume requires the same complete inputs and adapter versions."
                );
            }

            lastLine = saved.LastLine;
            physicalRows = saved.PhysicalRows;
            counts = saved.Counts;
        }
        else
        {
            if (Directory.Exists(directory) || File.Exists(directory))
            {
                throw new IOException(
                    "Choose a new shadow directory or explicitly resume its checkpoint."
                );
            }

            Directory.CreateDirectory(directory);
            counts = new();
        }
    }

    internal long CompletedLine => lastLine;
    internal bool HasFailures =>
        counts.Values.Any(count => count.Outcomes.GetValueOrDefault("failed") > 0);

    internal async Task WriteInputAsync(IReadOnlyList<ShadowCatalogResult> results)
    {
        foreach (var result in results)
        {
            if (!writers.TryGetValue(result.Domain, out var chunk))
            {
                chunk = OpenChunk(result.Domain);
                writers.Add(result.Domain, chunk);
            }

            await chunk.Writer.WriteLineAsync(JsonSerializer.Serialize(result, Json));
        }

        // Advance logical counts only after every sibling domain is written. If a
        // write fails midway, resume repeats this input without double-counting it.
        foreach (var result in results)
        {
            var key = result.Provider + "|" + result.Domain;
            if (!counts.TryGetValue(key, out var count))
            {
                count = new(result.Provider, result.Domain);
                counts.Add(key, count);
            }

            count.Processed++;
            count.Outcomes[result.Outcome] = count.Outcomes.GetValueOrDefault(result.Outcome) + 1;
            physicalRows++;
        }
    }

    private OutputChunk OpenChunk(string domain)
    {
        var domainDirectory = Path.Combine(directory, domain);
        Directory.CreateDirectory(domainDirectory);
        var path = Path.Combine(
            domainDirectory,
            DateTimeOffset.UtcNow.UtcTicks + "-" + Guid.NewGuid().ToString("N") + ".jsonl.gz"
        );
        var stream = new FileStream(
            path + ".writing",
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read
        );
        // Full file runs repeat evidence across domains. Prefer the smaller retained
        // output: readers still consume ordinary gzip, and checkpoints remain unchanged.
        var compressed = new GZipStream(stream, CompressionLevel.Optimal);
        return new(
            new StreamWriter(compressed, new System.Text.UTF8Encoding(false)),
            path + ".writing",
            path
        );
    }

    internal async Task SaveAsync(long line, string state, object population, CancellationToken ct)
    {
        foreach (var (domain, chunk) in writers.ToArray())
        {
            await chunk.Writer.FlushAsync(ct);
            await chunk.Writer.DisposeAsync();
            File.Move(chunk.PartialPath, chunk.CompletedPath);
            writers.Remove(domain);
        }

        lastLine = line;
        await AtomicJsonAsync(
            "checkpoint.json",
            new Checkpoint(inputIdentity, methods, lastLine, physicalRows, counts),
            ct
        );
        await AtomicJsonAsync(
            "progress.json",
            new
            {
                version = "shadow-catalog-v1",
                state,
                updatedAt = DateTimeOffset.UtcNow,
                inputIdentity,
                methods,
                lastInputLine = lastLine,
                outputRows = physicalRows,
                counts = counts.Values,
                population,
                replay = "Immutable gzip chunks; last (domain,inputKey) result wins. Counts describe logical outcomes. A restart may retain repeated chunks; unfinished .writing files are retained but never indexed.",
            },
            ct
        );
    }

    private async Task AtomicJsonAsync(string name, object value, CancellationToken ct)
    {
        var path = Path.Combine(directory, name);
        await File.WriteAllTextAsync(path + ".writing", JsonSerializer.Serialize(value, Json), ct);
        File.Move(path + ".writing", path, overwrite: true);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var chunk in writers.Values)
        {
            await chunk.Writer.DisposeAsync();
        }
    }

    private sealed record Checkpoint(
        string InputIdentity,
        IReadOnlyDictionary<string, string> Methods,
        long LastLine,
        long PhysicalRows,
        Dictionary<string, ShadowOutcomeCounts> Counts
    );

    private sealed record OutputChunk(
        StreamWriter Writer,
        string PartialPath,
        string CompletedPath
    );

    internal sealed record ShadowOutcomeCounts(string Provider, string Domain)
    {
        public long Processed { get; set; }
        public Dictionary<string, long> Outcomes { get; init; } = new();
    }
}
