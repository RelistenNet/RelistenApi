using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationVenueResolutionStore
{
    private static void RequireObservationRetirementShape(
        VenueResolutionAttempt attempt,
        VenueResolutionStage stage
    )
    {
        var preparation = attempt.Preparation!;
        var subscription = preparation.Context.ObservationSubscription;
        if (
            attempt.Request.Kind != VenueResolutionRequestKind.ShowObservation
            || preparation.Source.Claim is not { IsShow: true, CatalogUuid: { } showUuid }
            || showUuid == Guid.Empty
            || subscription == null
            || stage.DerivedContext != null
            || !Same(stage.Publication, new VenuePublication([], [], [], [], [], []))
            || !Same(stage.DecisionReadSet, preparation.ReadSet)
            || !Same(stage.OwnerMembership, subscription.Membership)
        )
        {
            throw new NormalizationConflictException(
                "Observation retirement requires its bound show handoff and exact original subscription."
            );
        }
        RequireBoundHandoffStage(attempt);
        var expected = ObservationPeerReadSet(
            subscription,
            VenueResolutionOwner.ForObservation(preparation.Source).Uuid
        );
        var revisions = expected.Inputs.Select(value => value.RevisionUuid).ToHashSet();
        var references = subscription.InputReferences.Where(value =>
            revisions.Contains(value.InputRevisionUuid)
        );
        if (!Same(stage.ReadSet, expected) || !SetEquals(stage.OwnerInputReferences, references))
        {
            throw new NormalizationConflictException(
                "Observation retirement retains only peer coordination and removes only the resolver's own keys."
            );
        }
    }

    private static VenueResolutionStage RequireBoundHandoffStage(VenueResolutionAttempt attempt)
    {
        var bound = attempt.Stages.SingleOrDefault(value =>
            value.Kind
                is VenueResolutionStageKind.Assignment
                    or VenueResolutionStageKind.AcknowledgeDecision
        );
        if (
            bound?.Ticket == null
            || bound.Receipt?.Status != NormalizationPublicationStatus.Published
            || bound.Receipt.WorkUuid != bound.WorkUuid
        )
        {
            throw new NormalizationConflictException(
                "The bound show stage must publish before its previous observation subscription is retired."
            );
        }
        return bound;
    }

    /// <summary>The new owner already enrolled the source; retirement authenticates that immutable transfer, not fresh source state.</summary>
    internal static async Task RequireObservationHandoffAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        VenueResolutionAttempt attempt,
        CancellationToken ct
    )
    {
        var bound = RequireBoundHandoffStage(attempt);
        var run = await c.QuerySingleOrDefaultAsync<WorkRow>(
            new CommandDefinition(
                "SELECT s.domain AS \"Domain\",s.subject_kind AS \"Kind\",s.subject_uuid AS \"EntityUuid\",r.state AS \"State\",r.ticket::text AS \"Ticket\" FROM normalization.work_runs r JOIN normalization.work_subjects s ON s.uuid=r.subject_uuid WHERE r.uuid=@workUuid",
                new { workUuid = bound.WorkUuid },
                t,
                cancellationToken: ct
            )
        );
        var owner = VenueResolutionOwner.For(attempt.Request, attempt.Preparation!.Source);
        if (
            owner.Kind != "show"
            || run == null
            || run.Domain != owner.Domain
            || run.Kind != owner.Kind
            || run.EntityUuid != owner.Uuid
            || run.State != "published"
            || !Same(JsonSerializer.Deserialize<NormalizationWorkTicket>(run.Ticket), bound.Ticket)
        )
        {
            throw new NormalizationConflictException(
                "Observation retirement requires the exact completed Work publication under its new show owner."
            );
        }
    }

    private static VenueResolutionReadSet ObservationPeerReadSet(
        VenueObservationSubscription subscription,
        Guid ownerUuid
    )
    {
        var ownKey = new NormalizationResourceKey(
            "relisten",
            "venue_resolution",
            ownerUuid.ToString("D")
        );
        var membership = subscription.Membership;
        var peers = membership
            .Contributions.Where(value => value.Contributor != ownKey)
            .Concat(membership.UnmanagedBaseline is { } baseline ? [baseline] : [])
            .ToArray();
        var read = subscription.Bindings;
        var resources = peers.SelectMany(value => value.Resources).ToHashSet();
        var revisions = subscription
            .InputReferences.Where(value => resources.Contains(value.Resource))
            .Select(value => value.InputRevisionUuid)
            .ToHashSet();
        var dependencies = peers.SelectMany(value => value.DependencySubjectUuids).ToHashSet();
        var artists = peers.SelectMany(value => value.CatalogArtistUuids).ToHashSet();
        var identities = peers
            .SelectMany(value => value.SourceIdentifiers)
            .ToHashSet(StringComparer.Ordinal);
        var venues = peers.SelectMany(value => value.ImportedVenueUuids ?? []).ToHashSet();
        var universes = peers.SelectMany(value => value.CandidateUniverses ?? []).ToHashSet();
        var owners = peers
            .SelectMany(value => value.OwnerObservationSubjectUuids ?? [])
            .ToHashSet();
        var sources = peers.SelectMany(value => value.SourceUuids ?? []).ToHashSet();
        return new(
            read.Inputs.Where(value => revisions.Contains(value.RevisionUuid)).ToArray(),
            read.Dependencies.Where(value => dependencies.Contains(value.SubjectUuid)).ToArray(),
            read.CatalogInputs.Where(value => artists.Contains(value.ArtistUuid)).ToArray(),
            read.SourceIdentities.Where(value => identities.Contains(value.UpstreamIdentifier))
                .ToArray(),
            peers.Any(value => value.UsesMappingRegistry) ? read.MappingRegistry : null,
            read.ImportedVenues.Where(value => venues.Contains(value.VenueUuid)).ToArray(),
            read.CandidateUniverses.Where(value => universes.Contains(value.Universe)).ToArray(),
            read.OwnerObservations.Where(value => owners.Contains(value.SubjectUuid)).ToArray(),
            read.SourceUuids.Where(value => sources.Contains(value.SourceUuid)).ToArray()
        );
    }
}
