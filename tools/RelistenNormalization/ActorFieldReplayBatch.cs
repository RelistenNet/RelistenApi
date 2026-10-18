using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Replays the frozen development field population through the public local extractor.
/// This tool reads authentication metadata, never annotation answers; it starts no host or network client.
/// Raw outputs belong in ignored local storage because source quotes can contain contact details.
/// </summary>
public sealed class ActorFieldReplayBatch
{
    public const string Version = "actor-field-replay-v1";
    private const int FieldCount = 543;
    private const int MaximumFileBytes = 8 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Authenticates the entire input before creating a fresh output directory. Per-field failures
    /// retain error records and return completed_partial; only zero-error completion writes manifest.json.
    /// Cancellation or command failure retains flushed partial output and status.json, then propagates.
    /// Recovery uses a new directory. Progress is reported after a flushed field write.
    /// </summary>
    public async Task<ActorFieldReplayReport> RunAsync(
        string inputPath,
        string exportReceiptPath,
        string frozenBundleDirectory,
        string newOutputDirectory,
        CancellationToken cancellationToken = default,
        IProgress<int>? progress = null
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(newOutputDirectory) || File.Exists(newOutputDirectory))
        {
            throw new IOException("Choose a new actor-field replay output directory.");
        }

        var selection = await ReadSelectionAsync(
            inputPath,
            exportReceiptPath,
            frozenBundleDirectory,
            cancellationToken
        );
        var apiAssembly = await AssemblyReceiptAsync(
            typeof(RecordingCreditExtractor).Assembly,
            cancellationToken
        );
        var toolAssembly = await AssemblyReceiptAsync(
            typeof(ActorFieldReplayBatch).Assembly,
            cancellationToken
        );
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(newOutputDirectory);
        // Only the owner may finalize partial status. A competing invocation must fail before
        // entering the audit try/finally, so it cannot publish status for someone else's run.
        using var owner = new FileStream(
            Path.Combine(newOutputDirectory, ".actor-field-replay-owner"),
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None
        );

        var resultsPath = Path.Combine(newOutputDirectory, "results.jsonl");
        var processed = 0;
        var errors = 0;
        var state = "running";
        var extractor = new RecordingCreditExtractor();
        ActorFieldReplayReport? report = null;
        try
        {
            await using (
                var writer = new StreamWriter(
                    new FileStream(resultsPath, FileMode.CreateNew, FileAccess.Write),
                    Utf8
                )
            )
            {
                foreach (var field in selection.Fields)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = ExtractField(extractor, field);
                    // The public extractor is synchronous. Finish recording this outcome before
                    // observing cancellation at the next field boundary; an audit must not lose it.
                    await writer.WriteLineAsync(
                        JsonSerializer.Serialize(result, Json).AsMemory(),
                        CancellationToken.None
                    );
                    await writer.FlushAsync(CancellationToken.None);
                    processed++;
                    if (result.State == "error")
                    {
                        errors++;
                    }
                    progress?.Report(processed);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            state = errors == 0 ? "completed" : "completed_partial";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            state = "cancelled";
            throw;
        }
        catch
        {
            state = "failed";
            throw;
        }
        finally
        {
            // Cancellation must not suppress the audit of already written source results.
            report = new ActorFieldReplayReport(
                state,
                Version,
                RecordingCreditExtractor.Version,
                selection.Fields.Count,
                processed,
                errors,
                selection.Input,
                selection.Receipt,
                selection.BundleManifest,
                selection.FieldIndex,
                apiAssembly,
                toolAssembly,
                File.Exists(resultsPath)
                    ? await FingerprintAsync(resultsPath, CancellationToken.None)
                    : null
            );
            await WriteNewJsonAsync(
                Path.Combine(newOutputDirectory, "status.json"),
                report,
                CancellationToken.None
            );
            if (state == "completed")
            {
                var manifestPath = Path.Combine(newOutputDirectory, "manifest.json");
                await WriteNewJsonAsync(manifestPath + ".writing", report, CancellationToken.None);
                File.Move(manifestPath + ".writing", manifestPath);
            }
        }

        return report!;
    }

