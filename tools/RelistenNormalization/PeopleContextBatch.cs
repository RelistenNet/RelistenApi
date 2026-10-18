using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Runs a bounded local experiment over captured actor pairs. Each flushed row retains its
/// original witnesses and model proposal. Completed prefixes remain readable after interruption.
/// PostgreSQL owns the response cache and spend ledger independently of output files.
/// </summary>
public sealed class PeopleContextBatch
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Reads at most 500 JSONL batches and 5,000 questions before admitting requests. Uses the
    /// dedicated local normalization database, a $25/1,000-attempt job and four concurrent calls.
    /// Failed model responses are recorded individually while unrelated batches continue.
    /// </summary>
    public async Task<PeopleContextBatchReport> RunAsync(
        string inputPath,
        string outputDirectory,
        Guid jobUuid,
        CancellationToken cancellationToken = default
    )
    {
        if (jobUuid == Guid.Empty)
        {
            throw new ArgumentException("Use a nonempty extraction job UUID.", nameof(jobUuid));
        }
        if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory))
        {
            throw new IOException("Choose a new output directory.");
        }

        var batches = await ReadInputsAsync(inputPath, cancellationToken);
        var db = NormalizationLocalDatabase.Create();
        var cache = new NormalizationExtractionCacheService(db);
        using var http = new HttpClient();
        var matcher = new LunaPeopleContextMatcher(
            http,
            cache,
            () => Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? ""
        );
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "inputs.jsonl"),
            string.Join("\n", batches.Select(batch => JsonSerializer.Serialize(batch, Json)))
                + "\n",
            cancellationToken
        );
        await cache.CreateJobAsync(jobUuid, 25m, cancellationToken, maximumTotalAttempts: 1000);

        var states = new Dictionary<string, int>(StringComparer.Ordinal);
        var completed = 0;
        var fromCache = 0;
        var knownActualUsd = 0m;
        var state = "completed";
        PeopleContextBatchReport? report = null;
        using var writerGate = new SemaphoreSlim(1);
        await using var writer = new StreamWriter(
            new FileStream(Path.Combine(outputDirectory, "results.jsonl"), FileMode.CreateNew),
            new UTF8Encoding(false)
        );
        try
        {
            await Parallel.ForEachAsync(
                batches,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = 4,
                    CancellationToken = cancellationToken,
                },
                async (batch, ct) =>
                {
                    var result = await matcher.MatchAsync(jobUuid, batch, ct);
                    // Once HTTP settles, append its receipt even if the caller cancels while waiting.
                    await writerGate.WaitAsync(CancellationToken.None);
                    try
                    {
                        await writer.WriteLineAsync(
                            JsonSerializer.Serialize(new PeopleContextBatchRow(batch, result), Json)
                        );
                        await writer.FlushAsync(CancellationToken.None);
                        completed++;
                        fromCache += result.FromCache ? 1 : 0;
                        knownActualUsd += result.ActualUsd ?? 0m;
                        states[result.State] = states.GetValueOrDefault(result.State) + 1;
                    }
                    finally
                    {
                        writerGate.Release();
                    }
                }
            );
            if (states.Keys.Any(key => key != "proposed"))
            {
                state = "completed_partial";
            }
        }
        catch
        {
            state = cancellationToken.IsCancellationRequested ? "cancelled" : "failed";
            throw;
        }
        finally
        {
            // Parallel.ForEachAsync has settled all admitted work, including durable cancellation
            // reservations. Its ledger total may exceed known usage when a response was uncertain.
            await writer.FlushAsync(CancellationToken.None);
            var job = await cache.GetJobAsync(jobUuid, CancellationToken.None);
            report = new PeopleContextBatchReport(
                state,
                jobUuid,
                LunaPeopleContextMatcher.Version,
                batches.Count,
                batches.Sum(batch => batch.Questions.Count),
                completed,
                fromCache,
                states,
                job.CapUsd,
                job.AccountedUsd,
                knownActualUsd,
                job.MaximumTotalAttempts,
                await FileHashAsync(Path.Combine(outputDirectory, "inputs.jsonl")),
                await FileHashAsync(Path.Combine(outputDirectory, "results.jsonl"))
            );
            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, "manifest.json"),
                JsonSerializer.Serialize(report, Json),
                CancellationToken.None
            );
        }
        return report!;
    }

    private static async Task<IReadOnlyList<PeopleContextBatchInput>> ReadInputsAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        var batches = new List<PeopleContextBatchInput>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var questions = 0;
        using var reader = new StreamReader(path);
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (Encoding.UTF8.GetByteCount(line) > LunaPeopleContextMatcher.MaxRenderedRequestBytes)
            {
                throw new ArgumentException("A batch input line exceeds the request byte limit.");
            }
            var batch =
                JsonSerializer.Deserialize<PeopleContextBatchInput>(line, Json)
                ?? throw new ArgumentException("Expected a batch object on each JSONL line.");
            LunaPeopleContextMatcher.ValidateInput(batch);
            questions += batch.Questions.Count;
            if (!ids.Add(batch.BatchId) || batches.Count >= 500 || questions > 5000)
            {
                throw new ArgumentException(
                    "Use unique batch IDs, at most 500 batches and at most 5,000 questions."
                );
            }
            batches.Add(batch);
        }
        if (batches.Count == 0)
        {
            throw new ArgumentException("Supply at least one batch.");
        }
        return batches;
    }

    private static async Task<string> FileHashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
    }
}

/// <summary>Witness coordinates remain attached to every decision, including cached proposals.</summary>
public sealed record PeopleContextBatchRow(
    PeopleContextBatchInput Input,
    PeopleContextMatchResult Result
);

public sealed record PeopleContextBatchReport(
    string State,
    Guid JobUuid,
    string Method,
    int InputBatches,
    int Questions,
    int CompletedBatches,
    int FromCache,
    IReadOnlyDictionary<string, int> States,
    decimal CapUsd,
    decimal AccountedUsd,
    decimal KnownActualUsd,
    int? MaximumTotalAttempts,
    string InputSha256,
    string ResultsSha256
);
