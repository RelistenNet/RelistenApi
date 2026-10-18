using System.Security.Cryptography;
using System.Text.Json;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Authenticates a pending-only continuation against a sealed original run. A new job owns
/// new reservations; the existing shared request cache retains all prior attempts and charges.
/// </summary>
public sealed class PeopleContextContinuation
{
    public const string Version = "people-context-continuation-v1";
    public const long MinimumFreeBytes = 11L * 1024 * 1024 * 1024 / 2;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Checks the complete original partition and exact pending bytes before creating a job.</summary>
    public async Task<PeopleContextContinuationPlan> ValidateAsync(
        string planPath,
        string expectedPlanSha256,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var planBytes = await VerifiedBytesAsync(
                planPath,
                expectedPlanSha256,
                1024 * 1024,
                cancellationToken
            );
            var plan =
                JsonSerializer.Deserialize<PeopleContextContinuationPlan>(planBytes, Json)
                ?? throw new InvalidDataException("A continuation plan is required.");
            if (
                plan.Version != Version
                || plan.Method != LunaPeopleContextMatcher.Version
                || plan.NewJobUuid == Guid.Empty
                || plan.OriginalJobUuid == Guid.Empty
                || plan.NewJobUuid == plan.OriginalJobUuid
                || !Path.IsPathFullyQualified(plan.OriginalRun)
                || !Path.IsPathFullyQualified(plan.PendingInputsPath)
                || plan.CapUsd != 10m
                || plan.MaximumDurationSeconds != 1800
                || plan.MinimumFreeBytes != MinimumFreeBytes
                || plan.MaximumTotalAttempts != 508
                || plan.MaximumTotalAttempts < plan.PendingBatches
            )
            {
                throw new InvalidDataException(
                    "The continuation identity or execution bounds differ."
                );
            }
            var original = Path.GetFullPath(plan.OriginalRun);
            var manifestBytes = await VerifiedBytesAsync(
                Path.Combine(original, "manifest.json"),
                plan.OriginalManifestSha256,
                1024 * 1024,
                cancellationToken
            );
            var report =
                JsonSerializer.Deserialize<PeopleContextBatchReport>(manifestBytes, Json)
                ?? throw new InvalidDataException("A sealed original run is required.");
            if (
                report.State is not ("completed" or "completed_partial" or "cancelled")
                || report.JobUuid != plan.OriginalJobUuid
                || report.Method != plan.Method
                || report.InputSha256 != plan.OriginalInputSha256
                || report.ResultsSha256 != plan.OriginalResultsSha256
            )
            {
                throw new InvalidDataException(
                    "The original run is not the selected sealed execution."
                );
            }
            var originalInputs = await VerifiedBytesAsync(
                Path.Combine(original, "inputs.jsonl"),
                report.InputSha256,
                100_001_000,
                cancellationToken
            );
            var originalResults = await VerifiedBytesAsync(
                Path.Combine(original, "results.jsonl"),
                report.ResultsSha256,
                256 * 1024 * 1024,
                cancellationToken
            );
            var continuationBytes = await VerifiedBytesAsync(
                Path.Combine(original, "continuation.json"),
                plan.ContinuationSha256,
                1024 * 1024,
                cancellationToken
            );
            using var continuationDocument = JsonDocument.Parse(continuationBytes);
            var continuation = continuationDocument.RootElement;
            var batches = ReadOriginalBatches(originalInputs);
            var completed = ReadCompletedBatches(originalResults, batches);
            var selectedCompleted = continuation
                .GetProperty("completedBatchIds")
                .EnumerateArray()
                .Select(value => value.GetString()!)
                .ToArray();
            var selectedPending = continuation
                .GetProperty("pendingBatchIds")
                .EnumerateArray()
                .Select(value => value.GetString()!)
                .ToArray();
            var pending = batches.Keys.Where(id => !completed.Contains(id)).ToArray();
            var pendingQuestions = pending.Sum(id => batches[id].Input.Questions.Count);
            var recordedQuestions = completed.Sum(id => batches[id].Input.Questions.Count);
            if (
                continuation.GetProperty("jobUuid").GetGuid() != report.JobUuid
                || continuation.GetProperty("inputSha256").GetString() != report.InputSha256
                || selectedCompleted.Length != completed.Count
                || !selectedCompleted.ToHashSet(StringComparer.Ordinal).SetEquals(completed)
                || selectedPending.Length != pending.Length
                || !selectedPending.ToHashSet(StringComparer.Ordinal).SetEquals(pending)
                || pending.Length == 0
                || pending.Length != plan.PendingBatches
                || completed.Count != report.CompletedBatches
                || completed.Count != plan.RecordedBatches
                || batches.Count != report.InputBatches
                || pendingQuestions != plan.PendingQuestions
                || recordedQuestions != plan.RecordedQuestions
                || pendingQuestions + recordedQuestions != report.Questions
                || continuation.GetProperty("recordedQuestions").GetInt32() != recordedQuestions
                || continuation.GetProperty("pendingQuestions").GetInt32() != pendingQuestions
            )
            {
                throw new InvalidDataException(
                    "The continuation does not contain the exact pending partition."
                );
            }
            using var selectedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var id in pending)
            {
                selectedHash.AppendData(batches[id].Bytes.Span);
            }
            var expectedPending = Convert.ToHexStringLower(selectedHash.GetHashAndReset());
            if (expectedPending != plan.PendingInputsSha256)
            {
                throw new InvalidDataException(
                    "Pending input bytes differ from the original selected batches."
                );
            }
            await VerifiedBytesAsync(
                plan.PendingInputsPath,
                expectedPending,
                100_001_000,
                cancellationToken
            );
            return plan;
        }
        catch (Exception error)
            when (error
                    is JsonException
                        or ArgumentException
                        or KeyNotFoundException
                        or InvalidOperationException
            )
        {
            throw new InvalidDataException(
                "The continuation artifacts have an invalid shape or scope.",
                error
            );
        }
    }

    /// <summary>
    /// Executes a new bounded job over validated pending inputs. The legacy job is never resumed.
    /// Sealed output also retains the exact plan, including after cancellation, for attachment/recovery.
    /// </summary>
    public async Task<PeopleContextBatchReport> RunAsync(
        string planPath,
        string expectedPlanSha256,
        string outputDirectory,
        CancellationToken cancellationToken = default
    )
    {
        var plan = await ValidateAsync(planPath, expectedPlanSha256, cancellationToken);
        if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory))
        {
            throw new IOException("Choose a new continuation output directory.");
        }
        var planBytes = await VerifiedBytesAsync(
            planPath,
            expectedPlanSha256,
            1024 * 1024,
            cancellationToken
        );
        try
        {
            return await new PeopleContextBatch().RunAsync(
                plan.PendingInputsPath,
                outputDirectory,
                plan.NewJobUuid,
                cancellationToken,
                new(
                    plan.CapUsd,
                    plan.MaximumTotalAttempts,
                    TimeSpan.FromSeconds(plan.MaximumDurationSeconds),
                    plan.MinimumFreeBytes,
                    plan.PendingInputsSha256
                )
            );
        }
        finally
        {
            // RunAsync seals the completed prefix before propagating cancellation. Keep this
            // plan beside that prefix; do not rewrite the original run or its result manifest.
            if (File.Exists(Path.Combine(outputDirectory, "manifest.json")))
            {
                await using (
                    var destination = new FileStream(
                        Path.Combine(outputDirectory, "continuation-plan.json"),
                        FileMode.CreateNew
                    )
                )
                {
                    await destination.WriteAsync(planBytes, CancellationToken.None);
                }
                await File.WriteAllTextAsync(
                    Path.Combine(outputDirectory, "continuation-reference.json"),
                    JsonSerializer.Serialize(
                        new
                        {
                            planSha256 = expectedPlanSha256,
                            plan.NewJobUuid,
                            plan.OriginalJobUuid,
                            plan.OriginalManifestSha256,
                        },
                        Json
                    ),
                    CancellationToken.None
                );
            }
        }
    }

    private static Dictionary<string, OriginalBatch> ReadOriginalBatches(byte[] bytes)
    {
        var batches = new Dictionary<string, OriginalBatch>(StringComparer.Ordinal);
        var questions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in Lines(bytes))
        {
            var batch = JsonSerializer.Deserialize<PeopleContextBatchInput>(line.Span, Json)!;
            LunaPeopleContextMatcher.ValidateInput(batch);
            var contentLength = line.Length;
            if (contentLength > 0 && line.Span[contentLength - 1] == (byte)'\n')
            {
                contentLength--;
            }
            if (contentLength > 0 && line.Span[contentLength - 1] == (byte)'\r')
            {
                contentLength--;
            }
            if (
                contentLength > LunaPeopleContextMatcher.MaxRenderedRequestBytes
                || !batches.TryAdd(batch.BatchId, new(batch, line))
                || batches.Count > 500
                || batch.Questions.Any(question => !questions.Add(question.QuestionId))
                || questions.Count > 5000
            )
            {
                throw new InvalidDataException(
                    "The original batches repeat identities or exceed their bounds."
                );
            }
        }
        return batches;
    }

    private static HashSet<string> ReadCompletedBatches(
        byte[] bytes,
        IReadOnlyDictionary<string, OriginalBatch> batches
    )
    {
        var completed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in Lines(bytes))
        {
            var row = JsonSerializer.Deserialize<PeopleContextBatchRow>(line.Span, Json)!;
            if (
                !batches.TryGetValue(row.Input.BatchId, out var original)
                || !completed.Add(row.Input.BatchId)
                || row.Result.BatchId != row.Input.BatchId
                || JsonSerializer.Serialize(row.Input, Json)
                    != JsonSerializer.Serialize(original.Input, Json)
            )
            {
                throw new InvalidDataException(
                    "An original result differs from its exact input batch."
                );
            }
        }
        return completed;
    }

    private static IEnumerable<ReadOnlyMemory<byte>> Lines(byte[] bytes)
    {
        var start = 0;
        while (start < bytes.Length)
        {
            var newline = Array.IndexOf(bytes, (byte)'\n', start);
            var end = newline < 0 ? bytes.Length : newline + 1;
            yield return bytes.AsMemory(start, end - start);
            start = end;
        }
    }

    private static async Task<byte[]> VerifiedBytesAsync(
        string path,
        string hash,
        int maximumBytes,
        CancellationToken ct
    )
    {
        if (new FileInfo(path).Length > maximumBytes)
        {
            throw new InvalidDataException("A continuation artifact exceeds its byte bound.");
        }
        var bytes = await File.ReadAllBytesAsync(path, ct);
        if (
            !StringComparer.OrdinalIgnoreCase.Equals(
                Convert.ToHexStringLower(SHA256.HashData(bytes)),
                hash
            )
        )
        {
            throw new InvalidDataException("A continuation artifact differs from its pinned hash.");
        }
        return bytes;
    }

    private sealed record OriginalBatch(PeopleContextBatchInput Input, ReadOnlyMemory<byte> Bytes);
}

/// <summary>Operator-pinned scope and limits for one new pending-only job.</summary>
public sealed record PeopleContextContinuationPlan(
    string Version,
    Guid NewJobUuid,
    string OriginalRun,
    Guid OriginalJobUuid,
    string OriginalManifestSha256,
    string ContinuationSha256,
    string OriginalInputSha256,
    string OriginalResultsSha256,
    string PendingInputsPath,
    string PendingInputsSha256,
    int PendingBatches,
    int PendingQuestions,
    int RecordedBatches,
    int RecordedQuestions,
    decimal CapUsd,
    int MaximumTotalAttempts,
    int MaximumDurationSeconds,
    long MinimumFreeBytes,
    string Method
);
