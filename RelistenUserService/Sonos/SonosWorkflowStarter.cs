using System.Linq.Expressions;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Exceptions;

namespace RelistenUserService.Sonos;

public sealed class SonosWorkflowStarter(
    ITemporalClient client,
    ILogger<SonosWorkflowStarter> logger
)
{
    internal const string TaskQueue = "sonos";

    internal Task<bool> TrySignalQueueAsync(Guid queueId, CancellationToken ct) =>
        TrySignalAsync(
            (SonosQueueRefreshWorkflow workflow) => workflow.RunAsync(queueId),
            $"sonos-queue-refresh-{queueId}",
            nameof(SonosQueueRefreshWorkflow.QueueChanged),
            ct
        );

    internal Task<bool> TrySignalHouseholdAsync(string household, CancellationToken ct) =>
        TrySignalAsync(
            (SonosHouseholdWorkflow workflow) => workflow.RunAsync(household),
            $"sonos-household-{household}",
            nameof(SonosHouseholdWorkflow.GroupsChanged),
            ct
        );

    private async Task<bool> TrySignalAsync<TWorkflow>(
        Expression<Func<TWorkflow, Task>> run,
        string id,
        string signal,
        CancellationToken ct
    )
    {
        // Queue edits hold advisory locks and an open database transaction until this returns.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(1));

        try
        {
            // The SDK connects lazily before it applies the RPC cancellation token.
            await client.Connection.ConnectAsync().WaitAsync(deadline.Token);
            await client.StartWorkflowAsync(
                run,
                new WorkflowOptions(id, TaskQueue)
                {
                    IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting,
                    IdReusePolicy = WorkflowIdReusePolicy.AllowDuplicate,
                    StartSignal = signal,
                    Rpc = new RpcOptions { CancellationToken = deadline.Token },
                }
            );
            return true;
        }
        catch (Exception error)
            when (!ct.IsCancellationRequested
                && error
                    is (RpcException or InvalidOperationException or OperationCanceledException)
            )
        {
            logger.LogWarning(
                error,
                "Could not confirm a signal for Sonos workflow {WorkflowId}.",
                id
            );
            return false;
        }
    }
}