    private static ActorFieldReplayResult ExtractField(
        RecordingCreditExtractor extractor,
        ActorFieldReplayInput field
    )
    {
        RecordingCreditExtraction? extraction = null;
        try
        {
            extraction = extractor.Extract(
                new Dictionary<string, string?> { [field.Field] = field.Text }
            );
            var citations = extraction
                .Credits.SelectMany(credit =>
                    new[] { credit.Evidence }.Concat(credit.ActorFacts?.Citations() ?? [])
                )
                .Concat(extraction.Unresolved.Select(item => item.Evidence));
            foreach (var evidence in citations)
            {
                ValidateEvidence(field, evidence);
            }
            return new(
                field.TaskId,
                field.EvidenceId,
                field.Field,
                field.FieldSha256,
                "extracted",
                extraction,
                null
            );
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException and not OperationCanceledException)
        {
            return new(
                field.TaskId,
                field.EvidenceId,
                field.Field,
                field.FieldSha256,
                "error",
                extraction,
                new(exception.GetType().FullName ?? exception.GetType().Name, exception.Message)
            );
        }
    }

    private static void ValidateEvidence(
        ActorFieldReplayInput field,
        NormalizationTextEvidence evidence
    )
    {
        var runes = field.Text.EnumerateRunes().ToArray();
        if (
            evidence.Field != field.Field
            || evidence.JsonPointer != "/" + field.Field.Replace("~", "~0").Replace("/", "~1")
            || evidence.StartCodePoint < 0
            || evidence.EndCodePoint <= evidence.StartCodePoint
            || evidence.EndCodePoint > runes.Length
        )
        {
            throw new InvalidDataException(
                "Extractor evidence escaped its original field or Unicode span."
            );
        }
        var quote = string.Concat(
            runes[evidence.StartCodePoint..evidence.EndCodePoint].Select(rune => rune.ToString())
        );
        if (quote != evidence.Quote)
        {
            throw new InvalidDataException(
                "Extractor evidence quote does not match the original raw field."
            );
        }
    }

    private static async Task<Selection> ReadSelectionAsync(
        string inputPath,
        string receiptPath,
        string bundle,
        CancellationToken ct
    )
    {
        try
        {
            return await ReadSelectionCoreAsync(inputPath, receiptPath, bundle, ct);
        }
        catch (Exception exception)
            when (exception
                    is JsonException
                        or KeyNotFoundException
                        or InvalidOperationException
                        or DecoderFallbackException
                        or FormatException
            )
        {
            throw new InvalidDataException(
                "Malformed actor-field replay input metadata: " + exception.Message,
                exception
            );
        }
    }

