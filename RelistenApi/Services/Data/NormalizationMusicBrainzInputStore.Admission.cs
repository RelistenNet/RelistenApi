using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationMusicBrainzInputStore
{
    internal Task<MusicBrainzAcquisitionStep> AdvanceAsync(
        Guid commandUuid,
        MusicBrainzAcquisitionOptions policy,
        CancellationToken ct = default
    )
    {
        // Policy belongs to preparation; caller mutation after this point cannot change deadlines.
        policy = MusicBrainzHttpPolicy.SnapshotPolicy(policy);
        var userAgent = policy.UserAgent;
        var sendWindow = policy.SendWindow;
        var requestTimeout = policy.RequestTimeout;
        var leaseDuration = policy.LeaseDuration;
        return InTransaction<MusicBrainzAcquisitionStep>(
            async (c, t) =>
            {
                var gate = await LockGate(c, t, ct);
                var clock = new MusicBrainzDispatchClock(timeProvider ?? TimeProvider.System);
                var now = await DatabaseNow(c, t, ct);
                await ExpireUnsavedOwner(c, t, gate, now, ct);
                gate = await LockGate(c, t, ct);
                var command = await RequireCommand(c, t, commandUuid, ct);
                if (command.ResultJson != null)
                {
                    return new(await ReadAcquisition(c, t, command, ct));
                }

                var attempts = await ReadAttempts(c, t, commandUuid, ct);
                var latest = attempts.LastOrDefault();
                if (
                    latest?.Phase == "DispatchStarted"
                    && (
                        gate.AttemptUuid != latest.Dispatch.AttemptUuid
                        || gate.Token != latest.Dispatch.Token
                    )
                )
                {
                    await c.ExecuteAsync(
                        new CommandDefinition(
                            """
                            UPDATE normalization.musicbrainz_attempts SET phase='Uncertain',reason='admission_lost_without_saved_response'
                            WHERE attempt_uuid=@AttemptUuid AND phase='DispatchStarted'
                            """,
                            latest.Dispatch,
                            t,
                            cancellationToken: ct
                        )
                    );
                    attempts = await ReadAttempts(c, t, commandUuid, ct);
                    latest = attempts.LastOrDefault();
                }
                var resumed = await ResumeRetainedAttempt(c, t, command, latest, ct);
                if (resumed != null)
                {
                    return resumed;
                }

                if (await TryCompleteFromCache(c, t, command, now, ct))
                {
                    return new(await ReadAcquisition(c, t, command, ct));
                }
                if (attempts.Length >= MusicBrainzAcquisitionLimits.MaximumAttempts)
                {
                    await CompleteCommand(
                        c,
                        t,
                        command,
                        new(
                            MusicBrainzAcquisitionDisposition.Failed,
                            Reason: latest?.Reason ?? "attempt_budget_exhausted"
                        ),
                        ct
                    );
                    return new(await ReadAcquisition(c, t, command, ct));
                }

                var blockedUntil =
                    gate.LeaseUntil is { } lease && lease > now ? lease : gate.NotBefore;
                if (
                    gate.NotBefore is { } cooldown
                    && (blockedUntil == null || cooldown > blockedUntil)
                )
                {
                    blockedUntil = cooldown;
                }
                if (blockedUntil > now)
                {
                    await SavePendingTime(c, t, command, blockedUntil, ct);
                    return new(await ReadAcquisition(c, t, command, ct));
                }

                // This gate lock also excludes peer adapter commits between cache recheck and claim.
                // Evidence from another producer may still advance later: capture retains this clock.
                var dispatch = await PrepareDispatch(
                    c,
                    t,
                    command,
                    attempts.Length + 1,
                    userAgent,
                    now,
                    sendWindow,
                    requestTimeout,
                    leaseDuration,
                    ct
                );
                await SavePendingTime(c, t, command, dispatch.LeaseUntil.UtcDateTime, ct);
                return new(await ReadAcquisition(c, t, command, ct), dispatch, Clock: clock);
            },
            ct
        );
    }

    private static async Task<MusicBrainzAcquisitionStep?> ResumeRetainedAttempt(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        CommandRow command,
        AttemptRow? latest,
        CancellationToken ct
    )
    {
        if (latest == null)
        {
            return null;
        }
        if (latest.Phase == "ResponseSaved")
        {
            var saved =
                await ReadResponse(c, t, latest.Dispatch.AttemptUuid, ct)
                ?? throw new InvalidOperationException("Saved MusicBrainz response is missing.");
            return new(await ReadAcquisition(c, t, command, ct), latest.Dispatch, saved);
        }
        if (latest.Phase == "DispatchStarted")
        {
            // The dispatch intent is already committed. Only the caller that created it can
            // send; a concurrent Resume sees Pending, never another grant for this attempt.
            await SavePendingTime(c, t, command, latest.Dispatch.LeaseUntil.UtcDateTime, ct);
            return new(await ReadAcquisition(c, t, command, ct));
        }
        return null;
    }

    private static async Task ExpireUnsavedOwner(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        GateRow gate,
        DateTime now,
        CancellationToken ct
    )
    {
        if (gate.AttemptUuid == null || gate.LeaseUntil > now)
        {
            return;
        }
        var commandUuid = await c.ExecuteScalarAsync<Guid>(
            new CommandDefinition(
                """
                SELECT command_uuid FROM normalization.musicbrainz_attempts WHERE attempt_uuid=@AttemptUuid
                """,
                gate,
                t,
                cancellationToken: ct
            )
        );
        await RequireCommand(c, t, commandUuid, ct);
        // A response already saved with eligible proof stays recoverable after expiry. Saving
        // normally clears ownership atomically; this guard also preserves future recovery paths.
        await c.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE normalization.musicbrainz_attempts SET phase='Uncertain',reason='admission_expired_without_saved_response'
                WHERE attempt_uuid=@AttemptUuid AND phase='DispatchStarted'
                    AND NOT EXISTS(SELECT 1 FROM normalization.musicbrainz_responses WHERE attempt_uuid=@AttemptUuid);
                UPDATE normalization.musicbrainz_gate SET attempt_uuid=NULL,token=NULL,lease_until=NULL,
                    not_before=greatest(not_before,@LeaseUntil::timestamptz + interval '1.25 seconds')
                WHERE singleton AND token=@Token
                """,
                gate,
                t,
                cancellationToken: ct
            )
        );
    }

    private static async Task<MusicBrainzDispatch> PrepareDispatch(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        CommandRow command,
        int ordinal,
        string userAgent,
        DateTime now,
        TimeSpan sendWindow,
        TimeSpan requestTimeout,
        TimeSpan leaseDuration,
        CancellationToken ct
    )
    {
        var profile = command.Profile;
        var generation =
            await c.ExecuteScalarAsync<long?>(
                new CommandDefinition(
                    """
                    SELECT generation FROM normalization.input_resources
                    WHERE provider=@Provider AND resource_type=@ResourceType AND provider_key=@ProviderKey
                    """,
                    profile.Resource,
                    t,
                    cancellationToken: ct
                )
            ) ?? 0;
        var prepared = new DateTimeOffset(now);
        var dispatch = new MusicBrainzDispatch(
            command.CommandUuid,
            Guid.NewGuid(),
            Guid.NewGuid(),
            profile,
            userAgent,
            generation,
            prepared,
            prepared + sendWindow,
            prepared + sendWindow + requestTimeout,
            prepared + leaseDuration
        );
        await c.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO normalization.musicbrainz_attempts(attempt_uuid,command_uuid,ordinal,preparation,phase)
                VALUES(@AttemptUuid,@CommandUuid,@ordinal,@preparation::jsonb,'DispatchStarted');
                UPDATE normalization.musicbrainz_gate SET attempt_uuid=@AttemptUuid,token=@Token,lease_until=@LeaseUntil
                WHERE singleton
                """,
                new
                {
                    dispatch.AttemptUuid,
                    dispatch.CommandUuid,
                    ordinal,
                    preparation = JsonSerializer.Serialize(dispatch),
                    dispatch.Token,
                    dispatch.LeaseUntil,
                },
                t,
                cancellationToken: ct
            )
        );
        return dispatch;
    }

    private static async Task<bool> TryCompleteFromCache(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        CommandRow command,
        DateTime now,
        CancellationToken ct
    )
    {
        if (command.CacheMode != MusicBrainzCacheMode.ReuseFresh)
        {
            return false;
        }
        var profile = command.Profile;
        var maximumAge = command.MaximumCacheAge!.Value;
        // Any explicit positive age is valid, including a span longer than the UTC date range.
        var oldest =
            maximumAge.Ticks >= now.Ticks
                ? DateTime.SpecifyKind(DateTime.MinValue.AddTicks(10), DateTimeKind.Utc)
                : now - maximumAge;
        // The latest admitted fetch, current revision and resource generation are selected in one
        // statement. An older successful receipt for equal bytes cannot bless a later fetch.
        var cached = await c.QuerySingleOrDefaultAsync<CacheRow>(
            new CommandDefinition(
                """
                SELECT f.fetch_uuid AS "FetchUuid",f.input_revision_uuid AS "InputRevisionUuid",p.generation AS "Generation"
                FROM normalization.input_resources p
                JOIN normalization.input_revisions r ON r.uuid=p.current_revision_uuid
                JOIN LATERAL(SELECT * FROM normalization.input_fetches WHERE resource_uuid=p.uuid AND accepted_as_current
                    ORDER BY sequence DESC LIMIT 1) f ON f.input_revision_uuid=r.uuid AND f.resource_generation=p.generation
                JOIN normalization.musicbrainz_attempts a ON a.attempt_uuid=f.fetch_uuid AND a.capture_fetch_uuid=f.fetch_uuid AND a.phase='Captured'
                JOIN normalization.musicbrainz_responses s ON s.attempt_uuid=a.attempt_uuid AND s.eligible
                WHERE p.provider=@Provider AND p.resource_type=@ResourceType AND p.provider_key=@ProviderKey
                    AND r.capture_version=@CaptureVersion AND f.observed_at>=@oldest AND f.observed_at<=@now
                    AND a.preparation->'Profile'=@profile::jsonb
                """,
                new
                {
                    profile.Resource.Provider,
                    profile.Resource.ResourceType,
                    profile.Resource.ProviderKey,
                    profile.CaptureVersion,
                    oldest,
                    now,
                    profile = JsonSerializer.Serialize(profile),
                },
                t,
                cancellationToken: ct
            )
        );
        if (cached == null)
        {
            return false;
        }
        await CompleteCommand(
            c,
            t,
            command,
            new(
                MusicBrainzAcquisitionDisposition.Cached,
                new(cached.InputRevisionUuid, profile.Resource),
                cached.FetchUuid,
                cached.Generation
            ),
            ct
        );
        return true;
    }

    private static Task ReleaseGate(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        MusicBrainzDispatch dispatch,
        CancellationToken ct
    ) =>
        c.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE normalization.musicbrainz_gate SET attempt_uuid=NULL,token=NULL,lease_until=NULL
                WHERE singleton AND attempt_uuid=@AttemptUuid AND token=@Token
                """,
                dispatch,
                t,
                cancellationToken: ct
            )
        );

    private static async Task SavePendingTime(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        CommandRow command,
        DateTime? notBefore,
        CancellationToken ct
    )
    {
        command.NotBefore = notBefore;
        await c.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE normalization.musicbrainz_commands SET not_before=@NotBefore WHERE command_uuid=@CommandUuid AND result IS NULL
                """,
                command,
                t,
                cancellationToken: ct
            )
        );
    }

    private sealed class CacheRow
    {
        public Guid FetchUuid { get; init; }
        public Guid InputRevisionUuid { get; init; }
        public long Generation { get; init; }
    }
}
