using System.Linq.Expressions;
using System.Net;
using Temporalio.Activities;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

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

[Workflow]
internal sealed class SonosQueueRefreshWorkflow
{
    private bool _changed;

    [WorkflowSignal]
    public Task QueueChanged()
    {
        _changed = true;
        return Task.CompletedTask;
    }

    [WorkflowRun]
    public async Task RunAsync(Guid queueId)
    {
        do
        {
            _changed = false;
            try
            {
                await Workflow.ExecuteActivityAsync(
                    (SonosQueueRefreshActivities activities) => activities.RefreshAsync(queueId),
                    new ActivityOptions
                    {
                        StartToCloseTimeout = TimeSpan.FromSeconds(45),
                        RetryPolicy = new RetryPolicy
                        {
                            InitialInterval = TimeSpan.FromSeconds(2),
                            MaximumInterval = TimeSpan.FromSeconds(30),
                        },
                    }
                );
            }
            catch (ActivityFailureException error) when (_changed)
            {
                // A permanent rejection of an earlier edit must not discard a newer signal.
                Workflow.Logger.LogWarning(
                    error,
                    "Refresh failed for Sonos queue {QueueId}; another edit is pending.",
                    queueId
                );
            }
        } while (_changed);
    }
}

internal sealed class SonosQueueRefreshActivities(
    IServiceScopeFactory scopes,
    SonosIntegration integration,
    TimeProvider clock
)
{
    [Activity]
    public async Task RefreshAsync(Guid queueId)
    {
        if (integration.Configuration is not { } config)
        {
            throw new ApplicationFailureException(
                "Sonos is disabled on this worker.",
                nonRetryable: true
            );
        }

        var ct = ActivityExecutionContext.Current.CancellationToken;
        await using var scope = scopes.CreateAsyncScope();
        var refresh = scope.ServiceProvider.GetRequiredService<SonosQueueRefresh>();
        try
        {
            await refresh.RefreshAsync(config, queueId, ct);
        }
        catch (SonosRequestException error)
        {
            var retryAfter = error.RetryAfter?.Delta ?? error.RetryAfter?.Date - clock.GetUtcNow();
            throw new ApplicationFailureException(
                $"Sonos rejected the queue refresh with HTTP {(int)error.Status}.",
                error,
                nonRetryable: error.Status
                    is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
                    && (int)error.Status < 500,
                nextRetryDelay: retryAfter > TimeSpan.Zero ? retryAfter : null
            );
        }
        catch (OperationCanceledException error) when (!ct.IsCancellationRequested)
        {
            throw new ApplicationFailureException("The Sonos request timed out.", error);
        }
    }
}
