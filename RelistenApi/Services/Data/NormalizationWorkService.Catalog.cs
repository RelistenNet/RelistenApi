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
        IReadOnlyList<NormalizationSourceIdentityDependency> SourceIdentities
    );

    /// <summary>
    /// Take artist clocks before source-identity clocks, then input resources and work owners.
    /// Shared locks let independent publications proceed together while catalog writers wait.
    /// A publication callback must never acquire catalog row locks or mutate catalog tables.
    /// </summary>
    private static async Task<CatalogReadSet> LockCatalogReadSet(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid[] artistUuids,
        string[] sourceIdentifiers,
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
        return new(artistRows, identityRows);
    }

    private static void RequireCatalogReadSet(
        CatalogReadSet current,
        IReadOnlyList<NormalizationCatalogDependency> expectedArtists,
        IReadOnlyList<NormalizationSourceIdentityDependency> expectedSources
    )
    {
        if (
            expectedArtists.Any(expected => !current.Artists.Contains(expected))
            || expectedSources.Any(expected => !current.SourceIdentities.Contains(expected))
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
            );

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
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                DELETE FROM normalization.work_catalog_inputs
                WHERE subject_uuid=@subjectUuid AND NOT (artist_uuid=ANY(@artists));
                DELETE FROM normalization.work_source_identity_inputs
                WHERE subject_uuid=@subjectUuid AND NOT (upstream_identifier=ANY(@identifiers));
                INSERT INTO normalization.work_catalog_inputs(subject_uuid,artist_uuid)
                SELECT @subjectUuid,unnest(@artists::uuid[]) ON CONFLICT DO NOTHING;
                INSERT INTO normalization.work_source_identity_inputs(subject_uuid,upstream_identifier)
                SELECT @subjectUuid,unnest(@identifiers::text[]) ON CONFLICT DO NOTHING;
                """,
                new
                {
                    subjectUuid,
                    artists,
                    identifiers,
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
                """,
                new
                {
                    ticket.SubjectUuid,
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
