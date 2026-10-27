using Npgsql;
using Relisten.Normalization;

namespace Relisten.Services.Normalization;

/// Normalization's database, through the API's connection settings (pgbouncer in production).
public sealed class DbServiceNormalizationDatabase(DbService db) : INormalizationDatabase
{
    public NpgsqlConnection CreateWriteConnection() =>
        db.CreateConnection(longTimeout: true, readOnly: false);
}
