using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationCandidateService
{
    /// <summary>
    /// After bound publication, this resolver can retire its prior unbound subscription. Capture
    /// that owner's CAS and complete bindings without observing it as a matching dependency:
    /// retirement would otherwise requeue the successfully bound owner. Peer and baseline
    /// subscriptions are not owned by this resolver and cannot authorize a retirement stage.
    /// </summary>
    private static async Task<VenueObservationSubscription?> ReadVenueObservationSubscription(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        VenueResolutionSource source,
        CandidateFactsContext context,
        CancellationToken ct
    )
    {
        if (
            source.Claim is not { IsShow: true, CatalogUuid: not null }
            || source.OriginalTicket == null
        )
        {
            return null;
        }
        var key = VenueResolutionOwner.ForObservation(source);
        var owner = await ReadVenueResolverOwner(c, t, key, ct);
        if (owner == null)
        {
            return null;
        }

        var membership = await NormalizationWorkService.GetInputContributionsInSnapshotAsync(
            c,
            t,
            key,
            new(),
            ct
        );
        var contributor = new NormalizationResourceKey(
            "relisten",
            "venue_resolution",
            key.Uuid.ToString("D")
        );
        var own = membership.Contributions.SingleOrDefault(state =>
            state.Contributor == contributor
        );
        if (
            own == null
            || own.Resources.Count == 0 && owner.PublishedGeneration == owner.DesiredGeneration
        )
        {
            return null;
        }
        // Clearing membership and acknowledging that configuration are separate durable steps.
        // A new attempt may finish an interrupted clear; an acknowledged empty contributor needs
        // no further retirement, while peer-only owners remain outside this resolver's authority.

        var subscriptionFacts = new CandidateFactsContext();
        var bindings = await ReadVenueOwnerBindings(c, t, owner.SubjectUuid, subscriptionFacts, ct);
        foreach (var issue in subscriptionFacts.Issues)
        {
            if (issue.PreventsAutomaticAcceptance)
            {
                context.AddIssue(issue.Kind, issue.CandidateUuid, issue.FactKind);
            }
        }
        return new(membership, bindings.ReadSet, bindings.InputReferences);
    }
}
