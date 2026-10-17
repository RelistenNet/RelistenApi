using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

/// <summary>Reads are enabled separately from normalization writers and remain disabled until rollout validation.</summary>
public sealed class NormalizationReadOptions
{
    public bool Enabled { get; set; }
}

public sealed class NormalizationUnavailableException(Exception? innerException = null)
    : Exception("Normalization reads are unavailable.", innerException);

/// <summary>
/// Reads accepted normalization state and current playback relationships together on the primary.
/// Each request uses one read-only repeatable-read transaction, so a correction cannot produce a
/// detail assembled from two different decisions. No GET performs extraction or matching.
/// </summary>
public sealed partial class NormalizationReadService(
    DbService db,
    IOptions<NormalizationReadOptions> options
)
{
    private sealed record ReadCursor(
        int Version,
        string Route,
        Guid Parent,
        string? Filter,
        string Order,
        string Sort,
        Guid Uuid
    );

    private const string EvidenceColumns = """
        e.input_revision_uuid,e.json_pointer,e.start_code_point,e.end_code_point,e.quote,
        CASE WHEN r.uuid IS NULL OR resource.current_revision_uuid IS NULL THEN 'missing'
             WHEN resource.current_revision_uuid=e.input_revision_uuid THEN 'current' ELSE 'changed' END AS evidence_status
        """;
    private const string EvidenceJoins = """
        LEFT JOIN normalization.input_revisions r ON r.uuid=e.input_revision_uuid
        LEFT JOIN normalization.input_resources resource ON resource.uuid=r.resource_uuid
        """;

    // Match the collection playback contract: orphaned tracks, mismatched sets/artists and
    // unrevealed intake artists never establish playable results. This is catalog availability,
    // not a network probe of the remote URL.
    private const string PlayableSources = """
        SELECT s.uuid AS source_uuid,a.uuid AS artist_uuid,a.name AS artist_name,a.slug AS artist_slug,
            sh.uuid AS show_uuid,sh.display_date AS show_date,s.display_date,
            tracks.track_count,tracks.duration
        FROM sources s JOIN artists a ON a.id=s.artist_id
        JOIN shows sh ON sh.id=s.show_id AND sh.artist_id=s.artist_id
        JOIN LATERAL (
            SELECT count(*) AS track_count,COALESCE(sum(t.duration),0)::double precision AS duration
            FROM source_tracks t JOIN source_sets sets ON sets.id=t.source_set_id AND sets.source_id=s.id
            WHERE t.source_id=s.id AND t.artist_id=s.artist_id AND NOT t.is_orphaned
                AND NULLIF(trim(t.mp3_url),'') IS NOT NULL
        ) tracks ON tracks.track_count>0
        WHERE NOT ((a.featured & 2)<>0 AND EXISTS (
            SELECT 1 FROM normalization.archive_artist_candidates candidate
            WHERE candidate.artist_uuid=a.uuid AND NOT candidate.is_existing_artist))
        """;

    private async Task<T> ReadAsync<T>(
        Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> read,
        CancellationToken cancellationToken
    )
    {
        if (!options.Value.Enabled)
        {
            throw new NormalizationUnavailableException();
        }
        try
        {
            await using var connection = db.CreateConnection(false, false);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(
                IsolationLevel.RepeatableRead,
                cancellationToken
            );
            await connection.ExecuteAsync(
                new CommandDefinition(
                    "SET TRANSACTION READ ONLY",
                    transaction: transaction,
                    cancellationToken: cancellationToken
                )
            );
            var result = await read(connection, transaction);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (Exception error) when (error is NpgsqlException or TimeoutException)
        {
            throw new NormalizationUnavailableException(error);
        }
    }

    private static string? NormalizeQuery(string? query)
    {
        if (query == null)
        {
            return null;
        }
        query = query.Trim();
        var length = query.EnumerateRunes().Count();
        if (length is < 2 or > 200)
        {
            throw new ArgumentException(
                "Query must contain 2–200 Unicode characters.",
                nameof(query)
            );
        }
        return NormalizedPeopleService.NameKey(query);
    }

    private static ReadCursor? ParseCursor(
        string? token,
        string route,
        Guid parent,
        string? filter,
        string order,
        int limit
    )
    {
        if (limit is < 1 or > 200)
        {
            throw new ArgumentException("Limit must be between 1 and 200.", nameof(limit));
        }
        if (token == null)
        {
            return null;
        }
        try
        {
            if (token.Length is 0 or > 4096)
            {
                throw new FormatException();
            }
            var padded = token.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight((padded.Length + 3) / 4 * 4, '=');
            var cursor = JsonSerializer.Deserialize<ReadCursor>(Convert.FromBase64String(padded));
            if (
                cursor == null
                || cursor.Version != 1
                || cursor.Route != route
                || cursor.Parent != parent
                || cursor.Filter != filter
                || cursor.Order != order
                || cursor.Uuid == Guid.Empty
                || cursor.Sort == null
            )
            {
                throw new FormatException();
            }
            return cursor;
        }
        catch (Exception error) when (error is FormatException or JsonException)
        {
            throw new ArgumentException(
                "Cursor does not belong to this resource and filter.",
                nameof(token),
                error
            );
        }
    }

    private static string EncodeCursor(
        string route,
        Guid parent,
        string? filter,
        string order,
        string sort,
        Guid uuid
    ) =>
        Convert
            .ToBase64String(
                JsonSerializer.SerializeToUtf8Bytes(
                    new ReadCursor(1, route, parent, filter, order, sort, uuid)
                )
            )
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static NormalizationPage<T> Page<T>(
        IReadOnlyList<T> rows,
        int limit,
        Func<T, string> next
    ) =>
        new()
        {
            items = rows.Take(limit).ToArray(),
            next_cursor = rows.Count > limit ? next(rows[limit - 1]) : null,
        };

    private static async Task<Guid?> ResolveAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid uuid,
        bool person,
        CancellationToken ct
    )
    {
        // The write service rejects cycles. UNION also bounds reads if manually edited redirects
        // violate that invariant; absence of a terminal survivor then returns missing.
        var table = person ? "people" : "canonical_venues";
        var redirects = person ? "person_redirects" : "venue_redirects";
        return await connection.QuerySingleOrDefaultAsync<Guid?>(
            new CommandDefinition(
                $"""
                WITH RECURSIVE identities(uuid) AS (
                    SELECT uuid FROM normalization.{table} WHERE uuid=@uuid
                    UNION
                    SELECT r.survivor_uuid FROM normalization.{redirects} r JOIN identities i ON i.uuid=r.retired_uuid
                )
                SELECT uuid FROM identities i WHERE NOT EXISTS(SELECT 1 FROM normalization.{redirects} r WHERE r.retired_uuid=i.uuid)
                """,
                new { uuid },
                transaction,
                cancellationToken: ct
            )
        );
    }

    private static async Task RequireArtistAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid? artistUuid,
        CancellationToken ct
    )
    {
        if (
            artistUuid != null
            && !await connection.QuerySingleAsync<bool>(
                new CommandDefinition(
                    "SELECT EXISTS(SELECT 1 FROM artists WHERE uuid=@artistUuid)",
                    new { artistUuid },
                    transaction,
                    cancellationToken: ct
                )
            )
        )
        {
            throw new NormalizationEntityNotFoundException();
        }
    }
}

public sealed class NormalizationEntityNotFoundException : Exception { }
