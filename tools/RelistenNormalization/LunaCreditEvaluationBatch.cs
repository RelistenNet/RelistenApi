using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Runs a bounded comparison sample, including apparent local successes. Results are
/// extraction proposals for independent assessment; this command never publishes identities.
/// PostgreSQL owns the spend ledger and response cache so a new output directory can reuse
/// completed requests after interruption without paying for them again.
/// </summary>
public sealed class LunaCreditEvaluationBatch(
    LunaRecordingCreditExtractor extractor,
    NormalizationExtractionCacheService cache
)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>
    /// Audits complete source inputs with bounded parallel requests. Identical field maps
    /// share one in-process request; PostgreSQL still owns all cross-process claims and cost.
    /// After interruption, use the same job and inputs with a new output directory. Completed
    /// requests replay from the cache; uncertain attempts retain their reservation and limits.
    /// </summary>
    public async Task<LunaCreditEvaluationReport> RunAsync(
        string extractionsPath,
        string outputDirectory,
        Guid jobUuid,
        int perCohort = 32,
        CancellationToken cancellationToken = default,
        int concurrency = 8
    )
    {
        if (perCohort is < 1 or > 5000 || jobUuid == Guid.Empty)
        {
            throw new ArgumentException("Use a job UUID and 1–5000 records per comparison cohort.");
        }
        if (concurrency is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(
                nameof(concurrency),
                "Use 1–32 concurrent requests."
            );
        }
        if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory))
        {
            throw new IOException("Choose a new evaluation output directory.");
        }

        // Authenticate the entire input before creating a job or admitting provider work.
        var selection = await SelectAsync(extractionsPath, perCohort, cancellationToken);
        var groups = GroupRequests(selection.Items);
        Directory.CreateDirectory(outputDirectory);
        await WriteJsonAsync(
            Path.Combine(outputDirectory, "selection.json"),
            selection,
            cancellationToken
        );
        var selectionSha256 = await FileSha256Async(
            Path.Combine(outputDirectory, "selection.json")
        );
        await cache.CreateJobAsync(jobUuid, 25m, cancellationToken);

        var results = await ExtractSelectionAsync(
            selection,
            groups,
            outputDirectory,
            jobUuid,
            concurrency,
            cancellationToken
        );

        // All dispatched work has settled. Do not let late caller cancellation hide its receipt.
        var job = await cache.GetJobAsync(jobUuid, CancellationToken.None);
        var report = new LunaCreditEvaluationReport(
            results.States.Keys.All(state => state == "extracted")
                ? "completed"
                : "completed_partial",
            jobUuid,
            selection.InputSha256,
            LunaRecordingCreditExtractor.Version,
            results.Records,
            results.FromCache,
            results.States,
            job.CapUsd,
            job.AccountedUsd,
            concurrency,
            groups.Count,
            results.ReusedWithinBatch,
            selectionSha256,
            await FileSha256Async(Path.Combine(outputDirectory, "results.jsonl"))
        );
        await WriteJsonAsync(
            Path.Combine(outputDirectory, "manifest.json"),
            report,
            CancellationToken.None
        );
        await WriteProgressAsync(
            outputDirectory,
            report.State,
            jobUuid,
            selection.Items.Count,
            groups.Count,
            concurrency,
            results
        );
        return report;
    }

    private async Task<BatchResults> ExtractSelectionAsync(
        LunaCreditEvaluationSelection selection,
        IReadOnlyList<RequestGroup> groups,
        string outputDirectory,
        Guid jobUuid,
        int concurrency,
        CancellationToken cancellationToken
    )
    {
        var results = new BatchResults();
        Exception? outputFailure = null;
        using var writerGate = new SemaphoreSlim(1);
        await using var output = new StreamWriter(
            new FileStream(Path.Combine(outputDirectory, "results.jsonl"), FileMode.CreateNew)
        );
        await WriteProgressAsync(
            outputDirectory,
            "running",
            jobUuid,
            selection.Items.Count,
            groups.Count,
            concurrency,
            results
        );

        try
        {
            await Parallel.ForEachAsync(
                groups,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = concurrency,
                    CancellationToken = cancellationToken,
                },
                async (group, ct) =>
                {
                    var result = await extractor.ExtractAsync(
                        jobUuid,
                        group.Fields,
                        auditLocalSuccess: true,
                        cancellationToken: ct
                    );

                    // The cache has committed this result. Flush it even when another worker
                    // fails or caller cancellation stops new dispatch. This gate owns the
                    // stream and every logical counter; completion order is not input order.
                    await writerGate.WaitAsync(CancellationToken.None);
                    try
                    {
                        if (outputFailure is not null)
                        {
                            throw new IOException(
                                "Evaluation output is unavailable.",
                                outputFailure
                            );
                        }
                        for (var index = 0; index < group.Items.Count; index++)
                        {
                            var item = group.Items[index];
                            var reused = index > 0;
                            await output.WriteLineAsync(
                                JsonSerializer.Serialize(
                                    new
                                    {
                                        item.Cohort,
                                        item.Input.ResourceUuid,
                                        item.Input.ArtistUuid,
                                        item.Input.Family,
                                        local = item.Input.CreditExtraction,
                                        luna = result,
                                        reusedWithinBatch = reused,
                                    },
                                    Json
                                )
                            );
                            await output.FlushAsync(CancellationToken.None);
                            results.States[result.State] =
                                results.States.GetValueOrDefault(result.State) + 1;
                            results.FromCache += result.FromCache ? 1 : 0;
                            results.ReusedWithinBatch += reused ? 1 : 0;
                            results.Records++;
                            await WriteProgressAsync(
                                outputDirectory,
                                "running",
                                jobUuid,
                                selection.Items.Count,
                                groups.Count,
                                concurrency,
                                results
                            );
                        }
                    }
                    catch (Exception error)
                    {
                        outputFailure = error;
                        throw;
                    }
                    finally
                    {
                        writerGate.Release();
                    }
                }
            );
        }
        catch
        {
            // Parallel.ForEachAsync has drained its workers, including the extractor's
            // durable uncertain-attempt handling. This is a prefix receipt, not completion.
            try
            {
                await WriteProgressAsync(
                    outputDirectory,
                    "interrupted",
                    jobUuid,
                    selection.Items.Count,
                    groups.Count,
                    concurrency,
                    results
                );
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // The failed storage may also prevent a prefix receipt. Preserve the original
                // failure; completed requests and uncertain reservations remain in the cache.
            }
            throw;
        }
        return results;
    }

    private static IReadOnlyList<RequestGroup> GroupRequests(
        IReadOnlyList<LunaCreditEvaluationInput> items
    )
    {
        var groups = new Dictionary<string, RequestGroup>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            // Artist/source context is intentionally absent from this extractor's request.
            // Compare exact fields on a digest hit, just as the durable cache verifies its body.
            var fields = item.Input.Inputs;
            var canonical = JsonSerializer.Serialize(
                fields
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(pair => pair.Key, pair => pair.Value),
                Json
            );
            var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
            if (!groups.TryGetValue(key, out var group))
            {
                group = new(fields, []);
                groups.Add(key, group);
            }
            else if (
                fields.Count != group.Fields.Count
                || fields.Any(pair =>
                    !group.Fields.TryGetValue(pair.Key, out var value) || value != pair.Value
                )
            )
            {
                throw new InvalidDataException("Source field digest collision.");
            }
            group.Items.Add(item);
        }
        return groups.Values.ToArray();
    }

    private static Task WriteProgressAsync(
        string directory,
        string state,
        Guid jobUuid,
        int selected,
        int requests,
        int concurrency,
        BatchResults results
    ) =>
        WriteJsonAsync(
            Path.Combine(directory, "progress.json"),
            new
            {
                state,
                jobUuid,
                selected,
                records = results.Records,
                remaining = selected - results.Records,
                uniqueRequests = requests,
                concurrency,
                fromCache = results.FromCache,
                reusedWithinBatch = results.ReusedWithinBatch,
                states = results.States,
            },
            CancellationToken.None
        );

    private static async Task WriteJsonAsync(string path, object value, CancellationToken ct)
    {
        await File.WriteAllTextAsync(path + ".writing", JsonSerializer.Serialize(value, Json), ct);
        File.Move(path + ".writing", path, overwrite: true);
    }

    private static async Task<string> FileSha256Async(string path)
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(input));
    }

    private sealed record RequestGroup(
        IReadOnlyDictionary<string, string?> Fields,
        List<LunaCreditEvaluationInput> Items
    );

    private sealed class BatchResults
    {
        internal int Records { get; set; }
        internal int FromCache { get; set; }
        internal int ReusedWithinBatch { get; set; }
        internal SortedDictionary<string, int> States { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// Selects by stable resource hash, independently within two local-output cohorts.
    /// This is a diagnostic sample, not a random estimate of catalog accuracy. No labels
    /// or identity truth are inferred from the local extractor's output.
    /// </summary>
    public static async Task<LunaCreditEvaluationSelection> SelectAsync(
        string extractionsPath,
        int perCohort,
        CancellationToken cancellationToken = default
    )
    {
        if (perCohort is < 1 or > 5000)
        {
            throw new ArgumentOutOfRangeException(nameof(perCohort));
        }
        var manifestPath = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(extractionsPath))!,
            "manifest.json"
        );
        var receiptBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
        using var manifest = JsonDocument.Parse(receiptBytes);
        var root = manifest.RootElement;
        ValidateReceipt(root);
        var expectedCount = root.GetProperty("outputRecords").GetInt64();
        var cohorts = new Dictionary<string, SortedDictionary<string, NormalizationBatchExtraction>>
        {
            ["local_credit_present"] = new(StringComparer.Ordinal),
            ["local_unresolved_or_missing"] = new(StringComparer.Ordinal),
        };
        using var hash = SHA256.Create();
        var sourceUuids = new HashSet<Guid>();
        await using var input = File.OpenRead(extractionsPath);
        await using var hashing = new CryptoStream(input, hash, CryptoStreamMode.Read);
        using var reader = new StreamReader(
            hashing,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false
        );
        long rows = 0;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            rows++;
            using var record = JsonDocument.Parse(line);
            if (record.RootElement.GetProperty("kind").GetString() != "source_sample")
            {
                continue;
            }
            var item =
                JsonSerializer.Deserialize<NormalizationBatchExtraction>(line, Json)
                ?? throw new InvalidDataException("Missing extraction record.");
            if (
                item.ResourceUuid == Guid.Empty
                || item.ArtistUuid == Guid.Empty
                || item.CreditExtraction is null
            )
            {
                throw new InvalidDataException("Incomplete source extraction context.");
            }
            if (!sourceUuids.Add(item.ResourceUuid))
            {
                throw new InvalidDataException("Duplicate source UUID.");
            }
            if (!item.Inputs.Any(pair => !string.IsNullOrWhiteSpace(pair.Value)))
            {
                continue;
            }
            var credits = item.CreditExtraction;
            var cohort =
                credits.Unresolved.Count == 0
                && credits.Credits.Any(credit =>
                    credit.Role is RecordingCreditRole.Taper or RecordingCreditRole.Transferrer
                )
                    ? "local_credit_present"
                    : "local_unresolved_or_missing";
            var rank = Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(item.ResourceUuid.ToString("D")))
            );
            var selected = cohorts[cohort];
            if (!selected.TryAdd(rank, item))
            {
                throw new InvalidDataException("Duplicate selected source UUID.");
            }
            if (selected.Count > perCohort)
            {
                selected.Remove(selected.Last().Key);
            }
        }
        if (rows != expectedCount)
        {
            throw new InvalidDataException("Extraction rows do not match the completion receipt.");
        }
        return new(
            Convert.ToHexStringLower(hash.Hash!),
            Convert.ToHexStringLower(SHA256.HashData(receiptBytes)),
            root.Clone(),
            cohorts
                .SelectMany(cohort =>
                    cohort.Value.Values.Select(input => new LunaCreditEvaluationInput(
                        cohort.Key,
                        input
                    ))
                )
                .ToArray()
        );
    }

    private static void ValidateReceipt(JsonElement root)
    {
        try
        {
            var snapshot = root.GetProperty("snapshot");
            var completion = root.GetProperty("snapshotCompletion");
            var versions = root.GetProperty("extractorVersions");
            if (
                root.GetProperty("state").GetString() is not ("completed" or "completed_partial")
                || snapshot.GetProperty("kind").GetString() != "snapshot"
                || string.IsNullOrWhiteSpace(snapshot.GetProperty("snapshot").GetString())
                || snapshot.GetProperty("read_only").GetString() != "on"
                || !snapshot.GetProperty("in_recovery").GetBoolean()
                || completion.GetProperty("kind").GetString() != "snapshot_complete"
                || string.IsNullOrWhiteSpace(completion.GetProperty("completed_at").GetString())
                || !IsSha256(root.GetProperty("inputSha256").GetString())
                || !IsSha256(root.GetProperty("runIdentitySha256").GetString())
                || string.IsNullOrWhiteSpace(versions.GetProperty("recording_credits").GetString())
                || string.IsNullOrWhiteSpace(versions.GetProperty("track_title").GetString())
                || root.GetProperty("outputRecords").GetInt64() < 0
            )
            {
                throw new InvalidDataException(
                    "A complete read-only extraction receipt is required."
                );
            }
        }
        catch (Exception error)
            when (error is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException(
                "The extraction receipt is missing required provenance.",
                error
            );
        }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);
}

public sealed record LunaCreditEvaluationInput(string Cohort, NormalizationBatchExtraction Input);

public sealed record LunaCreditEvaluationSelection(
    string InputSha256,
    string ExtractionReceiptSha256,
    JsonElement ExtractionReceipt,
    IReadOnlyList<LunaCreditEvaluationInput> Items
);

public sealed record LunaCreditEvaluationReport(
    string State,
    Guid JobUuid,
    string InputSha256,
    string ExtractorVersion,
    int Records,
    int FromCache,
    IReadOnlyDictionary<string, int> States,
    decimal CapUsd,
    decimal AccountedUsd,
    int Concurrency = 1,
    int UniqueRequests = 0,
    int ReusedWithinBatch = 0,
    string? SelectionSha256 = null,
    string? ResultsSha256 = null
);
