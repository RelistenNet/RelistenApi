using System;
using System.Data;
using Npgsql;

namespace Relisten
{
    public class DbService : IDisposable
    {
        private static string connStr { get; set; }
        public static void SetConnectionURL(string url)
        {
            var uri = new Uri(url);
            var parts = uri.UserInfo.Split(':');
            connStr = $"Host={uri.Host};Username={parts[0]};Password={parts[1]};Database={uri.AbsolutePath.Substring(1)}";
        }

        public DbService()
        {
            Console.WriteLine("Connecting to: " + connStr);

            connection = new NpgsqlConnection(connStr);
            connection.Open();
        }

        public void Dispose()
        {
            connection.Dispose();
        }

        public IDbConnection connection { get; set; }
    }
}