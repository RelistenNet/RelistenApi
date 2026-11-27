using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Worker;

namespace RelistenUserService.Sonos;

internal sealed class SonosTemporalWorker(
    ITemporalClient client,
    SonosQueueRefreshActivities refresh,
    SonosHouseholdActivities households,
    ILogger<SonosTemporalWorker> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await ConnectAsync(ct);

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

    private async Task ConnectAsync(CancellationToken ct)
    {
        // A cold Temporal outage must leave HTTP serving. The edit endpoint can then
        // decline edits while the worker waits for Temporal to return.
        while (true)
        {
            try
            {
                await client.Connection.ConnectAsync().WaitAsync(ct);
                return;
            }
            catch (Exception error) when (error is InvalidOperationException or RpcException)
            {
                logger.LogWarning(error, "Could not connect the Sonos worker to Temporal.");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }
}
