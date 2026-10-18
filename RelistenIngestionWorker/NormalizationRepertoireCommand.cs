using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenIngestionWorker;

/// <summary>Operator requests contain source coordinates, never proposed matching decisions.</summary>
public abstract record NormalizationRepertoireCommandRequest
{
    public sealed record Resolve(Guid AttemptUuid, RepertoireResolutionRequest Source)
        : NormalizationRepertoireCommandRequest;

    public sealed record Retry(Guid AttemptUuid, Guid RegistrationUuid)
        : NormalizationRepertoireCommandRequest;

    public sealed record Get(Guid AttemptUuid) : NormalizationRepertoireCommandRequest;

    public sealed record Decisions(Guid AttemptUuid, int Limit, Guid? AfterUuid)
        : NormalizationRepertoireCommandRequest;

    public sealed record Pending(int Limit, Guid? AfterUuid)
        : NormalizationRepertoireCommandRequest;

    public sealed record Recovery(int Limit, Guid? AfterUuid)
        : NormalizationRepertoireCommandRequest;
}

/// <summary>
/// Runs explicit repertoire operations without starting workers or acquiring provider data.
/// Requests are parsed before catalog services are resolved. JSON receipts omit source text
/// and candidate preparations; retained decisions and attempts remain owned by the resolver.
/// </summary>
public sealed class NormalizationRepertoireCommand(
    NormalizationRepertoireResolutionProcessor processor
)
{
    private const int MaximumRequestBytes = 16 * 1024;
    private static readonly JsonSerializerOptions InputOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 16,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private static readonly JsonSerializerOptions OutputOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>Reads a bounded source-request file only for resolve; all other operations use UUIDs.</summary>
    public static NormalizationRepertoireCommandRequest Parse(IConfiguration configuration)
    {
        var operation = configuration["normalize-repertoire"];
        var paged = operation is "decisions" or "pending" or "recovery";
        var needsAttempt = operation is "resolve" or "retry" or "get" or "decisions";
        if (
            operation
            is not ("resolve" or "retry" or "get" or "decisions" or "pending" or "recovery")
        )
        {
            throw new ArgumentException(
                "Choose repertoire resolve, retry, get, decisions, pending or recovery."
            );
        }

        RequireOption(configuration, "attempt", needsAttempt);
        RequireOption(configuration, "request", operation == "resolve");
        RequireOption(configuration, "registration", operation == "retry");
        if (!paged && (configuration["limit"] != null || configuration["after"] != null))
        {
            throw new ArgumentException(
                "After and limit require repertoire decisions, pending or recovery."
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
                "Repertoire page limit must be an integer from 1 through 200."
            );
        }
        var after = configuration["after"] is { } cursor ? ParseUuid(cursor, "After") : (Guid?)null;

        return operation switch
        {
            "resolve" => new NormalizationRepertoireCommandRequest.Resolve(
                attempt,
                ReadSource(configuration["request"]!)
            ),
            "retry" => new NormalizationRepertoireCommandRequest.Retry(
                attempt,
                ParseUuid(configuration["registration"], "Registration")
            ),
            "get" => new NormalizationRepertoireCommandRequest.Get(attempt),
            "decisions" => new NormalizationRepertoireCommandRequest.Decisions(
                attempt,
                limit,
                after
            ),
            "pending" => new NormalizationRepertoireCommandRequest.Pending(limit, after),
            _ => new NormalizationRepertoireCommandRequest.Recovery(limit, after),
        };
    }

    /// <summary>
    /// Zero means the operation settled, including explicit review/unresolved decisions.
    /// Exit 2 means the attempt is pending or stale; stale work needs a new retry attempt.
    /// Read commands return bounded summaries and never publish accepted music.
    /// </summary>
    public async Task<int> RunAsync(
        NormalizationRepertoireCommandRequest request,
        TextWriter output,
        CancellationToken cancellationToken = default
    )
    {
        object receipt;
        var exitCode = 0;
        switch (request)
        {
            case NormalizationRepertoireCommandRequest.Resolve resolve:
            {
                var attempt = await processor.ResolveAsync(
                    resolve.AttemptUuid,
                    resolve.Source,
                    cancellationToken
                );
                receipt = Summary(attempt);
                exitCode = AttemptExitCode(attempt);
                break;
            }
            case NormalizationRepertoireCommandRequest.Retry retry:
            {
                var attempt = await processor.RetryAsync(
                    retry.AttemptUuid,
                    retry.RegistrationUuid,
                    cancellationToken
                );
                receipt = Summary(attempt);
                exitCode = AttemptExitCode(attempt);
                break;
            }
            case NormalizationRepertoireCommandRequest.Get get:
            {
                var attempt = await processor.GetAsync(get.AttemptUuid, cancellationToken);
                receipt =
                    attempt == null
                        ? new { attempt_uuid = get.AttemptUuid, state = "not_found" }
                        : Summary(attempt);
                exitCode = attempt == null ? 1 : AttemptExitCode(attempt);
                break;
            }
            case NormalizationRepertoireCommandRequest.Decisions decisions:
            {
                var page = await processor.GetDecisionsAsync(
                    decisions.AttemptUuid,
                    decisions.Limit,
                    decisions.AfterUuid,
                    cancellationToken
                );
                receipt = new
                {
                    decisions.AttemptUuid,
                    Decisions = page.Decisions.Select(decision => new
                    {
                        decision.DecisionUuid,
                        decision.ClaimUuid,
                        decision.RepertoireSongUuid,
                        decision.Disposition,
                        decision.Reason,
                    }),
                    page.NextCursor,
                };
                break;
            }
            case NormalizationRepertoireCommandRequest.Pending pending:
            {
                var page = await processor.GetPendingRegistrationsAsync(
                    pending.Limit,
                    pending.AfterUuid,
                    cancellationToken
                );
                receipt = new
                {
                    Registrations = page.Registrations.Select(registration => new
                    {
                        registration.RegistrationUuid,
                        registration.ExpectedArtistUuid,
                        registration.CurrentAttemptUuid,
                    }),
                    page.NextCursor,
                };
                break;
            }
            case NormalizationRepertoireCommandRequest.Recovery recovery:
            {
                var page = await processor.GetRecoveryAttemptsAsync(
                    recovery.Limit,
                    recovery.AfterUuid,
                    cancellationToken
                );
                receipt = new { Attempts = page.Attempts.Select(Summary), page.NextCursor };
                break;
            }
            default:
                throw new ArgumentException(
                    "Unsupported repertoire operator request.",
                    nameof(request)
                );
        }

        await output.WriteLineAsync(
            JsonSerializer.Serialize(receipt, OutputOptions).AsMemory(),
            cancellationToken
        );
        return exitCode;
    }

    private static object Summary(RepertoireResolutionAttempt attempt) =>
        new
        {
            attempt.AttemptUuid,
            attempt.Registration.RegistrationUuid,
            attempt.ProcessingVersion,
            attempt.PreviousAttemptUuid,
            attempt.Result,
            StageCount = attempt.Stages.Count,
        };

    private static int AttemptExitCode(RepertoireResolutionAttempt attempt) =>
        attempt.Result?.Disposition == RepertoireResolutionDisposition.Completed ? 0 : 2;

    private static void RequireOption(IConfiguration configuration, string key, bool required)
    {
        if (required != (configuration[key] != null))
        {
            throw new ArgumentException(
                required
                    ? $"Repertoire operation requires --{key}."
                    : $"This repertoire operation does not accept --{key}."
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

    private static RepertoireResolutionRequest ReadSource(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var bytes = new byte[MaximumRequestBytes + 1];
            var length = 0;
            while (length < bytes.Length)
            {
                var count = stream.Read(bytes, length, bytes.Length - length);
                if (count == 0)
                {
                    break;
                }
                length += count;
            }
            if (length > MaximumRequestBytes)
            {
                throw new ArgumentException("Repertoire source request exceeds 16 KiB.");
            }
            var request = JsonSerializer.Deserialize<RepertoireResolutionRequest>(
                bytes.AsSpan(0, length),
                InputOptions
            );
            if (request?.Source == null || request.ExpectedArtistUuid == Guid.Empty)
            {
                throw new ArgumentException(
                    "A repertoire source and valid optional artist constraint are required."
                );
            }
            var coordinatesPresent = request.Source switch
            {
                ImportedRepertoireResolutionSource imported => imported.FetchUuid != Guid.Empty,
                RecordingRepertoireResolutionSource recording =>
                    recording.PublishedProcessingAttemptUuid != Guid.Empty,
                SetlistRepertoireResolutionSource setlist => setlist.PublishedProcessingAttemptUuid
                    != Guid.Empty,
                ProviderRepertoireResolutionSource provider => provider.Input.InputRevisionUuid
                    != Guid.Empty,
                _ => false,
            };
            if (!coordinatesPresent)
            {
                throw new ArgumentException(
                    "Repertoire source coordinates require nonempty UUIDs."
                );
            }
            return request;
        }
        catch (Exception error)
            when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new ArgumentException("Could not read a valid repertoire source request file.");
        }
    }
}
