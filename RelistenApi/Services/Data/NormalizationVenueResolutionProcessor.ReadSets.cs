using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationVenueResolutionProcessor
{
    private static VenueResolutionReadSet VenuePeerCoordinationReadSet(
        VenueResolutionContext context,
        Guid ownerUuid
    )
    {
        var ownKey = new NormalizationResourceKey(
            "relisten",
            "venue_resolution",
            ownerUuid.ToString("D")
        );
        var membership = context.OwnerMembership;
        var peers = (membership?.Contributions ?? [])
            .Where(state => state.Contributor != ownKey)
            .Concat(membership?.UnmanagedBaseline is { } baseline ? [baseline] : [])
            .ToArray();
        var read = context.OwnerBindings ?? EmptyVenueReadSet();
        var resources = peers.SelectMany(state => state.Resources).ToHashSet();
        var revisions = (context.OwnerInputReferences ?? [])
            .Where(input => resources.Contains(input.Resource))
            .Select(input => input.InputRevisionUuid)
            .ToHashSet();
        var dependencies = peers.SelectMany(state => state.DependencySubjectUuids).ToHashSet();
        var artists = peers.SelectMany(state => state.CatalogArtistUuids).ToHashSet();
        var identities = peers
            .SelectMany(state => state.SourceIdentifiers)
            .ToHashSet(StringComparer.Ordinal);
        var venues = peers.SelectMany(state => state.ImportedVenueUuids ?? []).ToHashSet();
        var universes = peers.SelectMany(state => state.CandidateUniverses ?? []).ToHashSet();
        var owners = peers
            .SelectMany(state => state.OwnerObservationSubjectUuids ?? [])
            .ToHashSet();
        var sources = peers.SelectMany(state => state.SourceUuids ?? []).ToHashSet();
        // Previous keys owned only by this resolver are intentionally retired. Only retained
        // peers contribute coordination keys to the complete resulting stage union.
        return new(
            read.Inputs.Where(input => revisions.Contains(input.RevisionUuid)).ToArray(),
            read.Dependencies.Where(input => dependencies.Contains(input.SubjectUuid)).ToArray(),
            read.CatalogInputs.Where(input => artists.Contains(input.ArtistUuid)).ToArray(),
            read.SourceIdentities.Where(input => identities.Contains(input.UpstreamIdentifier))
                .ToArray(),
            peers.Any(state => state.UsesMappingRegistry) ? read.MappingRegistry : null,
            read.ImportedVenues.Where(input => venues.Contains(input.VenueUuid)).ToArray(),
            read.CandidateUniverses.Where(input => universes.Contains(input.Universe)).ToArray(),
            read.OwnerObservations.Where(input => owners.Contains(input.SubjectUuid)).ToArray(),
            read.SourceUuids.Where(input => sources.Contains(input.SourceUuid)).ToArray()
        );
    }

    private static NormalizationPreparedInputContribution PrepareVenueOwner(
        NormalizationResourceKey contributor,
        long revision,
        VenueResolutionReadSet read,
        IReadOnlyList<NormalizationBindingInputReference> references
    ) =>
        new(
            new(
                contributor,
                revision,
                references.Select(input => input.Resource).ToArray(),
                read.Dependencies.Select(input => input.SubjectUuid).ToArray(),
                read.OwnerObservations.Select(input => input.SubjectUuid).ToArray()
            ),
            references,
            read.CatalogInputs,
            read.SourceIdentities,
            read.MappingRegistry,
            read.ImportedVenues,
            read.CandidateUniverses,
            read.OwnerObservations,
            read.SourceUuids
        );

    private static NormalizationPreparedInputContribution PrepareVenuePeer(
        NormalizationInputContributionState state,
        VenueResolutionReadSet read,
        IReadOnlyList<NormalizationBindingInputReference> references
    )
    {
        // Peer membership is retained exactly. Current coordination stamps preserve it; they
        // do not claim that this resolver independently interpreted any peer source document.
        var inputs = references.Where(input => state.Resources.Contains(input.Resource)).ToArray();
        var catalog = read
            .CatalogInputs.Where(input => state.CatalogArtistUuids.Contains(input.ArtistUuid))
            .ToArray();
        var identities = read
            .SourceIdentities.Where(input =>
                state.SourceIdentifiers.Contains(input.UpstreamIdentifier)
            )
            .ToArray();
        var venues = read
            .ImportedVenues.Where(input =>
                (state.ImportedVenueUuids ?? []).Contains(input.VenueUuid)
            )
            .ToArray();
        var universes = read
            .CandidateUniverses.Where(input =>
                (state.CandidateUniverses ?? []).Contains(input.Universe)
            )
            .ToArray();
        var owners = read
            .OwnerObservations.Where(input =>
                (state.OwnerObservationSubjectUuids ?? []).Contains(input.SubjectUuid)
            )
            .ToArray();
        var sources = read
            .SourceUuids.Where(input => (state.SourceUuids ?? []).Contains(input.SourceUuid))
            .ToArray();
        if (
            inputs.Length != state.Resources.Count
            || catalog.Length != state.CatalogArtistUuids.Count
            || identities.Length != state.SourceIdentifiers.Count
            || venues.Length != (state.ImportedVenueUuids?.Count ?? 0)
            || universes.Length != (state.CandidateUniverses?.Count ?? 0)
            || owners.Length != (state.OwnerObservationSubjectUuids?.Count ?? 0)
            || sources.Length != (state.SourceUuids?.Count ?? 0)
            || (state.UsesMappingRegistry && read.MappingRegistry == null)
            || !state.DependencySubjectUuids.All(uuid =>
                read.Dependencies.Any(input => input.SubjectUuid == uuid)
            )
        )
        {
            throw new NormalizationConflictException(
                "The stage lacks a complete original peer coordination read set."
            );
        }
        return new(
            new(
                state.Contributor,
                state.Revision,
                state.Resources,
                state.DependencySubjectUuids,
                state.OwnerObservationSubjectUuids
            ),
            inputs,
            catalog,
            identities,
            state.UsesMappingRegistry ? read.MappingRegistry : null,
            venues,
            universes,
            owners,
            sources
        );
    }

    private async Task<NormalizationBindingInputReference[]> ReadStageInputReferencesAsync(
        IReadOnlyList<NormalizationInputDependency> inputs,
        CancellationToken ct
    )
    {
        var identities = (
            await ReadVenueInputIdentitiesAsync(
                inputs.Select(input => input.RevisionUuid).Distinct().ToArray(),
                ct
            )
        ).ToDictionary(row => row.RevisionUuid);
        return inputs
            .Select(input =>
            {
                var identity = identities[input.RevisionUuid];
                if (identity.ResourceUuid != input.ResourceUuid)
                {
                    throw new NormalizationConflictException(
                        "The stage input revision belongs to a different resource UUID."
                    );
                }
                return new NormalizationBindingInputReference(
                    input.RevisionUuid,
                    identity.Resource
                );
            })
            .Distinct()
            .ToArray();
    }

    private Task<VenueInputIdentity[]> ReadVenueInputIdentitiesAsync(
        Guid[] revisions,
        CancellationToken ct
    ) =>
        db.WithWriteConnection(async connection =>
        {
            var rows = (
                await connection.QueryAsync<VenueInputIdentity>(
                    new CommandDefinition(
                        """
                        SELECT r.uuid AS "RevisionUuid",r.resource_uuid AS "ResourceUuid",p.provider AS "Provider",
                            p.resource_type AS "ResourceType",p.provider_key AS "ProviderKey"
                        FROM normalization.input_revisions r JOIN normalization.input_resources p ON p.uuid=r.resource_uuid
                        WHERE r.uuid=ANY(@revisions) ORDER BY r.uuid
                        """,
                        new { revisions },
                        cancellationToken: ct
                    )
                )
            ).ToArray();
            if (rows.Length != revisions.Distinct().Count())
            {
                throw new NormalizationConflictException(
                    "A saved venue input revision is missing."
                );
            }
            return rows;
        });

    private static VenueResolutionReadSet MergeVenueReadSets(
        IReadOnlyList<VenueResolutionReadSet> reads
    )
    {
        var registries = reads
            .Select(read => read.MappingRegistry)
            .OfType<NormalizationMappingRegistryDependency>()
            .Distinct()
            .ToArray();
        if (registries.Length > 1)
        {
            throw new NormalizationConflictException(
                "Venue snapshots observed different mapping registry generations."
            );
        }
        var dependencies = CoherentVenueClocks(
            reads.SelectMany(read => read.Dependencies),
            value => value.SubjectUuid
        );
        var observations = CoherentVenueClocks(
            reads.SelectMany(read => read.OwnerObservations),
            value => value.SubjectUuid
        );
        if (
            dependencies.Any(dependency =>
                observations.Any(observation =>
                    observation.SubjectUuid == dependency.SubjectUuid
                    && (
                        observation.DesiredGeneration != dependency.DesiredGeneration
                        || observation.EditorialRevision != dependency.EditorialRevision
                        || observation.PublishedWorkUuid != dependency.PublishedWorkUuid
                    )
                )
            )
        )
        {
            throw new NormalizationConflictException(
                "Venue causal and observed owner clocks disagree."
            );
        }
        return new(
            CoherentVenueClocks(reads.SelectMany(read => read.Inputs), value => value.ResourceUuid),
            dependencies,
            CoherentVenueClocks(
                reads.SelectMany(read => read.CatalogInputs),
                value => value.ArtistUuid
            ),
            CoherentVenueClocks(
                reads.SelectMany(read => read.SourceIdentities),
                value => value.UpstreamIdentifier
            ),
            registries.SingleOrDefault(),
            CoherentVenueClocks(
                reads.SelectMany(read => read.ImportedVenues),
                value => value.VenueUuid
            ),
            CoherentVenueClocks(
                reads.SelectMany(read => read.CandidateUniverses),
                value => value.Universe
            ),
            observations,
            CoherentVenueClocks(
                reads.SelectMany(read => read.SourceUuids),
                value => value.SourceUuid
            )
        );
    }

    private static T[] CoherentVenueClocks<T, TKey>(IEnumerable<T> values, Func<T, TKey> key)
    {
        var result = values.Distinct().ToArray();
        if (result.GroupBy(key).Any(group => group.Count() > 1))
        {
            throw new NormalizationConflictException(
                "Venue snapshots observed conflicting original input or clock generations."
            );
        }
        return result;
    }

    private static VenueResolutionReadSet EmptyVenueReadSet() =>
        new([], [], [], [], null, [], [], [], []);

    private sealed record VenueInputIdentity(
        Guid RevisionUuid,
        Guid ResourceUuid,
        string Provider,
        string ResourceType,
        string ProviderKey
    )
    {
        public NormalizationResourceKey Resource => new(Provider, ResourceType, ProviderKey);
    }
}
