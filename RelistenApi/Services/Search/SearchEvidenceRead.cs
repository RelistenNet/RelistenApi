using System;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Relisten.Search;

internal static class SearchEvidenceRead
{
    internal const int CommandTimeoutSeconds = 20;

    internal static async Task<T> RetryOnRecoveryConflict<T>(
        Func<Task<T>> read,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await read();
        }
        catch (PostgresException error)
            when (error.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            // A fresh statement can run after the replica applies the conflicting WAL.
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            return await read();
        }
    }
}
