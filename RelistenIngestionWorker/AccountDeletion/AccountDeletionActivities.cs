using Relisten.Accounts.Contracts.Deletion;
using Sentry;
using Temporalio.Activities;

namespace RelistenIngestionWorker.AccountDeletion;

public sealed class AccountDeletionActivities(AccountPurge purge)
{
    [Activity]
    public async Task PurgeAsync(AccountDeletionInput input)
    {
        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        try
        {
            await purge.RunJobAsync(input, cancellationToken);
        }
        catch (Exception error)
            when (error is not OperationCanceledException
                || !cancellationToken.IsCancellationRequested
            )
        {
            SentrySdk.CaptureException(error);
            throw;
        }
    }
}
