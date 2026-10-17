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
    public const string ImportedVenueCaptureVersion = "legacy-imported-venue-v1";

    /// <summary>
    /// Retains one imported venue's current stored facts and producer revisions. Returns null
    /// for a missing UUID. Artist-null unused venues remain supported through their own clock.
    /// </summary>
    /// <remarks>
    /// Call outside a catalog transaction. A stale resource capture requires retrying the entire
    /// refresh. Consume the returned original input and catalog revisions during checked binding
    /// and work admission; rebinding old facts must never attach newer producer clocks.
    /// Coordinates retain unknown precision, and past names remain original prose.
    /// </remarks>
    public async Task<NormalizationImportedVenueSnapshot?> RefreshImportedVenueAsync(
        Guid venueUuid,
        CancellationToken ct = default
    )
    {
        RequireRefreshContext(venueUuid, nameof(venueUuid), ct);
        var resource = new NormalizationResourceKey(
            "legacy_catalog",
            "imported_venue",
            venueUuid.ToString("D")
        );
        var generation = (await evidence.GetCurrentInputAsync(resource, ct))?.Generation ?? 0;
        var snapshot = await WithReadSnapshotAsync(
            (connection, transaction, token) =>
                ReadVenueProjectionAsync(connection, transaction, venueUuid, token),
            ct
        );
        if (snapshot == null)
        {
            return null;
        }
        var capture = await CaptureSnapshotAsync(
            resource,
            ImportedVenueCaptureVersion,
            Encoding.UTF8.GetBytes(snapshot.Payload),
            generation,
            ct
        );
        return new(capture, snapshot.CatalogInputs, snapshot.ImportedVenues);
    }

    internal static async Task<ImportedVenueProjection?> ReadVenueProjectionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid venueUuid,
        CancellationToken ct
    )
    {
        var row = await connection.QuerySingleOrDefaultAsync<ImportedVenueRow>(
            new CommandDefinition(
                """
                WITH projection AS MATERIALIZED (
                SELECT v.uuid AS "Uuid",a.uuid AS "ArtistUuid",v.artist_id IS NOT NULL AS "HasArtist",
                    ar.generation AS "ArtistGeneration",vr.generation AS "VenueGeneration",
                    v.latitude AS "Latitude",v.longitude AS "Longitude",
                    jsonb_build_object('schema_version',1,'venue',jsonb_build_object(
                        'uuid',v.uuid,'artist_uuid',a.uuid,'name',v.name,'location',v.location,
                        'upstream_identifier',v.upstream_identifier,'slug',v.slug,'past_names',v.past_names,
                        'latitude',v.latitude,'longitude',v.longitude),
                        'catalog_inputs',CASE WHEN a.uuid IS NULL THEN '[]'::jsonb ELSE
                            jsonb_build_array(jsonb_build_object('artist_uuid',a.uuid,'generation',ar.generation)) END,
                        'imported_venues',jsonb_build_array(jsonb_build_object('venue_uuid',v.uuid,'generation',vr.generation)))::text AS "Payload"
                FROM venues v LEFT JOIN artists a ON a.id=v.artist_id
                LEFT JOIN normalization.catalog_artist_revisions ar ON ar.artist_uuid=a.uuid
                LEFT JOIN normalization.catalog_venue_revisions vr ON vr.venue_uuid=v.uuid
                WHERE v.uuid=@venueUuid
                )
                SELECT "Uuid","ArtistUuid","HasArtist","ArtistGeneration","VenueGeneration","Latitude","Longitude",
                    octet_length("Payload")::bigint AS "PayloadBytes",
                    CASE WHEN octet_length("Payload")<=@maximumBytes THEN "Payload" END AS "Payload"
                FROM projection
                """,
                new { venueUuid, maximumBytes = MaximumPayloadBytes },
                transaction,
                cancellationToken: ct
            )
        );
        if (row == null)
        {
            return null;
        }
        if (row.PayloadBytes > MaximumPayloadBytes)
        {
            throw new InvalidDataException("Imported catalog snapshot exceeds its byte limit.");
        }
        if (
            row.VenueGeneration == null
            || (row.HasArtist && (row.ArtistUuid == null || row.ArtistGeneration == null))
        )
        {
            throw new NormalizationConflictException(
                "Imported venue producer revisions are missing; repair the catalog before refreshing."
            );
        }
        if (
            row.Latitude is { } latitude && !double.IsFinite(latitude)
            || row.Longitude is { } longitude && !double.IsFinite(longitude)
        )
        {
            throw new InvalidDataException(
                "Imported venue coordinates must be finite when supplied."
            );
        }
        IReadOnlyList<NormalizationCatalogDependency> artists =
            row.ArtistUuid == null ? [] : [new(row.ArtistUuid.Value, row.ArtistGeneration!.Value)];
        return new(row.Payload!, artists, [new(row.Uuid, row.VenueGeneration.Value)]);
    }

    private sealed record ImportedVenueRow(
        Guid Uuid,
        Guid? ArtistUuid,
        bool HasArtist,
        long? ArtistGeneration,
        long? VenueGeneration,
        double? Latitude,
        double? Longitude,
        long PayloadBytes,
        string? Payload
    );

    internal sealed record ImportedVenueProjection(
        string Payload,
        IReadOnlyList<NormalizationCatalogDependency> CatalogInputs,
        IReadOnlyList<NormalizationImportedVenueDependency> ImportedVenues
    );
}
