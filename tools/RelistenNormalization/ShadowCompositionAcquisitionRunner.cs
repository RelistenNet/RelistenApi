using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace Relisten.Normalization.Batch;

/// <summary>The existing durable service owns rate admission, HTTP, cache and retry receipts.</summary>
public interface IShadowCompositionAcquisitionClient
{
    Task<ShadowCompositionAcquisitionReceipt?> GetAsync(
        ShadowCompositionAcquisitionCommand command,
        CancellationToken ct
    );
    Task<ShadowCompositionAcquisitionReceipt> AdvanceAsync(
        ShadowCompositionAcquisitionCommand command,
        CancellationToken ct
    );
    Task<NormalizationInputRevision?> ReadInputAsync(Guid inputUuid, CancellationToken ct);
}

public sealed record ShadowCompositionAcquisitionReceipt(
    Guid CommandUuid,
    MusicBrainzAcquisitionState State,
    DateTimeOffset? NotBefore,
    MusicBrainzAcquisitionResult? Result,
    IReadOnlyList<MusicBrainzAcquisitionAttempt> Attempts,
    JsonElement OriginalReceipt
);

public sealed class ShadowCompositionAcquisitionClient(
    NormalizationMusicBrainzInputService inputs,
    NormalizationEvidenceService evidence
) : IShadowCompositionAcquisitionClient
{
    public async Task<ShadowCompositionAcquisitionReceipt?> GetAsync(
        ShadowCompositionAcquisitionCommand command,
        CancellationToken ct
    )
    {
        if (command.Search != null)
        {
            var value = await inputs.GetSearchAsync(command.CommandUuid, ct);
            if (value != null && value.Request != command.Search)
            {
                throw new InvalidDataException(
                    "Existing search command belongs to another request."
                );
            }
            return value == null ? null : Receipt(value);
        }
        var lookup = await inputs.GetAsync(command.CommandUuid, ct);
        if (lookup != null && lookup.Request != command.Lookup)
        {
            throw new InvalidDataException("Existing lookup command belongs to another request.");
        }
        return lookup == null ? null : Receipt(lookup);
    }

    public async Task<ShadowCompositionAcquisitionReceipt> AdvanceAsync(
        ShadowCompositionAcquisitionCommand command,
        CancellationToken ct
    ) =>
        command.Search is { } search
            ? Receipt(await inputs.AcquireSearchAsync(command.CommandUuid, search, ct))
            : Receipt(await inputs.AcquireAsync(command.CommandUuid, command.Lookup!, ct));

    public Task<NormalizationInputRevision?> ReadInputAsync(Guid inputUuid, CancellationToken ct) =>
        evidence.GetInputAsync(inputUuid, ct);

    private static ShadowCompositionAcquisitionReceipt Receipt(
        MusicBrainzSearchAcquisition value
    ) =>
        new(
            value.CommandUuid,
            value.State,
            value.NotBefore,
            value.Result,
            value.Attempts,
            JsonSerializer.SerializeToElement(value)
        );

    private static ShadowCompositionAcquisitionReceipt Receipt(MusicBrainzAcquisition value) =>
        new(
            value.CommandUuid,
            value.State,
            value.NotBefore,
            value.Result,
            value.Attempts,
            JsonSerializer.SerializeToElement(value)
        );
}

public sealed record ShadowCompositionAcquisitionLimits(
    int MaximumServiceCalls,
    long MaximumNewResponseBytes,
    TimeSpan MaximumDuration,
    long MaximumRetainedPayloadBytes = 512L * 1024 * 1024
);

public sealed record ShadowCompositionAcquisitionInput(
    Guid Uuid,
    string Sha256,
    string File,
    long PayloadBytes
);

public sealed record ShadowCompositionAcquisitionCheckpoint(
    string Version,
    string RequestSha256,
    DateTimeOffset Deadline,
    int ReservedServiceCalls,
    IReadOnlyList<ShadowCompositionAcquisitionInput> Inputs,
    IReadOnlyList<ShadowCompositionAcquisitionCommand> Commands,
    IReadOnlyList<ShadowCompositionAcquisitionReceipt> Receipts,
    IReadOnlyList<string> Issues,
    string State,
    bool ExternalUniverseComplete = false,
    bool PublicationPermitted = false,
    IReadOnlyList<Guid>? ReservedCommandUuids = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        CompositionProgramQueryPlan? ProgramPlan = null
);

