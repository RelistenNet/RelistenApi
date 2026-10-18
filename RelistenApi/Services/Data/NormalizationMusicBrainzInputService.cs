using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

/// <summary>
/// Acquires fixed MusicBrainz entity representations. Each call drives at most one GET;
/// Pending results are resumed after NotBefore, sharing the command's three-attempt budget.
/// Captures are external assertions and never bind catalog or canonical identities.
/// </summary>
public sealed partial class NormalizationMusicBrainzInputService
{
    public const string Version = "musicbrainz-acquisition-v1";
    private readonly HttpClient http;
    private readonly NormalizationMusicBrainzInputStore store;
    private readonly NormalizationEvidenceService evidence;
    private readonly MusicBrainzAcquisitionOptions policy;

    public NormalizationMusicBrainzInputService(
        HttpClient http,
        NormalizationMusicBrainzInputStore store,
        NormalizationEvidenceService evidence,
        MusicBrainzAcquisitionOptions options
    )
    {
        this.http = http;
        this.store = store;
        this.evidence = evidence;
        policy = MusicBrainzHttpPolicy.SnapshotPolicy(options);
    }

    public async Task<MusicBrainzAcquisition> AcquireAsync(
        Guid commandUuid,
        MusicBrainzLookupRequest request,
        CancellationToken ct = default
    )
    {
        RequireAcquisitionContext(commandUuid, ct);
        var previous = await store.GetAsync(commandUuid, ct);
        if (previous != null)
        {
            if (previous.Request != request)
            {
                throw new NormalizationConflictException(
                    "The MusicBrainz command UUID already belongs to another request."
                );
            }
            return await ResumeCommandAsync(previous, ct);
        }

        var profile = MusicBrainzHttpPolicy.Profile(request);
        var command = await store.GetOrCreateAsync(commandUuid, request, profile, Version, ct);
        return await ResumeCommandAsync(command, ct);
    }

    public async Task<MusicBrainzAcquisition> ResumeAsync(
        Guid commandUuid,
        CancellationToken ct = default
    )
    {
        RequireAcquisitionContext(commandUuid, ct);
        var command =
            await store.GetAsync(commandUuid, ct)
            ?? throw new ArgumentException(
                "The MusicBrainz acquisition command does not exist.",
                nameof(commandUuid)
            );
        return await ResumeCommandAsync(command, ct);
    }

    public Task<MusicBrainzAcquisition?> GetAsync(
        Guid commandUuid,
        CancellationToken ct = default
    ) => store.GetAsync(commandUuid, ct);

    private async Task<MusicBrainzAcquisition> ResumeCommandAsync(
        MusicBrainzAcquisition command,
        CancellationToken ct
    )
    {
        if (command.State != MusicBrainzAcquisitionState.Pending)
        {
            return command;
        }
        if (command.AcquisitionVersion != Version)
        {
            return await store.FailAsync(
                command.CommandUuid,
                "unsupported_acquisition_version",
                ct
            );
        }

        var step = await store.AdvanceAsync(command.CommandUuid, policy, ct);
        if (step.SavedResponse != null)
        {
            return await CompleteSavedResponseAsync(step.Dispatch!, step.SavedResponse, ct);
        }
        if (step.Dispatch == null)
        {
            return step.Acquisition;
        }

        var response = await FetchOnceAsync(step.Dispatch, ct);
        // Once HTTP has run, bounded receipt persistence is independent of caller cancellation.
        // If this fails, the committed dispatch remains uncertain until automatic lease recovery.
        using var persist = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var saved = await store.SaveResponseAsync(step.Dispatch, response, persist.Token);
        ct.ThrowIfCancellationRequested();
        return await CompleteSavedResponseAsync(step.Dispatch, saved, ct);
    }

    private async Task<MusicBrainzAcquisition> CompleteSavedResponseAsync(
        MusicBrainzDispatch dispatch,
        MusicBrainzSavedResponse saved,
        CancellationToken ct
    )
    {
        var response = saved.Response;
        if (response.Reason != null)
        {
            var retryable =
                response.Reason
                is "transport_failure"
                    or "request_timeout"
                    or "caller_cancelled"
                    or "send_deadline_elapsed";
            return await store.FinishAttemptAsync(dispatch, null, response.Reason, retryable, ct);
        }
        if (response.StatusCode != 200)
        {
            var retryable = response.StatusCode == 429 || response.StatusCode >= 500;
            return await store.FinishAttemptAsync(
                dispatch,
                null,
                "http_" + response.StatusCode,
                retryable,
                ct
            );
        }
        if (!saved.Eligible)
        {
            return await store.FinishAttemptAsync(dispatch, null, "admission_lost", true, ct);
        }

        var invalid = ValidateRepresentation(dispatch, response);
        if (invalid != null)
        {
            return await store.FinishAttemptAsync(dispatch, null, invalid, false, ct);
        }

        var capture =
            await evidence.GetCaptureAsync(dispatch.AttemptUuid, ct)
            ?? await evidence.CaptureInputAsync(
                new(
                    dispatch.AttemptUuid,
                    dispatch.Profile.Resource,
                    response.Body!,
                    MusicBrainzHttpPolicy.Header(response, "Content-Type")!,
                    dispatch.Profile.CaptureVersion,
                    dispatch.ExpectedGeneration,
                    response.CompletedAt,
                    MusicBrainzHttpPolicy.Header(response, "ETag")
                        ?? MusicBrainzHttpPolicy.Header(response, "Last-Modified")
                ),
                ct
            );
        return await store.FinishAttemptAsync(
            dispatch,
            capture,
            capture.Fetch.AcceptedAsCurrent ? null : "stale_capture",
            !capture.Fetch.AcceptedAsCurrent,
            ct
        );
    }

    private static void RequireAcquisitionContext(Guid commandUuid, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (commandUuid == Guid.Empty)
        {
            throw new ArgumentException(
                "A stable MusicBrainz command UUID is required.",
                nameof(commandUuid)
            );
        }
        if (Transaction.Current != null)
        {
            throw new InvalidOperationException(
                "Acquire provider evidence outside the catalog transaction."
            );
        }
    }
}
