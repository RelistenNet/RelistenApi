using System;
using System.Threading.Tasks;
using Relisten.Normalization.Venues;
using Temporalio.Common;
using Temporalio.Workflows;

namespace Relisten.Normalization.Temporal;

/// <summary>
/// Reconciles stored counts after direct admin edits or failed post-write refresh hooks.
/// The database refresh is atomic and repeatable, so activity retries need no checkpoint.
/// </summary>
[Workflow]
public class ArtistVenueRefreshWorkflow
{
    [WorkflowRun]
    public Task<ArtistVenueRefreshResult> RunAsync() =>
        Workflow.ExecuteActivityAsync(
            (ArtistVenueRefreshActivities activity) => activity.RefreshAll(),
            new ActivityOptions
            {
                TaskQueue = NormalizationQueues.Database,
                StartToCloseTimeout = TimeSpan.FromMinutes(2),
                HeartbeatTimeout = TimeSpan.FromSeconds(30),
                CancellationType = ActivityCancellationType.WaitCancellationCompleted,
                RetryPolicy = new RetryPolicy
                {
                    MaximumAttempts = 3,
                    InitialInterval = TimeSpan.FromSeconds(1),
                },
            }
        );
}
