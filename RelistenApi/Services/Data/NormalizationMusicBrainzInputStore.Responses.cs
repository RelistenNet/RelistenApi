using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationMusicBrainzInputStore
{
    internal Task<MusicBrainzSavedResponse> SaveResponseAsync(
        MusicBrainzDispatch dispatch,
        MusicBrainzHttpResponse response,
        CancellationToken ct = default
    )
    {
        response = MusicBrainzHttpPolicy.SnapshotResponse(response);
        var frozen = response;
        var hash =
            frozen.Body == null ? null : Convert.ToHexStringLower(SHA256.HashData(frozen.Body));
        var responseJson = JsonSerializer.Serialize(frozen with { Body = null });
        return InTransaction(
            async (c, t) =>
            {
                var gate = await LockGate(c, t, ct);
                await RequireCommand(c, t, dispatch.CommandUuid, ct);
                await RequireDispatch(c, t, dispatch, ct);
                var prior = await ReadResponse(c, t, dispatch.AttemptUuid, ct);
                if (prior != null)
                {
                    if (
                        prior.Sha256 != hash
                        || !SameJson(
                            JsonSerializer.Serialize(prior.Response with { Body = null }),
                            responseJson
                        )
                        || (prior.Response.Body == null) != (frozen.Body == null)
                        || prior.Response.Body != null
                            && !prior.Response.Body.AsSpan().SequenceEqual(frozen.Body)
                    )
                    {
                        throw new NormalizationConflictException(
                            "MusicBrainz attempt already retains a different response."
                        );
                    }
                    return prior;
                }

                var now = await DatabaseNow(c, t, ct);
                var eligible =
                    gate.AttemptUuid == dispatch.AttemptUuid
                    && gate.Token == dispatch.Token
                    && gate.LeaseUntil > now
                    && frozen.CompletedAt <= dispatch.HttpDeadline
                    && frozen.CompletedAt >= dispatch.PreparedAt
                    && frozen.CompletedAt.UtcDateTime <= now;
                var notBefore = MusicBrainzHttpPolicy.ResponseCooldown(frozen, now);
                await c.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO normalization.musicbrainz_responses(attempt_uuid,response,body,sha256,eligible)
                        VALUES(@AttemptUuid,@responseJson::jsonb,@Body,@hash,@eligible);
                        UPDATE normalization.musicbrainz_attempts SET phase='ResponseSaved'
                        WHERE attempt_uuid=@AttemptUuid AND phase IN ('DispatchStarted','Uncertain','Retryable');
                        UPDATE normalization.musicbrainz_gate SET not_before=greatest(not_before,@notBefore)
                        WHERE singleton
                        """,
                        new
                        {
                            dispatch.AttemptUuid,
                            responseJson,
                            frozen.Body,
                            hash,
                            eligible,
                            notBefore,
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                // Cooldown and immutable eligibility commit before any caller starts evidence capture.
                // A late sender can extend backoff, but only its matching token can release ownership.
                await ReleaseGate(c, t, dispatch, ct);
                return new(dispatch.AttemptUuid, frozen, hash, eligible);
            },
            ct
        );
    }

    internal Task<MusicBrainzAcquisition> FinishAttemptAsync(
        MusicBrainzDispatch dispatch,
        NormalizationCaptureResult? capture,
        string? reason,
        bool retryable,
        CancellationToken ct = default
    )
    {
        if (capture == null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        }
        return InTransaction(
            async (c, t) =>
            {
                await LockGate(c, t, ct);
                var command = await RequireCommand(c, t, dispatch.CommandUuid, ct);
                await RequireDispatch(c, t, dispatch, ct);
                var saved = await ReadResponse(c, t, dispatch.AttemptUuid, ct);
                var admitted =
                    capture == null
                        ? null
                        : await RequireCapture(c, t, dispatch, saved, capture, ct);
                if (command.ResultJson != null)
                {
                    return await ReadAcquisition(c, t, command, ct);
                }
                var latest = (await ReadAttempts(c, t, command.CommandUuid, ct)).Last();
                if (
                    latest.Dispatch.AttemptUuid != dispatch.AttemptUuid
                    || latest.Phase != "ResponseSaved"
                )
                {
                    // An expired request may report late, but cannot finish a newer attempt or turn
                    // an uncertain dispatch into a reusable send. Its response history stays retained.
                    return await ReadAcquisition(c, t, command, ct);
                }

                await ReleaseGate(c, t, dispatch, ct);
                if (admitted?.AcceptedAsCurrent == true)
                {
                    await SaveAttemptOutcome(
                        c,
                        t,
                        dispatch,
                        "Captured",
                        null,
                        dispatch.AttemptUuid,
                        ct
                    );
                    await CompleteCommand(
                        c,
                        t,
                        command,
                        new(
                            MusicBrainzAcquisitionDisposition.Captured,
                            new(admitted.InputRevisionUuid, dispatch.Profile.Resource),
                            dispatch.AttemptUuid,
                            admitted.ResourceGeneration
                        ),
                        ct
                    );
                }
                else
                {
                    var failure = admitted == null ? reason! : "stale_resource_generation";
                    var canRetry =
                        (admitted != null || retryable)
                        && latest.Ordinal < MusicBrainzAcquisitionLimits.MaximumAttempts;
                    await SaveAttemptOutcome(
                        c,
                        t,
                        dispatch,
                        canRetry ? "Retryable" : "Failed",
                        failure,
                        admitted == null ? null : dispatch.AttemptUuid,
                        ct
                    );
                    if (canRetry)
                    {
                        var gate = await LockGate(c, t, ct);
                        await SavePendingTime(c, t, command, gate.NotBefore, ct);
                    }
                    else
                    {
                        await CompleteCommand(
                            c,
                            t,
                            command,
                            new(MusicBrainzAcquisitionDisposition.Failed, Reason: failure),
                            ct
                        );
                    }
                }
                return await ReadAcquisition(c, t, command, ct);
            },
            ct
        );
    }

    private static async Task RequireDispatch(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        MusicBrainzDispatch dispatch,
        CancellationToken ct
    )
    {
        var preparation = await c.ExecuteScalarAsync<string?>(
            new CommandDefinition(
                """
                SELECT preparation::text FROM normalization.musicbrainz_attempts
                WHERE attempt_uuid=@AttemptUuid AND command_uuid=@CommandUuid FOR UPDATE
                """,
                dispatch,
                t,
                cancellationToken: ct
            )
        );
        if (preparation == null || !SameJson(preparation, JsonSerializer.Serialize(dispatch)))
        {
            throw new NormalizationConflictException(
                "MusicBrainz dispatch does not match immutable preparation."
            );
        }
    }

    private static async Task<MusicBrainzSavedResponse?> ReadResponse(
        NpgsqlConnection c,
        NpgsqlTransaction? t,
        Guid attemptUuid,
        CancellationToken ct
    )
    {
        var row = await c.QuerySingleOrDefaultAsync<ResponseRow>(
            new CommandDefinition(
                """
                SELECT response::text AS "ResponseJson",body AS "Body",sha256 AS "Sha256",eligible AS "Eligible"
                FROM normalization.musicbrainz_responses WHERE attempt_uuid=@attemptUuid
                """,
                new { attemptUuid },
                t,
                cancellationToken: ct
            )
        );
        return row == null
            ? null
            : new(
                attemptUuid,
                JsonSerializer.Deserialize<MusicBrainzHttpResponse>(row.ResponseJson)! with
                {
                    Body = row.Body,
                },
                row.Sha256,
                row.Eligible
            );
    }

    private static async Task<CaptureRow> RequireCapture(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        MusicBrainzDispatch dispatch,
        MusicBrainzSavedResponse? saved,
        NormalizationCaptureResult capture,
        CancellationToken ct
    )
    {
        if (
            saved
            is not {
                Eligible: true,
                Response.StatusCode: 200,
                Response.BodyComplete: true,
                Sha256: not null
            }
        )
        {
            throw new NormalizationConflictException(
                "Capture requires a complete eligible successful MusicBrainz response."
            );
        }
        var mediaType = MusicBrainzHttpPolicy.Header(saved.Response, "Content-Type");
        var providerRevision =
            MusicBrainzHttpPolicy.Header(saved.Response, "ETag")
            ?? MusicBrainzHttpPolicy.Header(saved.Response, "Last-Modified");
        var row = await c.QuerySingleOrDefaultAsync<CaptureRow>(
            new CommandDefinition(
                """
                SELECT f.input_revision_uuid AS "InputRevisionUuid",f.resource_generation AS "ResourceGeneration",
                    f.accepted_as_current AS "AcceptedAsCurrent"
                FROM normalization.input_fetches f JOIN normalization.input_revisions r ON r.uuid=f.input_revision_uuid
                JOIN normalization.input_resources p ON p.uuid=f.resource_uuid AND p.uuid=r.resource_uuid
                WHERE f.fetch_uuid=@AttemptUuid AND f.expected_generation=@ExpectedGeneration
                    AND p.provider=@Provider AND p.resource_type=@ResourceType AND p.provider_key=@ProviderKey
                    AND r.capture_version=@CaptureVersion AND r.sha256=@Sha256 AND r.media_type=@mediaType
                    AND f.observed_at=@observedAt AND f.provider_revision IS NOT DISTINCT FROM @providerRevision
                """,
                new
                {
                    dispatch.AttemptUuid,
                    dispatch.ExpectedGeneration,
                    dispatch.Profile.Resource.Provider,
                    dispatch.Profile.Resource.ResourceType,
                    dispatch.Profile.Resource.ProviderKey,
                    dispatch.Profile.CaptureVersion,
                    saved.Sha256,
                    mediaType,
                    providerRevision,
                    observedAt = Microseconds(saved.Response.CompletedAt),
                },
                t,
                cancellationToken: ct
            )
        );
        if (
            row == null
            || capture.Fetch.FetchUuid != dispatch.AttemptUuid
            || capture.Fetch.InputRevisionUuid != row.InputRevisionUuid
            || capture.Revision.Uuid != row.InputRevisionUuid
            || capture.Revision.Resource != dispatch.Profile.Resource
            || capture.Revision.CaptureVersion != dispatch.Profile.CaptureVersion
            || capture.Revision.Sha256 != saved.Sha256
            || capture.Revision.MediaType != mediaType
            || capture.Fetch.ProviderRevision != providerRevision
            || capture.Fetch.ExpectedGeneration != dispatch.ExpectedGeneration
            || capture.Fetch.ResourceGeneration != row.ResourceGeneration
            || capture.Fetch.AcceptedAsCurrent != row.AcceptedAsCurrent
        )
        {
            throw new NormalizationConflictException(
                "MusicBrainz completion does not identify its original retained evidence capture."
            );
        }
        return row;
    }

    private static DateTime Microseconds(DateTimeOffset value) =>
        new(value.UtcTicks - value.UtcTicks % 10, DateTimeKind.Utc);

    private static Task SaveAttemptOutcome(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        MusicBrainzDispatch dispatch,
        string phase,
        string? reason,
        Guid? fetchUuid,
        CancellationToken ct
    ) =>
        c.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE normalization.musicbrainz_attempts SET phase=@phase,reason=@reason,capture_fetch_uuid=@fetchUuid
                WHERE attempt_uuid=@AttemptUuid
                """,
                new
                {
                    dispatch.AttemptUuid,
                    phase,
                    reason,
                    fetchUuid,
                },
                t,
                cancellationToken: ct
            )
        );

    private sealed class ResponseRow
    {
        public string ResponseJson { get; init; } = null!;
        public byte[]? Body { get; init; }
        public string? Sha256 { get; init; }
        public bool Eligible { get; init; }
    }

    private sealed class CaptureRow
    {
        public Guid InputRevisionUuid { get; init; }
        public long ResourceGeneration { get; init; }
        public bool AcceptedAsCurrent { get; init; }
    }
}
