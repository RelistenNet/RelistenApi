using Temporalio.Api.Enums.V1;
using Temporalio.Workflows;

namespace Relisten.Ingestion.Workflows;

/// <summary>Bounds children by their inherited deadline and waits for cancellation before closing the parent.</summary>
internal static class CoordinatorOptions
{
    internal static ChildWorkflowOptions Child(string workflowId, IngestionExecution execution)
    {
        ImportWorkflowOptions.RequireActive(execution);
        return new ChildWorkflowOptions
        {
            Id = workflowId,
            TaskQueue = IngestionQueues.Workflows,
            ParentClosePolicy = Temporalio.Workflows.ParentClosePolicy.RequestCancel,
            CancellationType = ChildWorkflowCancellationType.WaitCancellationCompleted,
            IdReusePolicy = WorkflowIdReusePolicy.AllowDuplicate,
            ExecutionTimeout = execution.Deadline - Workflow.UtcNow,
        };
    }
}
