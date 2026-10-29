using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;

namespace Relisten.Catalog;

/// <summary>
/// Owns a single primary snapshot for a catalog read. Readers must pass this
/// transaction to every command; materialized results may outlive the session.
/// </summary>
public sealed class CatalogReadSession : IAsyncDisposable
{
    private CatalogReadSession(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        Connection = connection;
        Transaction = transaction;
    }

    public NpgsqlConnection Connection { get; }
    public NpgsqlTransaction Transaction { get; }

    public static async Task<CatalogReadSession> Open(
        DbService db,
        CancellationToken cancellationToken
    )
    {
        var connection = db.CreateConnection(longTimeout: false, readOnly: false);
        try
        {
            await connection.OpenAsync(cancellationToken);
            var transaction = await connection.BeginTransactionAsync(
                IsolationLevel.RepeatableRead,
                cancellationToken
            );
            await connection.ExecuteAsync(
                new CommandDefinition(
                    "SET TRANSACTION READ ONLY",
                    transaction: transaction,
                    commandTimeout: 30,
                    cancellationToken: cancellationToken
                )
            );
            return new(connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public CommandDefinition Command(
        string sql,
        object? parameters,
        CancellationToken cancellationToken
    ) =>
        new(sql, parameters, Transaction, commandTimeout: 30, cancellationToken: cancellationToken);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Transaction.DisposeAsync();
        }
        finally
        {
            await Connection.DisposeAsync();
        }
    }
}
