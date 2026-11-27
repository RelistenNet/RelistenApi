using System.Net;
using Microsoft.EntityFrameworkCore;
using RelistenUserService.Persistence;

namespace RelistenUserService.Sonos;

internal sealed class SonosQueueRefresh(
    AccountsDbContext db,
    AdvisoryLockService locks,
    SonosAccountLink link,
    SonosProtocol protocol,
    TimeProvider clock,
    ILogger<SonosQueueRefresh> logger
)
{
    internal async Task RefreshAsync(SonosConfiguration config, Guid id, CancellationToken ct)
    {
        var queue = await ReadActiveQueueAsync(id, ct);
        if (queue is not null && await ReadAccessAsync(config, queue, ct) is { } access)
        {
            var outcome = await DeliverWithTokenRefreshAsync(config, queue, access, ct);
            if (outcome == DeliveryOutcome.GrantChanged)
            {
                throw new InvalidOperationException(
                    "The Sonos grant changed while preparing the queue refresh."
                );
            }
        }
    }

    internal async Task RefreshDuringCutoverAsync(
        SonosConfiguration config,
        Guid id,
        CancellationToken ct
    )
    {
        var queue = await ReadActiveQueueAsync(id, ct);
        if (queue is null)
        {
            return;
        }

        try
        {
            if (await ReadAccessAsync(config, queue, ct) is { } access)
            {
                // A concurrent grant update leaves the edit for the next cutover poll.
                await DeliverWithTokenRefreshAsync(
                    config,
                    queue,
                    access,
                    ct,
                    dueBefore: clock.GetUtcNow()
                );
            }
            else
            {
                await DeferAsync(queue, null, ct);
            }
        }
        catch (Exception error) when (CanRetry(error, ct))
        {
            await DeferAsync(queue, clock.GetUtcNow().AddSeconds(10), ct);
        }
    }

    private Task<SonosQueue?> ReadActiveQueueAsync(Guid id, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return db.Set<SonosQueue>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row =>
                    row.Id == id
                    && row.Status == "loaded"
                    && row.SonosSessionId != null
                    && row.ExpiresAt > now,
                ct
            );
    }

    private async Task<SonosAccess?> ReadAccessAsync(
        SonosConfiguration config,
        SonosQueue queue,
        CancellationToken ct
    ) =>
        await link.SessionActiveAsync(queue.UserId, queue.SessionId, ct)
            ? await link.AccessAsync(config, queue.UserId, ct)
            : null;

    private enum DeliveryOutcome
    {
        Finished,
        GrantChanged,
    }

    private async Task<DeliveryOutcome> DeliverWithTokenRefreshAsync(
        SonosConfiguration config,
        SonosQueue queue,
        SonosAccess access,
        CancellationToken ct,
        DateTimeOffset? dueBefore = null
    )
    {
        try
        {
            return await DeliverAsync(queue, access, ct, dueBefore);
        }
        catch (SonosRequestException error) when (error.Status == HttpStatusCode.Unauthorized)
        {
            // Release the delivery locks before refreshing the grant, which takes its own lock.
            var current = await link.AccessAsync(
                config,
                queue.UserId,
                ct,
                rejectedGrantVersion: access.GrantVersion
            );
            return current is null
                ? DeliveryOutcome.Finished
                : await DeliverAsync(queue, current, ct, dueBefore, retriedAfterUnauthorized: true);
        }
    }

    private async Task<DeliveryOutcome> DeliverAsync(
        SonosQueue queue,
        SonosAccess access,
        CancellationToken ct,
        DateTimeOffset? dueBefore,
        bool retriedAfterUnauthorized = false
    )
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await locks.LockAllAsync(
            [$"sonos-group:{queue.GroupId}", $"sonos-grant:{queue.UserId}"],
            ct
        );
        // Signal-With-Start can run this activity before the edit commits. Check the
        // version only after taking the same locks as the editor.
        var now = clock.GetUtcNow();
        var current = await db.Set<SonosQueue>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row =>
                    row.Id == queue.Id
                    && row.Status == "loaded"
                    && row.ExpiresAt > now
                    && row.RefreshAfter != null
                    && (dueBefore == null || row.RefreshAfter <= dueBefore)
                    && row.RefreshedQueueVersion < row.QueueVersion,
                ct
            );
        if (
            current?.SonosSessionId is not { } session
            || !await link.SessionActiveAsync(queue.UserId, queue.SessionId, ct)
        )
        {
            return DeliveryOutcome.Finished;
        }

        if (
            !await db.Set<SonosGrant>()
                .AnyAsync(
                    grant =>
                        grant.UserId == queue.UserId && grant.ProtectedToken == access.GrantVersion,
                    ct
                )
        )
        {
            return DeliveryOutcome.GrantChanged;
        }

        await NotifySonosAsync(access.Token, session, current, retriedAfterUnauthorized, ct);
        await transaction.CommitAsync(ct);
        return DeliveryOutcome.Finished;
    }

    private async Task NotifySonosAsync(
        string token,
        string session,
        SonosQueue current,
        bool retriedAfterUnauthorized,
        CancellationToken ct
    )
    {
        var target = db.Set<SonosQueue>()
            .Where(row => row.Id == current.Id && row.Status == "loaded");
        try
        {
            // Refresh is a notification. Retrying after a lost reply asks Sonos to read
            // the current version again and never reapplies the editing command.
            await protocol.ControlAsync(
                token,
                $"{SonosProtocol.SessionPath(session)}/playbackSession/refreshCloudQueue",
                new { },
                ct
            );
            await target.ExecuteUpdateAsync(
                update =>
                    update
                        .SetProperty(row => row.RefreshedQueueVersion, current.QueueVersion)
                        .SetProperty(row => row.RefreshAfter, (DateTimeOffset?)null),
                ct
            );
        }
        // This endpoint addresses a playback session. Gone, not found, or Sonos's
        // session-ended codes mean future refreshes have no room to deliver to.
        catch (SonosRequestException error)
            when (error.Status != HttpStatusCode.Unauthorized
                && (error.SessionEnded || error.Status == HttpStatusCode.NotFound)
            )
        {
            await target.EvictAsync(clock.GetUtcNow(), ct);
        }
        catch (SonosRequestException error)
            when (IsPermanentRejection(error.Status)
                && (retriedAfterUnauthorized || error.Status != HttpStatusCode.Unauthorized)
            )
        {
            logger.LogWarning(
                "Sonos rejected queue {QueueId} version {QueueVersion} with HTTP {Status}.",
                current.Id,
                current.QueueVersion,
                (int)error.Status
            );
            // The order is saved, but Sonos rejected delivery. Clear the pending marker
            // without claiming an acknowledgement. A different edit can try again.
            await DeferAsync(current, null, ct);
        }
    }

    private Task<int> DeferAsync(SonosQueue queue, DateTimeOffset? when, CancellationToken ct) =>
        db.Set<SonosQueue>()
            .Where(row =>
                row.Id == queue.Id
                && row.QueueVersion == queue.QueueVersion
                && row.RefreshedQueueVersion < row.QueueVersion
                // A cutover attempt can reach this after Temporal ended its delivery.
                && row.RefreshAfter != null
            )
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.RefreshAfter, when), ct);

    private static bool IsPermanentRejection(HttpStatusCode status) =>
        (int)status is >= 400 and < 500
        && status is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests);

    private static bool CanRetry(Exception error, CancellationToken ct) =>
        !ct.IsCancellationRequested
        && error
            is SonosRequestException
                or HttpRequestException
                or System.Text.Json.JsonException
                or OperationCanceledException;
}
