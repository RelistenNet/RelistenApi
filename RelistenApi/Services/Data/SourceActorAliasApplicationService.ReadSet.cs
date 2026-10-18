using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

public sealed partial class SourceActorAliasApplicationService
{
    private static async Task<NormalizationSubjectObservation> ReadOwner(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid uuid,
        CancellationToken ct
    ) =>
        await c.QuerySingleOrDefaultAsync<NormalizationSubjectObservation>(
            new CommandDefinition(
                """
                SELECT uuid AS "SubjectUuid",desired_generation AS "DesiredGeneration",editorial_revision AS "EditorialRevision",
                    published_work_uuid AS "PublishedWorkUuid" FROM normalization.work_subjects WHERE uuid=@uuid
                """,
                new { uuid },
                t,
                cancellationToken: ct
            )
        ) ?? throw new NormalizationConflictException("A consumed original owner is missing.");

    internal static async Task<NormalizationReviewReadSet> ReadStageReadSet(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        PreparedSource source,
        IReadOnlyList<SourceActorAliasScopeMapEntry> map,
        IReadOnlyList<SourceActorReviewTarget> targets,
        NormalizationInputContributionsSnapshot membership,
        CancellationToken ct,
        NormalizationSubjectObservation? originalPrimary = null
    )
    {
        var members = membership
            .Contributions.Where(value =>
                value.Contributor != SourceActorAliasApplicationStore.StableContributor
            )
            .Concat(
                membership.UnmanagedBaseline == null ? [] : new[] { membership.UnmanagedBaseline }
            )
            .ToArray();
        var historical = await ReadMapSources(c, t, map, ct);
        var resources = map.Select(entry => entry.Scope.Resource)
            .Concat(historical.SelectMany(value => value.Resources))
            .Concat(members.SelectMany(member => member.Resources))
            .Concat(
                source
                    .Inputs.SelectMany(input => input.OriginalInput.Binding.Inputs)
                    .Select(input => input.Resource)
            )
            .Distinct()
            .ToArray();
        var inputs = (
            await c.QueryAsync<NormalizationInputDependency>(
                new CommandDefinition(
                    """
                    SELECT resource.uuid AS "ResourceUuid",resource.current_revision_uuid AS "RevisionUuid",resource.generation AS "Generation"
                    FROM normalization.input_resources resource JOIN jsonb_to_recordset(@resources::jsonb)
                        AS key("Provider" text,"ResourceType" text,"ProviderKey" text)
                        ON resource.provider=key."Provider" AND resource.resource_type=key."ResourceType" AND resource.provider_key=key."ProviderKey"
                    ORDER BY resource.uuid
                    """,
                    new { resources = System.Text.Json.JsonSerializer.Serialize(resources) },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        if (
            inputs.Length != resources.Length
            || inputs.Any(input => input.RevisionUuid == Guid.Empty)
        )
        {
            throw new NormalizationConflictException(
                "Complete source map resources require their retained current heads."
            );
        }
        var assignmentIds = map.SelectMany(entry => entry.Bindings)
            .Select(binding => binding.AssignmentUuid)
            .Distinct()
            .ToArray();
        var authority = (
            await c.QueryAsync<SupportOwnerRow>(
                new CommandDefinition(
                    """
                    SELECT DISTINCT source_resolution_owner_uuid AS "SourceOwner",extraction_owner_uuid AS "ExtractionOwner"
                    FROM normalization.source_actor_alias_assignments WHERE uuid=ANY(@assignmentIds) AND retired_work_uuid IS NULL
                    """,
                    new { assignmentIds },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        var sourceUuids = map.Select(entry => entry.Scope.SourceUuid).Distinct().ToArray();
        var sourceOwners = (
            await c.QueryAsync<Guid>(
                new CommandDefinition(
                    """
                    SELECT uuid FROM normalization.work_subjects
                    WHERE domain='people' AND subject_kind='source_actor_resolution' AND subject_uuid=ANY(@sourceUuids)
                    """,
                    new { sourceUuids },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        if (sourceOwners.Length != sourceUuids.Length)
        {
            throw new NormalizationConflictException(
                "Complete map tombstones require their original source owners."
            );
        }
        var keys = authority
            .SelectMany(row => new[] { row.SourceOwner, row.ExtractionOwner })
            .Concat(sourceOwners)
            .Concat(historical.Select(value => value.ExtractionOwnerUuid))
            .Concat(
                targets
                    .SelectMany(target => target.OwnerObservations)
                    .Select(owner => owner.SubjectUuid)
            )
            .Append(source.Extraction.Ticket.SubjectUuid)
            .Concat(source.Reviews.OwnerObservation is { } owner ? new[] { owner.SubjectUuid } : [])
            .Concat(
                source.Policy is { } policy ? new[] { policy.OwnerObservation.SubjectUuid } : []
            )
            .Concat(source.Extraction.Ticket.Dependencies.Select(owner => owner.SubjectUuid))
            .Concat(
                (source.Extraction.Ticket.OwnerObservations ?? []).Select(owner =>
                    owner.SubjectUuid
                )
            )
            .Concat(members.SelectMany(member => member.DependencySubjectUuids))
            .Concat(members.SelectMany(member => member.OwnerObservationSubjectUuids ?? []))
            .Append(membership.Owner!.SubjectUuid)
            .Distinct()
            .ToArray();
        var observed = (
            await c.QueryAsync<NormalizationSubjectObservation>(
                new CommandDefinition(
                    """
                    SELECT uuid AS "SubjectUuid",desired_generation AS "DesiredGeneration",editorial_revision AS "EditorialRevision",
                        published_work_uuid AS "PublishedWorkUuid" FROM normalization.work_subjects WHERE uuid=ANY(@keys)
                    """,
                    new { keys },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToList();
        if (observed.Count != keys.Length)
        {
            throw new NormalizationConflictException(
                "Every active source support requires its original current owners."
            );
        }
        observed.AddRange(source.Pools.SelectMany(pool => pool.ReadSet.OwnerObservations));
        var original = source.Extraction.Ticket with
        {
            CatalogInputs = Coherent(
                (source.Extraction.Ticket.CatalogInputs ?? []).Concat(
                    source.Pools.SelectMany(pool => pool.ReadSet.CatalogInputs)
                ),
                value => value.ArtistUuid
            ),
        };
        var catalog = await ObserveCatalog(c, t, original, ct, members);
        // Write scopes protect canonical alias writes independently. Observe a candidate
        // epoch only when this new stage actually consumed a pool or a retained typed peer
        // allocation requires it; old own-source pools are not renewed by receipt carry.
        var universes = members
            .SelectMany(member => member.CandidateUniverses ?? [])
            .Concat(
                (source.Extraction.Ticket.CandidateUniverses ?? []).Select(value => value.Universe)
            )
            .Concat(
                source
                    .Pools.SelectMany(pool => pool.ReadSet.CandidateUniverses)
                    .Select(value => value.Universe)
            )
            .Distinct()
            .ToArray();
        var pools = (
            await c.QueryAsync<CandidateEpochRow>(
                new CommandDefinition(
                    """
                    SELECT key."Domain",key."ScopeUuid",COALESCE(epoch.generation,0) AS "Generation"
                    FROM jsonb_to_recordset(@universes::jsonb) AS key("Domain" text,"ScopeUuid" uuid)
                    LEFT JOIN normalization.candidate_universe_revisions epoch
                        ON epoch.domain=key."Domain" AND epoch.scope_uuid=key."ScopeUuid"
                    ORDER BY key."Domain",key."ScopeUuid"
                    """,
                    new { universes = System.Text.Json.JsonSerializer.Serialize(universes) },
                    t,
                    cancellationToken: ct
                )
            )
        ).Select(value => new NormalizationCandidateUniverseDependency(new(value.Domain, value.ScopeUuid), value.Generation)).ToArray();
        if (pools.Length != universes.Length)
        {
            throw new NormalizationConflictException("A consumed candidate universe is missing.");
        }
        return catalog with
        {
            Inputs = inputs,
            // Work authenticates the configured primary ticket after admission; preserve its
            // original observation in the immutable proof instead of restamping it.
            OwnerObservations = Coherent(
                    observed.Select(value =>
                        originalPrimary?.SubjectUuid == value.SubjectUuid ? originalPrimary : value
                    ),
                    value => value.SubjectUuid
                )
                .OrderBy(value => value.SubjectUuid)
                .ToArray(),
            CandidateUniverses = pools,
        };
    }

    private static async Task<IReadOnlyList<MapSource>> ReadMapSources(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        IReadOnlyList<SourceActorAliasScopeMapEntry> map,
        CancellationToken ct
    )
    {
        var references = map.SelectMany(entry =>
                entry
                    .Bindings.Select(binding => binding.Acceptance.Claim.Proposal)
                    .Concat(
                        entry.LastCompleteObservation == null
                            ? []
                            : new[] { entry.LastCompleteObservation.Proposal }
                    )
                    .Concat(entry.IncompleteObservations.Select(value => value.Proposal))
                    .Concat(
                        (entry.LastWithdrawalAuthorities ?? []).Select(value =>
                            value.Claim.Proposal
                        )
                    )
            )
            .Distinct()
            .ToArray();
        if (references.Length == 0)
        {
            return [];
        }
        var ids = references.Select(value => value.ExtractionAttemptUuid).Distinct().ToArray();
        // Carrying an accepted map does not rerun its historical parser or refresh its old
        // authority. Read only the original immutable producer headers to identify today's
        // consumed heads/owners, in bulk before materializing any source prose.
        var rows = (
            await c.QueryAsync<CarriedExtractionRow>(
                new CommandDefinition(
                    """
                    SELECT extraction.attempt_uuid AS "AttemptUuid",extraction.source_uuid AS "SourceUuid",run.ticket::text AS "Ticket"
                    FROM normalization.recording_processing_attempts extraction
                    JOIN normalization.work_runs run ON run.uuid=extraction.attempt_uuid
                    JOIN normalization.work_subjects owner ON owner.uuid=run.subject_uuid
                    WHERE extraction.attempt_uuid=ANY(@ids) AND extraction.disposition='published' AND run.state='published'
                        AND extraction.batch->'Ticket'=run.ticket AND owner.domain='extractions'
                        AND owner.subject_kind='recording' AND owner.subject_uuid=extraction.source_uuid
                    """,
                    new { ids },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        if (rows.Length != ids.Length)
        {
            throw new NormalizationConflictException(
                "A carried map's original extraction header is missing."
            );
        }
        var tickets = rows.ToDictionary(
            value => value.AttemptUuid,
            value =>
                System.Text.Json.JsonSerializer.Deserialize<NormalizationWorkTicket>(value.Ticket)!
        );
        var resourceIds = tickets
            .Values.SelectMany(value => value.Inputs)
            .Select(value => value.ResourceUuid)
            .Distinct()
            .ToArray();
        var keys = (
            await c.QueryAsync<ResourceKeyRow>(
                new CommandDefinition(
                    """
                    SELECT uuid AS "Uuid",provider AS "Provider",resource_type AS "ResourceType",provider_key AS "ProviderKey"
                    FROM normalization.input_resources WHERE uuid=ANY(@resourceIds)
                    """,
                    new { resourceIds },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToDictionary(value => value.Uuid, value => value.Key);
        if (
            keys.Count != resourceIds.Length
            || rows.Any(row => !map.Any(entry => entry.Scope.SourceUuid == row.SourceUuid))
        )
        {
            throw new NormalizationConflictException(
                "Carried extraction headers differ from their retained map origins."
            );
        }
        return tickets
            .Values.Select(ticket => new MapSource(
                ticket.SubjectUuid,
                ticket.Inputs.Select(value => keys[value.ResourceUuid]).ToArray()
            ))
            .ToArray();
    }

    private static async Task<NormalizationReviewReadSet> ObserveCatalog(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        NormalizationWorkTicket original,
        CancellationToken ct,
        IReadOnlyList<NormalizationInputContributionState>? members = null
    )
    {
        members ??= [];
        var artistKeys = (original.CatalogInputs ?? [])
            .Select(value => value.ArtistUuid)
            .Concat(members.SelectMany(value => value.CatalogArtistUuids))
            .Distinct()
            .ToArray();
        var identityKeys = (original.SourceIdentities ?? [])
            .Select(value => value.UpstreamIdentifier)
            .Concat(members.SelectMany(value => value.SourceIdentifiers))
            .Distinct()
            .ToArray();
        var sourceKeys = (original.SourceUuids ?? [])
            .Select(value => value.SourceUuid)
            .Concat(members.SelectMany(value => value.SourceUuids ?? []))
            .Distinct()
            .ToArray();
        var venueKeys = (original.ImportedVenues ?? [])
            .Select(value => value.VenueUuid)
            .Concat(members.SelectMany(value => value.ImportedVenueUuids ?? []))
            .Distinct()
            .ToArray();
        var artists = (
            await c.QueryAsync<NormalizationCatalogDependency>(
                new CommandDefinition(
                    "SELECT artist_uuid AS \"ArtistUuid\",generation AS \"Generation\" FROM normalization.catalog_artist_revisions WHERE artist_uuid=ANY(@ids)",
                    new { ids = artistKeys },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        var identities = (
            await c.QueryAsync<NormalizationSourceIdentityDependency>(
                new CommandDefinition(
                    "SELECT upstream_identifier AS \"UpstreamIdentifier\",generation AS \"Generation\" FROM normalization.catalog_source_identity_revisions WHERE upstream_identifier=ANY(@ids)",
                    new { ids = identityKeys },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        var sources = (
            await c.QueryAsync<NormalizationSourceUuidDependency>(
                new CommandDefinition(
                    "SELECT source_uuid AS \"SourceUuid\",generation AS \"Generation\" FROM normalization.catalog_source_uuid_revisions WHERE source_uuid=ANY(@ids)",
                    new { ids = sourceKeys },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        var venues = (
            await c.QueryAsync<NormalizationImportedVenueDependency>(
                new CommandDefinition(
                    "SELECT venue_uuid AS \"VenueUuid\",generation AS \"Generation\" FROM normalization.catalog_venue_revisions WHERE venue_uuid=ANY(@ids)",
                    new { ids = venueKeys },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        var registry =
            original.MappingRegistry == null && !members.Any(value => value.UsesMappingRegistry)
                ? null
                : new NormalizationMappingRegistryDependency(
                    await c.QuerySingleAsync<long>(
                        new CommandDefinition(
                            "SELECT generation FROM normalization.catalog_mapping_registry_revision WHERE singleton",
                            transaction: t,
                            cancellationToken: ct
                        )
                    )
                );
        if (
            artists.Length != artistKeys.Length
            || identities.Length != identityKeys.Length
            || sources.Length != sourceKeys.Length
            || venues.Length != venueKeys.Length
        )
        {
            throw new NormalizationConflictException("A consumed catalog clock is missing.");
        }
        return new(
            CatalogInputs: artists.OrderBy(value => value.ArtistUuid).ToArray(),
            SourceIdentities: identities
                .OrderBy(value => value.UpstreamIdentifier, StringComparer.Ordinal)
                .ToArray(),
            MappingRegistry: registry,
            ImportedVenues: venues.OrderBy(value => value.VenueUuid).ToArray(),
            SourceUuids: sources.OrderBy(value => value.SourceUuid).ToArray()
        );
    }

    private static T[] Coherent<T, TKey>(IEnumerable<T> values, Func<T, TKey> key)
        where TKey : notnull
    {
        return values
            .GroupBy(key)
            .Select(group =>
            {
                var first = group.First();
                if (group.Any(value => !Same(first, value)))
                {
                    throw new NormalizationConflictException(
                        "Complete source publication observed conflicting original facts."
                    );
                }
                return first;
            })
            .ToArray();
    }

    private static bool Same<T>(T left, T right) =>
        SourceActorAliasApplicationCheckpointCodec.Same(left, right);

    private static bool SameSet<T>(IEnumerable<T> left, IEnumerable<T> right) =>
        left.ToHashSet().SetEquals(right);

    private sealed record MapSource(
        Guid ExtractionOwnerUuid,
        IReadOnlyList<NormalizationResourceKey> Resources
    );

    private sealed record CarriedExtractionRow(Guid AttemptUuid, Guid SourceUuid, string Ticket);

    private sealed record CandidateEpochRow(string Domain, Guid ScopeUuid, long Generation);

    private sealed record SupportOwnerRow(Guid SourceOwner, Guid ExtractionOwner);
}
