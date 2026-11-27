using Temporalio.Client;
using Temporalio.Worker;

namespace RelistenUserService.Sonos;

internal sealed class SonosTemporalWorker(
    ITemporalClient client,
    SonosQueueRefreshActivities refresh,
    SonosHouseholdActivities households,
    ILogger<SonosTemporalWorker> logger
) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken ct) =>
        SuperviseAsync(RunWorkerAsync, logger, ct);

    internal static async Task SuperviseAsync(
        Func<CancellationToken, Task> runWorker,
        ILogger logger,
        CancellationToken ct
    )
    {
        // A broken poller must not bring down HTTP. Temporal remains responsible for work
        // already accepted, and requests can keep serving or time out independently.
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await runWorker(ct);
                return;
            }
            catch (Exception error) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(error, "The Sonos Temporal worker failed. Restarting it.");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    private async Task RunWorkerAsync(CancellationToken ct)
    {
        await client.Connection.ConnectAsync().WaitAsync(ct);

        using var worker = new TemporalWorker(
            client,
            new TemporalWorkerOptions(SonosWorkflowStarter.TaskQueue)
            {
                MaxConcurrentActivities = 8,
                GracefulShutdownTimeout = TimeSpan.FromSeconds(10),
            }
                .AddWorkflow<SonosQueueRefreshWorkflow>()
                .AddAllActivities(refresh)
                .AddWorkflow<SonosHouseholdWorkflow>()
                .AddAllActivities(households)
        );
        await worker.ExecuteAsync(ct);
    }
}
