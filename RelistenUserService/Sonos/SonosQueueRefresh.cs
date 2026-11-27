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
    internal async Task RefreshAsync(SonosConfiguration config, Guid id, CancellationToken ct)
    {
        var queue = await ReadActiveQueueAsync(id, ct);
        if (queue is not null && await ReadAccessAsync(config, queue, ct) is { } access)
        {
            await DeliverAsync(queue, access, ct);
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
                await DeliverAsync(queue, access, ct, dueBefore: clock.GetUtcNow());
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

    private async Task DeliverAsync(
        SonosQueue queue,
        SonosAccess access,
        CancellationToken ct,
        DateTimeOffset? dueBefore = null
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
                    && (dueBefore == null || row.RefreshAfter <= dueBefore)
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
            await target.EvictAsync(clock.GetUtcNow(), ct);
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