    private static async Task<Selection> ReadSelectionCoreAsync(
        string inputPath,
        string receiptPath,
        string bundle,
        CancellationToken ct
    )
    {
        var input = await ReadBoundedAsync(inputPath, ct);
        var receiptBytes = await ReadBoundedAsync(receiptPath, ct);
        var manifestBytes = await ReadBoundedAsync(Path.Combine(bundle, "manifest.json"), ct);
        var indexBytes = await ReadBoundedAsync(Path.Combine(bundle, "field-index.jsonl"), ct);
        using var receipt = JsonDocument.Parse(receiptBytes);
        using var manifest = JsonDocument.Parse(manifestBytes);
        var r = receipt.RootElement;
        var m = manifest.RootElement;
        Require(
            r.GetProperty("schema_version").GetString() == "actor-field-replay-input-v1"
                && r.GetProperty("state").GetString() == "completed"
                && r.GetProperty("usage").GetString() == "development_only"
                && r.GetProperty("task_count").GetInt32() == FieldCount
                && m.GetProperty("schema_version").GetString() == "actor-facts-development-v1"
                && m.GetProperty("usage").GetString() == "development_only"
                && m.GetProperty("evaluation_eligible").ValueKind == JsonValueKind.False
                && m.GetProperty("source_scope").GetProperty("whole_fields").GetInt32()
                    == FieldCount,
            "Unexpected development field population or usage contract."
        );
        CheckBytes(input, r.GetProperty("fields_file"));
        Require(
            r.GetProperty("fields_file").GetProperty("name").GetString()
                == Path.GetFileName(inputPath),
            "Export field filename changed."
        );
        CheckBytes(manifestBytes, r.GetProperty("frozen_bundle").GetProperty("manifest"));
        CheckBytes(indexBytes, r.GetProperty("frozen_bundle").GetProperty("field_index"));
        CheckBytes(indexBytes, m.GetProperty("files").GetProperty("field-index.jsonl"));
        var provenance = r.GetProperty("source_provenance");
        Require(
            provenance.GetProperty("annotation_evidence_sha256").GetString()
                == m.GetProperty("source_annotation_evidence_sha256").GetString()
                && provenance.GetProperty("original_sources_sha256").GetString()
                    == m.GetProperty("original_sources").GetProperty("sha256").GetString()
                && provenance.GetProperty("reservation_index_sha256").GetString()
                    == m.GetProperty("reservation_index").GetProperty("sha256").GetString()
                && provenance.GetProperty("labels_sha256").GetString()
                    == m.GetProperty("files")
                        .GetProperty("labels.jsonl")
                        .GetProperty("sha256")
                        .GetString()
                && provenance.GetProperty("citations_sha256").GetString()
                    == m.GetProperty("files")
                        .GetProperty("citations.jsonl")
                        .GetProperty("sha256")
                        .GetString(),
            "Export provenance differs from the frozen source bundle."
        );

        var expected = ReadIndex(indexBytes);
        var fields = ReadFields(input, expected);
        return new(
            fields,
            Fingerprint(inputPath, input),
            Fingerprint(receiptPath, receiptBytes),
            Fingerprint(Path.Combine(bundle, "manifest.json"), manifestBytes),
            Fingerprint(Path.Combine(bundle, "field-index.jsonl"), indexBytes)
        );
    }

    private static Dictionary<string, FieldMetadata> ReadIndex(byte[] bytes)
    {
        var expected = new Dictionary<string, FieldMetadata>(StringComparer.Ordinal);
        foreach (var line in PhysicalLines(bytes))
        {
            using var document = JsonDocument.Parse(line);
            var row = document.RootElement;
            var item = new FieldMetadata(
                row.GetProperty("evidence_id").GetString()!,
                row.GetProperty("field").GetString()!,
                row.GetProperty("field_path").GetString()!,
                row.GetProperty("field_sha256").GetString()!,
                row.GetProperty("code_points").GetInt32()
            );
            Require(
                row.GetProperty("artist_partition").GetString() == "development"
                    && row.GetProperty("family_partition").GetString() == "development",
                "Field index contains a held-out source."
            );
            Require(
                expected.TryAdd(row.GetProperty("task_id").GetString()!, item),
                "Duplicate frozen field task."
            );
        }
        Require(expected.Count == FieldCount, "Incomplete frozen field index.");
        return expected;
    }

