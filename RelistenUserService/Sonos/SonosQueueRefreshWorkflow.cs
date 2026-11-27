using System.Net;
using Temporalio.Activities;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace RelistenUserService.Sonos;

public sealed class SonosQueueRefreshStarter(
    ITemporalClient client,
    ILogger<SonosQueueRefreshStarter> logger
)
{
    internal const string TaskQueue = "sonos";

    internal async Task<bool> TrySignalAsync(Guid queueId, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(1));

        try
        {
            // The SDK connects lazily before it applies the RPC cancellation token.
            await client.Connection.ConnectAsync().WaitAsync(deadline.Token);
            await client.StartWorkflowAsync(
                (SonosQueueRefreshWorkflow workflow) => workflow.RunAsync(queueId),
                new WorkflowOptions($"sonos-queue-refresh-{queueId}", TaskQueue)
                {
                    IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting,
                    IdReusePolicy = WorkflowIdReusePolicy.AllowDuplicate,
                    StartSignal = nameof(SonosQueueRefreshWorkflow.QueueChanged),
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
                "Could not confirm a refresh for Sonos queue {QueueId}.",
                queueId
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
