using Microsoft.EntityFrameworkCore;
using RelistenUserService.Persistence;

namespace RelistenUserService.Sonos;

internal sealed class SonosQueueRefreshWorker(
    IServiceScopeFactory scopes,
    SonosIntegration integration,
    TimeProvider clock,
    ILogger<SonosQueueRefreshWorker> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (integration.Configuration is not { } config)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), clock);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AccountsDbContext>();
                var now = clock.GetUtcNow();
                var pending = await db.Set<SonosQueue>()
                    .AsNoTracking()
                    .Where(queue =>
                        queue.Status == "loaded"
                        && queue.SonosSessionId != null
                        && queue.ExpiresAt > now
                        && queue.RefreshAfter <= now
                        && queue.RefreshedQueueVersion < queue.QueueVersion
                    )
                    .OrderBy(queue => queue.RefreshAfter)
                    .Select(queue => queue.Id)
                    .Take(8)
                    .ToListAsync(ct);

                foreach (var id in pending)
                {
                    using var refreshScope = scopes.CreateScope();
                    await refreshScope
                        .ServiceProvider.GetRequiredService<SonosQueueRefresh>()
                        .TryRefreshAsync(config, id, ct);
                }
            }
            catch (Exception error) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(error, "Could not deliver pending Sonos queue refreshes.");
            }
        }
    }
}

internal sealed class SonosQueueRefresh(
    AccountsDbContext db,
    AdvisoryLockService locks,
    SonosAccountLink link,
    SonosProtocol protocol,
    TimeProvider clock
)
{
    internal async Task TryRefreshAsync(SonosConfiguration config, Guid id, CancellationToken ct)
    {
        var queue = await db.Set<SonosQueue>()
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == id, ct);
        if (queue is null)
        {
            return;
        }

        if (!await link.SessionActiveAsync(queue.UserId, queue.SessionId, ct))
        {
            await DeferAsync(queue, null, ct);
            return;
        }

        SonosAccess? access;
        try
        {
            access = await link.AccessAsync(config, queue.UserId, ct);
        }
        catch (Exception error) when (CanRetry(error, ct))
        {
            await DeferAsync(queue, clock.GetUtcNow().AddSeconds(10), ct);
            return;
        }

        if (access is null)
        {
            await DeferAsync(queue, null, ct);
            return;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await locks.LockAllAsync(
            [$"sonos-group:{queue.GroupId}", $"sonos-grant:{queue.UserId}"],
            ct
        );
        var now = clock.GetUtcNow();
        var current = await db.Set<SonosQueue>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row =>
                    row.Id == id
                    && row.Status == "loaded"
                    && row.ExpiresAt > now
                    && row.RefreshAfter <= now
                    && row.RefreshedQueueVersion < row.QueueVersion,
                ct
            );
        if (
            current?.SonosSessionId is not { } session
            || !await link.SessionActiveAsync(queue.UserId, queue.SessionId, ct)
            || !await db.Set<SonosGrant>()
                .AnyAsync(
                    grant =>
                        grant.UserId == queue.UserId && grant.ProtectedToken == access.GrantVersion,
                    ct
                )
        )
        {
            return;
        }

        var target = db.Set<SonosQueue>().Where(row => row.Id == id && row.Status == "loaded");
        try
        {
            // Refresh is a notification. Retrying after a lost reply asks Sonos to read
            // the current version again and never reapplies the editing command.
            await protocol.ControlAsync(
                access.Token,
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
        catch (Exception error) when (CanRetry(error, ct))
        {
            var retryAfter = clock.GetUtcNow().AddSeconds(10);
            await target.ExecuteUpdateAsync(
                update => update.SetProperty(row => row.RefreshAfter, retryAfter),
                ct
            );
        }

        await transaction.CommitAsync(ct);
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
