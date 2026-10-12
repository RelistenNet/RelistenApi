using System;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Relisten.Ingestion.Workflows;

/// <summary>Shared lifetime, retry, and cancellation policy. Children retain the original deadline.</summary>
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
                NonRetryableErrorTypes = ["ImportExpired"],
            },
        };

    // Queue delay consumes the same seven-day budget as execution. The first task
    // can start much later than the server admitted the workflow.
    internal static IngestionExecution AssignExecution(IngestionExecution? execution) =>
        execution
        ?? new(
            Workflow.NewGuid().ToString("D"),
            Workflow.Info.WorkflowStartTime + IngestionExecution.MaximumDuration
        );

    internal static void RequireActive(IngestionExecution execution)
    {
        if (Workflow.UtcNow >= execution.Deadline)
            throw new ApplicationFailureException(
                "The import execution deadline has passed.",
                "ImportExpired",
                nonRetryable: true
            );
    }

    internal static bool ShouldContinue(int completedCalls) =>
        completedCalls >= 100 || Workflow.ContinueAsNewSuggested;
}
