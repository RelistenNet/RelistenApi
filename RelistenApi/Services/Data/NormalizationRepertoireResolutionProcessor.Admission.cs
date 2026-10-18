using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationRepertoireResolutionProcessor
{
    private async Task<NormalizationWorkTicket> AdmitStageAsync(
        RepertoireResolutionAttempt attempt,
        RepertoireResolutionStage stage,
        CancellationToken ct
    )
    {
        var acknowledgment = stage.Kind == RepertoireResolutionStageKind.AcknowledgeSource;
        var owner = acknowledgment ? stage.Context.ObservationOwner : stage.Context.ArtistOwner!;
        var contributor = acknowledgment
            ? ObservationContributionKey(attempt.Registration.RegistrationUuid)
            : ArtistContributionKey(stage.Owner.Uuid);
        var references = await ReadInputReferencesAsync(stage.ReadSet, ct);
        var prepared = owner
            .Membership.Contributions.Where(x => x.Contributor != contributor)
            .Select(state =>
                PrepareContribution(
                    state.Contributor,
                    state.Revision,
                    RepertoireResolutionReadSets.ForContribution(stage.ReadSet, state, references),
                    references
                )
            )
            .ToList();
        if (owner.Membership.UnmanagedBaseline is { } baseline)
        {
            prepared.Add(
                PrepareContribution(
                    baseline.Contributor,
                    1,
                    RepertoireResolutionReadSets.ForContribution(
                        stage.ReadSet,
                        baseline,
                        references
                    ),
                    references
                )
            );
        }
        var own =
            stage.Kind == RepertoireResolutionStageKind.RetireArtistContribution
                ? RepertoireResolutionReadSets.Empty
                : stage.Context.ReadSet;
        prepared.Add(
            PrepareContribution(
                contributor,
                owner
                    .Membership.Contributions.SingleOrDefault(x => x.Contributor == contributor)
                    ?.Revision
                    ?? 0,
                own,
                references
            )
        );
        var expectedMembership = owner
            .Membership.Contributions.Select(x => new NormalizationInputContributionRevision(
                x.Contributor,
                x.Revision
            ))
            .Concat(
                owner.Membership.UnmanagedBaseline is { } old
                    ? [new NormalizationInputContributionRevision(old.Contributor, 0)]
                    : []
            )
            .ToArray();
        var ticket = await work.BeginContributedBatchAsync(
            stage.WorkUuid,
            stage.Owner,
            attempt.ProcessingVersion,
            prepared,
            owner.Membership.Owner?.DesiredGeneration ?? 0,
            ct,
            expectedMembership
        );
        return ticket;
    }

    private static NormalizationPreparedInputContribution PrepareContribution(
        NormalizationResourceKey contributor,
        long revision,
        RepertoireResolutionReadSet read,
        IReadOnlyList<NormalizationBindingInputReference> references
    )
    {
        var revisions = read.Inputs.Select(x => x.RevisionUuid).ToHashSet();
        var inputs = references.Where(x => revisions.Contains(x.InputRevisionUuid)).ToArray();
        if (inputs.Length != revisions.Count)
        {
            throw new NormalizationConflictException(
                "A prepared contribution lacks its exact immutable input references."
            );
        }
        return new(
            new(
                contributor,
                revision,
                inputs.Select(x => x.Resource).ToArray(),
                read.Dependencies.Select(x => x.SubjectUuid).ToArray(),
                read.OwnerObservations.Select(x => x.SubjectUuid).ToArray()
            ),
            inputs,
            read.CatalogInputs,
            read.SourceIdentities,
            read.MappingRegistry,
            read.ImportedVenues,
            read.CandidateUniverses,
            read.OwnerObservations,
            read.SourceUuids
        );
    }

    private static RepertoireResolutionReadSet PeerReadSet(
        RepertoireResolutionOwnerContext owner,
        NormalizationResourceKey ownKey
    )
    {
        var peers = owner
            .Membership.Contributions.Where(x => x.Contributor != ownKey)
            .Concat(owner.Membership.UnmanagedBaseline is { } baseline ? [baseline] : [])
            .ToArray();
        return RepertoireResolutionReadSets.Merge(
            peers
                .Select(state =>
                    RepertoireResolutionReadSets.ForContribution(
                        owner.Bindings,
                        state,
                        owner.InputReferences
                    )
                )
                .ToArray()
        );
    }

    private Task<NormalizationBindingInputReference[]> ReadInputReferencesAsync(
        RepertoireResolutionReadSet read,
        CancellationToken ct
    ) =>
        db.WithWriteConnection(async c =>
        {
            // Resource keys and revision ownership are immutable. This lookup never substitutes a
            // current resource generation for the original stamp saved in the stage.
            var rows = (
                await c.QueryAsync<InputReferenceRow>(
                    new CommandDefinition(
                        """
                        SELECT i.uuid AS "RevisionUuid",r.provider AS "Provider",r.resource_type AS "ResourceType",r.provider_key AS "ProviderKey"
                        FROM normalization.input_revisions i JOIN normalization.input_resources r ON r.uuid=i.resource_uuid
                        WHERE i.uuid=ANY(@revisions)
                        """,
                        new { revisions = read.Inputs.Select(x => x.RevisionUuid).ToArray() },
                        cancellationToken: ct
                    )
                )
            ).ToArray();
            if (rows.Length != read.Inputs.Count)
            {
                throw new NormalizationConflictException(
                    "An original repertoire input revision is unavailable."
                );
            }
            return rows.Select(x => new NormalizationBindingInputReference(
                    x.RevisionUuid,
                    new(x.Provider, x.ResourceType, x.ProviderKey)
                ))
                .ToArray();
        });

    private sealed record InputReferenceRow(
        Guid RevisionUuid,
        string Provider,
        string ResourceType,
        string ProviderKey
    );
}
