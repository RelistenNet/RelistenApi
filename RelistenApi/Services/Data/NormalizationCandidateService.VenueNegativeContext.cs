using System;
using System.Data;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationCandidateService
{
    /// <summary>
    /// Enrolls an evidenced account with no performance association under its extraction-scoped
    /// observation owner. Source absence clocks remain original; no show identity is fabricated.
    /// </summary>
    public Task<VenueResolutionContext> FindUnboundVenueResolverContextAsync(
        VenueResolutionSource source,
        CancellationToken cancellationToken = default
    )
    {
        if (
            source.Claim is not { IsShow: true, CatalogUuid: null }
            || source.OriginalTicket == null
            || source.ProposalOwner == null
            || source.ShowReference == null
        )
        {
            throw new ArgumentException(
                "An unbound venue context requires an authenticated published account without a performance."
            );
        }
        return ReadVenueNegativeContext(source, "unbound_performance", cancellationToken);
    }

    /// <summary>
    /// Captures the real assignment owner's complete bindings when source names/identities cannot
    /// form a supported candidate query. The empty facts are explicitly unsuitable for acceptance.
    /// </summary>
    public Task<VenueResolutionContext> ReadUnsupportedVenueResolverContextAsync(
        VenueResolutionSource source,
        CancellationToken cancellationToken = default
    )
    {
        if (source.Claim?.CatalogUuid is null)
        {
            throw new ArgumentException(
                "Unsupported bound claims require their real catalog assignment owner."
            );
        }
        return ReadVenueNegativeContext(source, "unsupported_candidate_query", cancellationToken);
    }

    private async Task<VenueResolutionContext> ReadVenueNegativeContext(
        VenueResolutionSource source,
        string reason,
        CancellationToken ct
    )
    {
        var claim = source.Claim!;
        var request = claim.IsShow
            ? new VenueResolutionRequest(
                VenueResolutionRequestKind.ShowObservation,
                Show: source.ShowReference
            )
            : new(VenueResolutionRequestKind.ImportedVenue, ImportedVenueUuid: claim.CatalogUuid);
        var ownerKey = VenueResolutionOwner.For(request, source);
        await using var c = db.CreateConnection(false, false);
        await c.OpenAsync(ct);
        await using var t = await c.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await c.ExecuteAsync(
            new CommandDefinition(
                "SET TRANSACTION READ ONLY",
                transaction: t,
                cancellationToken: ct
            )
        );

        var context = new CandidateFactsContext();
        context.AddIssue(CandidateFactsIssueKind.InvalidStoredFact, factKind: reason);
        var owner = await ReadVenueResolverOwner(c, t, ownerKey, ct);
        if (owner != null)
        {
            context.AddOwner(owner.SubjectUuid);
        }
        if (source.ProposalOwner != null)
        {
            context.AddOwner(source.ProposalOwner.SubjectUuid);
        }
        if (claim.ArtistUuid is { } artist)
        {
            context.AddArtist(artist);
        }
        var sourceCurrent =
            !claim.IsShow
            || (
                claim.CatalogUuid == null
                    ? await OriginalVenueProposalIsCurrent(c, t, source, ct)
                    : await ShowSourceIsCurrent(c, t, source, ct)
            );
        var membership = await NormalizationWorkService.GetInputContributionsInSnapshotAsync(
            c,
            t,
            ownerKey,
            new(),
            ct
        );
        var bindings =
            owner == null
                ? EmptyVenueOwnerBindings()
                : await ReadVenueOwnerBindings(c, t, owner.SubjectUuid, context, ct);
        var query = new NormalizationCandidateQuery(IdentityMatchingDomain.Venues, [], []);
        var limits = new CandidateFactsLimits();
        var universes = await ReadFactsUniversesAsync(c, t, query, ct);
        var observationSubscription = await ReadVenueObservationSubscription(
            c,
            t,
            source,
            context,
            ct
        );
        var readSet = await context.ReadReadSetAsync(c, t, universes, ct);
        var candidates = new NormalizationCandidateFactsSnapshot<VenueCandidateFacts>(
            FactsVersion,
            FactsQueryHash(query, limits),
            query,
            limits,
            new(IdentityMatchingDomain.Venues, [], false),
            [],
            readSet,
            context.Issues
        );
        var hash = Convert.ToHexStringLower(
            SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(
                    new
                    {
                        version = VenueResolverContextVersion,
                        ownerKey,
                        source.ShowReference,
                        reason,
                    }
                )
            )
        );
        await t.CommitAsync(ct);
        return new(
            VenueResolverContextVersion,
            hash,
            owner,
            [],
            [],
            candidates,
            CurrentCatalog: readSet.CatalogInputs.SingleOrDefault(input =>
                input.ArtistUuid == claim.ArtistUuid
            ),
            SourceCurrent: sourceCurrent,
            OwnerMembership: membership,
            OwnerBindings: bindings.ReadSet,
            OwnerInputReferences: bindings.InputReferences,
            ObservationSubscription: observationSubscription
        );
    }

    private static async Task<NormalizationSubjectState?> ReadVenueResolverOwner(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        NormalizationSubjectKey key,
        CancellationToken ct
    )
    {
        var row = await c.QuerySingleOrDefaultAsync<ImportedAssignmentOwner>(
            new CommandDefinition(
                """
                SELECT uuid AS "SubjectUuid",desired_generation AS "DesiredGeneration",editorial_revision AS "EditorialRevision",
                    policy_version AS "PolicyVersion",published_generation AS "PublishedGeneration",published_work_uuid AS "PublishedWorkUuid"
                FROM normalization.work_subjects WHERE domain=@Domain AND subject_kind=@Kind AND subject_uuid=@Uuid
                """,
                key,
                t,
                cancellationToken: ct
            )
        );
        return row == null
            ? null
            : new(
                row.SubjectUuid,
                key,
                row.DesiredGeneration,
                row.EditorialRevision,
                row.PolicyVersion,
                row.PublishedGeneration,
                row.PublishedWorkUuid
            );
    }
}
