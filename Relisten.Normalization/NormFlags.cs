using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;

namespace Relisten.Normalization;

/// The per-artist switches (public.features): norm_venues keeps the artist's venues current
/// in norm (after every import, and in the nightly run); norm_catalogue lets the API read
/// norm for the artist. Read here, never through the API's Features model, whose generated
/// writes would reset them on every import.
public sealed class NormFlags(INormalizationDatabase db)
{
    // A database not yet migrated to the norm schema (a worker deployed before --migrate)
    // has no flags: everything is off, and no import waits on it.
    private const string UndefinedColumn = "42703";

    public async Task<bool> VenuesOnAsync(Guid artist)
    {
        await using var connection = db.CreateWriteConnection();
        try
        {
            return await VenuesOn(connection, artist);
        }
        catch (Npgsql.PostgresException e) when (e.SqlState == UndefinedColumn)
        {
            return false;
        }
    }

    private static Task<bool> VenuesOn(Npgsql.NpgsqlConnection connection, Guid artist) =>
        connection.ExecuteScalarAsync<bool>(
            """
            SELECT coalesce(f.norm_venues, false)
            FROM artists a LEFT JOIN features f ON f.artist_id = a.id
            WHERE a.uuid = @artist
            """,
            new { artist }
        );

    public async Task<IReadOnlyList<Guid>> VenueArtistsAsync()
    {
        await using var connection = db.CreateWriteConnection();
        try
        {
            return (
                await connection.QueryAsync<Guid>(
                    "SELECT a.uuid FROM artists a JOIN features f ON f.artist_id = a.id WHERE f.norm_venues ORDER BY a.id"
                )
            ).ToList();
        }
        catch (Npgsql.PostgresException e) when (e.SqlState == UndefinedColumn)
        {
            return [];
        }
    }
}
