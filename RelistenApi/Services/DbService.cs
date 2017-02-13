using System;
using System.Data;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;
using Npgsql.Logging;

namespace Relisten
{
    public class DbService
    {
		public string ConnStr { get; set; }

        public DbService(string url)
        {
			Console.WriteLine("Attempting to connect to {0}", url);

			var uri = new Uri(url);
			var parts = uri.UserInfo.Split(':');
			ConnStr = $"Host={uri.Host};Username={parts[0]};Password={parts[1]};Database={uri.AbsolutePath.Substring(1)}";

			Console.WriteLine("DB Connection String: " + ConnStr);

			NpgsqlLogManager.LoggerFactory = (new LoggerFactory()).AddConsole((text, logLevel) => logLevel >= LogLevel.Debug);
        }

        public async Task<T> WithConnection<T>(Func<IDbConnection, Task<T>> getData)
        {
            try
            {
                using (var connection = new NpgsqlConnection(ConnStr))
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
        public async Task WithConnection(Func<IDbConnection, Task> getData)
        {
            try
            {
                using (var connection = new NpgsqlConnection(ConnStr))
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
        public async Task<TResult> WithConnection<TRead, TResult>(Func<IDbConnection, Task<TRead>> getData, Func<TRead, Task<TResult>> process)
        {
            try
            {
                using (var connection = new NpgsqlConnection(ConnStr))
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