/// <summary>
/// A sequential transport coordinator, not a second HTTP implementation. A service invocation
/// can send at most one GET. Reserving a call and the full 8MiB response allowance before
/// invocation makes crashes, cancellations and cache hits consume conservative run capacity.
/// Durable provider commands remain the authority on actual attempts and original responses.
/// </summary>
public sealed class ShadowCompositionAcquisitionRunner(IShadowCompositionAcquisitionClient client)
{
    private const long MaximumResponseBytes = 8L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ShadowCompositionAcquisitionCheckpoint> RunAsync(
        IReadOnlyList<ShadowCompositionSeed> seeds,
        IReadOnlyList<NormalizationInputRevision> retainedInputs,
        string outputDirectory,
        ShadowCompositionAcquisitionLimits limits,
        IReadOnlyList<ShadowCompositionAcquisitionCommand>? originalCommands = null,
        CancellationToken ct = default,
        CompositionProgramQueryPlan? programPlan = null
    )
    {
        if (
            limits.MaximumServiceCalls < 0
            || limits.MaximumNewResponseBytes < 0
            || limits.MaximumDuration <= TimeSpan.Zero
            || limits.MaximumRetainedPayloadBytes < 0
        )
        {
            throw new ArgumentException(
                "Acquisition run bounds must be nonnegative, with a positive duration."
            );
        }
        Directory.CreateDirectory(outputDirectory);
        // Exclusive file ownership covers the checkpoint as well as every invocation reservation.
        await using var lease = new FileStream(
            Path.Combine(outputDirectory, "run.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None
        );
        var request = new
        {
            version = ShadowCompositionAcquisition.Version,
            seeds,
            limits,
            inputs = retainedInputs
                .Select(input => new { input.Uuid, input.Sha256 })
                .OrderBy(input => input.Uuid),
            originalCommands,
        };
        // Existing runs keep their exact fingerprint. The program variant binds
        // the frozen initial queries, including their source-plan identity.
        var initialSearches = programPlan?.Queries.Select(query => query.Command.Search!).ToArray();
        var programIssues =
            programPlan
                ?.Unresolved.Select(issue =>
                    $"{issue.Reason}:{issue.Reference.Target.Scope}:{issue.Reference.Target.ArtistUuid:D}:{issue.Reference.Target.SongEntityKey}"
                )
                .ToArray()
            ?? [];
        var requestHash = Hash(
            programPlan is null
                ? JsonSerializer.SerializeToUtf8Bytes(request, Json)
                : JsonSerializer.SerializeToUtf8Bytes(
                    new
                    {
                        version = "shadow-program-acquisition-v1",
                        request,
                        programPlan,
                    },
                    Json
                )
        );
        var checkpointPath = Path.Combine(outputDirectory, "checkpoint.json");
        var state = File.Exists(checkpointPath)
            ? JsonSerializer.Deserialize<ShadowCompositionAcquisitionCheckpoint>(
                await File.ReadAllBytesAsync(checkpointPath, ct),
                Json
            )!
            : new(
                ShadowCompositionAcquisition.Version,
                requestHash,
                DateTimeOffset.UtcNow + limits.MaximumDuration,
                0,
                [],
                [],
                [],
                [],
                "pending",
                ProgramPlan: programPlan
            );
        if (
            state.Version != ShadowCompositionAcquisition.Version
            || state.RequestSha256 != requestHash
        )
        {
            throw new InvalidDataException(
                "Composition acquisition resume requires the exact original seeds, inputs, commands and bounds."
            );
        }
        var inputs = new Dictionary<Guid, NormalizationInputRevision>();
        foreach (var saved in state.Inputs)
        {
            var input = JsonSerializer.Deserialize<NormalizationInputRevision>(
                await File.ReadAllBytesAsync(Path.Combine(outputDirectory, saved.File), ct),
                Json
            )!;
            ShadowCompositionAcquisition.VerifyInput(input);
            if (
                input.Uuid != saved.Uuid
                || input.Sha256 != saved.Sha256
                || input.Payload.LongLength != saved.PayloadBytes
            )
            {
                throw new InvalidDataException(
                    "Saved acquisition input differs from the checkpoint."
                );
            }
            inputs.Add(input.Uuid, input);
        }
        foreach (var input in retainedInputs)
        {
            await RetainAsync(input);
        }
        await SaveAsync(state);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var remaining = state.Deadline - DateTimeOffset.UtcNow;
        if (remaining > TimeSpan.Zero)
        {
            deadline.CancelAfter(remaining);
        }
        var receipts = state.Receipts.ToDictionary(receipt => receipt.CommandUuid);
        ShadowCompositionAcquisitionCommand? inFlight = null;
        var nextServiceCallAt = DateTimeOffset.MinValue;
        try
        {
            while (DateTimeOffset.UtcNow < state.Deadline)
            {
                ct.ThrowIfCancellationRequested();
                var plan = ShadowCompositionAcquisition.Plan(
                    seeds,
                    inputs.Values.ToArray(),
                    originalCommands,
                    initialSearches
                );
                state = state with
                {
                    Commands = plan.Commands,
                    Issues = plan.Issues.Concat(programIssues).Distinct().ToArray(),
                    Receipts = receipts.Values.ToArray(),
                };
                var resources = inputs.Values.Select(input => input.Resource).ToHashSet();
                var commands = plan
                    .Commands.Where(command => !resources.Contains(command.Resource))
                    .OrderBy(command =>
                        command.Reason == "recording_performance_work" ? 0
                        : command.Reason == "matching_recording_performer_alternative" ? 1
                        : command.Lookup?.Kind == MusicBrainzLookupKind.Recording ? 2
                        : command.Lookup != null ? 3
                        : command.Search?.Query.Kind == MusicBrainzLookupKind.Recording ? 4
                        : 5
                    )
                    .ToArray();
                var advanced = 0;
                DateTimeOffset? nextEligible = null;
                foreach (var command in commands)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    var receipt = await client.GetAsync(command, deadline.Token);
                    if (receipt != null)
                    {
                        receipts[command.CommandUuid] = receipt;
                        if (await RetainResultAsync(command, receipt, deadline.Token))
                        {
                            advanced++;
                            continue;
                        }
                        if (receipt.State != MusicBrainzAcquisitionState.Pending)
                        {
                            continue;
                        }
                        if (receipt.NotBefore > DateTimeOffset.UtcNow)
                        {
                            nextEligible =
                                nextEligible == null || receipt.NotBefore < nextEligible
                                    ? receipt.NotBefore
                                    : nextEligible;
                            continue;
                        }
                    }
                    if (
                        state.ReservedServiceCalls >= limits.MaximumServiceCalls
                        || state.ReservedServiceCalls
                            >= limits.MaximumNewResponseBytes / MaximumResponseBytes
                    )
                    {
                        return await FinishAsync("run_bound_reached");
                    }
                    var spacing = nextServiceCallAt - DateTimeOffset.UtcNow;
                    if (spacing > TimeSpan.Zero)
                    {
                        await Task.Delay(spacing, deadline.Token);
                    }
                    state = state with
                    {
                        ReservedServiceCalls = state.ReservedServiceCalls + 1,
                        Receipts = receipts.Values.ToArray(),
                        ReservedCommandUuids = (state.ReservedCommandUuids ?? [])
                            .Append(command.CommandUuid)
                            .Distinct()
                            .ToArray(),
                    };
                    // Never refund: an exception may follow a committed or unsaved provider dispatch.
                    await SaveAsync(state);
                    inFlight = command;
                    receipt = await client.AdvanceAsync(command, deadline.Token);
                    // Avoid consuming a whole run's conservative tickets on the shared rate gate.
                    // The durable service remains the authority when another job uses that gate.
                    nextServiceCallAt = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(1250);
                    if (
                        receipt.State == MusicBrainzAcquisitionState.Pending
                        && receipt.NotBefore > nextServiceCallAt
                    )
                    {
                        nextServiceCallAt = receipt.NotBefore.Value;
                    }
                    inFlight = null;
                    receipts[command.CommandUuid] = receipt;
                    await RetainResultAsync(command, receipt, deadline.Token);
                    state = state with { Receipts = receipts.Values.ToArray() };
                    await SaveAsync(state);
                    if (++advanced == 32)
                    {
                        break; // Replan discovered work edges before sending more search pages.
                    }
                }
                if (advanced > 0)
                {
                    continue;
                }
                if (nextEligible == null)
                {
                    return await FinishAsync("plan_exhausted");
                }
                var delay = nextEligible.Value - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(
                        delay > TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : delay,
                        deadline.Token
                    );
                }
            }
            await ReconcileAsync();
            return await FinishAsync("deadline_reached");
        }
        catch (OperationCanceledException)
        {
            await ReconcileAsync();
            var final = await FinishAsync(
                ct.IsCancellationRequested ? "cancelled" : "deadline_reached"
            );
            if (ct.IsCancellationRequested)
            {
                throw;
            }
            return final;
        }
        catch
        {
            await ReconcileAsync();
            await FinishAsync("interrupted");
            throw;
        }

