using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Relisten.Normalization.Temporal;

/// <summary>Schedules the host's optional search refresh after catalog mutations commit.</summary>
public static class SearchReindexStep
{
    public const string ActivityName = "CatalogSearch.ReindexArtist";

    private static readonly ActivityOptions Options = new()
    {
        TaskQueue = NormalizationQueues.Database,
        StartToCloseTimeout = TimeSpan.FromMinutes(5),
        ScheduleToCloseTimeout = TimeSpan.FromMinutes(7),
        HeartbeatTimeout = TimeSpan.FromSeconds(30),
        CancellationType = ActivityCancellationType.WaitCancellationCompleted,
        RetryPolicy = new RetryPolicy { MaximumAttempts = 1 },
    };

    public static async Task BestEffort(Guid artist)
    {
        try
        {
            await Workflow.ExecuteActivityAsync(ActivityName, [artist], Options);
        }
        catch (ActivityFailureException exception)
            when (!TemporalException.IsCanceledException(exception))
        {
            // The committed catalog work is complete. The daily build repairs search.
            Workflow.Logger.LogWarning(
                exception,
                "Could not refresh search for artist {Artist}",
                artist
            );
        }
    }
}
