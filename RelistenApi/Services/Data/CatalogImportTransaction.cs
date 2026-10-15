using System;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using Dapper;

namespace Relisten.Data;

/// <summary>
/// Commits one catalog change atomically. Separate shows can write concurrently; normal
/// PostgreSQL row locks and unique constraints coordinate shared records. Fetch provider
/// responses before entering the transaction and load mutable catalog rows inside it.
/// </summary>
public sealed class CatalogImportTransaction(DbService db)
{
    private const int CollectionLockNamespace = 0x524C434F;

    /// <summary>
    /// Applies one catalog change. The callback must use sequential DbService calls,
    /// without parallel SQL, HTTP requests, or separately held connections. Database
    /// conflicts propagate to Temporal, which retries with a fresh importer and transaction.
    /// </summary>
    public async Task<T> RunAsync<T>(
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
                    "SELECT set_config('statement_timeout', @timeout, true)",
                    new
                    {
                        timeout = Math.Max(1, (int)timeout)
                            .ToString(System.Globalization.CultureInfo.InvariantCulture),
                    },
                    commandTimeout: (int)Math.Ceiling(Math.Max(1, timeout) / 1000),
                    cancellationToken: token
                )
            )
        );

        // Close the wrapper before the callback. Npgsql keeps its enlisted physical
        // connection for this ambient transaction, so later calls share its writes.
        RequireActive(deadline, token);
        var result = await apply();

        // Existing catalog SQL uses bounded command timeouts rather than a token on
        // every Dapper call. Cancellation during that SQL must still prevent commit.
        RequireActive(deadline, token);
        scope.Complete();
        return result;
    }

    /// <summary>
    /// Applies a fetched Archive collection page. A global lock protects artist creation
    /// and slug allocation, which happen before artist IDs exist. Show imports do not use it.
    /// </summary>
    public Task<T> RunCollectionPageAsync<T>(
        DateTimeOffset deadline,
        CancellationToken token,
        Func<Task<T>> apply
    ) =>
        RunAsync(
            deadline,
            token,
            async () =>
            {
                await db.WithWriteConnection(connection =>
                    connection.ExecuteAsync(
                        new CommandDefinition(
                            "SELECT pg_advisory_xact_lock(@lockNamespace, 0)",
                            new { lockNamespace = CollectionLockNamespace },
                            commandTimeout: 300,
                            cancellationToken: token
                        )
                    )
                );

                RequireActive(deadline, token);
                return await apply();
            }
        );

    private static void RequireActive(DateTimeOffset deadline, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (DateTimeOffset.UtcNow >= deadline)
        {
            throw new CatalogImportExpiredException();
        }
    }
}

/// <summary>An expired import cannot commit catalog changes; start a fresh import.</summary>
public sealed class CatalogImportExpiredException()
    : Exception("This import has expired. Start a fresh import.");
