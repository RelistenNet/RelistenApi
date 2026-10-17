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

    public async Task<LunaCreditEvaluationReport> RunAsync(
        string extractionsPath,
        string outputDirectory,
        Guid jobUuid,
        int perCohort = 32,
        CancellationToken cancellationToken = default
    )
    {
        if (perCohort is < 1 or > 5000 || jobUuid == Guid.Empty)
        {
            throw new ArgumentException("Use a job UUID and 1–5000 records per comparison cohort.");
        }

        if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory))
        {
            throw new IOException("Choose a new evaluation output directory.");
        }

        // Validate and hash the entire retained extraction before admitting any paid work.
        // A truncated input cannot become a silently smaller comparison sample.
        var selection = await SelectAsync(extractionsPath, perCohort, cancellationToken);
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "selection.json"),
            JsonSerializer.Serialize(selection, Json),
            cancellationToken
        );

        await cache.CreateJobAsync(jobUuid, 25m, cancellationToken);
        var states = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var cached = 0;
        var completed = 0;
        await using (
            var output = new StreamWriter(
                new FileStream(Path.Combine(outputDirectory, "results.jsonl"), FileMode.CreateNew)
            )
        )
        {
            foreach (var item in selection.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await extractor.ExtractAsync(
                    jobUuid,
                    item.Input.Inputs,
                    auditLocalSuccess: true,
                    cancellationToken: cancellationToken
                );
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
                        },
                        Json
                    )
                );
                // Keep a usable prefix if cancellation or process failure interrupts the run.
                await output.FlushAsync(cancellationToken);
                states[result.State] = states.GetValueOrDefault(result.State) + 1;
                cached += result.FromCache ? 1 : 0;
                completed++;
            }
        }

        var job = await cache.GetJobAsync(jobUuid, cancellationToken);
        var report = new LunaCreditEvaluationReport(
            states.Keys.All(state => state == "extracted") ? "completed" : "completed_partial",
            jobUuid,
            selection.InputSha256,
            LunaRecordingCreditExtractor.Version,
            completed,
            cached,
            states,
            job.CapUsd,
            job.AccountedUsd
        );
        var manifestPath = Path.Combine(outputDirectory, "manifest.json");
        await File.WriteAllTextAsync(
            manifestPath + ".writing",
            JsonSerializer.Serialize(report, Json),
            cancellationToken
        );
        File.Move(manifestPath + ".writing", manifestPath);
        return report;
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
    decimal AccountedUsd
);
