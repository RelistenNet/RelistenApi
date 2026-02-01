using System;
using System.Data;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Relisten
{
    public class DbService
    {
        private const int MaxSerializationRetries = 3;
        private readonly ILogger<DbService> _logger;

        public DbService(string url, IHostEnvironment hostEnvironment, ILogger<DbService> logger)
        {
            _logger = logger;
            var uri = new Uri(url);
            var parts = uri.UserInfo.Split(':');
            var port = uri.Port.ToString(CultureInfo.InvariantCulture);
            var database = uri.AbsolutePath.Substring(1);

            ConnStr =
                $"Host={uri.Host};Port={port};Username={parts[0]};Password={parts[1]};Database={database};Include Error Detail=true";

            // For read-only connections: try RO first, fall back to RW if unavailable
            // Npgsql handles multi-host failover automatically
            var roHost = uri.Host.Replace("relisten-db-rw.default", "relisten-db-ro.default");
            ReadOnlyConnStr =
                $"Host={roHost},{uri.Host};Port={port};Username={parts[0]};Password={parts[1]};Database={database};Include Error Detail=true;Target Session Attrs=prefer-standby";

            var maskedUrl = url.Replace(parts[1], "********");
            var maskedConnStr = ConnStr.Replace(parts[1], "********");
            var maskedReadOnlyConnStr = ReadOnlyConnStr.Replace(parts[1], "********");

            _logger.LogInformation("Database connection initialized from {DatabaseUrl}", maskedUrl);
            _logger.LogInformation("Primary connection string: {ConnectionString}", maskedConnStr);
            _logger.LogInformation("Read-only connection string (with fallback): {ReadOnlyConnectionString}", maskedReadOnlyConnStr);

            if (hostEnvironment.IsDevelopment())
            {
                var loggerFactory =
                    LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddConsole());
                NpgsqlLoggingConfiguration.InitializeLogging(loggerFactory, parameterLoggingEnabled: true);
                _logger.LogDebug("Npgsql parameter logging enabled for development environment");
            }
        }

        public static string ConnStr { get; set; } = null!;
        public static string ReadOnlyConnStr { get; set; } = null!;

        public NpgsqlConnection CreateConnection(bool longTimeout, bool readOnly)
        {
            var connectionSuffix = ((!readOnly || longTimeout) ? ";CommandTimeout=300" : "");

            if (readOnly)
            {
                return new NpgsqlConnection(ReadOnlyConnStr + connectionSuffix);
            }

            return new NpgsqlConnection(ConnStr + connectionSuffix);
        }

        public Task<T> WithWriteConnection<T>(Func<IDbConnection, Task<T>> getData, bool longTimeout = false)
        {
            return WithConnection(getData, longTimeout, readOnly: false);
        }

        public Task WithWriteConnection(Func<IDbConnection, Task> getData, bool longTimeout = false)
        {
            return WithConnection(getData, longTimeout, readOnly: false);
        }

        public async Task<T> WithConnection<T>(Func<IDbConnection, Task<T>> getData, bool longTimeout = false, bool readOnly = true)
        {
            if (System.Transactions.Transaction.Current != null)
            {
                // If there's an active transaction make sure we always hit the primary db to replicate the old behavior
                readOnly = false;
            }

            var attempts = 0;

            while (true)
            {
                attempts++;

                try
                {
                    using (var connection = CreateConnection(longTimeout, readOnly))
                    {
                        await connection.OpenAsync();
                        return await getData(connection);
                    }
                }
                catch (TimeoutException ex)
                {
                    throw new Exception(
                        string.Format("{0}.WithConnection() experienced a SQL timeout", GetType().FullName), ex);
                }
                catch (NpgsqlException ex) when (IsSerializationConflict(ex) && attempts < MaxSerializationRetries)
                {
                    // Retry serialization/deadlock conflicts with a small backoff.
                    await Task.Delay(TimeSpan.FromMilliseconds(100 * attempts * attempts));
                }
                catch (NpgsqlException ex)
                {
                    throw new Exception(
                        string.Format("{0}.WithConnection() experienced a SQL exception (not a timeout)",
                            GetType().FullName), ex);
                }
            }
        }

        // use for buffered queries that do not return a type
        public async Task WithConnection(Func<IDbConnection, Task> getData, bool longTimeout = false, bool readOnly = false)
        {
            var attempts = 0;

            while (true)
            {
                attempts++;

                try
                {
                    using (var connection = CreateConnection(longTimeout, readOnly))
                    {
                        await connection.OpenAsync();
                        await getData(connection);
                        return;
                    }
                }
                catch (TimeoutException ex)
                {
                    throw new Exception(
                        string.Format("{0}.WithConnection() experienced a SQL timeout", GetType().FullName), ex);
                }
                catch (NpgsqlException ex) when (IsSerializationConflict(ex) && attempts < MaxSerializationRetries)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100 * attempts * attempts));
                }
                catch (NpgsqlException ex)
                {
                    throw new Exception(
                        string.Format("{0}.WithConnection() experienced a SQL exception (not a timeout)",
                            GetType().FullName), ex);
                }
            }
        }

        // use for non-buffered queries that return a type
        public async Task<TResult> WithConnection<TRead, TResult>(Func<IDbConnection, Task<TRead>> getData,
            Func<TRead, Task<TResult>> process, bool longTimeout = false, bool readOnly = false)
        {
            var attempts = 0;

            while (true)
            {
                attempts++;

                try
                {
                    using (var connection = CreateConnection(longTimeout, readOnly))
                    {
                        await connection.OpenAsync();
                        var data = await getData(connection);
                        return await process(data);
                    }
                }
                catch (TimeoutException ex)
                {
                    throw new Exception(
                        string.Format("{0}.WithConnection() experienced a SQL timeout", GetType().FullName), ex);
                }
                catch (NpgsqlException ex) when (IsSerializationConflict(ex) && attempts < MaxSerializationRetries)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100 * attempts * attempts));
                }
                catch (NpgsqlException ex)
                {
                    throw new Exception(
                        string.Format("{0}.WithConnection() experienced a SQL exception (not a timeout)",
                            GetType().FullName), ex);
                }
            }
        }

        private static bool IsSerializationConflict(NpgsqlException ex)
        {
            if (ex is PostgresException pg)
            {
                return pg.SqlState == PostgresErrorCodes.SerializationFailure ||
                       pg.SqlState == PostgresErrorCodes.DeadlockDetected;
            }

            return false;
        }
    }
}
