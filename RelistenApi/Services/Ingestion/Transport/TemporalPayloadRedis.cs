using System;
using StackExchange.Redis;

namespace Relisten.Ingestion.Transport;

/// <summary>
/// Gives clients and workers the same payload store. Production can retain workflow
/// inputs separately from the disposable cache; local development reuses the cache.
/// </summary>
internal sealed class TemporalPayloadRedis : IDisposable
{
    private readonly ConnectionMultiplexer? connection;

    public TemporalPayloadRedis(string redisUrl)
    {
        connection = ConnectionMultiplexer.Connect(RedisService.BuildConfiguration(redisUrl));
        Database = connection.GetDatabase();
    }

    public TemporalPayloadRedis(IDatabase database)
    {
        Database = database;
    }

    public IDatabase Database { get; }

    public void Dispose()
    {
        // The fallback connection belongs to RedisService, so only close a connection
        // created here for the dedicated payload store.
        connection?.Dispose();
    }
}
