using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationCompositionResolutionProcessor
{
    private async Task<CompositionResolutionReadSet> ReadExternalReadSetAsync(
        CompositionResolutionAttempt attempt,
        CompositionResolutionContext context,
        CancellationToken ct
    ) =>
        MergeCompositionReadSets([
            await ReadCompositionDecisionReadSetAsync(attempt, context, ct),
            CompositionPeerCoordinationReadSet(context),
        ]);

    private static CompositionResolutionReadSet CompositionPeerCoordinationReadSet(
        CompositionResolutionContext context
    )
    {
        var ownKey = new NormalizationResourceKey(
            "relisten",
            "composition_resolution",
            context.Claim.RepertoireSongUuid.ToString("D")
        );
        var membership = context.OwnerMembership;
        var peers = (membership?.Contributions ?? [])
            .Where(state => state.Contributor != ownKey)
            .Concat(membership?.UnmanagedBaseline is { } baseline ? [baseline] : [])
            .ToArray();
        var read = context.OwnerBindings ?? EmptyCompositionReadSet();
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

    private async Task<CompositionResolutionReadSet> ReadCompositionDecisionReadSetAsync(
        CompositionResolutionAttempt attempt,
        CompositionResolutionContext context,
        CancellationToken ct
    )
    {
        var captured = attempt
            .Checkpoints.SelectMany(checkpoint =>
                (checkpoint.Search?.Pages ?? [])
                    .Select(page => page.Acquisition?.Result)
                    .Concat(
                        (checkpoint.Hydration?.Lookups ?? []).Select(lookup =>
                            lookup.Acquisition?.Result
                        )
                    )
            )
            .OfType<MusicBrainzAcquisitionResult>()
            .Where(result => result.Input != null)
            .DistinctBy(result => (result.Input, result.ResourceGeneration))
            .ToArray();
        if (
            captured.Length > 32
            || captured.Any(result => result.ResourceGeneration is null or <= 0)
        )
        {
            throw new NormalizationConflictException(
                "Captured composition inputs exceed the bounded plan or lack original generations."
            );
        }
        var refs = captured.Select(result => result.Input!).Distinct().ToArray();
        // Validate immutable references and bounded representations. A resource UUID lookup
        // below supplies identity only; its current head never supplies a replacement generation.
        await evidence.GetInputsAsync(
            refs,
            new(
                MaximumInputs: 32,
                MaximumInputBytes: 8 * 1024 * 1024,
                MaximumTotalBytes: 128 * 1024 * 1024
            ),
            ct
        );
        var identities = (
            await ReadCompositionInputIdentitiesAsync(
                refs.Select(input => input.InputRevisionUuid).ToArray(),
                ct
            )
        ).ToDictionary(row => row.RevisionUuid);
        var inputs = captured
            .Select(result =>
            {
                var input = result.Input!;
                var identity = identities[input.InputRevisionUuid];
                if (identity.Resource != input.Resource)
                {
                    throw new NormalizationConflictException(
                        "A captured composition input belongs to a different resource."
                    );
                }
                return new NormalizationInputDependency(
                    identity.ResourceUuid,
                    input.InputRevisionUuid,
                    result.ResourceGeneration!.Value
                );
            })
            .ToArray();
        var external = new CompositionResolutionReadSet(
            inputs,
            [],
            [],
            [],
            null,
            [],
            [],
            attempt.Preparation!.Policy is { } policy ? [policy.OwnerObservation] : [],
            []
        );
        return MergeCompositionReadSets([context.ReadSet, external]);
    }

    private async Task<NormalizationWorkTicket> AdmitStageAsync(
        CompositionResolutionAttempt attempt,
        CompositionResolutionStage stage,
        CancellationToken ct
    )
    {
        var context = stage.DerivedContext ?? attempt.Preparation!.Context;
        var creation = stage.Kind == CompositionResolutionStageKind.CreateComposition;
        var subject = creation
            ? new NormalizationSubjectKey(
                "compositions",
                "composition",
                stage.Publication.Compositions.Single().Uuid
            )
            : new NormalizationSubjectKey("songs", "artist_repertoire", context.ArtistUuid);
        // Creation is reserved for a fresh stable target UUID. Keeping these expectations
        // fixed also recovers admission-before-ticket-save without rebuilding a different request.
        var membership = creation
            ? new NormalizationInputContributionsSnapshot(null, [])
            : context.OwnerMembership
                ?? throw new NormalizationConflictException(
                    "The original repertoire contribution membership is missing."
                );
        var contributor = new NormalizationResourceKey(
            "relisten",
            creation ? "composition_creation" : "composition_resolution",
            (creation ? attempt.AttemptUuid : attempt.Claim.RepertoireSongUuid).ToString("D")
        );
        var references = await ReadStageInputReferencesAsync(stage.ReadSet.Inputs, ct);
        var prepared = membership
            .Contributions.Where(state => state.Contributor != contributor)
            .Select(state => PrepareCompositionPeer(state, stage.ReadSet, references))
            .ToList();
        if (membership.UnmanagedBaseline is { } originalBaseline)
        {
            // Shared admission first adopts the virtual baseline at revision one, then
            // applies this same-membership replacement. The complete original membership
            // CAS below still expects its pre-adoption revision zero.
            prepared.Add(
                PrepareCompositionPeer(
                    originalBaseline with
                    {
                        Revision = 1,
                    },
                    stage.ReadSet,
                    references
                )
            );
        }

        // Coordination keeps peers intact for this admission, but does not transfer their
        // keys to our contributor. Otherwise removing a peer could never remove its inputs.
        var decision = await ReadCompositionDecisionReadSetAsync(attempt, context, ct);
        if (!creation)
        {
            var targetUuid = attempt.Proposal!.CompositionUuid!.Value;
            var targetOwner = context
                .Candidates.Facts.Single(fact => fact.Header.Uuid == targetUuid)
                .Header.OwnerSubjectUuid;
            var targetDependency = stage.ReadSet.Dependencies.Single(dependency =>
                dependency.SubjectUuid == targetOwner
            );
            decision = decision with
            {
                Dependencies = decision.Dependencies.Append(targetDependency).Distinct().ToArray(),
            };
        }
        var decisionRevisions = decision.Inputs.Select(input => input.RevisionUuid).ToHashSet();
        var decisionReferences = references
            .Where(input => decisionRevisions.Contains(input.InputRevisionUuid))
            .ToArray();
        var ownRevision =
            membership
                .Contributions.SingleOrDefault(state => state.Contributor == contributor)
                ?.Revision
            ?? 0;
        prepared.Add(
            PrepareCompositionOwner(contributor, ownRevision, decision, decisionReferences)
        );
        var expectedMembership = membership
            .Contributions.Select(state => new NormalizationInputContributionRevision(
                state.Contributor,
                state.Revision
            ))
            .Concat(
                membership.UnmanagedBaseline is { } baseline
                    ? [new NormalizationInputContributionRevision(baseline.Contributor, 0)]
                    : []
            )
            .ToArray();
        var ticket = await work.BeginContributedBatchAsync(
            stage.WorkUuid,
            subject,
            attempt.ProcessingVersion,
            prepared,
            creation ? 0 : membership.Owner?.DesiredGeneration,
            ct,
            expectedMembership
        );
        NormalizationCompositionResolutionStore.RequireTicket(
            stage,
            ticket,
            attempt.ProcessingVersion
        );
        return ticket;
    }

    private static NormalizationPreparedInputContribution PrepareCompositionOwner(
        NormalizationResourceKey contributor,
        long revision,
        CompositionResolutionReadSet read,
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

    private static NormalizationPreparedInputContribution PrepareCompositionPeer(
        NormalizationInputContributionState state,
        CompositionResolutionReadSet read,
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
            await ReadCompositionInputIdentitiesAsync(
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

    private Task<CompositionInputIdentity[]> ReadCompositionInputIdentitiesAsync(
        Guid[] revisions,
        CancellationToken ct
    ) =>
        db.WithWriteConnection(async connection =>
        {
            var rows = (
                await connection.QueryAsync<CompositionInputIdentity>(
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
                    "A saved composition input revision is missing."
                );
            }
            return rows;
        });

    private static CompositionResolutionReadSet MergeCompositionReadSets(
        IReadOnlyList<CompositionResolutionReadSet> reads
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
                "Composition snapshots observed different mapping registry generations."
            );
        }
        var dependencies = CoherentCompositionClocks(
            reads.SelectMany(read => read.Dependencies),
            value => value.SubjectUuid
        );
        var observations = CoherentCompositionClocks(
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
                "Composition causal and observed owner clocks disagree."
            );
        }
        return new(
            CoherentCompositionClocks(
                reads.SelectMany(read => read.Inputs),
                value => value.ResourceUuid
            ),
            dependencies,
            CoherentCompositionClocks(
                reads.SelectMany(read => read.CatalogInputs),
                value => value.ArtistUuid
            ),
            CoherentCompositionClocks(
                reads.SelectMany(read => read.SourceIdentities),
                value => value.UpstreamIdentifier
            ),
            registries.SingleOrDefault(),
            CoherentCompositionClocks(
                reads.SelectMany(read => read.ImportedVenues),
                value => value.VenueUuid
            ),
            CoherentCompositionClocks(
                reads.SelectMany(read => read.CandidateUniverses),
                value => value.Universe
            ),
            observations,
            CoherentCompositionClocks(
                reads.SelectMany(read => read.SourceUuids),
                value => value.SourceUuid
            )
        );
    }

    private static T[] CoherentCompositionClocks<T, TKey>(IEnumerable<T> values, Func<T, TKey> key)
    {
        var result = values.Distinct().ToArray();
        if (result.GroupBy(key).Any(group => group.Count() > 1))
        {
            throw new NormalizationConflictException(
                "Composition snapshots observed conflicting original input or clock generations."
            );
        }
        return result;
    }

    private static CompositionResolutionReadSet EmptyCompositionReadSet() =>
        new([], [], [], [], null, [], [], [], []);

    private sealed record CompositionInputIdentity(
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
