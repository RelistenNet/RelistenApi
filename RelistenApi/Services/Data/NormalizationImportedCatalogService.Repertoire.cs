using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationImportedCatalogService
{
    public const string RepertoireCaptureVersion = "legacy-artist-repertoire-v1";
    public const int MaximumRepertoireSongs = 50_000;

    /// <summary>
    /// Retains the artist's complete stored dictionary and the catalog clock read with it.
    /// A missing artist returns null; an existing artist without songs produces an empty account.
    /// Empty wording remains unknown, and upstream identifiers retain their catalog provenance.
    /// </summary>
    /// <remarks>
    /// Read the resource generation before the catalog snapshot. A losing capture remains valid
    /// historical evidence but throws so the caller retries the whole refresh with fresh facts.
    /// Neither operational timestamps nor capture time enter the stable payload.
    /// </remarks>
    public async Task<NormalizationArtistRepertoireSnapshot?> RefreshArtistRepertoireAsync(
        Guid artistUuid,
        CancellationToken ct = default
    )
    {
        RequireRefreshContext(artistUuid, nameof(artistUuid), ct);
        var resource = new NormalizationResourceKey(
            "legacy_catalog",
            "artist_repertoire",
            artistUuid.ToString("D")
        );
        var generation = (await evidence.GetCurrentInputAsync(resource, ct))?.Generation ?? 0;
        var snapshot = await WithReadSnapshotAsync(
            (connection, transaction, cancellation) =>
                ReadRepertoireProjectionAsync(connection, transaction, artistUuid, cancellation),
            ct
        );
        if (snapshot == null)
        {
            return null;
        }

        var capture = await CaptureSnapshotAsync(
            resource,
            RepertoireCaptureVersion,
            Encoding.UTF8.GetBytes(snapshot.Payload),
            generation,
            ct
        );
        return new(capture, snapshot.CatalogInputs);
    }

    /// <summary>One complete projection for capture and checked binding inside the caller's primary snapshot.</summary>
    internal static async Task<RepertoireProjection?> ReadRepertoireProjectionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid artistUuid,
        CancellationToken ct
    )
    {
        var artist = await connection.QuerySingleOrDefaultAsync<RepertoireArtist>(
            new CommandDefinition(
                """
                SELECT a.id AS "Id",revision.generation AS "Generation"
                FROM artists a LEFT JOIN normalization.catalog_artist_revisions revision ON revision.artist_uuid=a.uuid
                WHERE a.uuid=@artistUuid
                """,
                new { artistUuid },
                transaction,
                cancellationToken: ct
            )
        );
        if (artist == null)
        {
            return null;
        }
        if (artist.Generation == null)
        {
            throw new NormalizationConflictException(
                "The repertoire artist has no catalog revision; repair it before refreshing."
            );
        }

        // This lookahead detects an oversized dictionary without reading or retaining a
        // partial song list. RepeatableRead keeps the subsequent complete projection equal
        // to this count, including concurrent inserts, moves and removals.
        var budget = await connection.QuerySingleAsync<RepertoireBudget>(
            new CommandDefinition(
                """
                /* artist_repertoire_snapshot_count */
                SELECT count(*)::integer AS "Count",COALESCE(sum(
                    COALESCE(octet_length(name),0)::bigint+COALESCE(octet_length(slug),0)
                    +COALESCE(octet_length(upstream_identifier),0)+36
                ),0)::bigint AS "FieldBytes"
                FROM (
                    SELECT name,slug,upstream_identifier FROM setlist_songs WHERE artist_id=@Id LIMIT @lookahead
                ) songs
                """,
                new { artist.Id, lookahead = MaximumRepertoireSongs + 1 },
                transaction,
                cancellationToken: ct
            )
        );
        if (budget.Count > MaximumRepertoireSongs)
        {
            throw new InvalidDataException("Imported repertoire exceeds its song limit.");
        }

        if (budget.FieldBytes > MaximumPayloadBytes)
        {
            throw new InvalidDataException("Imported repertoire exceeds its byte limit.");
        }

        var payload = await connection.QuerySingleAsync<RepertoirePayload>(
            new CommandDefinition(
                """
                WITH snapshot AS (
                    SELECT jsonb_build_object(
                        'schema_version',1,
                        'artist_uuid',@artistUuid::uuid,
                        'songs',COALESCE((
                            SELECT jsonb_agg(jsonb_build_object(
                                'uuid',song.uuid,'name',song.name,'slug',song.slug,
                                'upstream_identifier',song.upstream_identifier
                            ) ORDER BY song.uuid)
                            FROM setlist_songs song WHERE song.artist_id=@Id
                        ),'[]'::jsonb),
                        'catalog_inputs',jsonb_build_array(jsonb_build_object(
                            'artist_uuid',@artistUuid::uuid,'generation',@generation::bigint
                        ))
                    )::text AS payload
                )
                SELECT CASE WHEN octet_length(payload)<=@maxBytes THEN payload END AS "Payload",
                    octet_length(payload)::bigint AS "ByteCount" FROM snapshot
                """,
                new
                {
                    artistUuid,
                    artist.Id,
                    generation = artist.Generation.Value,
                    maxBytes = MaximumPayloadBytes,
                },
                transaction,
                cancellationToken: ct
            )
        );
        if (payload.ByteCount > MaximumPayloadBytes)
        {
            throw new InvalidDataException("Imported repertoire exceeds its byte limit.");
        }
        return new(payload.Payload!, [new(artistUuid, artist.Generation.Value)]);
    }

    private sealed record RepertoireArtist(int Id, long? Generation);

    private sealed record RepertoirePayload(string? Payload, long ByteCount);

    private sealed record RepertoireBudget(int Count, long FieldBytes);

    internal sealed record RepertoireProjection(
        string Payload,
        IReadOnlyList<NormalizationCatalogDependency> CatalogInputs
    );
}
