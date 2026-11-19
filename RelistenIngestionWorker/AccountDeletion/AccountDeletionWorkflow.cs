using Relisten.Accounts.Contracts.Deletion;
using Temporalio.Common;
using Temporalio.Workflows;

namespace RelistenIngestionWorker.AccountDeletion;

[Workflow(AccountDeletionInput.WorkflowName)]
public sealed class AccountDeletionWorkflow
{
    [WorkflowRun]
    public Task RunAsync(AccountDeletionInput input) =>
        Workflow.ExecuteActivityAsync(
            (AccountDeletionActivities activities) => activities.PurgeAsync(input),
            new ActivityOptions
            {
                StartToCloseTimeout = TimeSpan.FromMinutes(5),
                RetryPolicy = new RetryPolicy
                {
                    MaximumAttempts = 0,
                    MaximumInterval = TimeSpan.FromHours(1),
                },
            }
        );
}
