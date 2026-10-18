using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenIngestionWorker;

public enum MusicMaterializationCommandDomain
{
    Setlists,
    RecordingContents,
}

/// <summary>Only retained repertoire coordinates and bounded read selectors cross the operator boundary.</summary>
public abstract record NormalizationMusicMaterializationCommandRequest(
    MusicMaterializationCommandDomain Domain
)
{
    public sealed record Process(
        MusicMaterializationCommandDomain Domain,
        Guid AttemptUuid,
        Guid RepertoireAttemptUuid
    ) : NormalizationMusicMaterializationCommandRequest(Domain);

    public sealed record Retry(
        MusicMaterializationCommandDomain Domain,
        Guid AttemptUuid,
        Guid RegistrationUuid
    ) : NormalizationMusicMaterializationCommandRequest(Domain);

    public sealed record Get(MusicMaterializationCommandDomain Domain, Guid AttemptUuid)
        : NormalizationMusicMaterializationCommandRequest(Domain);

    public sealed record Decisions(
        MusicMaterializationCommandDomain Domain,
        Guid AttemptUuid,
        int Limit,
        Guid? AfterUuid
    ) : NormalizationMusicMaterializationCommandRequest(Domain);

    public sealed record Pending(
        MusicMaterializationCommandDomain Domain,
        int Limit,
        Guid? AfterUuid
    ) : NormalizationMusicMaterializationCommandRequest(Domain);

    public sealed record Recovery(
        MusicMaterializationCommandDomain Domain,
        int Limit,
        Guid? AfterUuid
    ) : NormalizationMusicMaterializationCommandRequest(Domain);
}

