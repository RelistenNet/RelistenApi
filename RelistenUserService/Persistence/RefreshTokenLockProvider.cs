using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace RelistenUserService.Persistence;

public sealed class RefreshTokenLockProvider : IAsyncDisposable
{
    private const int MaximumPoolSize = 2;
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<RefreshTokenLockProvider> _logger;

    public RefreshTokenLockProvider(
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger<RefreshTokenLockProvider> logger)
    {
        _logger = logger;
        // This tiny pool is separate from ordinary traffic. Lock transactions stay open while
        // OpenIddict uses the main pool; sharing it could make every connection hold a lock
        // while waiting for another connection to perform token I/O.
        var connectionString = DatabaseConnectionString.ResolveRefreshTokenLocks(
            configuration,
            environment);
        var options = new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = "Relisten.UserService.RefreshLocks",
            CommandTimeout = 10,
            Enlist = false,
            MaxPoolSize = MaximumPoolSize,
            MinPoolSize = 0,
            Pooling = true
        };
        _dataSource = NpgsqlDataSource.Create(options.ConnectionString);
    }

    public async Task<IAsyncDisposable> AcquireAsync(
        string refreshToken,
        CancellationToken cancellationToken)
    {
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        NpgsqlTransaction? transaction = null;
        try
        {
            transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT pg_advisory_xact_lock(@key)";
            command.Parameters.AddWithValue("key", CreateKey(refreshToken));
            await command.ExecuteNonQueryAsync(cancellationToken);
            return new Lease(connection, transaction, _logger);
        }
        catch
        {
            if (transaction is not null)
            {
                try
                {
                    await transaction.DisposeAsync();
                }
                catch (Exception exception)
                {
                    _logger.LogError(
                        exception,
                        "Failed to dispose a refresh-token lock transaction after acquisition failed.");
                }
            }

            try
            {
                // Do not rely on today's transaction-disposal implementation to return the
                // connection. Every failed acquisition must release its dedicated pool slot.
                await connection.DisposeAsync();
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Failed to dispose a refresh-token lock connection after acquisition failed.");
            }

            throw;
        }
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();

    private static long CreateKey(string refreshToken)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"refresh-token:{refreshToken}"));
        return BinaryPrimitives.ReadInt64BigEndian(hash);
    }

    private sealed class Lease(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ILogger logger)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await transaction.DisposeAsync();
            }
            catch (Exception exception)
            {
                // Cleanup runs after the response pipeline. Report it without replacing a more
                // useful downstream exception or changing an already-produced token response.
                logger.LogError(exception, "Failed to dispose a refresh-token lock transaction.");
            }

            try
            {
                await connection.DisposeAsync();
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to dispose a refresh-token lock connection.");
            }
        }
    }
}
