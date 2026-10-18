using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationPeopleResolutionApplier
{
    /// <summary>
    /// A recording acknowledgment consumes the complete Work owner union. Retain peer bindings
    /// from their last published receipt; never turn current input heads into proof that a peer
    /// interpreted them. Unpublished configurations need their owning producer to finish first.
    /// </summary>
    private async Task<PeopleAcceptanceStage> PrepareDecisionPeersAsync(
        PeopleAcceptanceStage stage,
        NormalizationInputContributionsSnapshot membership,
        CancellationToken ct
    )
    {
        var own = stage.Contributions.Select(value => value.Contribution.Contributor).ToHashSet();
        var peers = membership
            .Contributions.Where(value => !own.Contains(value.Contributor))
            .ToArray();
        if (peers.Length == 0)
        {
            return stage;
        }
        var owner = membership.Owner!;
        if (owner.PublishedWorkUuid == null || owner.PublishedGeneration != owner.DesiredGeneration)
        {
            throw new NormalizationConflictException(
                "Peer owner configuration has not been published."
            );
        }
        var published =
            await store.ReadPublishedTicketAsync(
                owner.PublishedWorkUuid.Value,
                owner.SubjectUuid,
                ct
            ) ?? throw new NormalizationConflictException("Peer publication receipt is missing.");
        var resources = await ReadPeerResourcesAsync(published, ct);
        RequirePublishedMembership(membership, published, resources);
        var prepared = peers
            .Select(peer => PrepareDecisionPeer(peer, published, resources))
            .ToArray();
        return stage with
        {
            Contributions = stage.Contributions.Concat(prepared).ToArray(),
            PeerTicket = published,
        };
    }

    private Task<Dictionary<Guid, NormalizationResourceKey>> ReadPeerResourcesAsync(
        NormalizationWorkTicket ticket,
        CancellationToken ct
    ) =>
        db.WithWriteConnection(async c =>
        {
            var rows = (
                await c.QueryAsync<PeerResource>(
                    new CommandDefinition(
                        """
                        SELECT uuid AS "ResourceUuid",provider AS "Provider",resource_type AS "ResourceType",
                            provider_key AS "ProviderKey" FROM normalization.input_resources WHERE uuid=ANY(@uuids)
                        """,
                        new
                        {
                            uuids = ticket
                                .Inputs.Select(value => value.ResourceUuid)
                                .Distinct()
                                .ToArray(),
                        },
                        cancellationToken: ct
                    )
                )
            ).ToArray();
            if (rows.Length != ticket.Inputs.Select(value => value.ResourceUuid).Distinct().Count())
            {
                throw new NormalizationConflictException(
                    "A published peer input resource is missing."
                );
            }
            return rows.ToDictionary(
                value => value.ResourceUuid,
                value => new NormalizationResourceKey(
                    value.Provider,
                    value.ResourceType,
                    value.ProviderKey
                )
            );
        });

    private static void RequirePublishedMembership(
        NormalizationInputContributionsSnapshot membership,
        NormalizationWorkTicket ticket,
        IReadOnlyDictionary<Guid, NormalizationResourceKey> resources
    )
    {
        var contributors = membership.Contributions;
        if (
            ticket.SubjectUuid != membership.Owner!.SubjectUuid
            || ticket.DesiredGeneration != membership.Owner.DesiredGeneration
            || ticket.PolicyVersion != membership.Owner.PolicyVersion
            || !SameSet(contributors.SelectMany(value => value.Resources), resources.Values)
            || !SameSet(
                contributors.SelectMany(value => value.DependencySubjectUuids),
                ticket.Dependencies.Select(value => value.SubjectUuid)
            )
            || !SameSet(
                contributors.SelectMany(value => value.CatalogArtistUuids),
                (ticket.CatalogInputs ?? []).Select(value => value.ArtistUuid)
            )
            || !SameSet(
                contributors.SelectMany(value => value.SourceIdentifiers),
                (ticket.SourceIdentities ?? []).Select(value => value.UpstreamIdentifier)
            )
            || !SameSet(
                contributors.SelectMany(value => value.ImportedVenueUuids ?? []),
                (ticket.ImportedVenues ?? []).Select(value => value.VenueUuid)
            )
            || !SameSet(
                contributors.SelectMany(value => value.CandidateUniverses ?? []),
                (ticket.CandidateUniverses ?? []).Select(value => value.Universe)
            )
            || !SameSet(
                contributors.SelectMany(value => value.OwnerObservationSubjectUuids ?? []),
                (ticket.OwnerObservations ?? []).Select(value => value.SubjectUuid)
            )
            || !SameSet(
                contributors.SelectMany(value => value.SourceUuids ?? []),
                (ticket.SourceUuids ?? []).Select(value => value.SourceUuid)
            )
            || contributors.Any(value => value.UsesMappingRegistry)
                != (ticket.MappingRegistry != null)
        )
        {
            throw new NormalizationConflictException(
                "The published peer receipt does not cover current owner membership."
            );
        }
    }

    private static NormalizationPreparedInputContribution PrepareDecisionPeer(
        NormalizationInputContributionState peer,
        NormalizationWorkTicket ticket,
        IReadOnlyDictionary<Guid, NormalizationResourceKey> resources
    ) =>
        new(
            new(
                peer.Contributor,
                peer.Revision,
                peer.Resources,
                peer.DependencySubjectUuids,
                peer.OwnerObservationSubjectUuids
            ),
            ticket
                .Inputs.Where(value => peer.Resources.Contains(resources[value.ResourceUuid]))
                .Select(value => new NormalizationBindingInputReference(
                    value.RevisionUuid,
                    resources[value.ResourceUuid]
                ))
                .ToArray(),
            (ticket.CatalogInputs ?? [])
                .Where(value => peer.CatalogArtistUuids.Contains(value.ArtistUuid))
                .ToArray(),
            (ticket.SourceIdentities ?? [])
                .Where(value => peer.SourceIdentifiers.Contains(value.UpstreamIdentifier))
                .ToArray(),
            peer.UsesMappingRegistry ? ticket.MappingRegistry : null,
            (ticket.ImportedVenues ?? [])
                .Where(value => (peer.ImportedVenueUuids ?? []).Contains(value.VenueUuid))
                .ToArray(),
            (ticket.CandidateUniverses ?? [])
                .Where(value => (peer.CandidateUniverses ?? []).Contains(value.Universe))
                .ToArray(),
            (ticket.OwnerObservations ?? [])
                .Where(value =>
                    (peer.OwnerObservationSubjectUuids ?? []).Contains(value.SubjectUuid)
                )
                .ToArray(),
            (ticket.SourceUuids ?? [])
                .Where(value => (peer.SourceUuids ?? []).Contains(value.SourceUuid))
                .ToArray()
        );

    private static bool SameSet<T>(IEnumerable<T> left, IEnumerable<T> right) =>
        left.ToHashSet().SetEquals(right);

    private sealed record PeerResource(
        Guid ResourceUuid,
        string Provider,
        string ResourceType,
        string ProviderKey
    );
}