        async Task ReconcileAsync()
        {
            // Read-only reconciliation cannot send HTTP. In particular, do not Resume a
            // ResponseSaved command after the run ceiling or deadline has closed.
            using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var touched = state
                .Commands.Where(command =>
                    receipts.ContainsKey(command.CommandUuid)
                    || command.CommandUuid == inFlight?.CommandUuid
                    || (state.ReservedCommandUuids ?? []).Contains(command.CommandUuid)
                )
                .ToArray();
            foreach (var command in touched)
            {
                try
                {
                    var receipt = await client.GetAsync(command, recovery.Token);
                    if (receipt != null)
                    {
                        receipts[command.CommandUuid] = receipt;
                        await RetainResultAsync(command, receipt, recovery.Token);
                    }
                }
                catch (Exception error)
                {
                    state = state with
                    {
                        Issues = state
                            .Issues.Append(
                                "ReceiptReconciliationIncomplete:"
                                    + command.CommandUuid.ToString("D")
                                    + ":"
                                    + error.GetType().Name
                            )
                            .Distinct()
                            .ToArray(),
                    };
                    break;
                }
            }
        }

        async Task<bool> RetainResultAsync(
            ShadowCompositionAcquisitionCommand command,
            ShadowCompositionAcquisitionReceipt receipt,
            CancellationToken readToken
        )
        {
            if (receipt.CommandUuid != command.CommandUuid)
            {
                throw new InvalidDataException("Provider receipt belongs to another command.");
            }
            if (receipt.Result?.Input is not { } reference)
            {
                return false;
            }
            var input =
                await client.ReadInputAsync(reference.InputRevisionUuid, readToken)
                ?? throw new InvalidDataException(
                    "Completed provider receipt has no retained input."
                );
            if (
                input.Uuid != reference.InputRevisionUuid
                || input.Resource != reference.Resource
                || input.Resource != command.Resource
            )
            {
                throw new InvalidDataException(
                    "Provider result references different retained bytes."
                );
            }
            var added = !inputs.ContainsKey(input.Uuid);
            await RetainAsync(input);
            return added;
        }

