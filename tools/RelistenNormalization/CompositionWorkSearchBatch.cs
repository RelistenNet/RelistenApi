using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Executes a frozen queue of literal work searches. Dictionary result witnesses
/// remain dictionary evidence; they are never fabricated provider input revisions.
/// Transport, cache, leases and retry admission belong to the existing service.
/// </summary>
public sealed class CompositionWorkSearchBatch(ICompositionWorkSearchClient client)
{
    public const string Version = "composition-work-search-batch-v1";
    public const string RequestVersion = "composition-work-search-request-v1";
    private const long ResponseAllowance = 8L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public async Task<CompositionWorkSearchCheckpoint> RunAsync(
        string requestPath,
        string outputDirectory,
        string requestSha256,
        bool resume = false,
        CancellationToken ct = default
    )
    {
        // Validate the complete queue before even read-only client consultation.
        var prepared = await AuthenticateAsync(requestPath, requestSha256, ct);
        var session = new WorkSearchSession(
            client,
            prepared.Request,
            prepared.Tasks,
            requestSha256,
            Path.GetFullPath(outputDirectory)
        );
        return await session.RunAsync(resume, ct);
    }

    private static async Task<(
        CompositionWorkSearchRequest Request,
        CompositionWorkSearchTask[] Tasks
    )> AuthenticateAsync(string path, string expectedSha, CancellationToken ct)
    {
        path = Path.GetFullPath(path);
        var bytes = await ReadPinnedAsync(path, expectedSha, 64 * 1024, ct);
        var request =
            JsonSerializer.Deserialize<CompositionWorkSearchRequest>(bytes, Json)
            ?? throw new InvalidDataException("A work-search request is required.");
        var limits = request.Limits;
        if (
            request.Version != RequestVersion
            || request.PlanUuid == Guid.Empty
            || limits.MaximumServiceCalls is < 0 or > 2000
            || limits.MaximumNewResponseBytes is < 0 or > 256L * 1024 * 1024
            || limits.MaximumArtifactBytes is <= 0 or > 512L * 1024 * 1024
            || limits.MinimumFreeBytes < 0
            || (limits.MaximumDuration ?? TimeSpan.FromHours(2)) <= TimeSpan.Zero
            || (limits.MaximumDuration ?? TimeSpan.FromHours(2)) > TimeSpan.FromHours(2)
        )
        {
            throw new InvalidDataException(
                "Work-search request version, identity or bounds are invalid."
            );
        }
        var directory = Path.GetDirectoryName(path)!;
        var dictionaryManifest = Path.GetFullPath(request.DictionaryManifest.Path, directory);
        var dictionaryBytes = await ReadPinnedAsync(
            dictionaryManifest,
            request.DictionaryManifest.Sha256,
            16 * 1024 * 1024,
            ct
        );
        var music =
            JsonSerializer.Deserialize<MusicConsolidationInput>(dictionaryBytes, Json)
            ?? throw new InvalidDataException("A completed dictionary export is required.");
        if (
            music.Version != "music-consolidation-input-v1"
            || music.State != "completed"
            || music.DictionaryNames.Rows is < 0 or > 100000
        )
        {
            throw new InvalidDataException(
                "Work searches require a bounded completed dictionary export."
            );
        }
        var dictionary = Path.GetFullPath(
            music.DictionaryNames.Path,
            Path.GetDirectoryName(dictionaryManifest)!
        );
        await RequirePinAsync(dictionary, music.DictionaryNames.Sha256, ct);
        if (new FileInfo(dictionary).Length != music.DictionaryNames.Bytes)
        {
            throw new InvalidDataException("Dictionary byte length differs from its export.");
        }
        var names = await ReadRowsAsync<MusicDictionaryName>(dictionary, 128 * 1024 * 1024, ct);
        await RequirePinAsync(dictionary, music.DictionaryNames.Sha256, ct);
        if (names.Count != music.DictionaryNames.Rows)
        {
            throw new InvalidDataException("Dictionary row census differs from its export.");
        }
        ValidateDictionary(names, music);
        var identityCounts = names
            .GroupBy(name => (name.ArtistUuid, name.LegacySongUuid))
            .ToDictionary(group => group.Key, group => group.Count());
        var shared = names
            .GroupBy(name => (name.ArtistUuid, name.LegacySongUuid))
            .Select(group =>
            {
                if (group.Select(name => name.Title).Distinct(StringComparer.Ordinal).Count() != 1)
                {
                    throw new InvalidDataException(
                        "A captured dictionary identity changes its literal title."
                    );
                }
                return group.First();
            })
            .GroupBy(name => ShadowCompositionAdapter.NormalizeTitle(name.Title))
            .Where(group => group.Select(name => name.ArtistUuid).Distinct().Count() >= 2)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var taskPath = Path.GetFullPath(request.TaskArtifact.Path, directory);
        await RequirePinAsync(taskPath, request.TaskArtifact.Sha256, ct);
        var rows = await ReadRowsAsync<JsonElement>(taskPath, 32 * 1024 * 1024, ct);
        await RequirePinAsync(taskPath, request.TaskArtifact.Sha256, ct);
        var tasks = new List<CompositionWorkSearchTask>();
        var seenFamilies = new HashSet<string>(StringComparer.Ordinal);
        var resources = new HashSet<NormalizationResourceKey>();
        foreach (var row in rows)
        {
            var title = row.GetProperty("retrievalTitleKey").GetString()!;
            if (!seenFamilies.Add(title) || !shared.TryGetValue(title, out var expectedMembers))
            {
                throw new InvalidDataException(
                    "Task artifact changes the shared dictionary population."
                );
            }
            var members = ReadMembers(row, names, expectedMembers, identityCounts);
            var planned = row.GetProperty("tasks").EnumerateArray().ToArray();
            var state = row.GetProperty("state").GetString();
            if (
                (state == "work_search_required") != (planned.Length > 0)
                || state
                    is not (
                        "work_search_required"
                        or "missing_search_text"
                        or "retained_candidates_available"
                        or "retained_query_window_available"
                    )
                || (title.Length == 0) != (state == "missing_search_text")
            )
            {
                throw new InvalidDataException(
                    "Task state cannot invent queries for blank or reused source groups."
                );
            }
            var literals = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in planned)
            {
                var search = item.GetProperty("pageRequest")
                    .Deserialize<MusicBrainzSearchPageRequest>(Json)!;
                var query = search.Query;
                if (
                    query.Kind != MusicBrainzLookupKind.Work
                    || query.RelatedArtist != null
                    || search.Offset != 0
                    || search.CacheMode != MusicBrainzCacheMode.ReuseFresh
                    || search.MaximumCacheAge != TimeSpan.FromDays(7)
                    || search.PlanIdentity != null
                    || !members.Any(member => member.Title == query.Text)
                    || !literals.Add(query.Text)
                )
                {
                    throw new InvalidDataException(
                        "Only observed literal offset-zero work queries without artist constraints are allowed."
                    );
                }
                var descriptor = MusicBrainzCandidateQueryBuilder.CreateDescriptor(search);
                if (
                    item.GetProperty("existingResource").Deserialize<NormalizationResourceKey>(Json)
                        != descriptor.Resource
                    || !resources.Add(descriptor.Resource)
                )
                {
                    throw new InvalidDataException(
                        "A work task duplicates or changes its public query resource."
                    );
                }
                var queryMembers = members.Where(member => member.Title == query.Text).ToArray();
                var catalog = queryMembers
                    .Select(member => member.LegacySongUuid)
                    .Distinct()
                    .Order()
                    .ToArray();
                var declared = item.GetProperty("sourceMemberUuids").Deserialize<Guid[]>(Json)!;
                if (!declared.Order().SequenceEqual(catalog))
                {
                    throw new InvalidDataException(
                        "A work task changes its cited dictionary members."
                    );
                }
                var uuid = new Guid(
                    SHA256.HashData(
                        Encoding.UTF8.GetBytes(
                            request.PlanUuid.ToString("D") + ":" + descriptor.Resource.ProviderKey
                        )
                    )[..16]
                );
                var command = new ShadowCompositionAcquisitionCommand(
                    uuid,
                    descriptor.Resource,
                    search with
                    {
                        PlanIdentity = new(request.PlanUuid, expectedSha),
                    },
                    null,
                    catalog,
                    "shared_dictionary_work_search_only"
                );
                tasks.Add(new(title, command, queryMembers));
            }
            if (planned.Length > 0 && !literals.SetEquals(members.Select(member => member.Title)))
            {
                throw new InvalidDataException(
                    "A work-search family omits a retained literal variant."
                );
            }
        }
        if (!seenFamilies.SetEquals(shared.Keys) || tasks.Count > 10000)
        {
            throw new InvalidDataException(
                "Task artifact is incomplete or exceeds the queue bound."
            );
        }
        return (request, tasks.ToArray());
    }

    private static void ValidateDictionary(
        IReadOnlyList<MusicDictionaryName> names,
        MusicConsolidationInput input
    )
    {
        var sources = input
            .Sources.Select(source => (source.Scope, Path.GetFullPath(source.Run)))
            .ToHashSet();
        var chunks = input.WitnessChunks.ToDictionary(
            chunk => (Path.GetFullPath(chunk.Run), chunk.Chunk),
            chunk => chunk.Sha256
        );
        foreach (var name in names)
        {
            var witness = name.Witness;
            if (
                name.ArtistUuid == Guid.Empty
                || name.LegacySongUuid == Guid.Empty
                || !sources.Contains((name.Scope, Path.GetFullPath(witness.Run)))
                || witness.Line < 1
                || witness.ValuePointer == null
                || !chunks.TryGetValue((Path.GetFullPath(witness.Run), witness.Chunk), out var sha)
                || sha != witness.ChunkSha256
                || (
                    witness.DocumentKind == "catalog_context"
                        ? witness.RecordId != 0
                        : witness.DocumentKind != "retained_result" || witness.RecordId < 1
                )
            )
            {
                throw new InvalidDataException(
                    "Dictionary evidence lies outside its pinned source scope."
                );
            }
        }
    }

    private static MusicDictionaryName[] ReadMembers(
        JsonElement family,
        IReadOnlyList<MusicDictionaryName> names,
        IReadOnlyList<MusicDictionaryName> expected,
        IReadOnlyDictionary<(Guid, Guid), int> identityCounts
    )
    {
        var found = new HashSet<(Guid, Guid)>();
        var members = new List<MusicDictionaryName>();
        foreach (var member in family.GetProperty("sourceMembers").EnumerateArray())
        {
            var artist = member.GetProperty("artistUuid").GetGuid();
            var song = member.GetProperty("legacySongUuid").GetGuid();
            var title = member.GetProperty("title").GetString();
            if (
                !found.Add((artist, song))
                || !expected.Any(name =>
                    name.ArtistUuid == artist && name.LegacySongUuid == song && name.Title == title
                )
            )
            {
                throw new InvalidDataException(
                    "Task members change captured dictionary identity or wording."
                );
            }
            var seenRows = new HashSet<int>();
            foreach (var observation in member.GetProperty("observations").EnumerateArray())
            {
                var line = observation.GetProperty("dictionaryRow").GetInt32();
                if (line < 1 || line > names.Count || !seenRows.Add(line))
                {
                    throw new InvalidDataException("Task dictionary row is absent or duplicated.");
                }
                var name = names[line - 1];
                if (
                    name.ArtistUuid != artist
                    || name.LegacySongUuid != song
                    || name.Title != title
                    || name.Scope != observation.GetProperty("scope").GetString()
                    || name.SongKey != observation.GetProperty("songKey").GetString()
                    || name.Witness
                        != observation.GetProperty("witness").Deserialize<MusicTitleWitness>(Json)
                    || !name.WorkKeys.SequenceEqual(
                        observation.GetProperty("workKeys").Deserialize<string[]>(Json)!
                    )
                )
                {
                    throw new InvalidDataException(
                        "Task dictionary locator differs from its original row."
                    );
                }
                members.Add(name);
            }
            if (seenRows.Count != identityCounts[(artist, song)])
            {
                throw new InvalidDataException("Task omits a retained dictionary source scope.");
            }
        }
        if (found.Count != expected.Count)
        {
            throw new InvalidDataException("Task omits an observed shared dictionary member.");
        }
        return members.ToArray();
    }

    private static async Task<List<T>> ReadRowsAsync<T>(
        string path,
        long maximumBytes,
        CancellationToken ct
    )
    {
        var rows = new List<T>();
        await foreach (
            var line in ShadowJsonLines.ReadTextAsync(path, 1024 * 1024, maximumBytes, ct)
        )
        {
            rows.Add(JsonSerializer.Deserialize<T>(line, Json)!);
        }
        return rows;
    }

    private static async Task<byte[]> ReadPinnedAsync(
        string path,
        string sha,
        long maximumBytes,
        CancellationToken ct
    )
    {
        if (new FileInfo(path).Length > maximumBytes)
        {
            throw new InvalidDataException("Work-search metadata file exceeds its bound.");
        }
        var bytes = await File.ReadAllBytesAsync(path, ct);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != sha)
        {
            throw new InvalidDataException("Pinned work-search metadata changed.");
        }
        return bytes;
    }

    private static async Task RequirePinAsync(string path, string sha, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        if (Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)) != sha)
        {
            throw new InvalidDataException("Pinned work-search input changed.");
        }
    }

    private sealed class WorkSearchSession(
        ICompositionWorkSearchClient client,
        CompositionWorkSearchRequest request,
        CompositionWorkSearchTask[] tasks,
        string requestHash,
        string output
    )
    {
        private CompositionWorkSearchCheckpoint state = null!;
        private readonly Dictionary<Guid, long> charges = new();
        private readonly HashSet<Guid> unmeasured = new();
        private readonly List<CompositionWorkSearchTaskResult> results = new();

        public async Task<CompositionWorkSearchCheckpoint> RunAsync(
            bool resume,
            CancellationToken ct
        )
        {
            var checkpoint = Path.Combine(output, "checkpoint.json");
            if (Directory.Exists(output) && !resume)
            {
                throw new IOException("A new work-search output directory is required.");
            }
            Directory.CreateDirectory(output);
            await using var lease = new FileStream(
                Path.Combine(output, "run.lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None
            );
            if (resume)
            {
                state = JsonSerializer.Deserialize<CompositionWorkSearchCheckpoint>(
                    await File.ReadAllBytesAsync(checkpoint, ct),
                    Json
                )!;
                if (
                    state.Version != Version
                    || state.RequestSha256 != requestHash
                    || state.PlanUuid != request.PlanUuid
                    || !JsonSerializer
                        .SerializeToUtf8Bytes(
                            state.Tasks.Select(result => result.Task).ToArray(),
                            Json
                        )
                        .AsSpan()
                        .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(tasks, Json))
                )
                {
                    throw new InvalidDataException(
                        "Work-search resume requires its exact frozen queue, request and original deadline."
                    );
                }
            }
            else
            {
                state = new(
                    Version,
                    requestHash,
                    request.PlanUuid,
                    DateTimeOffset.UtcNow
                        + (request.Limits.MaximumDuration ?? TimeSpan.FromHours(2)),
                    0,
                    new Dictionary<Guid, long>(),
                    [],
                    null,
                    tasks.Select(task => new CompositionWorkSearchTaskResult(task)).ToArray(),
                    "pending"
                );
            }
            foreach (var pair in state.AttemptBodyCharges)
            {
                charges.Add(pair.Key, pair.Value);
            }
            unmeasured.UnionWith(state.UnmeasuredAttempts);
            results.AddRange(state.Tasks);
            await SaveAsync("running", ct);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var remaining = state.Deadline - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                deadline.CancelAfter(remaining);
            }
            try
            {
                if (remaining <= TimeSpan.Zero)
                {
                    await RecoverAsync();
                    return await FinishAsync("deadline_reached");
                }
                await ReconcileAsync(deadline.Token);
                var nextCall = DateTimeOffset.MinValue;
                for (var index = 0; index < results.Count; index++)
                {
                    while (results[index].State is "unexecuted" or "pending")
                    {
                        if (DateTimeOffset.UtcNow >= state.Deadline)
                        {
                            return await FinishAsync("deadline_reached");
                        }
                        deadline.Token.ThrowIfCancellationRequested();
                        var task = results[index].Task;
                        var receipt = await client.GetAsync(task.Command, deadline.Token);
                        if (receipt != null)
                        {
                            await RetainAsync(index, receipt, deadline.Token);
                        }
                        if (results[index].State is "completed" or "failed")
                        {
                            break;
                        }
                        if (state.ReservedServiceCalls >= request.Limits.MaximumServiceCalls)
                        {
                            return await FinishAsync("service_call_bound");
                        }
                        if (
                            charges.Values.Sum() + ResponseAllowance
                            > request.Limits.MaximumNewResponseBytes
                        )
                        {
                            return await FinishAsync("response_byte_bound");
                        }
                        var notBefore = receipt?.NotBefore ?? DateTimeOffset.MinValue;
                        var eligible = nextCall > notBefore ? nextCall : notBefore;
                        var delay = eligible - DateTimeOffset.UtcNow;
                        if (delay > TimeSpan.Zero)
                        {
                            await Task.Delay(delay, deadline.Token);
                        }
                        CheckResources(ResponseAllowance);
                        // Reserve before dispatch. A crash cannot refund an unknown send;
                        // saved attempt responses reconcile actual bytes on exact resume.
                        state = state with
                        {
                            ReservedServiceCalls = state.ReservedServiceCalls + 1,
                            InFlightCommand = task.Command.CommandUuid,
                        };
                        await SaveAsync("running", deadline.Token);
                        receipt = await client.AdvanceAsync(task.Command, deadline.Token);
                        await RetainAsync(index, receipt, deadline.Token);
                        state = state with { InFlightCommand = null };
                        await SaveAsync("running", deadline.Token);
                        nextCall = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(1250);
                    }
                }
                return await FinishAsync(
                    results.Any(result => result.State == "failed" || result.Issues?.Count > 0)
                        ? "completed_partial"
                        : "completed"
                );
            }
            catch (OperationCanceledException)
            {
                await RecoverAsync();
                var result = await FinishAsync(
                    ct.IsCancellationRequested ? "cancelled" : "deadline_reached"
                );
                if (ct.IsCancellationRequested)
                {
                    throw;
                }
                return result;
            }
            catch (WorkSearchCapacityException capacity)
            {
                await RecoverAsync();
                return await FinishAsync(capacity.State);
            }
            catch
            {
                await RecoverAsync();
                await FinishAsync("interrupted");
                throw;
            }
        }

        private async Task ReconcileAsync(CancellationToken ct)
        {
            for (var index = 0; index < results.Count; index++)
            {
                var result = results[index];
                if (
                    result.Receipt == null
                    && result.Task.Command.CommandUuid != state.InFlightCommand
                )
                {
                    continue;
                }
                var receipt = await client.GetAsync(result.Task.Command, ct);
                if (receipt != null)
                {
                    await RetainAsync(index, receipt, ct);
                }
                else if (result.Task.Command.CommandUuid == state.InFlightCommand)
                {
                    // No saved receipt can prove whether a reserved dispatch sent.
                    charges[result.Task.Command.CommandUuid] = ResponseAllowance;
                    unmeasured.Add(result.Task.Command.CommandUuid);
                }
            }
            state = state with { InFlightCommand = null };
        }

        private async Task RecoverAsync()
        {
            using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await ReconcileAsync(recovery.Token);
            }
            catch
            {
                // Durable service receipts remain authoritative when local reads fail.
                if (state.InFlightCommand is { } command)
                {
                    var receipt = results
                        .Single(result => result.Task.Command.CommandUuid == command)
                        .Receipt;
                    var missingAttempts = receipt
                        ?.Attempts.Where(attempt => !charges.ContainsKey(attempt.AttemptUuid))
                        .Select(attempt => attempt.AttemptUuid)
                        .ToArray();
                    foreach (
                        var unknown in missingAttempts is { Length: > 0 }
                            ? missingAttempts
                            : [command]
                    )
                    {
                        charges[unknown] = ResponseAllowance;
                        unmeasured.Add(unknown);
                    }
                }
            }
        }

        private async Task RetainAsync(
            int index,
            ShadowCompositionAcquisitionReceipt receipt,
            CancellationToken ct
        )
        {
            var task = results[index].Task;
            if (receipt.CommandUuid != task.Command.CommandUuid)
            {
                throw new InvalidDataException("Work-search receipt belongs to another command.");
            }
            results[index] = results[index] with
            {
                Receipt = receipt,
                State = receipt.State == MusicBrainzAcquisitionState.Failed ? "failed" : "pending",
            };
            charges.Remove(task.Command.CommandUuid);
            unmeasured.Remove(task.Command.CommandUuid);
            var prefix = Path.Combine("tasks", task.Command.CommandUuid.ToString("D"));
            foreach (var attempt in receipt.Attempts)
            {
                var response = await client.ReadResponseAsync(attempt.AttemptUuid, ct);
                var body = response?.Response.Body;
                charges[attempt.AttemptUuid] = body?.LongLength ?? ResponseAllowance;
                if (body == null)
                {
                    unmeasured.Add(attempt.AttemptUuid);
                }
                else
                {
                    unmeasured.Remove(attempt.AttemptUuid);
                }
                if (response != null)
                {
                    if (
                        body != null
                        && response.Sha256 is { } sha
                        && Convert.ToHexStringLower(SHA256.HashData(body)) != sha
                    )
                    {
                        throw new InvalidDataException(
                            "Saved work-search response bytes differ from their original hash."
                        );
                    }
                    await WriteJsonAsync(
                        Path.Combine(prefix, attempt.AttemptUuid.ToString("D"), "response.json"),
                        response with
                        {
                            Response = response.Response with { Body = null },
                        },
                        ct
                    );
                    if (body != null)
                    {
                        await WriteAsync(
                            Path.Combine(prefix, attempt.AttemptUuid.ToString("D"), "body.bin"),
                            body,
                            ct
                        );
                    }
                }
                if (
                    receipt.Result?.FetchUuid != attempt.AttemptUuid
                    && await client.ReadCaptureAsync(attempt.AttemptUuid, ct) is { } historical
                )
                {
                    if (historical.Fetch.FetchUuid != attempt.AttemptUuid)
                    {
                        throw new InvalidDataException("Saved capture belongs to another attempt.");
                    }
                    await ExportCaptureAsync(
                        Path.Combine(prefix, attempt.AttemptUuid.ToString("D")),
                        historical,
                        task.Command.Resource,
                        ct
                    );
                }
            }
            var result = results[index] with
            {
                Receipt = receipt,
                State = receipt.State == MusicBrainzAcquisitionState.Failed ? "failed" : "pending",
            };
            if (receipt.Result?.Input is { } reference)
            {
                var input =
                    await client.ReadInputAsync(reference.InputRevisionUuid, ct)
                    ?? throw new InvalidDataException("Work-search capture input is unavailable.");
                ShadowCompositionAcquisition.VerifyInput(input);
                var descriptor = MusicBrainzCandidateQueryBuilder.CreateDescriptor(
                    task.Command.Search!
                );
                if (
                    input.Uuid != reference.InputRevisionUuid
                    || input.Resource != task.Command.Resource
                    || input.Resource != reference.Resource
                    || input.Payload.LongLength > ResponseAllowance
                    || input.CaptureVersion != descriptor.CaptureVersion
                )
                {
                    throw new InvalidDataException(
                        "Work-search captured input changes its qualified resource, capture profile or byte bound."
                    );
                }
                var decoded = new NormalizationProviderDecoder().Decode(input, ct);
                var page = decoded.MusicBrainzSearchPages?.SingleOrDefault();
                var issues = decoded.Issues.Select(issue => issue.Code).ToList();
                if (page?.Offset?.Value != 0 || page?.Count == null || page?.Hits == null)
                {
                    issues.Add("invalid_first_work_search_page");
                }
                using var original = JsonDocument.Parse(input.Payload);
                int? slots =
                    original.RootElement.TryGetProperty("works", out var values)
                    && values.ValueKind == JsonValueKind.Array
                        ? values.GetArrayLength()
                        : null;
                var reportedCount = page?.Count?.Value;
                if (
                    slots is > 50
                    || (slots is { } length && reportedCount is { } total && total < length)
                )
                {
                    issues.Add("invalid_first_work_search_page_count");
                }
                if (slots is < 50 && reportedCount is { } reported && reported > slots.Value)
                {
                    issues.Add("short_first_page_before_reported_end");
                }
                if (
                    page?.Hits is { } hits
                    && hits.Select(hit => hit.Entity.Identity.Identity).Distinct().Count()
                        != hits.Count
                )
                {
                    issues.Add("duplicate_work_search_hits");
                }
                var file = Path.Combine("inputs", input.Uuid.ToString("D") + ".json");
                await WriteJsonAsync(file, input, ct);
                if (receipt.Result.FetchUuid is { } fetch)
                {
                    var capture =
                        await client.ReadCaptureAsync(fetch, ct)
                        ?? throw new InvalidDataException(
                            "Original work-search capture envelope is unavailable."
                        );
                    if (
                        capture.Revision.Uuid != input.Uuid
                        || capture.Revision.Sha256 != input.Sha256
                        || capture.Fetch.InputRevisionUuid != input.Uuid
                        || capture.Fetch.FetchUuid != fetch
                    )
                    {
                        throw new InvalidDataException(
                            "Original work-search capture envelope changes its revision."
                        );
                    }
                    await ExportCaptureAsync(prefix, capture, task.Command.Resource, ct);
                }
                result = result with
                {
                    State = "completed",
                    InputFile = file,
                    InputUuid = input.Uuid,
                    InputSha256 = input.Sha256,
                    ReportedCount = page?.Count?.Value,
                    ReturnedSlots = slots,
                    PageState =
                        issues.Count > 0 ? "malformed_retained_page"
                        : page!.Count!.Value > (slots ?? 0) ? "first_page_partial_window"
                        : "observed_first_page_end",
                    Issues = issues,
                };
            }
            results[index] = result;
        }

        private async Task ExportCaptureAsync(
            string prefix,
            NormalizationCaptureResult capture,
            NormalizationResourceKey resource,
            CancellationToken ct
        )
        {
            var input = capture.Revision;
            ShadowCompositionAcquisition.VerifyInput(input);
            if (
                input.Resource != resource
                || capture.Fetch.InputRevisionUuid != input.Uuid
                || input.Payload.LongLength > ResponseAllowance
            )
            {
                throw new InvalidDataException(
                    "Historical work-search capture changes its original resource or revision."
                );
            }
            var inputFile = Path.Combine("inputs", input.Uuid.ToString("D") + ".json");
            await WriteJsonAsync(inputFile, input, ct);
            await WriteJsonAsync(
                Path.Combine(prefix, "capture.json"),
                new
                {
                    revision = new
                    {
                        input.Uuid,
                        input.Resource,
                        input.Sha256,
                        input.MediaType,
                        input.CaptureVersion,
                        input.CapturedAt,
                        inputFile,
                    },
                    capture.Fetch,
                },
                ct
            );
        }

        private async Task SaveAsync(string status, CancellationToken ct)
        {
            state = state with
            {
                Tasks = results.ToArray(),
                AttemptBodyCharges = new Dictionary<Guid, long>(charges),
                UnmeasuredAttempts = unmeasured.ToArray(),
                State = status,
            };
            await WriteJsonAsync("checkpoint.json", state, ct);
        }

        private async Task<CompositionWorkSearchCheckpoint> FinishAsync(string status)
        {
            using var audit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await SaveAsync(status, audit.Token);
            var inputs = new List<object>();
            foreach (
                var result in results
                    .Where(result => result.InputFile != null)
                    .DistinctBy(result => result.InputUuid)
            )
            {
                var path = Path.Combine(output, result.InputFile!);
                await using var stream = File.OpenRead(path);
                inputs.Add(
                    new
                    {
                        path = result.InputFile,
                        bytes = stream.Length,
                        sha256 = Convert.ToHexStringLower(
                            await SHA256.HashDataAsync(stream, audit.Token)
                        ),
                        rows = 1,
                        format = "input_revision_json",
                        inputRevisionUuid = result.InputUuid,
                        payloadSha256 = result.InputSha256,
                    }
                );
            }
            var counts = results
                .GroupBy(result => result.State)
                .ToDictionary(group => group.Key, group => group.Count());
            await WriteJsonAsync(
                "manifest.json",
                new
                {
                    version = Version,
                    state = status,
                    requestSha256 = requestHash,
                    request.PlanUuid,
                    state.Deadline,
                    state.ReservedServiceCalls,
                    responseBodyBytes = charges
                        .Where(pair => !unmeasured.Contains(pair.Key))
                        .Sum(pair => pair.Value),
                    responseBudgetChargedBytes = charges.Values.Sum(),
                    unmeasuredAttempts = unmeasured.Count,
                    tasks = results.Count,
                    inputs,
                    counts,
                    checkpointSha256 = Convert.ToHexStringLower(
                        SHA256.HashData(
                            await File.ReadAllBytesAsync(
                                Path.Combine(output, "checkpoint.json"),
                                audit.Token
                            )
                        )
                    ),
                    externalUniverseComplete = false,
                    publicationPermitted = false,
                    identitySelectionPerformed = false,
                    scope = "offset_zero_literal_work_searches_only; dictionary evidence remains retained result/context evidence",
                },
                audit.Token
            );
            return state;
        }

        private long ControlHeadroom()
        {
            // Two checkpoint images cover atomic replacement plus terminal state.
            // The compact input inventory is bounded by the frozen task count;
            // one response allowance also covers the next receipt's metadata growth.
            var snapshot = state with
            {
                Tasks = results.ToArray(),
            };
            return 2L * JsonSerializer.SerializeToUtf8Bytes(snapshot, Json).LongLength
                + 1024L * results.Count
                + 8192
                + ResponseAllowance;
        }

        private void CheckResources(long additionalBytes, bool reserveControls = true)
        {
            var required = additionalBytes + (reserveControls ? ControlHeadroom() : 0);
            var used = Directory
                .EnumerateFiles(output, "*", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length);
            if (used + required > request.Limits.MaximumArtifactBytes)
            {
                throw new WorkSearchCapacityException(
                    "artifact_byte_bound",
                    "Work-search artifact byte bound reached; durable captures remain recoverable."
                );
            }
            if (
                new DriveInfo(Path.GetPathRoot(output)!).AvailableFreeSpace
                < request.Limits.MinimumFreeBytes + required
            )
            {
                throw new WorkSearchCapacityException(
                    "disk_reserve_reached",
                    "Work-search stopped to preserve the host disk reserve."
                );
            }
        }

        private Task WriteJsonAsync(string relative, object value, CancellationToken ct) =>
            WriteAsync(relative, JsonSerializer.SerializeToUtf8Bytes(value, Json), ct);

        private async Task WriteAsync(string relative, byte[] bytes, CancellationToken ct)
        {
            var path = Path.Combine(output, relative);
            if (File.Exists(path))
            {
                if ((await File.ReadAllBytesAsync(path, ct)).AsSpan().SequenceEqual(bytes))
                {
                    return;
                }
                if (relative is not ("checkpoint.json" or "manifest.json"))
                {
                    throw new InvalidDataException(
                        "Retained work-search evidence cannot be replaced with different bytes."
                    );
                }
            }
            CheckResources(
                bytes.LongLength,
                relative is not ("checkpoint.json" or "manifest.json")
            );
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".writing";
            await File.WriteAllBytesAsync(temporary, bytes, ct);
            File.Move(temporary, path, true);
        }
    }

    private sealed class WorkSearchCapacityException(string state, string message)
        : IOException(message)
    {
        public string State { get; } = state;
    }
}
