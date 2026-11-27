using Microsoft.EntityFrameworkCore;
using RelistenUserService.Persistence;

namespace RelistenUserService.Sonos;

internal sealed class SonosQueueRefreshCutoverPoller(
    IServiceScopeFactory scopes,
    SonosIntegration integration,
    TimeProvider clock,
    ILogger<SonosQueueRefreshCutoverPoller> logger
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
                        .RefreshDuringCutoverAsync(config, id, ct);
                }
            }
            catch (Exception error) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(error, "Could not deliver pending Sonos queue refreshes.");
            }
        }
    }
}
