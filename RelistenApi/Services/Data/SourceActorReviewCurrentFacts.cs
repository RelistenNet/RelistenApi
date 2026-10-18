using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

/// <summary>
/// Reads today's resource and catalog observations separately from immutable producer proof.
/// Positive review must still match the original extraction's consumed context; historical
/// withdrawal may observe newer values without declaring its old field current.
/// </summary>
internal static class SourceActorReviewCurrentFacts
{
    internal static async Task<(Guid? ArtistUuid, NormalizationReviewReadSet ReadSet)> ReadAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SourceActorExtractionConsumption extraction,
        CancellationToken ct
    )
    {
        var original = extraction.Ticket;
        var artistUuid = await c.QuerySingleOrDefaultAsync<Guid?>(
            new CommandDefinition(
                """
                SELECT artist.uuid FROM sources source JOIN artists artist ON artist.id=source.artist_id
                WHERE source.uuid=@SourceUuid
                """,
                extraction,
                t,
                cancellationToken: ct
            )
        );
        var resources = original.Inputs.Select(input => input.ResourceUuid).Distinct().ToArray();
        var heads = (
            await c.QueryAsync<NormalizationInputDependency>(
                new CommandDefinition(
                    """
                    SELECT uuid AS "ResourceUuid",current_revision_uuid AS "RevisionUuid",generation AS "Generation"
                    FROM normalization.input_resources WHERE uuid=ANY(@resources) ORDER BY uuid
                    """,
                    new { resources },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        var artistKeys = (original.CatalogInputs ?? [])
            .Select(input => input.ArtistUuid)
            .Concat(artistUuid is { } currentArtist ? [currentArtist] : Array.Empty<Guid>())
            .Distinct()
            .ToArray();
        var artists = (
            await c.QueryAsync<NormalizationCatalogDependency>(
                new CommandDefinition(
                    """
                    SELECT artist_uuid AS "ArtistUuid",generation AS "Generation"
                    FROM normalization.catalog_artist_revisions WHERE artist_uuid=ANY(@artistKeys) ORDER BY artist_uuid
                    """,
                    new { artistKeys },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        var identifiers = (original.SourceIdentities ?? [])
            .Select(input => input.UpstreamIdentifier)
            .Distinct()
            .ToArray();
        var sourceIdentities = (
            await c.QueryAsync<NormalizationSourceIdentityDependency>(
                new CommandDefinition(
                    """
                    SELECT upstream_identifier AS "UpstreamIdentifier",generation AS "Generation"
                    FROM normalization.catalog_source_identity_revisions
                    WHERE upstream_identifier=ANY(@identifiers) ORDER BY upstream_identifier COLLATE "C"
                    """,
                    new { identifiers },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        var sourceKeys = (original.SourceUuids ?? [])
            .Select(input => input.SourceUuid)
            .Append(extraction.SourceUuid)
            .Distinct()
            .ToArray();
        var sources = (
            await c.QueryAsync<NormalizationSourceUuidDependency>(
                new CommandDefinition(
                    """
                    SELECT source_uuid AS "SourceUuid",generation AS "Generation"
                    FROM normalization.catalog_source_uuid_revisions WHERE source_uuid=ANY(@sourceKeys) ORDER BY source_uuid
                    """,
                    new { sourceKeys },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        var venueKeys = (original.ImportedVenues ?? [])
            .Select(input => input.VenueUuid)
            .Distinct()
            .ToArray();
        var venues = (
            await c.QueryAsync<NormalizationImportedVenueDependency>(
                new CommandDefinition(
                    """
                    SELECT venue_uuid AS "VenueUuid",generation AS "Generation"
                    FROM normalization.catalog_venue_revisions WHERE venue_uuid=ANY(@venueKeys) ORDER BY venue_uuid
                    """,
                    new { venueKeys },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        var registry =
            original.MappingRegistry == null
                ? null
                : await c.QuerySingleAsync<NormalizationMappingRegistryDependency>(
                    new CommandDefinition(
                        "SELECT generation AS \"Generation\" FROM normalization.catalog_mapping_registry_revision WHERE singleton",
                        transaction: t,
                        cancellationToken: ct
                    )
                );
        if (
            heads.Length != resources.Length
            || artists.Length != artistKeys.Length
            || sourceIdentities.Length != identifiers.Length
            || sources.Length != sourceKeys.Length
            || venues.Length != venueKeys.Length
        )
        {
            throw new NormalizationConflictException(
                "The producer's resource/catalog observation keys are incomplete."
            );
        }
        return (
            artistUuid,
            new NormalizationReviewReadSet(
                Inputs: heads.OrderBy(value => value.ResourceUuid).ToArray(),
                CatalogInputs: artists.Length == 0
                    ? null
                    : artists.OrderBy(value => value.ArtistUuid).ToArray(),
                SourceIdentities: sourceIdentities.Length == 0
                    ? null
                    : sourceIdentities
                        .OrderBy(value => value.UpstreamIdentifier, StringComparer.Ordinal)
                        .ToArray(),
                MappingRegistry: registry,
                ImportedVenues: venues.Length == 0
                    ? null
                    : venues.OrderBy(value => value.VenueUuid).ToArray(),
                SourceUuids: sources.OrderBy(value => value.SourceUuid).ToArray()
            )
        );
    }

    internal static bool StillSupports(
        NormalizationWorkTicket original,
        NormalizationReviewReadSet current
    ) =>
        original.Inputs.All(input => current.Inputs!.Contains(input))
        && (original.CatalogInputs ?? []).All(input =>
            (current.CatalogInputs ?? []).Contains(input)
        )
        && (original.SourceIdentities ?? []).All(input =>
            (current.SourceIdentities ?? []).Contains(input)
        )
        && (original.SourceUuids ?? []).All(input => current.SourceUuids!.Contains(input))
        && (original.ImportedVenues ?? []).All(input =>
            (current.ImportedVenues ?? []).Contains(input)
        )
        && original.MappingRegistry == current.MappingRegistry;
}
