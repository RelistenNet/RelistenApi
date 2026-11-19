using Relisten.Accounts.Contracts.Deletion;
using Sentry;
using Temporalio.Activities;

namespace RelistenIngestionWorker.AccountDeletion;

public sealed class AccountDeletionActivities(AccountPurge purge)
{
    // AccountDeletionWorkflow's retry policy retries without limit and backs off to one
    // attempt per hour, which it reaches by roughly the 13th attempt. Reporting the first
    // failure and then every 24th attempt surfaces a stuck purge immediately and about
    // once a day after that.
    private const int ReportEveryAttempts = 24;

    [Activity]
    public async Task PurgeAsync(AccountDeletionInput input)
    {
        var context = ActivityExecutionContext.Current;
        try
        {
            await purge.RunJobAsync(input, context.CancellationToken);
        }
        catch (Exception error) when (IsReportableFailure(context, error))
        {
            SentrySdk.CaptureException(error);
            throw;
        }
    }

    private static bool IsReportableFailure(ActivityExecutionContext context, Exception error)
    {
        // Worker shutdown and explicit cancellation are expected and the attempt is retried.
        // A start-to-close timeout also surfaces as cancellation of this token, but a purge
        // that times out on every attempt never completes, so it must be reported.
        var expectedCancellation =
            error is OperationCanceledException
            && context.CancellationToken.IsCancellationRequested
            && context.CancelReason != ActivityCancelReason.Timeout;
        if (expectedCancellation)
        {
            return false;
        }

        var attempt = context.Info.Attempt;
        return attempt == 1 || attempt % ReportEveryAttempts == 0;
    }
}
