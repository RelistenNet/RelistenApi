using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Relisten.Ingestion;

/// <summary>Reclaims expired discovery data and receipts at worker startup and hourly thereafter.</summary>
public sealed class IngestionCleanupService(
    IServiceScopeFactory scopes,
    ILogger<IngestionCleanupService> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope
                    .ServiceProvider.GetRequiredService<IngestionStore>()
                    .DeleteExpiredAsync();
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(
                    error,
                    "Could not remove expired ingestion snapshots and receipts; retrying next hour"
                );
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
