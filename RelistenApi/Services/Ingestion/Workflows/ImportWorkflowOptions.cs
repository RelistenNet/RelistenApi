using System;
using Temporalio.Common;
using Temporalio.Workflows;

namespace Relisten.Ingestion.Workflows;

/// <summary>Retry and cancellation policy for bounded ingestion operations.</summary>
internal static class ImportWorkflowOptions
{
    internal static ActivityOptions Activity(string queue) =>
        new()
        {
            TaskQueue = queue,
            CancellationType = ActivityCancellationType.WaitCancellationCompleted,
            StartToCloseTimeout = TimeSpan.FromMinutes(5),
            HeartbeatTimeout = TimeSpan.FromSeconds(10),
            ScheduleToCloseTimeout = TimeSpan.FromHours(6),
            RetryPolicy = new RetryPolicy
            {
                MaximumAttempts = 5,
                InitialInterval = TimeSpan.FromSeconds(2),
                MaximumInterval = TimeSpan.FromMinutes(1),
            },
        };

    internal static ArtistImportRequest AssignRun(ArtistImportRequest request) =>
        request with
        {
            Run =
                request.Run
                ?? new IngestionRun(
                    request.RequestId,
                    Workflow.UtcNow + IngestionRun.MaximumDuration
                ),
        };
}
