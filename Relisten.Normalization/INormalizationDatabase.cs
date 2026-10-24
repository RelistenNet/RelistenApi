using Npgsql;

namespace Relisten.Normalization;

/// The database normalization writes to. The host supplies it (RelistenApi adapts its
/// DbService), so this project never references the API.
public interface INormalizationDatabase
{
    /// A new, unopened read-write connection with a long command timeout: catalogue steps read
    /// a whole artist and write in one transaction.
    NpgsqlConnection CreateWriteConnection();
}
