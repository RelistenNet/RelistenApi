using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationCandidateService
{
    private static async Task<RepertoireOwnerBindings> ReadRepertoireOwnerBindings(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid owner,
        CandidateFactsContext context,
        CancellationToken ct
    )
    {
        var bindings = await c.ExecuteScalarAsync<long>(
            new CommandDefinition(
                """
                SELECT (SELECT count(*) FROM normalization.work_inputs WHERE subject_uuid=@owner)
                    +(SELECT count(*) FROM normalization.work_dependencies WHERE subject_uuid=@owner)
                    +(SELECT count(*) FROM normalization.work_catalog_inputs WHERE subject_uuid=@owner)
                    +(SELECT count(*) FROM normalization.work_source_identity_inputs WHERE subject_uuid=@owner)
                    +(SELECT count(*) FROM normalization.work_mapping_registry_inputs WHERE subject_uuid=@owner)
                    +(SELECT count(*) FROM normalization.work_venue_inputs WHERE subject_uuid=@owner)
                    +(SELECT count(*) FROM normalization.work_candidate_inputs WHERE subject_uuid=@owner)
                    +(SELECT count(*) FROM normalization.work_owner_observations WHERE subject_uuid=@owner)
                    +(SELECT count(*) FROM normalization.work_source_uuid_inputs WHERE subject_uuid=@owner)
                """,
                new { owner },
                t,
                cancellationToken: ct
            )
        );
        if (bindings > 100000)
        {
            throw new InvalidOperationException(
                "The complete repertoire owner binding snapshot exceeds its 100,000 binding limit."
            );
        }

        using var rows = await c.QueryMultipleAsync(
            new CommandDefinition(
                """
                SELECT p.uuid AS "ResourceUuid",p.current_revision_uuid AS "RevisionUuid",p.generation AS "Generation",
                    p.provider AS "Provider",p.resource_type AS "ResourceType",p.provider_key AS "ProviderKey"
                FROM normalization.work_inputs i JOIN normalization.input_resources p ON p.uuid=i.resource_uuid
                WHERE i.subject_uuid=@owner ORDER BY p.uuid;

                SELECT s.uuid AS "SubjectUuid",s.desired_generation AS "DesiredGeneration",
                    s.editorial_revision AS "EditorialRevision",s.published_work_uuid AS "PublishedWorkUuid"
                FROM normalization.work_dependencies i JOIN normalization.work_subjects s ON s.uuid=i.dependency_uuid
                WHERE i.subject_uuid=@owner ORDER BY s.uuid;

                SELECT r.artist_uuid AS "ArtistUuid",r.generation AS "Generation"
                FROM normalization.work_catalog_inputs i JOIN normalization.catalog_artist_revisions r ON r.artist_uuid=i.artist_uuid
                WHERE i.subject_uuid=@owner ORDER BY r.artist_uuid;

                SELECT r.upstream_identifier AS "UpstreamIdentifier",r.generation AS "Generation"
                FROM normalization.work_source_identity_inputs i JOIN normalization.catalog_source_identity_revisions r
                    ON r.upstream_identifier=i.upstream_identifier
                WHERE i.subject_uuid=@owner ORDER BY r.upstream_identifier COLLATE "C";

                SELECT r.generation AS "Generation"
                FROM normalization.work_mapping_registry_inputs i CROSS JOIN normalization.catalog_mapping_registry_revision r
                WHERE i.subject_uuid=@owner AND r.singleton;

                SELECT r.venue_uuid AS "VenueUuid",r.generation AS "Generation"
                FROM normalization.work_venue_inputs i JOIN normalization.catalog_venue_revisions r ON r.venue_uuid=i.venue_uuid
                WHERE i.subject_uuid=@owner ORDER BY r.venue_uuid;

                SELECT r.domain AS "Domain",r.scope_uuid AS "ScopeUuid",r.generation AS "Generation"
                FROM normalization.work_candidate_inputs i JOIN normalization.candidate_universe_revisions r
                    ON r.domain=i.domain AND r.scope_uuid=i.scope_uuid
                WHERE i.subject_uuid=@owner ORDER BY r.domain,r.scope_uuid;

                SELECT s.uuid AS "SubjectUuid",s.desired_generation AS "DesiredGeneration",
                    s.editorial_revision AS "EditorialRevision",s.published_work_uuid AS "PublishedWorkUuid"
                FROM normalization.work_owner_observations i JOIN normalization.work_subjects s ON s.uuid=i.observed_subject_uuid
                WHERE i.subject_uuid=@owner ORDER BY s.uuid;

                SELECT r.source_uuid AS "SourceUuid",r.generation AS "Generation"
                FROM normalization.work_source_uuid_inputs i JOIN normalization.catalog_source_uuid_revisions r ON r.source_uuid=i.source_uuid
                WHERE i.subject_uuid=@owner ORDER BY r.source_uuid;
                """,
                new { owner },
                t,
                cancellationToken: ct
            )
        );
        var resources = (await rows.ReadAsync<RepertoireOwnerInput>()).ToArray();
        var dependencies = (await rows.ReadAsync<NormalizationSubjectDependency>()).ToArray();
        var catalog = (await rows.ReadAsync<NormalizationCatalogDependency>()).ToArray();
        var identities = (await rows.ReadAsync<NormalizationSourceIdentityDependency>()).ToArray();
        var registry =
            await rows.ReadSingleOrDefaultAsync<NormalizationMappingRegistryDependency>();
        var venues = (await rows.ReadAsync<NormalizationImportedVenueDependency>()).ToArray();
        var universes = (await rows.ReadAsync<RepertoireOwnerUniverse>()).ToArray();
        var observations = (await rows.ReadAsync<NormalizationSubjectObservation>()).ToArray();
        var sources = (await rows.ReadAsync<NormalizationSourceUuidDependency>()).ToArray();
        if (
            resources.Length
                + dependencies.Length
                + catalog.Length
                + identities.Length
                + (registry == null ? 0 : 1)
                + venues.Length
                + universes.Length
                + observations.Length
                + sources.Length
            != bindings
        )
        {
            context.AddIssue(
                CandidateFactsIssueKind.InvalidStoredFact,
                factKind: "owner_binding_stamp_missing"
            );
        }
        if (resources.Any(resource => resource.RevisionUuid == null))
        {
            context.AddIssue(
                CandidateFactsIssueKind.InvalidStoredFact,
                factKind: "owner_input_head_missing"
            );
        }
        var inputs = resources.Where(resource => resource.RevisionUuid.HasValue).ToArray();
        return new(
            new(
                inputs
                    .Select(input => new NormalizationInputDependency(
                        input.ResourceUuid,
                        input.RevisionUuid!.Value,
                        input.Generation
                    ))
                    .ToArray(),
                dependencies,
                catalog,
                identities,
                registry,
                venues,
                universes
                    .Select(universe => new NormalizationCandidateUniverseDependency(
                        new(universe.Domain, universe.ScopeUuid),
                        universe.Generation
                    ))
                    .ToArray(),
                observations,
                sources
            ),
            inputs
                .Select(input => new NormalizationBindingInputReference(
                    input.RevisionUuid!.Value,
                    new(input.Provider, input.ResourceType, input.ProviderKey)
                ))
                .ToArray()
        );
    }

    private sealed record RepertoireOwnerInput(
        Guid ResourceUuid,
        Guid? RevisionUuid,
        long Generation,
        string Provider,
        string ResourceType,
        string ProviderKey
    );

    private sealed record RepertoireOwnerUniverse(string Domain, Guid ScopeUuid, long Generation);

    private sealed record RepertoireOwnerBindings(
        RepertoireResolutionReadSet ReadSet,
        IReadOnlyList<NormalizationBindingInputReference> InputReferences
    );

    private static RepertoireOwnerBindings EmptyRepertoireOwnerBindings() =>
        new(new([], [], [], [], null, [], [], [], []), []);
}
