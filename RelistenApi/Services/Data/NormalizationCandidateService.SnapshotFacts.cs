using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationCandidateService
{
    /// <summary>
    /// Reads people selection, facts and original clocks inside the caller's primary
    /// repeatable-read snapshot. The caller owns isolation, cancellation and transaction
    /// lifetime; this method opens no connection and never commits or configures Work.
    /// </summary>
    internal Task<
        NormalizationCandidateFactsSnapshot<PersonCandidateFacts>
    > FindPeopleFactsInSnapshotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        NormalizationCandidateQuery query,
        CandidateFactsLimits? limits = null,
        CancellationToken ct = default
    )
    {
        var prepared = PrepareFactsQuery(query, IdentityMatchingDomain.People, limits);
        return ReadFactsInSnapshotAsync(
            connection,
            transaction,
            prepared.Query,
            prepared.Names,
            prepared.Limits,
            ReadPeopleFactsAsync,
            ct
        );
    }

    private static (
        NormalizationCandidateQuery Query,
        string[] Names,
        CandidateFactsLimits Limits
    ) PrepareFactsQuery(
        NormalizationCandidateQuery query,
        IdentityMatchingDomain domain,
        CandidateFactsLimits? requestedLimits
    )
    {
        if (query.Domain != domain)
        {
            throw new ArgumentException(
                "Use the facts method for the query's identity domain.",
                nameof(query)
            );
        }

        var frozen = FreezeFactsQuery(query);
        var names = ValidateQuery(frozen);
        var limits = ValidateFactsLimits(requestedLimits ?? new());
        if (domain == IdentityMatchingDomain.People)
        {
            limits = limits with
            {
                MaximumActorMatchingBytes = limits.MaximumActorMatchingBytes ?? 4 * 1024 * 1024,
                MaximumAliasMatchingNames = limits.MaximumAliasMatchingNames ?? 5000,
                MaximumAliasMatchingBytes = limits.MaximumAliasMatchingBytes ?? 4 * 1024 * 1024,
                MaximumAliasEvidenceBytes = limits.MaximumAliasEvidenceBytes ?? 4 * 1024 * 1024,
            };
        }
        else if (
            limits.MaximumActorMatchingBytes != null
            || limits.MaximumAliasMatchingNames != null
            || limits.MaximumAliasMatchingBytes != null
            || limits.MaximumAliasEvidenceBytes != null
        )
        {
            throw new ArgumentException(
                "Actor and alias matching limits apply only to people facts.",
                nameof(requestedLimits)
            );
        }

        return (frozen, names, limits);
    }

    private static async Task<NormalizationCandidateFactsSnapshot<T>> ReadFactsInSnapshotAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        NormalizationCandidateQuery frozen,
        string[] names,
        CandidateFactsLimits limits,
        Func<
            NpgsqlConnection,
            NpgsqlTransaction,
            NormalizationCandidateSet,
            CandidateFactsLimits,
            CandidateFactsContext,
            CancellationToken,
            Task<T[]>
        > readFacts,
        CancellationToken ct
    )
    {
        // Selection and every later assertion/clock read use this original primary snapshot.
        // Calling FindAsync and then reading fresh clocks would not establish this contract.
        var selection = await FindInSnapshotAsync(connection, transaction, frozen, names, ct);
        var context = new CandidateFactsContext();
        var selected = selection;
        if (selection.Items.Count > limits.MaximumCandidates)
        {
            context.AddIssue(CandidateFactsIssueKind.CandidateLimit);
            selected = selection with
            {
                Items = selection.Items.Take(limits.MaximumCandidates).ToArray(),
            };
        }

        foreach (var candidate in selected.Items)
        {
            context.AddOwner(candidate.OwnerSubjectUuid, candidate.Uuid);
        }

        if (frozen.ArtistUuid is { } artistUuid)
        {
            context.AddArtist(artistUuid);
        }

        var facts = await readFacts(connection, transaction, selected, limits, context, ct);
        var universes = await ReadFactsUniversesAsync(connection, transaction, frozen, ct);
        var readSet = await context.ReadReadSetAsync(connection, transaction, universes, ct);
        var result = new NormalizationCandidateFactsSnapshot<T>(
            frozen.Domain == IdentityMatchingDomain.People ? PeopleFactsVersion : FactsVersion,
            FactsQueryHash(frozen, limits),
            frozen,
            limits,
            selection,
            facts,
            readSet,
            context.Issues
        );

        return result;
    }
}