/// <summary>
/// Runs one explicit consumer operation without acquiring evidence or starting workers.
/// Durable preparations and accepted images stay in the materializers; stdout contains only
/// identifiers, outcomes and progress needed to resume or inspect the saved operation.
/// </summary>
public sealed class NormalizationMusicMaterializationCommand(
    NormalizationSetlistMaterializer setlists,
    NormalizationRecordingContentMaterializer recordings
)
{
    private static readonly JsonSerializerOptions OutputOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>Validates mutually exclusive selectors before resolving catalog services.</summary>
    public static NormalizationMusicMaterializationCommandRequest Parse(
        IConfiguration configuration
    )
    {
        var setlistOperation = configuration["normalize-setlists"];
        var recordingOperation = configuration["normalize-recording-contents"];
        if ((setlistOperation == null) == (recordingOperation == null))
        {
            throw new ArgumentException(
                "Choose exactly one setlist or recording-content operation."
            );
        }

        var domain =
            setlistOperation != null
                ? MusicMaterializationCommandDomain.Setlists
                : MusicMaterializationCommandDomain.RecordingContents;
        var operation = setlistOperation ?? recordingOperation;
        if (
            operation
            is not ("process" or "retry" or "get" or "decisions" or "pending" or "recovery")
        )
        {
            throw new ArgumentException(
                "Choose materialization process, retry, get, decisions, pending or recovery."
            );
        }

        var paged = operation is "decisions" or "pending" or "recovery";
        var needsAttempt = operation is "process" or "retry" or "get" or "decisions";
        RequireOption(configuration, "attempt", needsAttempt);
        RequireOption(configuration, "repertoire-attempt", operation == "process");
        RequireOption(configuration, "registration", operation == "retry");
        RequireOption(configuration, "request", false);
        RequireOption(configuration, "artist", false);
        if (!paged && (configuration["after"] != null || configuration["limit"] != null))
        {
            throw new ArgumentException(
                "After and limit require materialization decisions, pending or recovery."
            );
        }

        var attempt = needsAttempt ? ParseUuid(configuration["attempt"], "Attempt") : Guid.Empty;
        var limit = 100;
        if (
            configuration["limit"] is { } value
            && (!int.TryParse(value, out limit) || limit is < 1 or > 200)
        )
        {
            throw new ArgumentException(
                "Materialization page limit must be an integer from 1 through 200."
            );
        }
        var after = configuration["after"] is { } cursor ? ParseUuid(cursor, "After") : (Guid?)null;

        return operation switch
        {
            "process" => new NormalizationMusicMaterializationCommandRequest.Process(
                domain,
                attempt,
                ParseUuid(configuration["repertoire-attempt"], "Completed repertoire attempt")
            ),
            "retry" => new NormalizationMusicMaterializationCommandRequest.Retry(
                domain,
                attempt,
                ParseUuid(configuration["registration"], "Registration")
            ),
            "get" => new NormalizationMusicMaterializationCommandRequest.Get(domain, attempt),
            "decisions" => new NormalizationMusicMaterializationCommandRequest.Decisions(
                domain,
                attempt,
                limit,
                after
            ),
            "pending" => new NormalizationMusicMaterializationCommandRequest.Pending(
                domain,
                limit,
                after
            ),
            _ => new NormalizationMusicMaterializationCommandRequest.Recovery(domain, limit, after),
        };
    }

    /// <summary>
    /// Completed operations return zero, including explicit review or unresolved decisions.
    /// Pending and stale attempts return 2. Inspect RetryRequirement before retrying stale work;
    /// an unchanged rejected source cannot become eligible by repeatedly allocating attempts.
    /// </summary>
    public async Task<int> RunAsync(
        NormalizationMusicMaterializationCommandRequest request,
        TextWriter output,
        CancellationToken cancellationToken = default
    )
    {
        var result = request.Domain switch
        {
            MusicMaterializationCommandDomain.Setlists => await RunSetlistAsync(
                request,
                cancellationToken
            ),
            MusicMaterializationCommandDomain.RecordingContents => await RunRecordingAsync(
                request,
                cancellationToken
            ),
            _ => throw new ArgumentException(
                "Unknown music materialization domain.",
                nameof(request)
            ),
        };

        await output.WriteLineAsync(
            JsonSerializer.Serialize(result.Receipt, OutputOptions).AsMemory(),
            cancellationToken
        );
        return result.ExitCode;
    }

    private async Task<CommandResult> RunSetlistAsync(
        NormalizationMusicMaterializationCommandRequest request,
        CancellationToken ct
    )
    {
        switch (request)
        {
            case NormalizationMusicMaterializationCommandRequest.Process process:
                return Result(
                    await setlists.ProcessAsync(
                        process.AttemptUuid,
                        new(process.RepertoireAttemptUuid),
                        ct
                    )
                );
            case NormalizationMusicMaterializationCommandRequest.Retry retry:
                return Result(
                    await setlists.RetryAsync(retry.AttemptUuid, retry.RegistrationUuid, ct)
                );
            case NormalizationMusicMaterializationCommandRequest.Get get:
            {
                var attempt = await setlists.GetAsync(get.AttemptUuid, ct);
                return attempt == null ? NotFound(get.AttemptUuid) : Result(attempt);
            }
            case NormalizationMusicMaterializationCommandRequest.Decisions decisions:
            {
                var page = await setlists.GetDecisionsAsync(
                    decisions.AttemptUuid,
                    decisions.Limit,
                    decisions.AfterUuid,
                    ct
                );
                return new(
                    new
                    {
                        decisions.AttemptUuid,
                        Decisions = page.Decisions.Select(decision => new
                        {
                            decision.DecisionUuid,
                            decision.Disposition,
                            decision.Reason,
                        }),
                        page.NextCursor,
                    }
                );
            }
            case NormalizationMusicMaterializationCommandRequest.Pending pending:
            {
                var page = await setlists.GetPendingRegistrationsAsync(
                    pending.Limit,
                    pending.AfterUuid,
                    ct
                );
                return new(
                    new
                    {
                        Registrations = page.Registrations.Select(registration => new
                        {
                            registration.RegistrationUuid,
                            registration.ProcessingRegistrationUuid,
                            registration.CurrentAttemptUuid,
                            registration.EditionUuid,
                            registration.ObservationRetired,
                        }),
                        page.NextCursor,
                    }
                );
            }
            case NormalizationMusicMaterializationCommandRequest.Recovery recovery:
            {
                var page = await setlists.GetRecoveryAttemptsAsync(
                    recovery.Limit,
                    recovery.AfterUuid,
                    ct
                );
                return new(new { Attempts = page.Attempts.Select(Summary), page.NextCursor });
            }
            default:
                throw new ArgumentException(
                    "Unsupported setlist materialization operation.",
                    nameof(request)
                );
        }
    }

    private async Task<CommandResult> RunRecordingAsync(
        NormalizationMusicMaterializationCommandRequest request,
        CancellationToken ct
    )
    {
        switch (request)
        {
            case NormalizationMusicMaterializationCommandRequest.Process process:
                return Result(
                    await recordings.ProcessAsync(
                        process.AttemptUuid,
                        new(process.RepertoireAttemptUuid),
                        ct
                    )
                );
            case NormalizationMusicMaterializationCommandRequest.Retry retry:
                return Result(
                    await recordings.RetryAsync(retry.AttemptUuid, retry.RegistrationUuid, ct)
                );
            case NormalizationMusicMaterializationCommandRequest.Get get:
            {
                var attempt = await recordings.GetAsync(get.AttemptUuid, ct);
                return attempt == null ? NotFound(get.AttemptUuid) : Result(attempt);
            }
            case NormalizationMusicMaterializationCommandRequest.Decisions decisions:
            {
                var page = await recordings.GetDecisionsAsync(
                    decisions.AttemptUuid,
                    decisions.Limit,
                    decisions.AfterUuid,
                    ct
                );
                return new(
                    new
                    {
                        decisions.AttemptUuid,
                        Decisions = page.Decisions.Select(decision => new
                        {
                            decision.DecisionUuid,
                            decision.ContentUuid,
                            decision.Disposition,
                            decision.Reason,
                            decision.Withdrawal,
                        }),
                        page.NextCursor,
                    }
                );
            }
            case NormalizationMusicMaterializationCommandRequest.Pending pending:
            {
                var page = await recordings.GetPendingRegistrationsAsync(
                    pending.Limit,
                    pending.AfterUuid,
                    ct
                );
                return new(
                    new
                    {
                        Registrations = page.Registrations.Select(registration => new
                        {
                            registration.RegistrationUuid,
                            registration.SourceUuid,
                            registration.CurrentAttemptUuid,
                        }),
                        page.NextCursor,
                    }
                );
            }
            case NormalizationMusicMaterializationCommandRequest.Recovery recovery:
            {
                var page = await recordings.GetRecoveryAttemptsAsync(
                    recovery.Limit,
                    recovery.AfterUuid,
                    ct
                );
                return new(new { Attempts = page.Attempts.Select(Summary), page.NextCursor });
            }
            default:
                throw new ArgumentException(
                    "Unsupported recording-content materialization operation.",
                    nameof(request)
                );
        }
    }

    private static object Summary(SetlistMaterializationAttempt attempt) =>
        new
        {
            attempt.AttemptUuid,
            attempt.Registration.RegistrationUuid,
            EditionUuid = attempt.TargetEditionUuid,
            attempt.ProcessingVersion,
            attempt.SelectedRepertoireAttemptUuid,
            attempt.Result,
            PublicationStatus = attempt.Receipt?.Status,
            Retirement = attempt.Retirement == null
                ? null
                : new
                {
                    attempt.Retirement.WorkUuid,
                    PublicationStatus = attempt.Retirement.Receipt?.Status,
                    attempt.Retirement.Superseded,
                    attempt.Retirement.FailureReason,
                },
        };

    private static object Summary(RecordingContentMaterializationAttempt attempt) =>
        new
        {
            attempt.AttemptUuid,
            attempt.Registration.RegistrationUuid,
            attempt.Registration.SourceUuid,
            attempt.ProcessingVersion,
            attempt.SelectedRepertoireAttemptUuid,
            attempt.Result,
            PublicationStatus = attempt.Receipt?.Status,
        };

    private static CommandResult Result(SetlistMaterializationAttempt attempt) =>
        new(Summary(attempt), ExitCode(attempt.Result));

    private static CommandResult Result(RecordingContentMaterializationAttempt attempt) =>
        new(Summary(attempt), ExitCode(attempt.Result));

    private static int ExitCode(MusicMaterializationResult? result) =>
        result?.Disposition == MusicMaterializationDisposition.Completed ? 0 : 2;

    private static CommandResult NotFound(Guid attemptUuid) =>
        new(new { attempt_uuid = attemptUuid, state = "not_found" }, 1);

    private static void RequireOption(IConfiguration configuration, string key, bool required)
    {
        if (required != (configuration[key] != null))
        {
            throw new ArgumentException(
                required
                    ? $"Materialization operation requires --{key}."
                    : $"This materialization operation does not accept --{key}."
            );
        }
    }

    private static Guid ParseUuid(string? value, string label)
    {
        if (!Guid.TryParse(value, out var uuid) || uuid == Guid.Empty)
        {
            throw new ArgumentException($"{label} selector must be a nonempty UUID.");
        }
        return uuid;
    }

    private sealed record CommandResult(object Receipt, int ExitCode = 0);
}
