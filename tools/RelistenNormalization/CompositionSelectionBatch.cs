using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Runs an explicitly bounded selection plan over completed retained hypotheses.
/// Preflight reads no key or database. Execution uses the existing capped cache;
/// append-only chunks retain interrupted prefixes and original source coordinates.
/// Resume never changes attempt policy or retries an uncertain provider attempt.
/// </summary>
public sealed partial class CompositionSelectionBatch
{
    public const string PlanVersion = "composition-selection-plan-v1";
    public const string Version = "composition-selection-batch-v1";
    public const string TargetContextPlanVersion = "composition-selection-plan-v2";
    public const string TargetContextVersion = "composition-selection-batch-v2";
    private const int MaximumPlanBytes = 8 * 1024 * 1024;
    private const int MaximumLineCharacters = 64 * 1024 * 1024;
    private const long MaximumChunkCharacters = 8L * 1024 * 1024 * 1024;
    private const long ResultReserveBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = ShadowCatalogRun.Json;
    private readonly NormalizationExtractionCacheService? suppliedCache;
    private readonly ICompositionHypothesisSelector? suppliedSelector;

    public CompositionSelectionBatch() { }

    /// <summary>Supplies an operator-owned cache and selector, including fake transport for local tests.</summary>
    public CompositionSelectionBatch(
        NormalizationExtractionCacheService cache,
        ICompositionHypothesisSelector selector
    )
    {
        suppliedCache = cache;
        suppliedSelector = selector;
    }

