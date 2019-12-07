using System;
using System.Data;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Npgsql.Logging;

namespace Relisten
{
    public class DbService
    {
        public static string ConnStr { get; set; }

        public DbService(string url, IHostEnvironment hostEnvironment)
        {
            var uri = new Uri(url);
            var parts = uri.UserInfo.Split(':');
            ConnStr = $"Host={uri.Host};Port={uri.Port.ToString()};Username={parts[0]};Password={parts[1]};Database={uri.AbsolutePath.Substring(1)}";

            Console.WriteLine("Attempting to connect to {0}", url.Replace(parts[1], "********"));
            Console.WriteLine($"DB Connection String: {ConnStr.Replace(parts[1], "********")}");

            if (hostEnvironment.IsDevelopment())
            {
                NpgsqlLogManager.IsParameterLoggingEnabled = true;
                NpgsqlLogManager.Provider = new ConsoleLoggingProvider(NpgsqlLogLevel.Debug, true);
            }
        }

        public NpgsqlConnection CreateConnection(bool longTimeout) => new NpgsqlConnection(ConnStr + (longTimeout ? ";CommandTimeout=300" : ""));

        public async Task<T> WithConnection<T>(Func<IDbConnection, Task<T>> getData, bool longTimeout = false)
        {
            try
            {
                using (var connection = CreateConnection(longTimeout))
                {
                    await connection.OpenAsync();
                    return await getData(connection);
                }
            }
            catch (TimeoutException ex)
            {
                throw new Exception(String.Format("{0}.WithConnection() experienced a SQL timeout", GetType().FullName), ex);
            }
            catch (NpgsqlException ex)
            {
                throw new Exception(String.Format("{0}.WithConnection() experienced a SQL exception (not a timeout)", GetType().FullName), ex);
            }
        }

        // use for buffered queries that do not return a type
        public async Task WithConnection(Func<IDbConnection, Task> getData, bool longTimeout = false)
        {
            try
            {
                using (var connection = CreateConnection(longTimeout))
                {
                    await connection.OpenAsync();
                    await getData(connection);
                }
            }
            catch (TimeoutException ex)
            {
                throw new Exception(String.Format("{0}.WithConnection() experienced a SQL timeout", GetType().FullName), ex);
            }
            catch (NpgsqlException ex)
            {
                throw new Exception(String.Format("{0}.WithConnection() experienced a SQL exception (not a timeout)", GetType().FullName), ex);
            }
        }

        // use for non-buffered queries that return a type
        public async Task<TResult> WithConnection<TRead, TResult>(Func<IDbConnection, Task<TRead>> getData, Func<TRead, Task<TResult>> process, bool longTimeout = false)
        {
            try
            {
                using (var connection = CreateConnection(longTimeout))
                {
                    await connection.OpenAsync();
                    var data = await getData(connection);
                    return await process(data);
                }
            }
            catch (TimeoutException ex)
            {
                throw new Exception(String.Format("{0}.WithConnection() experienced a SQL timeout", GetType().FullName), ex);
            }
            catch (NpgsqlException ex)
            {
                throw new Exception(String.Format("{0}.WithConnection() experienced a SQL exception (not a timeout)", GetType().FullName), ex);
            }
        }
    }
}