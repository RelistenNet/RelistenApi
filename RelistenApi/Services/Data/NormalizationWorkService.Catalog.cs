using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationWorkService
{
    private sealed record CatalogReadSet(
        IReadOnlyList<NormalizationCatalogDependency> Artists,
        IReadOnlyList<NormalizationSourceIdentityDependency> SourceIdentities,
        IReadOnlyList<NormalizationSourceUuidDependency> SourceUuids,
        IReadOnlyList<NormalizationImportedVenueDependency> ImportedVenues,
        NormalizationMappingRegistryDependency? MappingRegistry
    );

    /// <summary>
    /// Take artist, source-identifier, source-UUID, venue and mapping-registry clocks in that order, then resources and owners.
    /// Shared locks let independent publications proceed together while catalog writers wait.
    /// A publication callback must never acquire catalog row locks or mutate catalog tables.
    /// </summary>
    private static async Task<CatalogReadSet> LockCatalogReadSet(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid[] artistUuids,
        string[] sourceIdentifiers,
        Guid[] sourceUuids,
        Guid[] importedVenueUuids,
        bool usesMappingRegistry,
        bool initializeMissingClocks,
        CancellationToken ct
    )
    {
        var artists = artistUuids.Distinct().ToArray();
        var identifiers = sourceIdentifiers.Distinct(StringComparer.Ordinal).ToArray();
        IReadOnlyList<NormalizationCatalogDependency> artistRows = [];
        if (artists.Length > 0)
        {
            artistRows = (
                await connection.QueryAsync<NormalizationCatalogDependency>(
                    new CommandDefinition(
                        """
                        SELECT artist_uuid AS "ArtistUuid",generation AS "Generation"
                        FROM normalization.catalog_artist_revisions
                        WHERE artist_uuid=ANY(@artists) ORDER BY artist_uuid FOR SHARE
                        """,
                        new { artists },
                        transaction,
                        cancellationToken: ct
                    )
                )
            ).ToArray();
        }
        IReadOnlyList<NormalizationSourceIdentityDependency> identityRows = [];
        if (identifiers.Length > 0)
        {
            if (initializeMissingClocks)
            {
                await connection.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO normalization.catalog_source_identity_revisions(upstream_identifier,generation)
                        SELECT identifier,0 FROM unnest(@identifiers::text[]) s(identifier)
                        ORDER BY identifier COLLATE "C" ON CONFLICT DO NOTHING
                        """,
                        new { identifiers },
                        transaction,
                        cancellationToken: ct
                    )
                );
            }
            identityRows = (
                await connection.QueryAsync<NormalizationSourceIdentityDependency>(
                    new CommandDefinition(
                        """
                        SELECT upstream_identifier AS "UpstreamIdentifier",generation AS "Generation"
                        FROM normalization.catalog_source_identity_revisions
                        WHERE upstream_identifier=ANY(@identifiers)
                        ORDER BY upstream_identifier COLLATE "C" FOR SHARE
                        """,
                        new { identifiers },
                        transaction,
                        cancellationToken: ct
                    )
                )
            ).ToArray();
        }
        if (artistRows.Count != artists.Length || identityRows.Count != identifiers.Length)
        {
            throw new NormalizationConflictException(
                "Catalog binding revisions are missing; reload the catalog binding."
            );
        }
        var sourceUuidRows = await LockSourceUuidClocks(
            connection,
            transaction,
            sourceUuids,
            initializeMissingClocks,
            ct
        );
        var venues = await LockImportedVenueClocks(
            connection,
            transaction,
            importedVenueUuids,
            initializeMissingClocks,
            ct
        );
        NormalizationMappingRegistryDependency? mappingRegistry = null;
        if (usesMappingRegistry)
        {
            mappingRegistry =
                await connection.QuerySingleAsync<NormalizationMappingRegistryDependency>(
                    new CommandDefinition(
                        "SELECT generation AS \"Generation\" FROM normalization.catalog_mapping_registry_revision WHERE singleton FOR SHARE",
                        transaction: transaction,
                        cancellationToken: ct
                    )
                );
        }
        return new(artistRows, identityRows, sourceUuidRows, venues, mappingRegistry);
    }

    private static async Task<
        IReadOnlyList<NormalizationImportedVenueDependency>
    > LockImportedVenueClocks(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid[] venueUuids,
        bool initialize,
        CancellationToken ct
    )
    {
        if (venueUuids.Length == 0)
        {
            return [];
        }
        if (initialize)
        {
            // A missing venue is still a lookup dependency. Initialize its tombstone under
            // the same ordering as catalog DML, then compare the caller's original clock.
            await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO normalization.catalog_venue_revisions(venue_uuid,generation)
                    SELECT venue_uuid,0 FROM unnest(@venueUuids::uuid[]) AS v(venue_uuid)
                    ORDER BY venue_uuid ON CONFLICT DO NOTHING
                    """,
                    new { venueUuids },
                    transaction,
                    cancellationToken: ct
                )
            );
        }
        var rows = (
            await connection.QueryAsync<NormalizationImportedVenueDependency>(
                new CommandDefinition(
                    """
                    SELECT venue_uuid AS "VenueUuid",generation AS "Generation"
                    FROM normalization.catalog_venue_revisions WHERE venue_uuid=ANY(@venueUuids)
                    ORDER BY venue_uuid FOR SHARE
                    """,
                    new { venueUuids },
                    transaction,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        if (rows.Length != venueUuids.Length)
        {
            throw new NormalizationConflictException(
                "Imported venue revisions are missing; reload the catalog snapshot."
            );
        }
        return rows;
    }

    private static void RequireCatalogReadSet(
        CatalogReadSet current,
        IReadOnlyList<NormalizationCatalogDependency> expectedArtists,
        IReadOnlyList<NormalizationSourceIdentityDependency> expectedSources,
        IReadOnlyList<NormalizationSourceUuidDependency> expectedSourceUuids,
        IReadOnlyList<NormalizationImportedVenueDependency> expectedVenues,
        NormalizationMappingRegistryDependency? expectedMappingRegistry
    )
    {
        if (
            expectedArtists.Any(expected => !current.Artists.Contains(expected))
            || expectedSources.Any(expected => !current.SourceIdentities.Contains(expected))
            || expectedSourceUuids.Any(expected => !current.SourceUuids.Contains(expected))
            || expectedVenues.Any(expected => !current.ImportedVenues.Contains(expected))
            || (
                expectedMappingRegistry != null
                && expectedMappingRegistry != current.MappingRegistry
            )
        )
        {
            throw new NormalizationConflictException(
                "Catalog bindings changed; reload them before starting normalization work."
            );
        }
    }

    private static bool CatalogIsCurrent(NormalizationWorkTicket ticket, CatalogReadSet current) =>
        (ticket.CatalogInputs ?? [])
            .OrderBy(value => value.ArtistUuid)
            .SequenceEqual(current.Artists.OrderBy(value => value.ArtistUuid))
        && (ticket.SourceIdentities ?? [])
            .OrderBy(value => value.UpstreamIdentifier, StringComparer.Ordinal)
            .SequenceEqual(
                current.SourceIdentities.OrderBy(
                    value => value.UpstreamIdentifier,
                    StringComparer.Ordinal
                )
            )
        && (ticket.SourceUuids ?? [])
            .OrderBy(value => value.SourceUuid)
            .SequenceEqual(current.SourceUuids.OrderBy(value => value.SourceUuid))
        && (ticket.ImportedVenues ?? [])
            .OrderBy(value => value.VenueUuid)
            .SequenceEqual(current.ImportedVenues.OrderBy(value => value.VenueUuid))
        && ticket.MappingRegistry == current.MappingRegistry;

    private static async Task ConfigureCatalogBindings(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid subjectUuid,
        CatalogReadSet readSet,
        CancellationToken ct
    )
    {
        var artists = readSet.Artists.Select(value => value.ArtistUuid).ToArray();
        var identifiers = readSet
            .SourceIdentities.Select(value => value.UpstreamIdentifier)
            .ToArray();
        var venues = readSet.ImportedVenues.Select(value => value.VenueUuid).ToArray();
        var sourceUuids = readSet.SourceUuids.Select(value => value.SourceUuid).ToArray();
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                DELETE FROM normalization.work_catalog_inputs
                WHERE subject_uuid=@subjectUuid AND NOT (artist_uuid=ANY(@artists));
                DELETE FROM normalization.work_source_identity_inputs
                WHERE subject_uuid=@subjectUuid AND NOT (upstream_identifier=ANY(@identifiers));
                DELETE FROM normalization.work_source_uuid_inputs
                WHERE subject_uuid=@subjectUuid AND NOT (source_uuid=ANY(@sourceUuids));
                INSERT INTO normalization.work_source_uuid_inputs(subject_uuid,source_uuid)
                SELECT @subjectUuid,unnest(@sourceUuids::uuid[]) ON CONFLICT DO NOTHING;
                DELETE FROM normalization.work_venue_inputs
                WHERE subject_uuid=@subjectUuid AND NOT (venue_uuid=ANY(@venues));
                INSERT INTO normalization.work_venue_inputs(subject_uuid,venue_uuid)
                SELECT @subjectUuid,unnest(@venues::uuid[]) ON CONFLICT DO NOTHING;
                INSERT INTO normalization.work_catalog_inputs(subject_uuid,artist_uuid)
                SELECT @subjectUuid,unnest(@artists::uuid[]) ON CONFLICT DO NOTHING;
                INSERT INTO normalization.work_source_identity_inputs(subject_uuid,upstream_identifier)
                SELECT @subjectUuid,unnest(@identifiers::text[]) ON CONFLICT DO NOTHING;
                DELETE FROM normalization.work_mapping_registry_inputs WHERE subject_uuid=@subjectUuid AND NOT @usesMappingRegistry;
                INSERT INTO normalization.work_mapping_registry_inputs(subject_uuid)
                SELECT @subjectUuid WHERE @usesMappingRegistry ON CONFLICT DO NOTHING;
                """,
                new
                {
                    subjectUuid,
                    artists,
                    identifiers,
                    venues,
                    sourceUuids,
                    usesMappingRegistry = readSet.MappingRegistry != null,
                },
                transaction,
                cancellationToken: ct
            )
        );
    }

    private static Task RecordCatalogPublication(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        NormalizationWorkTicket ticket,
        CancellationToken ct
    ) =>
        connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE normalization.work_catalog_inputs i SET published_generation=c.generation
                FROM unnest(@artists::uuid[],@artistGenerations::bigint[]) AS c(artist_uuid,generation)
                WHERE i.subject_uuid=@SubjectUuid AND i.artist_uuid=c.artist_uuid;
                UPDATE normalization.work_source_identity_inputs i SET published_generation=c.generation
                FROM unnest(@identifiers::text[],@identityGenerations::bigint[]) AS c(identifier,generation)
                WHERE i.subject_uuid=@SubjectUuid AND i.upstream_identifier=c.identifier;
                UPDATE normalization.work_source_uuid_inputs i SET published_generation=c.generation
                FROM unnest(@sourceUuids::uuid[],@sourceUuidGenerations::bigint[]) AS c(source_uuid,generation)
                WHERE i.subject_uuid=@SubjectUuid AND i.source_uuid=c.source_uuid;
                UPDATE normalization.work_venue_inputs i SET published_generation=c.generation
                FROM unnest(@venues::uuid[],@venueGenerations::bigint[]) AS c(venue_uuid,generation)
                WHERE i.subject_uuid=@SubjectUuid AND i.venue_uuid=c.venue_uuid;
                UPDATE normalization.work_mapping_registry_inputs SET published_generation=@mappingGeneration
                WHERE subject_uuid=@SubjectUuid;
                """,
                new
                {
                    ticket.SubjectUuid,
                    sourceUuids = (ticket.SourceUuids ?? [])
                        .Select(value => value.SourceUuid)
                        .ToArray(),
                    sourceUuidGenerations = (ticket.SourceUuids ?? [])
                        .Select(value => value.Generation)
                        .ToArray(),
                    venues = (ticket.ImportedVenues ?? [])
                        .Select(value => value.VenueUuid)
                        .ToArray(),
                    venueGenerations = (ticket.ImportedVenues ?? [])
                        .Select(value => value.Generation)
                        .ToArray(),
                    mappingGeneration = ticket.MappingRegistry?.Generation,
                    artists = (ticket.CatalogInputs ?? [])
                        .Select(value => value.ArtistUuid)
                        .ToArray(),
                    artistGenerations = (ticket.CatalogInputs ?? [])
                        .Select(value => value.Generation)
                        .ToArray(),
                    identifiers = (ticket.SourceIdentities ?? [])
                        .Select(value => value.UpstreamIdentifier)
                        .ToArray(),
                    identityGenerations = (ticket.SourceIdentities ?? [])
                        .Select(value => value.Generation)
                        .ToArray(),
                },
                transaction,
                cancellationToken: ct
            )
        );
}
