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

    public static async Task RefreshSongCounts(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction = null
    )
    {
        var artists = await connection.QueryAsync<Guid>(
            """
            SELECT a.uuid FROM artists a
            WHERE EXISTS (SELECT 1 FROM norm.songs s WHERE s.artist_uuid=a.uuid)
               OR EXISTS (SELECT 1 FROM setlist_songs s WHERE s.artist_id=a.id)
            ORDER BY hashtext(a.uuid::text)
            """,
            transaction: transaction
        );
        foreach (var artist in artists)
        {
            await connection.ExecuteAsync(
                "SELECT norm.refresh_song_counts(@artist)",
                new { artist },
                transaction: transaction
            );
        }
    }

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
                var songs = await File.ReadAllTextAsync(
                    Path.Combine(AppContext.BaseDirectory, "Fixtures", "v4", "songs.sql")
                );
                await connection.ExecuteAsync(songs);
                await RefreshSongCounts(connection);
                loaded = true;
            }
        }
        finally
        {
            Gate.Release();
        }
    }
}
