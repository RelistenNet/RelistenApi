using System;
using System.Data;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Relisten
{
    public class DbService
    {
        public DbService(string url, IHostEnvironment hostEnvironment)
        {
            var uri = new Uri(url);
            var parts = uri.UserInfo.Split(':');
            ConnStr =
                $"Host={uri.Host};Port={uri.Port.ToString()};Username={parts[0]};Password={parts[1]};Database={uri.AbsolutePath.Substring(1)};Include Error Detail=true";

            // A bit of a hack, but it'll work well enough in prod and locally we don't need multiple dbs
            ReadOnlyConnStr = ConnStr.Replace("relisten-db-rw.default", "relisten-db-ro.default");

            Console.WriteLine("Attempting to connect to {0}", url.Replace(parts[1], "********"));
            Console.WriteLine($"DB Connection: {ConnStr.Replace(parts[1], "********")}");
            Console.WriteLine($"DB Connection (read-only): {ReadOnlyConnStr.Replace(parts[1], "********")}");

            if (hostEnvironment.IsDevelopment())
            {
                var loggerFactory =
                    LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddConsole());
                NpgsqlLoggingConfiguration.InitializeLogging(loggerFactory, parameterLoggingEnabled: true);
            }
        }

        public static string ConnStr { get; set; }
        public static string ReadOnlyConnStr { get; set; }

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
            catch (NpgsqlException ex)
            {
                throw new Exception(
                    string.Format("{0}.WithConnection() experienced a SQL exception (not a timeout)",
                        GetType().FullName), ex);
            }
        }

        // use for buffered queries that do not return a type
        public async Task WithConnection(Func<IDbConnection, Task> getData, bool longTimeout = false, bool readOnly = false)
        {
            try
            {
                using (var connection = CreateConnection(longTimeout, readOnly))
                {
                    await connection.OpenAsync();
                    await getData(connection);
                }
            }
            catch (TimeoutException ex)
            {
                throw new Exception(
                    string.Format("{0}.WithConnection() experienced a SQL timeout", GetType().FullName), ex);
            }
            catch (NpgsqlException ex)
            {
                throw new Exception(
                    string.Format("{0}.WithConnection() experienced a SQL exception (not a timeout)",
                        GetType().FullName), ex);
            }
        }

        // use for non-buffered queries that return a type
        public async Task<TResult> WithConnection<TRead, TResult>(Func<IDbConnection, Task<TRead>> getData,
            Func<TRead, Task<TResult>> process, bool longTimeout = false, bool readOnly = false)
        {
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
            catch (NpgsqlException ex)
            {
                throw new Exception(
                    string.Format("{0}.WithConnection() experienced a SQL exception (not a timeout)",
                        GetType().FullName), ex);
            }
        }
    }
}
