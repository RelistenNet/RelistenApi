using System;
using System.Data;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationCandidateService
{
    public const string VenueResolverContextVersion = "venue-resolution-context-v1";

    /// <summary>
    /// Captures absence without inventing input bytes. The retained UUID clock, owner and
    /// complete contributor bindings are observed together, including an absent owner/configuration.
    /// </summary>
    public async Task<(
        VenueResolutionAbsenceProbe Probe,
        VenueResolutionContext Context
    )> ReadMissingImportedVenueResolverContextAsync(
        Guid importedVenueUuid,
        CancellationToken cancellationToken = default
    )
    {
        if (importedVenueUuid == Guid.Empty)
        {
            throw new ArgumentException("An imported venue UUID is required.");
        }
        await using var c = db.CreateConnection(false, false);
        await c.OpenAsync(cancellationToken);
        await using var t = await c.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken
        );
        await c.ExecuteAsync(
            new CommandDefinition(
                "SET TRANSACTION READ ONLY",
                transaction: t,
                cancellationToken: cancellationToken
            )
        );
        var probe = await c.QuerySingleAsync<VenueResolutionAbsenceProbe>(
            new CommandDefinition(
                """
                SELECT @importedVenueUuid AS "ImportedVenueUuid",EXISTS(SELECT 1 FROM venues WHERE uuid=@importedVenueUuid) AS "Exists",
                    COALESCE((SELECT generation FROM normalization.catalog_venue_revisions WHERE venue_uuid=@importedVenueUuid),0) AS "Generation"
                """,
                new { importedVenueUuid },
                t,
                cancellationToken: cancellationToken
            )
        );
        var owner = await ReadImportedAssignmentOwner(c, t, importedVenueUuid, cancellationToken);
        var membership = await NormalizationWorkService.GetInputContributionsInSnapshotAsync(
            c,
            t,
            new("venues", "imported_venue", importedVenueUuid),
            new(),
            cancellationToken
        );
        var factsContext = new CandidateFactsContext();
        if (owner != null)
        {
            factsContext.AddOwner(owner.SubjectUuid);
        }
        var bindings =
            owner == null
                ? EmptyVenueOwnerBindings()
                : await ReadVenueOwnerBindings(
                    c,
                    t,
                    owner.SubjectUuid,
                    factsContext,
                    cancellationToken
                );
        var readSet = await factsContext.ReadReadSetAsync(c, t, [], cancellationToken);
        var query = new NormalizationCandidateQuery(IdentityMatchingDomain.Venues, [], []);
        var limits = new CandidateFactsLimits();
        var hash = Convert.ToHexStringLower(
            SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(
                    new
                    {
                        version = VenueResolverContextVersion,
                        importedVenueUuid,
                        missingSource = true,
                    }
                )
            )
        );
        var candidates = new NormalizationCandidateFactsSnapshot<VenueCandidateFacts>(
            FactsVersion,
            FactsQueryHash(query, limits),
            query,
            limits,
            new(IdentityMatchingDomain.Venues, [], false),
            [],
            readSet,
            factsContext.Issues
        );
        await t.CommitAsync(cancellationToken);
        return (
            probe,
            new(
                VenueResolverContextVersion,
                hash,
                owner,
                [],
                [],
                candidates,
                SourceCurrent: !probe.Exists,
                OwnerMembership: membership,
                OwnerBindings: bindings.ReadSet,
                OwnerInputReferences: bindings.InputReferences
            )
        );
    }

    /// <summary>Imported venue matching facts and complete assignment-owner membership share one primary snapshot.</summary>
    public async Task<VenueResolutionContext> FindImportedVenueResolverContextAsync(
        Guid importedVenueUuid,
        NormalizationCandidateQuery query,
        CandidateFactsLimits? limits = null,
        int maximumAssignments = 1000,
        CancellationToken cancellationToken = default
    )
    {
        if (importedVenueUuid == Guid.Empty || query.Domain != IdentityMatchingDomain.Venues)
        {
            throw new ArgumentException(
                "Imported venue resolution requires a stable catalog UUID and a global venue query."
            );
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumAssignments, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumAssignments, 10_000);
        var frozen = FreezeFactsQuery(query);
        var names = ValidateQuery(frozen);
        var bounds = ValidateFactsLimits(limits ?? new());

        await using var connection = db.CreateConnection(false, false);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken
        );
        await connection.ExecuteAsync(
            new CommandDefinition(
                "SET TRANSACTION READ ONLY",
                transaction: transaction,
                cancellationToken: cancellationToken
            )
        );
        var context = new CandidateFactsContext();
        var owner = await ReadImportedAssignmentOwner(
            connection,
            transaction,
            importedVenueUuid,
            cancellationToken
        );
        var assignments = await ReadImportedAssignmentFacts(
            connection,
            transaction,
            importedVenueUuid,
            maximumAssignments,
            bounds.MaximumEvidencePerAssertion,
            context,
            cancellationToken
        );
        if (owner != null || assignments.Length > 0)
        {
            context.AddOwner(owner?.SubjectUuid);
        }
        var selection = await FindInSnapshotAsync(
            connection,
            transaction,
            frozen,
            names,
            cancellationToken
        );
        var selected = selection;
        if (selection.Items.Count > bounds.MaximumCandidates)
        {
            context.AddIssue(CandidateFactsIssueKind.CandidateLimit);
            selected = selection with
            {
                Items = selection.Items.Take(bounds.MaximumCandidates).ToArray(),
            };
        }
        var facts = await ReadVenueFactsAsync(
            connection,
            transaction,
            selected,
            bounds,
            context,
            cancellationToken
        );
        var targets = await ReadImportedAssignmentTargets(
            connection,
            transaction,
            assignments,
            facts,
            bounds,
            context,
            cancellationToken
        );
        var universes = await ReadFactsUniversesAsync(
            connection,
            transaction,
            frozen,
            cancellationToken
        );
        var membership = await NormalizationWorkService.GetInputContributionsInSnapshotAsync(
            connection,
            transaction,
            new("venues", "imported_venue", importedVenueUuid),
            new(),
            cancellationToken
        );
        var ownerBindings =
            owner == null
                ? EmptyVenueOwnerBindings()
                : await ReadVenueOwnerBindings(
                    connection,
                    transaction,
                    owner.SubjectUuid,
                    context,
                    cancellationToken
                );
        var readSet = await context.ReadReadSetAsync(
            connection,
            transaction,
            universes,
            cancellationToken
        );
        var candidates = new NormalizationCandidateFactsSnapshot<VenueCandidateFacts>(
            FactsVersion,
            FactsQueryHash(frozen, bounds),
            frozen,
            bounds,
            selection,
            facts,
            readSet,
            context.Issues
        );
        var hash = Convert.ToHexStringLower(
            SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(
                    new
                    {
                        version = VenueResolverContextVersion,
                        importedVenueUuid,
                        queryHash = candidates.QueryHash,
                        maximumAssignments,
                    }
                )
            )
        );
        await transaction.CommitAsync(cancellationToken);
        return new(
            VenueResolverContextVersion,
            hash,
            owner,
            assignments,
            targets,
            candidates,
            OwnerMembership: membership,
            OwnerBindings: ownerBindings.ReadSet,
            OwnerInputReferences: ownerBindings.InputReferences
        );
    }
}
