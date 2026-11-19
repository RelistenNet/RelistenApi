using Relisten.Accounts.Contracts.Deletion;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Exceptions;

namespace RelistenUserService.Identity.Deletion;

/// <summary>Starts a deletion once per committed job and treats Temporal's duplicate response as success.</summary>
public sealed class AccountDeletionStarter(ITemporalClient client)
{
    public async Task StartAsync(AccountDeletionInput input, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            // The SDK establishes its lazy connection before applying the RPC cancellation token.
            await client.Connection.ConnectAsync().WaitAsync(deadline.Token);
            await client.StartWorkflowAsync(
                AccountDeletionInput.WorkflowName,
                [input],
                new WorkflowOptions(
                    AccountDeletionInput.WorkflowId(input.JobId),
                    AccountDeletionInput.TaskQueue
                )
                {
                    IdConflictPolicy = WorkflowIdConflictPolicy.Fail,
                    IdReusePolicy = WorkflowIdReusePolicy.RejectDuplicate,
                    Rpc = new RpcOptions { CancellationToken = deadline.Token },
                }
            );
        }
        catch (WorkflowAlreadyStartedException)
        {
            // Both an open workflow and a retained closed workflow satisfy this request.
        }
    }
}