    private static List<ActorFieldReplayInput> ReadFields(
        byte[] bytes,
        Dictionary<string, FieldMetadata> expected
    )
    {
        var fields = new List<ActorFieldReplayInput>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in PhysicalLines(bytes))
        {
            var field =
                JsonSerializer.Deserialize<ActorFieldReplayInput>(line, Json)
                ?? throw new InvalidDataException("Missing exported original field.");
            Require(
                !string.IsNullOrEmpty(field.TaskId) && seen.Add(field.TaskId),
                "Missing or duplicate exported task."
            );
            Require(
                expected.TryGetValue(field.TaskId, out var meta),
                "Exported task is outside the frozen population."
            );
            Require(
                field.EvidenceId == meta!.EvidenceId
                    && field.Field == meta.Field
                    && field.FieldPath == meta.FieldPath
                    && field.FieldSha256 == meta.Sha256
                    && field.Text is not null
                    && Hash(Utf8.GetBytes(field.Text)) == meta.Sha256
                    && field.Text.EnumerateRunes().Count() == meta.CodePoints,
                "Exported field identity or original text hash changed."
            );
            fields.Add(field);
        }
        Require(seen.SetEquals(expected.Keys), "Export is missing original whole fields.");
        return fields;
    }

    private static IEnumerable<string> PhysicalLines(byte[] bytes)
    {
        // ReadLine recognizes physical LF/CRLF; Unicode paragraph separators remain field content.
        using var reader = new StringReader(Utf8.GetString(bytes));
        while (reader.ReadLine() is { } line)
        {
            Require(line.Length > 0, "Blank physical JSONL record.");
            yield return line;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken ct)
    {
        Require(
            new FileInfo(path).Length <= MaximumFileBytes,
            "Replay input exceeds its bounded file size."
        );
        var bytes = await File.ReadAllBytesAsync(path, ct);
        Require(
            bytes.Length <= MaximumFileBytes,
            "Replay input grew beyond its bounded file size."
        );
        return bytes;
    }

    private static void CheckBytes(byte[] bytes, JsonElement receipt) =>
        Require(
            receipt.GetProperty("bytes").GetInt64() == bytes.LongLength
                && receipt.GetProperty("sha256").GetString() == Hash(bytes),
            "Replay input receipt mismatch."
        );

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static ActorReplayFile Fingerprint(string path, byte[] bytes) =>
        new(Path.GetFullPath(path), bytes.LongLength, Hash(bytes));

    private static async Task<ActorReplayFile> FingerprintAsync(
        string path,
        CancellationToken ct
    ) => Fingerprint(path, await File.ReadAllBytesAsync(path, ct));

    private static async Task<ActorReplayAssembly> AssemblyReceiptAsync(
        Assembly assembly,
        CancellationToken ct
    ) =>
        new(
            assembly.FullName!,
            assembly.ManifestModule.ModuleVersionId,
            await FingerprintAsync(assembly.Location, ct)
        );

    private static async Task WriteNewJsonAsync(string path, object value, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        await JsonSerializer.SerializeAsync(stream, value, Json, ct);
        await stream.FlushAsync(ct);
    }

    private sealed record Selection(
        List<ActorFieldReplayInput> Fields,
        ActorReplayFile Input,
        ActorReplayFile Receipt,
        ActorReplayFile BundleManifest,
        ActorReplayFile FieldIndex
    );

    private sealed record FieldMetadata(
        string EvidenceId,
        string Field,
        string FieldPath,
        string Sha256,
        int CodePoints
    );
}

/// <summary>One authenticated whole original field; no annotation answers are supplied to extraction.</summary>
public sealed record ActorFieldReplayInput(
    [property: JsonPropertyName("task_id")] string TaskId,
    [property: JsonPropertyName("evidence_id")] string EvidenceId,
    [property: JsonPropertyName("field")] string Field,
    [property: JsonPropertyName("field_path")] string FieldPath,
    [property: JsonPropertyName("field_sha256")] string FieldSha256,
    [property: JsonPropertyName("field_text")] string Text
);

public sealed record ActorReplayFile(string Path, long Bytes, string Sha256);

public sealed record ActorReplayAssembly(string Name, Guid ModuleVersionId, ActorReplayFile File);

public sealed record ActorFieldReplayError(string Type, string Message);

/// <summary>Actual extractor outcome, including empty output and unresolved source claims.</summary>
public sealed record ActorFieldReplayResult(
    string TaskId,
    string EvidenceId,
    string Field,
    string FieldSha256,
    string State,
    RecordingCreditExtraction? Extraction,
    ActorFieldReplayError? Error
);

/// <summary>Run provenance. Only state completed with all fields and zero errors permits a completion manifest.</summary>
public sealed record ActorFieldReplayReport(
    string State,
    string Version,
    string ExtractorVersion,
    int ExpectedFields,
    int ProcessedFields,
    int ErrorFields,
    ActorReplayFile Input,
    ActorReplayFile ExportReceipt,
    ActorReplayFile BundleManifest,
    ActorReplayFile FieldIndex,
    ActorReplayAssembly ApiAssembly,
    ActorReplayAssembly ToolAssembly,
    ActorReplayFile? Results
);
