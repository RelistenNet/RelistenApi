using System;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using Dapper;

namespace Relisten.Data;

/// <summary>
/// Serializes one catalog change and commits its sequential data-service calls together.
/// Fetch provider responses before entering this transaction. Load mutable catalog rows
/// inside the callback, after the lock, so an overlapping attempt sees committed changes.
/// </summary>
public sealed class CatalogImportTransaction(DbService db)
{
    // PostgreSQL's two-int advisory keys keep item locks ("RLIM", artist ID)
    // separate from the global collection-index lock ("RLCO", 0).
    private const int ArtistLockNamespace = 0x524C494D;
    private const int CollectionLockNamespace = 0x524C434F;

    /// <summary>
    /// Applies one artist's catalog writes. The callback must use sequential DbService
    /// calls, without parallel SQL, HTTP requests, or separately held connections.
    /// </summary>
    public Task<T> RunAsync<T>(
        int artistId,
        DateTimeOffset deadline,
        CancellationToken token,
        Func<Task<T>> apply
    ) => RunLockedAsync(ArtistLockNamespace, artistId, deadline, token, apply);

    /// <summary>
    /// Applies a fetched Archive collection page. One global lock protects artist creation,
    /// upstream mappings, and slug allocation, including artists whose IDs are not yet known.
    /// </summary>
    public Task<T> RunCollectionPageAsync<T>(
        DateTimeOffset deadline,
        CancellationToken token,
        Func<Task<T>> apply
    ) => RunLockedAsync(CollectionLockNamespace, 0, deadline, token, apply);

    private async Task<T> RunLockedAsync<T>(
        int lockNamespace,
        int lockKey,
        DateTimeOffset deadline,
        CancellationToken token,
        Func<Task<T>> apply
    )
    {
        RequireActive(deadline, token);
        using var scope = new TransactionScope(
            TransactionScopeOption.Required,
            new TransactionOptions
            {
                IsolationLevel = IsolationLevel.ReadCommitted,
                Timeout = TimeSpan.FromMinutes(5),
            },
            TransactionScopeAsyncFlowOption.Enabled
        );

        var timeout = Math.Min(300_000, (deadline - DateTimeOffset.UtcNow).TotalMilliseconds);
        await db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    SELECT set_config('statement_timeout', @timeout, true);
                    SELECT pg_advisory_xact_lock(@lockNamespace, @lockKey);
                    """,
                    new
                    {
                        timeout = Math.Max(1, (int)timeout)
                            .ToString(System.Globalization.CultureInfo.InvariantCulture),
                        lockNamespace,
                        lockKey,
                    },
                    commandTimeout: (int)Math.Ceiling(Math.Max(1, timeout) / 1000),
                    cancellationToken: token
                )
            )
        );

        // Close the wrapper before the callback. Npgsql keeps its enlisted physical
        // connection for this ambient transaction, so later calls retain the lock.
        RequireActive(deadline, token);
        var result = await apply();

        // Existing catalog SQL uses bounded command timeouts rather than a token on
        // every Dapper call. Cancellation during that SQL must still prevent commit.
        RequireActive(deadline, token);
        scope.Complete();
        return result;
    }

    private static void RequireActive(DateTimeOffset deadline, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (DateTimeOffset.UtcNow >= deadline)
            throw new CatalogImportExpiredException();
    }
}

/// <summary>An expired import cannot commit catalog changes; start a fresh import.</summary>
public sealed class CatalogImportExpiredException()
    : Exception("This import has expired. Start a fresh import.");
