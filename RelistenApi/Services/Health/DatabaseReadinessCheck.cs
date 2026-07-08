using System;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace Relisten.Services.Health;

public interface IReadinessCheck
{
    Task CheckAsync(CancellationToken cancellationToken);
}

public sealed class DatabaseReadinessCheck : IReadinessCheck
{
    private readonly DbService _db;

    public DatabaseReadinessCheck(DbService db)
    {
        _db = db;
    }

    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));

        await CheckConnectionAsync(readOnly: true, timeout.Token);
        await CheckConnectionAsync(readOnly: false, timeout.Token);
    }

    private async Task CheckConnectionAsync(bool readOnly, CancellationToken cancellationToken)
    {
        await using var connection = _db.CreateConnection(longTimeout: false, readOnly);
        await connection.OpenAsync(cancellationToken);
        await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT 1",
            commandTimeout: 3,
            cancellationToken: cancellationToken));
    }
}
