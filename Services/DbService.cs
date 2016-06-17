using System;
using System.Data;
using Npgsql;

namespace Relisten
{
    public class DbService : IDisposable {
        public DbService(string url) {
            var uri = new Uri(url);
            var parts = uri.UserInfo.Split(':');
            var connStr = $"Host={uri.Host};Username={parts[0]};Password={parts[1]};Database={uri.AbsolutePath.Substring(1)}";

            Console.WriteLine("Connecting to: " + connStr);

            connection = new NpgsqlConnection(connStr);
            connection.Open();
        }

        public void Dispose() {
            connection.Dispose();
        }

        public IDbConnection connection { get; set; }
    }
}