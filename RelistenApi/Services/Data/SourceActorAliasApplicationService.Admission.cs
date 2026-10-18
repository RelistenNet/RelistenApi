using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class SourceActorAliasApplicationService
{
    /// <summary>
    /// An admission exception alone is not authority to terminalize a preparation. Prove that
    /// one complete saved factual family differs in a fresh primary snapshot. All admitted
    /// input references carry their original resource generation, so A -> B -> A cannot make
    /// that failed preparation admissible again. New observations classify the old attempt;
    /// they are never copied into its facts, ticket or accepted interpretation.
    /// </summary>
    private Task<bool> AdmissionFactsChangedAsync(
        SourceActorAliasPersonStage stage,
        CancellationToken ct
    ) =>
        InSnapshot(
            async (c, t) =>
            {
                if (
                    stage
                        .Contributions.SelectMany(value => value.ExpectedInputRevisions)
                        .Any(input => input.Generation == null)
                )
                {
                    // The legacy revision-only contract cannot prove permanent failure after an
                    // ABA transition. Keep that stage retryable and propagate the original refusal.
                    return false;
                }
                NormalizationInputContributionsSnapshot membership;
                try
                {
                    membership =
                        await NormalizationWorkService.GetInputContributionsInSnapshotAsync(
                            c,
                            t,
                            new("people", "person", stage.OriginalPersonUuid),
                            PeopleResolutionCapacity.ContributionMembership,
                            ct
                        );
                }
                catch (NormalizationInputContributionsCapacityException)
                {
                    // The saved complete snapshot fit; the current complete membership does not.
                    // No partial vector is used as a replacement or as admission authority.
                    return true;
                }
                if (
                    membership.Owner == null
                    || membership.Owner.SubjectUuid != stage.OwnerSubjectUuid
                    || membership.Owner.DesiredGeneration != stage.ExpectedConfigurationGeneration
                )
                {
                    return true;
                }
                var headers = membership
                    .Contributions.Select(value => new NormalizationInputContributionRevision(
                        value.Contributor,
                        value.Revision
                    ))
                    .Concat(
                        membership.UnmanagedBaseline is { } baseline
                            ? [new(baseline.Contributor, 0)]
                            : []
                    )
                    .ToArray();
                if (!SameSet(headers, stage.ExpectedContributions))
                {
                    return true;
                }
                var saved = stage.Facts.CurrentReadSet;
                var originalOwner =
                    stage.Facts.Membership.Owner
                    ?? throw new NormalizationConflictException(
                        "A saved person stage is missing its original complete owner observation."
                    );
                var expectedOwners = Coherent(
                    (saved.OwnerObservations ?? []).Append(
                        new(
                            originalOwner.SubjectUuid,
                            originalOwner.DesiredGeneration,
                            originalOwner.EditorialRevision,
                            originalOwner.PublishedWorkUuid
                        )
                    ),
                    value => value.SubjectUuid
                );
                var owners = await c.QueryAsync<NormalizationSubjectObservation>(
                    new CommandDefinition(
                        """
                        SELECT uuid AS "SubjectUuid",desired_generation AS "DesiredGeneration",editorial_revision AS "EditorialRevision",
                            published_work_uuid AS "PublishedWorkUuid" FROM normalization.work_subjects WHERE uuid=ANY(@ids)
                        """,
                        new { ids = expectedOwners.Select(value => value.SubjectUuid).ToArray() },
                        t,
                        cancellationToken: ct
                    )
                );
                if (!SameSet(owners, expectedOwners))
                {
                    return true;
                }
                var inputs = await c.QueryAsync<NormalizationInputDependency>(
                    new CommandDefinition(
                        """
                        SELECT uuid AS "ResourceUuid",current_revision_uuid AS "RevisionUuid",generation AS "Generation"
                        FROM normalization.input_resources WHERE uuid=ANY(@ids)
                        """,
                        new
                        {
                            ids = (saved.Inputs ?? [])
                                .Select(value => value.ResourceUuid)
                                .ToArray(),
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                if (!SameSet(inputs, saved.Inputs ?? []))
                {
                    return true;
                }
                var dependencies = await c.QueryAsync<NormalizationSubjectDependency>(
                    new CommandDefinition(
                        """
                        SELECT uuid AS "SubjectUuid",desired_generation AS "DesiredGeneration",editorial_revision AS "EditorialRevision",
                            published_work_uuid AS "PublishedWorkUuid" FROM normalization.work_subjects WHERE uuid=ANY(@ids)
                        """,
                        new
                        {
                            ids = (saved.Dependencies ?? [])
                                .Select(value => value.SubjectUuid)
                                .ToArray(),
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                if (!SameSet(dependencies, saved.Dependencies ?? []))
                {
                    return true;
                }
                return await AdmissionCatalogFactsChanged(c, t, saved, ct)
                    || await AdmissionCandidateFactsChanged(
                        c,
                        t,
                        saved.CandidateUniverses ?? [],
                        ct
                    );
            },
            ct
        );

    private static async Task<bool> AdmissionCatalogFactsChanged(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        NormalizationReviewReadSet saved,
        CancellationToken ct
    )
    {
        // A missing negative clock has generation zero under Work's existing admission
        // contract. Preserve that observation instead of inventing a missing-row mismatch.
        var artists = await c.QueryAsync<NormalizationCatalogDependency>(
            new CommandDefinition(
                """
                SELECT expected.uuid AS "ArtistUuid",coalesce(clock.generation,0) AS "Generation"
                FROM unnest(@ids::uuid[]) expected(uuid)
                LEFT JOIN normalization.catalog_artist_revisions clock ON clock.artist_uuid=expected.uuid
                """,
                new
                {
                    ids = (saved.CatalogInputs ?? []).Select(value => value.ArtistUuid).ToArray(),
                },
                t,
                cancellationToken: ct
            )
        );
        if (!SameSet(artists, saved.CatalogInputs ?? []))
        {
            return true;
        }
        var identities = await c.QueryAsync<NormalizationSourceIdentityDependency>(
            new CommandDefinition(
                """
                SELECT expected.identifier AS "UpstreamIdentifier",coalesce(clock.generation,0) AS "Generation"
                FROM unnest(@ids::text[]) expected(identifier)
                LEFT JOIN normalization.catalog_source_identity_revisions clock ON clock.upstream_identifier=expected.identifier
                """,
                new
                {
                    ids = (saved.SourceIdentities ?? [])
                        .Select(value => value.UpstreamIdentifier)
                        .ToArray(),
                },
                t,
                cancellationToken: ct
            )
        );
        if (!SameSet(identities, saved.SourceIdentities ?? []))
        {
            return true;
        }
        var sources = await c.QueryAsync<NormalizationSourceUuidDependency>(
            new CommandDefinition(
                """
                SELECT expected.uuid AS "SourceUuid",coalesce(clock.generation,0) AS "Generation"
                FROM unnest(@ids::uuid[]) expected(uuid)
                LEFT JOIN normalization.catalog_source_uuid_revisions clock ON clock.source_uuid=expected.uuid
                """,
                new { ids = (saved.SourceUuids ?? []).Select(value => value.SourceUuid).ToArray() },
                t,
                cancellationToken: ct
            )
        );
        if (!SameSet(sources, saved.SourceUuids ?? []))
        {
            return true;
        }
        var venues = await c.QueryAsync<NormalizationImportedVenueDependency>(
            new CommandDefinition(
                """
                SELECT expected.uuid AS "VenueUuid",coalesce(clock.generation,0) AS "Generation"
                FROM unnest(@ids::uuid[]) expected(uuid)
                LEFT JOIN normalization.catalog_venue_revisions clock ON clock.venue_uuid=expected.uuid
                """,
                new
                {
                    ids = (saved.ImportedVenues ?? []).Select(value => value.VenueUuid).ToArray(),
                },
                t,
                cancellationToken: ct
            )
        );
        if (!SameSet(venues, saved.ImportedVenues ?? []))
        {
            return true;
        }
        if (saved.MappingRegistry is { } registry)
        {
            var generation = await c.QuerySingleAsync<long>(
                new CommandDefinition(
                    "SELECT coalesce((SELECT generation FROM normalization.catalog_mapping_registry_revision WHERE singleton),0)",
                    transaction: t,
                    cancellationToken: ct
                )
            );
            if (generation != registry.Generation)
            {
                return true;
            }
        }
        return false;
    }

    private static async Task<bool> AdmissionCandidateFactsChanged(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        IReadOnlyList<NormalizationCandidateUniverseDependency> expected,
        CancellationToken ct
    )
    {
        var rows = await c.QueryAsync<AdmissionCandidateClock>(
            new CommandDefinition(
                """
                SELECT expected."Domain" AS "Domain",expected."ScopeUuid" AS "ScopeUuid",coalesce(clock.generation,0) AS "Generation"
                FROM jsonb_to_recordset(@keys::jsonb) expected("Domain" text,"ScopeUuid" uuid)
                LEFT JOIN normalization.candidate_universe_revisions clock
                    ON clock.domain=expected."Domain" AND clock.scope_uuid=expected."ScopeUuid"
                """,
                new { keys = JsonSerializer.Serialize(expected.Select(value => value.Universe)) },
                t,
                cancellationToken: ct
            )
        );
        return !SameSet(
            rows.Select(value => new NormalizationCandidateUniverseDependency(
                new(value.Domain, value.ScopeUuid),
                value.Generation
            )),
            expected
        );
    }

    private sealed record AdmissionCandidateClock(string Domain, Guid ScopeUuid, long Generation);
}
