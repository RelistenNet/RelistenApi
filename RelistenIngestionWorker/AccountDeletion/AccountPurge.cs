using Dapper;
using Npgsql;
using Relisten.Accounts.Contracts.Deletion;

namespace RelistenIngestionWorker.AccountDeletion;

/// <summary>Deletes account data in one transaction for either a workflow or a restore replay.</summary>
public sealed class AccountPurge(string connectionString)
{
    public Task RunJobAsync(AccountDeletionInput input, CancellationToken cancellationToken) =>
        PurgeAsync(input.UserId, input.JobId, cancellationToken);

    public Task RunForUserAsync(string userId, CancellationToken cancellationToken) =>
        PurgeAsync(userId, null, cancellationToken);

    private async Task PurgeAsync(string userId, Guid? jobId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var parameters = new { UserId = userId, JobId = jobId };

        CommandDefinition Command(string sql) =>
            new(sql, parameters, transaction, cancellationToken: cancellationToken);

        if (jobId is not null)
        {
            // The job outlives the account and serializes retries, including a retry after
            // the database committed but Temporal did not record the activity result.
            var completed = await connection.QuerySingleAsync<bool>(
                Command(
                    """
                    SELECT completed_at IS NOT NULL
                    FROM identity.account_deletion_jobs
                    WHERE id = @JobId AND user_id = @UserId
                    FOR UPDATE
                    """
                )
            );
            if (completed)
            {
                return;
            }
        }

        var lockedUser = await connection.QuerySingleOrDefaultAsync<string>(
            Command(
                """
                SELECT id FROM identity.users WHERE id = @UserId FOR UPDATE
                """
            )
        );
        if (lockedUser is not null)
        {
            // OAuth issuance takes a conflicting share lock on the user. Once we hold
            // this lock, no issuance for this subject can commit behind these deletes.
            await connection.ExecuteAsync(
                Command(
                    """
                    DELETE FROM identity.openiddict_tokens WHERE subject = @UserId;
                    DELETE FROM identity.openiddict_authorizations WHERE subject = @UserId;
                    DELETE FROM identity.users WHERE id = @UserId;
                    """
                )
            );
        }

        if (jobId is not null)
        {
            await connection.ExecuteAsync(
                Command(
                    """
                    UPDATE identity.account_deletion_jobs
                    SET completed_at = clock_timestamp()
                    WHERE id = @JobId
                    """
                )
            );
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
