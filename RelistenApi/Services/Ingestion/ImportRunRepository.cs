using System;
using System.Threading.Tasks;
using System.Transactions;
using Dapper;
using Newtonsoft.Json;

namespace Relisten.Ingestion;

/// <summary>Stores staged inputs and operation receipts in the application database, not Temporal persistence.</summary>
public sealed class ImportRunRepository(DbService db)
{
    /// <summary>Returns a previously committed input/result, or null when it has not been staged.</summary>
    public async Task<T?> ReadAsync<T>(string requestId, string key) where T : class
    {
        var json = await db.WithWriteConnection(c => c.QuerySingleOrDefaultAsync<string>(
            "SELECT payload::text FROM ingestion_records WHERE request_id=@requestId AND record_key=@key",
            new { requestId, key }));
        return json == null ? null : JsonConvert.DeserializeObject<T>(json);
    }

    /// <summary>Persists the first successful version of an input. Retries reuse it for stable discovery.</summary>
    public Task SaveAsync<T>(string requestId, string key, T value) => db.WithWriteConnection(c => c.ExecuteAsync(@"
        INSERT INTO ingestion_records(request_id, record_key, payload)
        VALUES (@requestId, @key, CAST(@json AS jsonb))
        ON CONFLICT (request_id, record_key) DO NOTHING",
        new { requestId, key, json = JsonConvert.SerializeObject(value) }));

    /// <summary>Stages a complete page atomically, including its selected items and observed upstream IDs.</summary>
    public Task<ImportPage> SavePageAsync(string requestId, string provider, string key, ProviderPage page) =>
        ApplyOnceAsync(requestId, key, async () =>
        {
            var keys = new string[page.Items.Length];
            var payloads = new string[page.Items.Length];
            for (var i = 0; i < page.Items.Length; i++)
            {
                keys[i] = key + "/item/" + i;
                payloads[i] = JsonConvert.SerializeObject(page.Items[i]);
            }
            await db.WithWriteConnection(c => c.ExecuteAsync(@"
                INSERT INTO ingestion_records(request_id, record_key, payload)
                SELECT @requestId, item_key, CAST(body AS jsonb)
                FROM unnest(@keys::text[], @payloads::text[]) AS inputs(item_key, body)
                ON CONFLICT (request_id, record_key) DO NOTHING", new { requestId, keys, payloads }));
            if (page.ObservedKeys != null)
                await SaveAsync(requestId, provider + "/observed", page.ObservedKeys);
            return new ImportPage(key, page.Items.Length, page.NextCursor);
        });

    /// <summary>Returns recorded operation keys under a fixed prefix for bounded reconciliation.</summary>
    public async Task<string[]> KeysAsync(string requestId, string prefix)
    {
        var keys = await db.WithWriteConnection(c => c.QueryAsync<string>(@"
            SELECT substring(record_key FROM length(@prefix) + 1) FROM ingestion_records
            WHERE request_id=@requestId AND left(record_key, length(@prefix))=@prefix",
            new { requestId, prefix }));
        return System.Linq.Enumerable.ToArray(keys);
    }

    /// <summary>Commits domain writes and their result together; repeats return the committed receipt.</summary>
    public async Task<T> ApplyOnceAsync<T>(string requestId, string key, Func<Task<T>> apply) where T : class
    {
        using var scope = new TransactionScope(TransactionScopeOption.Required,
            new TransactionOptions { IsolationLevel = IsolationLevel.RepeatableRead, Timeout = TimeSpan.FromMinutes(5) },
            TransactionScopeAsyncFlowOption.Enabled);
        await db.WithWriteConnection(c => c.ExecuteAsync(@"
            INSERT INTO ingestion_records(request_id, record_key) VALUES (@requestId, @key)
            ON CONFLICT (request_id, record_key) DO NOTHING", new { requestId, key }));
        var json = await db.WithWriteConnection(c => c.QuerySingleOrDefaultAsync<string>(@"
            SELECT payload::text FROM ingestion_records
            WHERE request_id=@requestId AND record_key=@key FOR UPDATE", new { requestId, key }));
        var result = json != null ? JsonConvert.DeserializeObject<T>(json)! : await apply();
        if (json == null)
            await db.WithWriteConnection(c => c.ExecuteAsync(@"
                UPDATE ingestion_records SET payload=CAST(@json AS jsonb), updated_at=now()
                WHERE request_id=@requestId AND record_key=@key",
                new { requestId, key, json = JsonConvert.SerializeObject(result) }));
        scope.Complete();
        return result;
    }
}
