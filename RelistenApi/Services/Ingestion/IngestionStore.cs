using System;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using Dapper;
using Newtonsoft.Json;
using Temporalio.Exceptions;

namespace Relisten.Ingestion;

/// <summary>
/// Retains discovery snapshots and atomic catalog receipts until one day after a run's deadline.
/// Temporal owns progress. This store contains no request journal or fetched item responses.
/// </summary>
public sealed class IngestionStore(DbService db)
{
    /// <summary>Rejects expired work before it can recreate deleted snapshots or receipts.</summary>
    public static void RequireActive(IngestionRun run)
    {
        if (DateTimeOffset.UtcNow >= run.Deadline)
        {
            throw new ApplicationFailureException(
                "This import has expired. Start a fresh import.",
                errorType: "ImportExpired",
                nonRetryable: true
            );
        }
    }

    /// <summary>Fetches a snapshot once. Competing attempts both return the first committed value.</summary>
    public async Task<T> SnapshotAsync<T>(IngestionRun run, string name, Func<Task<T>> fetch)
        where T : class
    {
        RequireActive(run);
        var saved = await ReadSnapshotAsync<T>(run, name);
        if (saved != null)
        {
            return saved;
        }

        var value = await fetch();
        RequireActive(run);
        await db.WithWriteConnection(c =>
            c.ExecuteAsync(
                """
                INSERT INTO ingestion_snapshots(request_id, name, payload, expires_at)
                VALUES (@id, @name, CAST(@json AS jsonb), @expires)
                ON CONFLICT (request_id, name) DO NOTHING
                """,
                new
                {
                    id = run.Id,
                    name,
                    json = JsonConvert.SerializeObject(value),
                    expires = run.RetainUntil,
                }
            )
        );

        return (await ReadSnapshotAsync<T>(run, name))!;
    }

    /// <summary>Reads a prerequisite or discovery snapshot without creating it.</summary>
    public async Task<T?> ReadSnapshotAsync<T>(IngestionRun run, string name)
        where T : class
    {
        RequireActive(run);
        var json = await db.WithWriteConnection(c =>
            c.QuerySingleOrDefaultAsync<string>(
                """
                SELECT payload::text FROM ingestion_snapshots WHERE request_id=@id AND name=@name
                """,
                new { id = run.Id, name }
            )
        );
        return json == null ? null : JsonConvert.DeserializeObject<T>(json);
    }

    /// <summary>Returns only one item's listing metadata, rather than loading the full artist snapshot.</summary>
    public async Task<DiscoveredItem<TItem>> ReadItemAsync<TItem>(IngestionRun run, ImportItem item)
        where TItem : class
    {
        RequireActive(run);
        var json = await db.WithWriteConnection(c =>
            c.QuerySingleOrDefaultAsync<string>(
                """
                SELECT (payload->'Items'->@index)::text FROM ingestion_snapshots
                WHERE request_id=@id AND name=@name
                """,
                new
                {
                    id = run.Id,
                    name = item.Snapshot,
                    index = item.Index,
                }
            )
        );
        var found =
            json == null ? null : JsonConvert.DeserializeObject<DiscoveredItem<TItem>>(json);
        if (found?.Identifier != item.Identifier)
        {
            throw new InvalidOperationException("The item is missing from its discovery snapshot.");
        }

        return found;
    }

    /// <summary>Returns a committed result so a lost acknowledgement does not repeat HTTP or catalog work.</summary>
    public async Task<T?> ReadReceiptAsync<T>(IngestionRun run, string operation)
        where T : class
    {
        RequireActive(run);
        var json = await db.WithWriteConnection(c =>
            c.QuerySingleOrDefaultAsync<string>(
                """
                SELECT result::text FROM ingestion_receipts WHERE request_id=@id AND operation=@operation
                """,
                new { id = run.Id, operation }
            )
        );
        return json == null ? null : JsonConvert.DeserializeObject<T>(json);
    }

    /// <summary>Commits database effects and their result together. HTTP must finish before this call.</summary>
    public async Task<T> CommitAsync<T>(
        IngestionRun run,
        string operation,
        Func<Task<T>> apply,
        CancellationToken cancellationToken = default
    )
        where T : class
    {
        RequireActive(run);
        using var transaction = new TransactionScope(
            TransactionScopeOption.Required,
            new TransactionOptions
            {
                IsolationLevel = IsolationLevel.RepeatableRead,
                Timeout = TimeSpan.FromMinutes(5),
            },
            TransactionScopeAsyncFlowOption.Enabled
        );

        await db.WithWriteConnection(c =>
            c.ExecuteAsync(
                """
                INSERT INTO ingestion_receipts(request_id, operation, expires_at) VALUES (@id, @operation, @expires)
                ON CONFLICT (request_id, operation) DO NOTHING
                """,
                new
                {
                    id = run.Id,
                    operation,
                    expires = run.RetainUntil,
                }
            )
        );
        var json = await db.WithWriteConnection(c =>
            c.QuerySingleOrDefaultAsync<string>(
                """
                SELECT result::text FROM ingestion_receipts WHERE request_id=@id AND operation=@operation FOR UPDATE
                """,
                new { id = run.Id, operation }
            )
        );
        var result = json == null ? await apply() : JsonConvert.DeserializeObject<T>(json)!;

        if (json == null)
        {
            await db.WithWriteConnection(c =>
                c.ExecuteAsync(
                    """
                    UPDATE ingestion_receipts SET result=CAST(@json AS jsonb)
                    WHERE request_id=@id AND operation=@operation
                    """,
                    new
                    {
                        id = run.Id,
                        operation,
                        json = JsonConvert.SerializeObject(result),
                    }
                )
            );
        }

        RequireActive(run);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Complete();
        return result;
    }

    /// <summary>Deletes expired snapshots and receipts; active runs retain a full extra day for in-flight attempts.</summary>
    public Task DeleteExpiredAsync() =>
        db.WithWriteConnection(c =>
            c.ExecuteAsync(
                """
                DELETE FROM ingestion_snapshots WHERE expires_at <= now();
                DELETE FROM ingestion_receipts WHERE expires_at <= now();
                """
            )
        );
}
