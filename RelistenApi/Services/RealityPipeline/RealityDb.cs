using System;
using System.Threading.Tasks;
using Npgsql;
using Relisten.Data;

namespace Relisten.RealityPipeline;

/// Opens write connections for the reality pipeline. Production wraps DbService (PgBouncer
/// options included); tests pass a connection string.
public sealed class RealityDb(Func<NpgsqlConnection> create)
{
    public RealityDb(DbService db)
        : this(() => db.CreateConnection(longTimeout: true, readOnly: false)) { }

    public static RealityDb FromConnectionString(string connectionString) =>
        new(() => new NpgsqlConnection(connectionString));

    public async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = create();
        await connection.OpenAsync();
        return connection;
    }
}
