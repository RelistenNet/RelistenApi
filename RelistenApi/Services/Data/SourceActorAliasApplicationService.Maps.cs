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
    private async Task<PersonMap> ReadPersonMap(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid person,
        CancellationToken ct
    )
    {
        var membership = await NormalizationWorkService.GetInputContributionsInSnapshotAsync(
            c,
            t,
            new("people", "person", person),
            PeopleResolutionCapacity.ContributionMembership,
            ct
        );
        if (membership.Owner == null)
        {
            throw new NormalizationConflictException(
                "Register the existing original person's owner before source publication."
            );
        }
        var original = await SourceActorAliasApplicationStore.ReadConfiguredMapInSnapshotAsync(
            c,
            t,
            person,
            membership,
            ct
        );
        var configured = original.Configured;
        var committed = original.Committed;
        var own = membership.Contributions.SingleOrDefault(value =>
            value.Contributor == SourceActorAliasApplicationStore.StableContributor
        );
        if (configured == null && own != null)
        {
            throw new NormalizationConflictException(
                "A configured source producer requires its exact complete saved map, including clear tombstones."
            );
        }
        var map = committed?.ResultingMap ?? [];
        var assignments = (
            await c.QueryAsync<Guid>(
                new CommandDefinition(
                    "SELECT uuid FROM normalization.source_actor_alias_assignments WHERE person_uuid=@person AND retired_work_uuid IS NULL",
                    new { person },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        var support = (
            await c.QueryAsync<BindingRow>(
                new CommandDefinition(
                    """
                    SELECT support.uuid AS "SupportUuid",support.alias_uuid AS "AliasUuid",support.assignment_uuid AS "AssignmentUuid"
                    FROM normalization.person_alias_contributor_support support
                    JOIN normalization.source_actor_alias_assignments assignment ON assignment.uuid=support.assignment_uuid
                    WHERE assignment.person_uuid=@person AND assignment.retired_work_uuid IS NULL AND support.retired_work_uuid IS NULL
                    """,
                    new { person },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        var bindings = map.SelectMany(entry => entry.Bindings).ToArray();
        if (
            !SameSet(assignments, bindings.Select(binding => binding.AssignmentUuid))
            || !SameSet(
                support,
                bindings.Select(binding => new BindingRow(
                    binding.SupportUuid,
                    binding.AliasUuid,
                    binding.AssignmentUuid
                ))
            )
        )
        {
            throw new NormalizationConflictException(
                "Active source authority differs from the complete configured accepted map."
            );
        }
        return new(membership, configured, committed, map);
    }

    internal static SourceActorAliasScopeKey ScopeKey(
        Guid source,
        SourceActorResolutionInput input,
        SourceActorResolutionScope scope,
        string pointer
    ) =>
        new(
            source,
            input.OriginalInput.Contributor,
            scope.OriginalScope.Resource,
            pointer,
            scope.OriginalScope.ProducerVersion,
            OriginKey(scope.OriginalScope.RecordingOrigin),
            scope.OriginalScope.FileOrigin == null
                ? null
                : OriginKey(scope.OriginalScope.FileOrigin)
        );

    private static SourceActorAliasOriginKey OriginKey(ProviderObservationOrigin origin) =>
        new(origin.Item, origin.Scope, origin.Evidence.JsonPointer);

    private static SourceActorResolutionClaim Claim(
        PreparedSource source,
        SourceActorAliasClaimReference reference
    ) =>
        source
            .Inputs[reference.InputIndex]
            .Scopes[reference.ScopeIndex]
            .Claims.Single(value => value.AssertionIndex == reference.AssertionIndex);

    internal static SourceActorAliasCoordinates Coordinates(
        PreparedSource source,
        SourceActorAliasClaimReference reference
    )
    {
        var input = source.Inputs[reference.InputIndex];
        var scope = input.Scopes[reference.ScopeIndex].OriginalScope;
        var assertion = scope.Extraction.Assertions[reference.AssertionIndex];
        return new(
            source.Extraction.SourceUuid,
            input.OriginalInput.Contributor,
            source
                .Extraction.Ticket.Inputs.Single(value =>
                    value.RevisionUuid == scope.InputRevisionUuid
                )
                .ResourceUuid,
            scope.InputRevisionUuid,
            assertion.NameEvidence.JsonPointer,
            scope.ProducerVersion,
            assertion.AssertionUuid,
            assertion.ActorReferenceUuid,
            assertion.FieldSha256
        );
    }

    internal static IReadOnlyList<SourceActorAliasPreparedName> Names(
        Guid stage,
        SourceActorAliasClaimReference reference,
        SourceActorAliasAssertion<NormalizedEvidenceReference> assertion
    )
    {
        var names = new List<(string Wording, NormalizedEvidenceReference Evidence)>
        {
            (assertion.Name, assertion.NameEvidence),
        };
        names.AddRange(
            assertion.Aliases.SelectMany(alias =>
                alias.Evidence.Select(evidence => (alias.Wording, evidence))
            )
        );
        return names
            .Distinct()
            .Select(
                (name, index) =>
                    new SourceActorAliasPreparedName(
                        StableUuid(
                            stage,
                            $"name/{reference.InputIndex}/{reference.ScopeIndex}/{reference.AssertionIndex}/{index}"
                        ),
                        name.Wording,
                        name.Evidence
                    )
            )
            .ToArray();
    }

    private async Task<NormalizationPreparedInputContribution[]> Contributions(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        PersonMap personMap,
        NormalizationReviewReadSet read,
        IReadOnlyList<SourceActorAliasScopeMapEntry> map,
        PreparedSource source,
        CancellationToken ct
    )
    {
        var state = personMap.Membership;
        var own = state.Contributions.SingleOrDefault(value =>
            value.Contributor == SourceActorAliasApplicationStore.StableContributor
        );
        var carried = await ReadMapSources(c, t, map, ct);
        var ownKeys = map.Select(value => value.Scope.Resource)
            .Concat(carried.SelectMany(value => value.Resources))
            .Concat(
                source
                    .Inputs.SelectMany(value => value.OriginalInput.Binding.Inputs)
                    .Select(value => value.Resource)
            )
            .Distinct()
            .ToArray();
        var allResources = (read.Inputs ?? []).Select(value => value.ResourceUuid).ToArray();
        var resourceKeys = (
            await c.QueryAsync<ResourceKeyRow>(
                new CommandDefinition(
                    """
                    SELECT uuid AS "Uuid",provider AS "Provider",resource_type AS "ResourceType",provider_key AS "ProviderKey"
                    FROM normalization.input_resources WHERE uuid=ANY(@allResources)
                    """,
                    new { allResources },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToDictionary(value => value.Key, value => value.Uuid);
        var observed = read.OwnerObservations ?? [];
        var dependencies = observed
            .Where(value => value.SubjectUuid != state.Owner!.SubjectUuid)
            .Where(value =>
                value.SubjectUuid == source.Extraction.Ticket.SubjectUuid
                || value.SubjectUuid == source.Reviews.OwnerObservation?.SubjectUuid
            )
            .Select(value => value.SubjectUuid)
            .ToArray();
        var result = new List<NormalizationPreparedInputContribution>
        {
            new(
                new(
                    SourceActorAliasApplicationStore.StableContributor,
                    own?.Revision ?? 0,
                    ownKeys,
                    dependencies,
                    observed.Select(value => value.SubjectUuid).ToArray()
                ),
                CurrentInputs(ownKeys),
                read.CatalogInputs,
                read.SourceIdentities,
                read.MappingRegistry,
                read.ImportedVenues,
                read.CandidateUniverses,
                observed,
                read.SourceUuids
            ),
        };
        var peers = state
            .Contributions.Where(value =>
                value.Contributor != SourceActorAliasApplicationStore.StableContributor
            )
            .Concat(state.UnmanagedBaseline == null ? [] : new[] { state.UnmanagedBaseline })
            .ToArray();
        if (peers.Length == 0)
        {
            return result.ToArray();
        }
        NormalizationWorkTicket? nativeTicket = null;
        IReadOnlyDictionary<NormalizationResourceKey, Guid>? nativeResources = null;
        foreach (var peer in peers)
        {
            var saved = personMap.Committed?.Contributions.SingleOrDefault(value =>
                value.Contribution.Contributor == peer.Contributor
            );
            if (saved != null)
            {
                RequirePeerBindings(peer, saved);
                result.Add(CurrentPeer(peer, peer.Revision));
                continue;
            }
            if (
                peer.Contributor.Provider != "relisten"
                || peer.Contributor.ResourceType != "preexisting_owner_bindings"
                || peer.Contributor.ProviderKey != state.Owner!.SubjectUuid.ToString("D")
            )
            {
                throw new NormalizationConflictException(
                    "An unrecognized person producer needs its typed complete original receipt; keys cannot authenticate it."
                );
            }
            // A preexisting baseline is the complete old canonical owner's published input
            // envelope, not a source interpretation reconstructed from the alias spelling.
            // Reviews may advance desired/editorial without changing this accepted receipt.
            if (nativeTicket == null)
            {
                if (state.Owner!.PublishedWorkUuid == null)
                {
                    throw new NormalizationConflictException(
                        "Peer configuration lacks a complete published original receipt."
                    );
                }
                var originalJson =
                    await c.QuerySingleOrDefaultAsync<string>(
                        new CommandDefinition(
                            "SELECT ticket::text FROM normalization.work_runs WHERE uuid=@uuid AND subject_uuid=@owner AND state='published'",
                            new
                            {
                                uuid = state.Owner.PublishedWorkUuid,
                                owner = state.Owner.SubjectUuid,
                            },
                            t,
                            cancellationToken: ct
                        )
                    )
                    ?? throw new NormalizationConflictException(
                        "Peer publication ticket is missing."
                    );
                nativeTicket = JsonSerializer.Deserialize<NormalizationWorkTicket>(originalJson)!;
                var allInputIds = nativeTicket.Inputs.Select(value => value.ResourceUuid).ToArray();
                nativeResources = (
                    await c.QueryAsync<ResourceKeyRow>(
                        new CommandDefinition(
                            "SELECT uuid AS \"Uuid\",provider AS \"Provider\",resource_type AS \"ResourceType\",provider_key AS \"ProviderKey\" FROM normalization.input_resources WHERE uuid=ANY(@allInputIds)",
                            new { allInputIds },
                            t,
                            cancellationToken: ct
                        )
                    )
                ).ToDictionary(value => value.Key, value => value.Uuid);
            }
            RequireBaselineBindings(peer, nativeTicket, nativeResources!);
            result.Add(
                CurrentPeer(
                    peer,
                    state.UnmanagedBaseline?.Contributor == peer.Contributor ? 1 : peer.Revision
                )
            );
        }
        // This is a new observation of the carried configuration, not a replacement for an
        // old assignment's acceptance proof. Those immutable receipts remain in PriorMap.
        NormalizationBindingInputReference[] CurrentInputs(
            IEnumerable<NormalizationResourceKey> keys
        ) =>
            keys.Select(key =>
                {
                    var input = (read.Inputs ?? []).Single(value =>
                        value.ResourceUuid == resourceKeys[key]
                    );
                    return new NormalizationBindingInputReference(
                        input.RevisionUuid,
                        key,
                        input.Generation
                    );
                })
                .ToArray();

        NormalizationPreparedInputContribution CurrentPeer(
            NormalizationInputContributionState peer,
            long revision
        ) =>
            new(
                new(
                    peer.Contributor,
                    revision,
                    peer.Resources,
                    peer.DependencySubjectUuids,
                    peer.OwnerObservationSubjectUuids
                ),
                CurrentInputs(peer.Resources),
                (read.CatalogInputs ?? [])
                    .Where(value => peer.CatalogArtistUuids.Contains(value.ArtistUuid))
                    .ToArray(),
                (read.SourceIdentities ?? [])
                    .Where(value => peer.SourceIdentifiers.Contains(value.UpstreamIdentifier))
                    .ToArray(),
                peer.UsesMappingRegistry ? read.MappingRegistry : null,
                (read.ImportedVenues ?? [])
                    .Where(value => (peer.ImportedVenueUuids ?? []).Contains(value.VenueUuid))
                    .ToArray(),
                (read.CandidateUniverses ?? [])
                    .Where(value => (peer.CandidateUniverses ?? []).Contains(value.Universe))
                    .ToArray(),
                observed
                    .Where(value =>
                        (peer.OwnerObservationSubjectUuids ?? []).Contains(value.SubjectUuid)
                    )
                    .ToArray(),
                (read.SourceUuids ?? [])
                    .Where(value => (peer.SourceUuids ?? []).Contains(value.SourceUuid))
                    .ToArray()
            );
        return result.ToArray();
    }

    private static void RequirePeerBindings(
        NormalizationInputContributionState peer,
        SourceActorAliasContributionAllocation saved
    )
    {
        if (!saved.Matches(peer))
        {
            throw new NormalizationConflictException(
                "A peer's current allocation differs from its typed original preparation."
            );
        }
    }

    private static void RequireBaselineBindings(
        NormalizationInputContributionState baseline,
        NormalizationWorkTicket ticket,
        IReadOnlyDictionary<NormalizationResourceKey, Guid> resources
    )
    {
        if (
            !SameSet(
                baseline.Resources.Select(value => resources[value]),
                ticket.Inputs.Select(value => value.ResourceUuid)
            )
            || !SameSet(
                baseline.DependencySubjectUuids,
                ticket.Dependencies.Select(value => value.SubjectUuid)
            )
            || !SameSet(
                baseline.OwnerObservationSubjectUuids ?? [],
                (ticket.OwnerObservations ?? []).Select(value => value.SubjectUuid)
            )
            || !SameSet(
                baseline.CatalogArtistUuids,
                (ticket.CatalogInputs ?? []).Select(value => value.ArtistUuid)
            )
            || !SameSet(
                baseline.SourceIdentifiers,
                (ticket.SourceIdentities ?? []).Select(value => value.UpstreamIdentifier)
            )
            || baseline.UsesMappingRegistry != (ticket.MappingRegistry != null)
            || !SameSet(
                baseline.ImportedVenueUuids ?? [],
                (ticket.ImportedVenues ?? []).Select(value => value.VenueUuid)
            )
            || !SameSet(
                baseline.SourceUuids ?? [],
                (ticket.SourceUuids ?? []).Select(value => value.SourceUuid)
            )
            || !SameSet(
                baseline.CandidateUniverses ?? [],
                (ticket.CandidateUniverses ?? []).Select(value => value.Universe)
            )
        )
        {
            throw new NormalizationConflictException(
                "The complete canonical baseline differs from its original published envelope."
            );
        }
    }

    private sealed record PersonMap(
        NormalizationInputContributionsSnapshot Membership,
        SourceActorAliasMapStage? Configured,
        SourceActorAliasMapStage? Committed,
        IReadOnlyList<SourceActorAliasScopeMapEntry> Map
    );

    private sealed record BindingRow(Guid SupportUuid, Guid AliasUuid, Guid AssignmentUuid);

    private sealed record ResourceKeyRow(
        Guid Uuid,
        string Provider,
        string ResourceType,
        string ProviderKey
    )
    {
        public NormalizationResourceKey Key => new(Provider, ResourceType, ProviderKey);
    }
}
