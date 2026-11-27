using Microsoft.EntityFrameworkCore;
using RelistenUserService.Persistence;

namespace RelistenUserService.Sonos;

internal sealed class SonosQueueRefresh(
    AccountsDbContext db,
    AdvisoryLockService locks,
    SonosAccountLink link,
    SonosProtocol protocol,
    TimeProvider clock
)
{
    internal async Task RefreshAsync(
        SonosConfiguration config,
        Guid id,
        CancellationToken ct,
        bool legacy = false
    )
    {
        var queue = await db.Set<SonosQueue>()
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == id, ct);
        if (
            queue is not { Status: "loaded", SonosSessionId: not null }
            || queue.ExpiresAt <= clock.GetUtcNow()
        )
        {
            return;
        }

        try
        {
            await DeliverAsync(config, queue, legacy, ct);
        }
        catch (Exception error) when (legacy && CanRetry(error, ct))
        {
            // Only the temporary cutover poller schedules retries in Postgres.
            await DeferAsync(queue, clock.GetUtcNow().AddSeconds(10), ct);
        }
    }

    private async Task DeliverAsync(
        SonosConfiguration config,
        SonosQueue queue,
        bool legacy,
        CancellationToken ct
    )
    {
        if (
            !await link.SessionActiveAsync(queue.UserId, queue.SessionId, ct)
            || await link.AccessAsync(config, queue.UserId, ct) is not { } access
        )
        {
            if (legacy)
            {
                await DeferAsync(queue, null, ct);
            }

            return;
        }

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
                    && (!legacy || row.RefreshAfter <= now)
                    && row.RefreshedQueueVersion < row.QueueVersion,
                ct
            );
        if (
            current?.SonosSessionId is not { } session
            || !await link.SessionActiveAsync(queue.UserId, queue.SessionId, ct)
        )
        {
            return;
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
            throw new InvalidOperationException(
                "The Sonos grant changed while preparing the queue refresh."
            );
        }

        await NotifySonosAsync(access.Token, session, current, ct);
        await transaction.CommitAsync(ct);
    }

    private async Task NotifySonosAsync(
        string token,
        string session,
        SonosQueue current,
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
        catch (SonosRequestException error) when (error.SessionEnded)
        {
            await target.ExecuteUpdateAsync(
                update => update.SetProperty(row => row.Status, "evicted"),
                ct
            );
        }
    }

    private Task<int> DeferAsync(SonosQueue queue, DateTimeOffset? when, CancellationToken ct) =>
        db.Set<SonosQueue>()
            .Where(row =>
                row.Id == queue.Id
                && row.QueueVersion == queue.QueueVersion
                && row.RefreshedQueueVersion < row.QueueVersion
            )
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.RefreshAfter, when), ct);

    private static bool CanRetry(Exception error, CancellationToken ct) =>
        !ct.IsCancellationRequested
        && error
            is SonosRequestException
                or HttpRequestException
                or System.Text.Json.JsonException
                or OperationCanceledException;
}