    /// <summary>
    /// Writes only plan/preflight files when preflightOnly is true. Execute into a
    /// different new directory after approval. A resume requires its exact old plan.
    /// </summary>
    public async Task<CompositionSelectionBatchReport> RunAsync(
        string planPath,
        string outputDirectory,
        string planSha256,
        bool resume = false,
        bool preflightOnly = false,
        CancellationToken ct = default
    )
    {
        if (resume && preflightOnly)
        {
            throw new ArgumentException(
                "Preflight uses a new directory, not an execution checkpoint."
            );
        }
        planPath = Path.GetFullPath(planPath);
        outputDirectory = Path.GetFullPath(outputDirectory);
        var prepared = await PrepareAsync(planPath, planSha256, ct);
        using var duration = CancellationTokenSource.CreateLinkedTokenSource(ct);
        duration.CancelAfter(TimeSpan.FromSeconds(prepared.Plan.MaximumDurationSeconds));
        ct = duration.Token;
        if (preflightOnly)
        {
            RequireNewDirectory(outputDirectory);
            Directory.CreateDirectory(outputDirectory);
            CheckResources(prepared.Plan, outputDirectory);
            await WritePreparationAsync(outputDirectory, prepared, ct);
            var report = await ReportAsync(
                outputDirectory,
                prepared,
                "preflight_completed",
                null,
                ct
            );
            await AtomicJsonAsync(outputDirectory, "manifest.json", report, ct, prepared.Plan);
            return report;
        }

        var methods = new Dictionary<string, string>
        {
            [nameof(CompositionSelectionBatch)] = prepared.BatchVersion,
            [nameof(LunaCompositionHypothesisSelector)] = prepared.SelectorVersion,
        };
        var completion = Path.Combine(outputDirectory, "manifest.json");
        if (!resume)
        {
            RequireNewDirectory(outputDirectory);
        }
        await using var run = new ShadowCatalogRun(outputDirectory, planSha256, methods, resume);
        await ValidatePrefixAsync(outputDirectory, prepared, run.CompletedLine, ct);
        if (resume && File.Exists(completion))
        {
            var saved = JsonSerializer.Deserialize<CompositionSelectionBatchReport>(
                await File.ReadAllTextAsync(completion, ct),
                Json
            )!;
            if (saved.State == "completed" && run.CompletedLine == prepared.Rows.Count)
            {
                await ValidateCompletionAsync(outputDirectory, prepared, saved, ct);
                return await ReportAsync(
                    outputDirectory,
                    prepared,
                    "completed",
                    saved.JobAccountedUsd,
                    ct
                );
            }
        }
        CheckResources(
            prepared.Plan,
            outputDirectory,
            prepared.ControlReserveBytes + ResultReserveBytes
        );
        await WritePreparationAsync(outputDirectory, prepared, ct);

        // No key or job is touched until all selected request facts and the full
        // candidate-bearing denominator have passed preflight authentication.
        var cache =
            suppliedCache
            ?? new NormalizationExtractionCacheService(NormalizationLocalDatabase.Create());
        await cache.CreateJobAsync(
            prepared.Plan.JobUuid,
            prepared.Plan.CapUsd,
            ct,
            prepared.Plan.MaximumTotalAttempts
        );
        using var http = new HttpClient();
        var selector =
            suppliedSelector
            ?? new CachedCompositionHypothesisSelector(
                new LunaCompositionHypothesisSelector(
                    http,
                    cache,
                    () => Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? ""
                ),
                prepared.Plan.JobUuid,
                prepared.Plan.Mode == "retry_failed"
            );
        var completed = run.CompletedLine;
        try
        {
            while (completed < prepared.Rows.Count)
            {
                ct.ThrowIfCancellationRequested();
                var inputs = prepared
                    .Rows.Skip(checked((int)completed))
                    .Take(prepared.Plan.Concurrency)
                    .ToArray();
                CheckResources(
                    prepared.Plan,
                    outputDirectory,
                    prepared.ControlReserveBytes
                        + inputs.Sum(input =>
                            ResultReserveBytes
                            + JsonSerializer.SerializeToUtf8Bytes(input.Row, Json).LongLength
                        )
                );

                // Every admitted request settles in the shared cache before an
                // exception escapes. A failed batch can replay successful siblings
                // for free; it cannot silently retry uncertain provider requests.
                var results = await Task.WhenAll(
                    inputs.Select(input => SelectAsync(input, selector, ct))
                );
                foreach (var row in results)
                {
                    await run.WriteInputAsync([Result(row, prepared.BatchVersion)]);
                }
                completed += results.Length;

                // Only this ordered writer advances the checkpoint. Responses
                // already received retain durable output even after cancellation.
                await run.SaveAsync(
                    completed,
                    "running",
                    Population(prepared),
                    CancellationToken.None
                );
            }
            await RecheckPinsAsync(prepared.Pins, ct);
            var job = await cache.GetJobAsync(prepared.Plan.JobUuid, ct);
            var report = await ReportAsync(
                outputDirectory,
                prepared,
                "completed",
                job.AccountedUsd,
                ct
            );
            CheckResources(prepared.Plan, outputDirectory);
            await AtomicJsonAsync(outputDirectory, "manifest.json", report, ct, prepared.Plan);
            return report;
        }
        catch
        {
            await run.SaveAsync(
                completed,
                "interrupted",
                Population(prepared),
                CancellationToken.None
            );
            throw;
        }
    }

    private static async Task<CompositionSelectionBatchRow> SelectAsync(
        PreparedRow input,
        ICompositionHypothesisSelector selector,
        CancellationToken ct
    )
    {
        if (input.Row.SkipReason is not null)
        {
            return new(input.Row, null);
        }
        var result = await selector.SelectAsync(input.Hypothesis!.Song, input.Candidates, ct);
        CompositionHypothesisProjector.ValidateSelection(input.Hypothesis, result);
        return new(input.Row, result);
    }

