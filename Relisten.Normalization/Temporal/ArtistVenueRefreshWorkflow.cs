using System;
using System.Threading.Tasks;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Relisten.Normalization.Temporal;

/// <summary>
/// Drain pending artists in bounded batches. Reconciliation first requests every existing
/// relationship owner; neither mode calls providers or normalization models.
/// </summary>
public sealed record RefreshArtistVenues(
    bool Reconcile = false,
    int BatchSize = 100,
    int MaxBatches = 20
);

public sealed record ArtistVenueRefreshRunResult(
    int Batches,
    int ArtistsRefreshed,
    int RelationshipsChanged,
    bool ReachedBatchLimit
);

/// <summary>
/// Source transactions only mark durable pending work. Each activity commits one short
/// count refresh; completed batches survive a later failure or worker restart.
/// </summary>
[Workflow]
public class ArtistVenueRefreshWorkflow
{
    private static readonly ActivityOptions Step = new()
    {
        TaskQueue = NormalizationQueues.Database,
        StartToCloseTimeout = TimeSpan.FromMinutes(5),
        HeartbeatTimeout = TimeSpan.FromSeconds(30),
        CancellationType = ActivityCancellationType.WaitCancellationCompleted,
        RetryPolicy = new RetryPolicy
        {
            MaximumAttempts = 3,
            InitialInterval = TimeSpan.FromSeconds(1),
        },
    };

    [WorkflowRun]
    public async Task<ArtistVenueRefreshRunResult> RunAsync(RefreshArtistVenues request)
    {
        if (request.BatchSize < 1 || request.MaxBatches < 1)
        {
            throw new ApplicationFailureException(
                "BatchSize and MaxBatches must be positive.",
                "InvalidArtistVenueRefresh",
                nonRetryable: true
            );
        }

        if (request.Reconcile)
        {
            await Workflow.ExecuteActivityAsync(
                (ArtistVenueRefreshActivities activity) => activity.RequestReconciliation(),
                Step
            );
        }

        var artists = 0;
        var relationships = 0;
        for (var batch = 0; batch < request.MaxBatches; batch++)
        {
            var result = await Workflow.ExecuteActivityAsync(
                (ArtistVenueRefreshActivities activity) => activity.RefreshBatch(request.BatchSize),
                Step
            );
            if (result.ArtistsRefreshed == 0)
            {
                // A writer or another refresh may hold pending rows. The next scheduled
                // run tries again; an empty unlocked batch does not claim global quiescence.
                return new(batch, artists, relationships, false);
            }

            artists += result.ArtistsRefreshed;
            relationships += result.RelationshipsChanged;
        }

        return new(request.MaxBatches, artists, relationships, true);
    }
}
