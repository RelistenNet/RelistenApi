using Temporalio.Activities;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace RelistenUserService.Sonos;

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
                        ScheduleToCloseTimeout = TimeSpan.FromMinutes(5),
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
                // A failed delivery must not discard a newer signal.
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
                nextRetryDelay: retryAfter > TimeSpan.Zero ? retryAfter : null
            );
        }
        catch (OperationCanceledException error) when (!ct.IsCancellationRequested)
        {
            throw new ApplicationFailureException("The Sonos request timed out.", error);
        }
    }
}