    private static async Task<Prepared> PrepareAsync(
        string planPath,
        string expectedSha,
        CancellationToken ct
    )
    {
        if (new FileInfo(planPath).Length > MaximumPlanBytes)
        {
            throw new InvalidDataException("Selection plan exceeds its byte bound.");
        }
        var bytes = await File.ReadAllBytesAsync(planPath, ct);
        if (Hash(bytes) != expectedSha)
        {
            throw new InvalidDataException("Selection plan changed from its approved SHA256.");
        }
        var plan = JsonSerializer.Deserialize<CompositionSelectionPlan>(bytes, Json)!;
        ValidatePlan(plan);
        var pins = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [planPath] = expectedSha,
        };
        await PinAsync(plan.HypothesisManifest, pins, ct);
        var manifest = JsonSerializer.Deserialize<CompositionHypothesisOutput>(
            await File.ReadAllTextAsync(plan.HypothesisManifest.Path, ct),
            Json
        )!;
        if (manifest.Version != CompositionHypothesisBatch.Version || !manifest.Complete)
        {
            throw new InvalidDataException(
                "Selection requires a completed retained-hypothesis image."
            );
        }
        var root = Path.GetDirectoryName(Path.GetFullPath(plan.HypothesisManifest.Path))!;
        var works = new Dictionary<string, RetainedCompositionWork>(StringComparer.Ordinal);
        var hypotheses = new Dictionary<string, CompositionHypothesisResult>(
            StringComparer.Ordinal
        );
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        var origins = new Dictionary<string, CompositionSelectionOrigin>(StringComparer.Ordinal);
        long songs = 0;
        foreach (var artifact in manifest.Chunks)
        {
            var path = ArtifactPath(root, artifact.Path);
            await PinAsync(artifact with { Path = path }, pins, ct);
            long line = 0;
            await foreach (
                var text in ShadowJsonLines.ReadTextAsync(
                    path,
                    MaximumLineCharacters,
                    MaximumChunkCharacters,
                    ct
                )
            )
            {
                line++;
                using var document = JsonDocument.Parse(text);
                var record = document.RootElement;
                var domain = record.GetProperty("domain").GetString();
                if (domain is not ("retained_work_candidates" or "composition_hypotheses"))
                {
                    continue;
                }
                var proposal = record.GetProperty("proposal");
                if (domain == "composition_hypotheses")
                {
                    songs++;
                    // The completed producer owns the logical whole-song census.
                    // Stream empty-candidate rows without retaining their keys or
                    // deserializing their large source context. Only candidate-bearing
                    // songs need replay reconciliation for this selection stage.
                    if (proposal.GetProperty("candidates").GetArrayLength() == 0)
                    {
                        continue;
                    }
                }
                var key = domain + ":" + record.GetProperty("inputKey").GetString();
                var digest = Hash(Encoding.UTF8.GetBytes(proposal.GetRawText()));
                if (seen.TryGetValue(key, out var previous))
                {
                    if (previous != digest)
                    {
                        throw new InvalidDataException(
                            "A retained hypothesis key has contradictory results."
                        );
                    }
                    continue;
                }
                seen.Add(key, digest);
                if (domain == "retained_work_candidates")
                {
                    var work = proposal.Deserialize<RetainedCompositionWork>(Json)!;
                    works.Add(work.EntityKey, work);
                }
                else
                {
                    var hypothesis = proposal.Deserialize<CompositionHypothesisResult>(Json)!;
                    if (hypothesis.Candidates.Count == 0)
                    {
                        continue;
                    }
                    var songKey = Key(hypothesis.Song);
                    hypotheses.Add(songKey, hypothesis);
                    origins.Add(songKey, new(artifact.Path, line, artifact.Sha256));
                }
            }
            if (line != artifact.Rows)
            {
                throw new InvalidDataException(
                    "Hypothesis artifact physical rows differ from its manifest."
                );
            }
        }
        if (
            songs < manifest.Songs
            || works.Count != manifest.Works
            || hypotheses.Count != manifest.Outcomes.GetValueOrDefault("hypotheses")
        )
        {
            throw new InvalidDataException(
                "The complete retained hypothesis census differs from its manifest."
            );
        }
        var contexts = await ReadTargetContextsAsync(plan, manifest, pins, ct);
        var rows = new List<PreparedRow>();
        foreach (var key in plan.Songs)
        {
            if (!hypotheses.TryGetValue(Key(key), out var hypothesis))
            {
                rows.Add(
                    new(
                        new(key, null, null, null, "missing_candidate_bearing_hypothesis"),
                        null,
                        []
                    )
                );
                continue;
            }
            if (contexts is not null)
            {
                hypothesis = hypothesis with
                {
                    Song = hypothesis.Song with { TargetContext = contexts[Key(key)] },
                };
            }
            var candidates = hypothesis
                .Candidates.Select(candidate =>
                    works.TryGetValue(candidate.WorkEntityKey, out var work)
                        ? work
                        : throw new InvalidDataException(
                            "A supplied work is absent from the retained work image."
                        )
                )
                .ToArray();
            var input = CachedCompositionHypothesisSelector.ToInput(hypothesis.Song, candidates);
            var description = LunaCompositionHypothesisSelector.DescribeRequest(input);
            var inputSha = Hash(JsonSerializer.SerializeToUtf8Bytes(input, Json));
            rows.Add(
                new(
                    new(
                        key,
                        inputSha,
                        origins[Key(key)],
                        description,
                        description.WithinRequestLimit ? null : "input_too_large"
                    ),
                    hypothesis,
                    candidates
                )
            );
        }
        var skips = rows.Where(row => row.Row.SkipReason is not null)
            .GroupBy(row => row.Row.SkipReason!)
            .ToDictionary(group => group.Key, group => group.LongCount());
        var eligible = rows.Count(row => row.Row.SkipReason is null);
        var preflight = new CompositionSelectionPreflight(
            plan.TargetContextManifest is null ? Version : TargetContextVersion,
            expectedSha,
            plan.Mode,
            manifest.Songs,
            hypotheses.Count,
            works.Count,
            rows.Count,
            eligible,
            hypotheses.Count - rows.Count(row => row.Hypothesis is not null),
            rows.Where(row => row.Row.SkipReason is null)
                .Sum(row => row.Row.Request!.MaximumReservedUsd),
            skips,
            rows.Select(row => row.Row).ToArray()
        );
        await RecheckPinsAsync(pins, ct);
        var maximumChunks = Enumerable
            .Range(0, rows.Count)
            .Select(_ => new MusicConsolidationArtifact(
                "composition_selections/9999999999999999999-ffffffffffffffffffffffffffffffff.jsonl.gz",
                long.MaxValue,
                new string('f', 64),
                long.MaxValue
            ))
            .ToArray();
        var maximumReport = new CompositionSelectionBatchReport(
            preflight.Version,
            "preflight_completed",
            expectedSha,
            plan.JobUuid,
            plan.TargetContextManifest is null
                ? LunaCompositionHypothesisSelector.Version
                : LunaCompositionHypothesisSelector.TargetContextVersion,
            preflight,
            long.MaxValue,
            new Dictionary<string, long> { [new string('x', 4096)] = long.MaxValue },
            int.MaxValue,
            decimal.MaxValue,
            decimal.MaxValue,
            maximumChunks
        );
        // The final report repeats the bounded preflight once and may list one
        // immutable chunk per selected input. Progress/checkpoint rewrites contain
        // only compact counters, hashes and fixed method names (under 64 KiB).
        var controlReserve =
            JsonSerializer.SerializeToUtf8Bytes(maximumReport, Json).LongLength + 65536;
        return new(plan, bytes, expectedSha, rows, preflight, pins, controlReserve);
    }

    private static void ValidatePlan(CompositionSelectionPlan plan)
    {
        if (
            plan.Version
                != (plan.TargetContextManifest is null ? PlanVersion : TargetContextPlanVersion)
            || plan.JobUuid == Guid.Empty
            || plan.Mode is not ("initial" or "retry_failed")
            || plan.MaximumInputs < 1
            || plan.Songs.Count == 0
            || plan.Songs.Count > plan.MaximumInputs
            || plan.CapUsd <= 0
            || plan.MaximumTotalAttempts < 1
            || plan.MaximumDurationSeconds < 1
            || plan.MaximumOutputBytes < ResultReserveBytes
            || plan.MinimumFreeBytes < 0
            || plan.Concurrency is < 1 or > 4
        )
        {
            throw new ArgumentException(
                "Selection needs an explicit bounded initial or retry_failed plan."
            );
        }
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var song in plan.Songs)
        {
            if (
                string.IsNullOrWhiteSpace(song.Scope)
                || song.ArtistUuid == Guid.Empty
                || !song.SongEntityKey.StartsWith("repertoire:", StringComparison.Ordinal)
                || !keys.Add(Key(song))
            )
            {
                throw new ArgumentException(
                    "Selection keys must be distinct exact scoped artist songs."
                );
            }
        }
    }

    private static object Population(Prepared prepared) =>
        new
        {
            prepared.Preflight.HypothesisSongs,
            prepared.Preflight.CandidateBearingSongs,
            prepared.Preflight.SelectedInputs,
            prepared.Plan.Mode,
            prepared.Plan.Concurrency,
        };

    private static ShadowCatalogResult Result(CompositionSelectionBatchRow row, string method) =>
        new(
            "composition_selections",
            Key(row.Input.Key),
            "repertoire_target",
            Guid.Empty,
            row.Input.Key.ArtistUuid,
            "",
            "retained_music_target",
            row.Selection?.Outcome ?? "skipped",
            row.Input.SkipReason ?? row.Selection?.Inference?.Reason,
            method,
            new[] { row.Input.Key.SongEntityKey },
            row,
            row.Input.HypothesisOrigin
        );

    private static async Task ValidatePrefixAsync(
        string output,
        Prepared prepared,
        long completed,
        CancellationToken ct
    )
    {
        if (completed < 0 || completed > prepared.Rows.Count)
        {
            throw new InvalidDataException("Selection checkpoint exceeds the approved input list.");
        }
        var rows = await ReadResultsAsync(output, prepared, ct);
        for (var index = 0; index < completed; index++)
        {
            if (!rows.ContainsKey(Key(prepared.Rows[index].Row.Key)))
            {
                throw new InvalidDataException("Acknowledged selection output is missing.");
            }
        }
    }

    private static async Task ValidateCompletionAsync(
        string output,
        Prepared prepared,
        CompositionSelectionBatchReport report,
        CancellationToken ct
    )
    {
        if (
            report.Version != prepared.BatchVersion
            || report.PlanSha256 != prepared.Sha
            || report.JobUuid != prepared.Plan.JobUuid
            || report.Method != prepared.SelectorVersion
        )
        {
            throw new InvalidDataException(
                "Completed selections belong to a different plan or method."
            );
        }

        var pinned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifact in report.Chunks)
        {
            var path = ArtifactPath(output, artifact.Path);
            if (
                !pinned.Add(path)
                || new FileInfo(path).Length != artifact.Bytes
                || await HashFileAsync(path, ct) != artifact.Sha256
            )
            {
                throw new InvalidDataException(
                    "Completed selection output changed from its manifest."
                );
            }
        }
        var folder = Path.Combine(output, "composition_selections");
        var actual = (
            Directory.Exists(folder)
                ? Directory.GetFiles(folder, "*.jsonl.gz")
                : Array.Empty<string>()
        )
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(pinned))
        {
            throw new InvalidDataException(
                "Completed selection chunks differ from their manifest."
            );
        }
    }

    private static async Task<Dictionary<string, CompositionSelectionBatchRow>> ReadResultsAsync(
        string output,
        Prepared prepared,
        CancellationToken ct
    )
    {
        var selected = prepared.Rows.ToDictionary(row => Key(row.Row.Key), StringComparer.Ordinal);
        var results = new Dictionary<string, CompositionSelectionBatchRow>(StringComparer.Ordinal);
        var folder = Path.Combine(output, "composition_selections");
        foreach (
            var path in Directory.Exists(folder)
                ? Directory.GetFiles(folder, "*.jsonl.gz").Order(StringComparer.Ordinal)
                : Enumerable.Empty<string>()
        )
        {
            await foreach (
                var text in ShadowJsonLines.ReadTextAsync(
                    path,
                    MaximumLineCharacters,
                    MaximumChunkCharacters,
                    ct
                )
            )
            {
                using var document = JsonDocument.Parse(text);
                var record = document.RootElement;
                var row = record
                    .GetProperty("proposal")
                    .Deserialize<CompositionSelectionBatchRow>(Json)!;
                var key = Key(row.Input.Key);
                if (
                    record.GetProperty("domain").GetString() != "composition_selections"
                    || record.GetProperty("inputKey").GetString() != key
                    || !selected.TryGetValue(key, out var input)
                    || JsonSerializer.Serialize(row.Input, Json)
                        != JsonSerializer.Serialize(input.Row, Json)
                )
                {
                    throw new InvalidDataException(
                        "Saved selection output differs from its exact approved input."
                    );
                }
                if (row.Selection is not null)
                {
                    if (input.Hypothesis is null || input.Row.SkipReason is not null)
                    {
                        throw new InvalidDataException(
                            "A skipped input cannot acquire a selection."
                        );
                    }
                    CompositionHypothesisProjector.ValidateSelection(
                        input.Hypothesis,
                        row.Selection
                    );
                }
                results[key] = row;
            }
        }
        return results;
    }

    private static async Task<CompositionSelectionBatchReport> ReportAsync(
        string output,
        Prepared prepared,
        string state,
        decimal? accounted,
        CancellationToken ct
    )
    {
        var rows = await ReadResultsAsync(output, prepared, ct);
        var chunks = new List<MusicConsolidationArtifact>();
        var folder = Path.Combine(output, "composition_selections");
        foreach (
            var path in Directory.Exists(folder)
                ? Directory.GetFiles(folder, "*.jsonl.gz").Order(StringComparer.Ordinal)
                : Enumerable.Empty<string>()
        )
        {
            long count = 0;
            await foreach (
                var _ in ShadowJsonLines.ReadTextAsync(
                    path,
                    MaximumLineCharacters,
                    MaximumChunkCharacters,
                    ct
                )
            )
            {
                count++;
            }
            chunks.Add(
                new(
                    Path.GetRelativePath(output, path),
                    new FileInfo(path).Length,
                    await HashFileAsync(path, ct),
                    count
                )
            );
        }
        return new(
            prepared.BatchVersion,
            state,
            prepared.Sha,
            prepared.Plan.JobUuid,
            prepared.SelectorVersion,
            prepared.Preflight,
            rows.Count,
            rows.Values.GroupBy(row => row.Selection?.Outcome ?? "skipped")
                .ToDictionary(group => group.Key, group => group.LongCount()),
            rows.Values.Count(row => row.Selection?.Inference?.FromCache is true),
            accounted,
            rows.Values.Sum(row => row.Selection?.Inference?.ActualUsd ?? 0m),
            chunks
        );
    }

    private static async Task WritePreparationAsync(
        string output,
        Prepared prepared,
        CancellationToken ct
    )
    {
        var preflightBytes = JsonSerializer.SerializeToUtf8Bytes(prepared.Preflight, Json);
        CheckResources(
            prepared.Plan,
            output,
            prepared.PlanBytes.LongLength + preflightBytes.LongLength + prepared.ControlReserveBytes
        );
        await File.WriteAllBytesAsync(
            Path.Combine(output, "approved-plan.json"),
            prepared.PlanBytes,
            ct
        );
        await AtomicJsonAsync(output, "preflight.json", prepared.Preflight, ct, prepared.Plan);
        CheckResources(prepared.Plan, output);
    }

    private static void CheckResources(
        CompositionSelectionPlan plan,
        string output,
        long reserve = 0
    )
    {
        var parent = Directory.Exists(output) ? output : Path.GetDirectoryName(output)!;
        if (
            new DriveInfo(Path.GetPathRoot(parent)!).AvailableFreeSpace
            < plan.MinimumFreeBytes + reserve
        )
        {
            throw new IOException("Selection stopped to preserve the plan's free-space floor.");
        }
        var retained = Directory.Exists(output)
            ? Directory
                .EnumerateFiles(output, "*", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length)
            : 0;
        if (retained + reserve > plan.MaximumOutputBytes)
        {
            throw new IOException("Selection stopped at its output budget before another attempt.");
        }
    }

    private static async Task PinAsync(
        MusicConsolidationArtifact artifact,
        Dictionary<string, string> pins,
        CancellationToken ct
    )
    {
        var path = Path.GetFullPath(artifact.Path);
        if (
            new FileInfo(path).Length != artifact.Bytes
            || await HashFileAsync(path, ct) != artifact.Sha256
        )
        {
            throw new InvalidDataException("A pinned selection input artifact changed.");
        }
        pins[path] = artifact.Sha256;
    }

    private static async Task RecheckPinsAsync(
        Dictionary<string, string> pins,
        CancellationToken ct
    )
    {
        foreach (var pin in pins)
        {
            if (await HashFileAsync(pin.Key, ct) != pin.Value)
            {
                throw new InvalidDataException(
                    "Selection input changed during preparation or execution."
                );
            }
        }
    }

    private static string ArtifactPath(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (
            Path.IsPathRooted(relative)
            || !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || new FileInfo(path).LinkTarget is not null
        )
        {
            throw new InvalidDataException(
                "Hypothesis chunks must remain within their original output directory."
            );
        }
        return path;
    }

    private static void RequireNewDirectory(string output)
    {
        if (Directory.Exists(output) || File.Exists(output))
        {
            throw new IOException(
                "Selection requires a new output directory or an explicit matching resume."
            );
        }
    }

    private static async Task AtomicJsonAsync(
        string output,
        string filename,
        object value,
        CancellationToken ct,
        CompositionSelectionPlan plan
    )
    {
        var path = Path.Combine(output, filename);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        CheckResources(plan, output, bytes.LongLength);
        await File.WriteAllBytesAsync(path + ".writing", bytes, ct);
        File.Move(path + ".writing", path, true);
    }

    private static string Key(CompositionHypothesisSong song) =>
        Key(new CompositionSelectionSongKey(song.Scope, song.ArtistUuid, song.Song.EntityKey));

    private static string Key(CompositionSelectionSongKey song) =>
        JsonSerializer.Serialize(
            new object[] { song.Scope, song.ArtistUuid, song.SongEntityKey },
            Json
        );

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }

    private sealed record PreparedRow(
        CompositionSelectionPreparedRow Row,
        CompositionHypothesisResult? Hypothesis,
        IReadOnlyList<RetainedCompositionWork> Candidates
    );

    private sealed record Prepared(
        CompositionSelectionPlan Plan,
        byte[] PlanBytes,
        string Sha,
        IReadOnlyList<PreparedRow> Rows,
        CompositionSelectionPreflight Preflight,
        Dictionary<string, string> Pins,
        long ControlReserveBytes
    )
    {
        public string BatchVersion =>
            Plan.TargetContextManifest is null ? Version : TargetContextVersion;
        public string SelectorVersion =>
            Plan.TargetContextManifest is null
                ? LunaCompositionHypothesisSelector.Version
                : LunaCompositionHypothesisSelector.TargetContextVersion;
    }
}