        async Task RetainAsync(NormalizationInputRevision input)
        {
            ShadowCompositionAcquisition.VerifyInput(input);
            if (inputs.TryGetValue(input.Uuid, out var prior))
            {
                if (prior.Sha256 != input.Sha256 || prior.Resource != input.Resource)
                {
                    throw new InvalidDataException("Input UUID has conflicting retained content.");
                }
                return;
            }
            if (
                inputs.Values.Sum(value => value.Payload.LongLength) + input.Payload.LongLength
                > limits.MaximumRetainedPayloadBytes
            )
            {
                throw new InvalidDataException(
                    "Composition acquisition retained-byte bound reached; provider receipts remain recoverable."
                );
            }
            var file = "input-" + input.Uuid.ToString("D") + ".json";
            await AtomicWriteAsync(
                Path.Combine(outputDirectory, file),
                JsonSerializer.SerializeToUtf8Bytes(input, Json)
            );
            inputs.Add(input.Uuid, input);
            state = state with
            {
                Inputs = state
                    .Inputs.Append(new(input.Uuid, input.Sha256, file, input.Payload.LongLength))
                    .ToArray(),
            };
        }

        async Task SaveAsync(ShadowCompositionAcquisitionCheckpoint value) =>
            await AtomicWriteAsync(
                checkpointPath,
                JsonSerializer.SerializeToUtf8Bytes(value, Json)
            );

        async Task<ShadowCompositionAcquisitionCheckpoint> FinishAsync(string outcome)
        {
            var finalPlan = ShadowCompositionAcquisition.Plan(
                seeds,
                inputs.Values.ToArray(),
                originalCommands,
                initialSearches
            );
            state = state with
            {
                State = outcome,
                Receipts = receipts.Values.ToArray(),
                Commands = finalPlan.Commands,
                Issues = state.Issues.Concat(finalPlan.Issues).Distinct().ToArray(),
            };
            await SaveAsync(state);
            return state;
        }
    }

    private static async Task AtomicWriteAsync(string path, byte[] bytes)
    {
        // Checkpoints survive caller cancellation; no service operation is initiated here.
        await File.WriteAllBytesAsync(path + ".writing", bytes, CancellationToken.None);
        File.Move(path + ".writing", path, overwrite: true);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
