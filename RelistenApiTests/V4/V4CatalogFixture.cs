using Dapper;
using Npgsql;

namespace RelistenApiTests.V4;

/// <summary>
/// Loads shared synthetic edge cases once into the process-owned test catalog.
/// Both HTTP and read-session fixtures use these rows and restore their edits.
/// </summary>
internal static class V4CatalogFixture
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool loaded;

    public static async Task EnsureLoaded(NpgsqlConnection connection)
    {
        await Gate.WaitAsync();
        try
        {
            if (!loaded)
            {
                var sql = await File.ReadAllTextAsync(
                    Path.Combine(AppContext.BaseDirectory, "Fixtures", "v4", "catalog.sql")
                );
                await connection.ExecuteAsync(sql);
                loaded = true;
            }
        }
        finally
        {
            Gate.Release();
        }
    }
}
