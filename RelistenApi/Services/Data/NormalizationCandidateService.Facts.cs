using System;
using System.Collections.Generic;
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
    /// <summary>Projection version included in lookup fingerprints and retained matching receipts.</summary>
    public const string FactsVersion = "candidate-facts-v1";

    /// <summary>
    /// Reads global person candidates, original-owner assertions and exact accepted-credit
    /// artist context. Supporting examples are bounded separately from artist membership.
    /// </summary>
    public Task<NormalizationCandidateFactsSnapshot<PersonCandidateFacts>> FindPeopleFactsAsync(
        NormalizationCandidateQuery query,
        CandidateFactsLimits? limits = null,
        CancellationToken cancellationToken = default
    ) =>
        FindFactsAsync(
            query,
            IdentityMatchingDomain.People,
            limits,
            ReadPeopleFactsAsync,
            cancellationToken
        );

    /// <summary>
    /// Reads every supported dated venue assertion within explicit bounds. Raw geography is
    /// not a resolved locality, and historical alternatives remain separate assertions.
    /// </summary>
    public Task<NormalizationCandidateFactsSnapshot<VenueCandidateFacts>> FindVenueFactsAsync(
        NormalizationCandidateQuery query,
        CandidateFactsLimits? limits = null,
        CancellationToken cancellationToken = default
    ) =>
        FindFactsAsync(
            query,
            IdentityMatchingDomain.Venues,
            limits,
            ReadVenueFactsAsync,
            cancellationToken
        );

    /// <summary>
    /// Reads one artist's repertoire candidates, inherited titles and supported composition
    /// links. Both repertoire and global composition generations fence the result, even empty.
    /// </summary>
    public Task<
        NormalizationCandidateFactsSnapshot<RepertoireCandidateFacts>
    > FindRepertoireFactsAsync(
        NormalizationCandidateQuery query,
        CandidateFactsLimits? limits = null,
        CancellationToken cancellationToken = default
    ) =>
        FindFactsAsync(
            query,
            IdentityMatchingDomain.Repertoire,
            limits,
            ReadRepertoireFactsAsync,
            cancellationToken
        );

    /// <summary>
    /// Reads global works and role-specific credits. Person and artist targets stay distinct;
    /// individual credits cannot establish a complete author list.
    /// </summary>
    public Task<
        NormalizationCandidateFactsSnapshot<CompositionCandidateFacts>
    > FindCompositionFactsAsync(
        NormalizationCandidateQuery query,
        CandidateFactsLimits? limits = null,
        CancellationToken cancellationToken = default
    ) =>
        FindFactsAsync(
            query,
            IdentityMatchingDomain.Compositions,
            limits,
            ReadCompositionFactsAsync,
            cancellationToken
        );

    private async Task<NormalizationCandidateFactsSnapshot<T>> FindFactsAsync<T>(
        NormalizationCandidateQuery query,
        IdentityMatchingDomain domain,
        CandidateFactsLimits? requestedLimits,
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

        await using var connection = db.CreateConnection(false, false);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            ct
        );
        await connection.ExecuteAsync(
            new CommandDefinition(
                "SET TRANSACTION READ ONLY",
                transaction: transaction,
                cancellationToken: ct
            )
        );

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
            FactsVersion,
            FactsQueryHash(frozen, limits),
            frozen,
            limits,
            selection,
            facts,
            readSet,
            context.Issues
        );

        await transaction.CommitAsync(ct);
        return result;
    }

    private static NormalizationCandidateQuery FreezeFactsQuery(
        NormalizationCandidateQuery query
    ) =>
        query with
        {
            Names = Array.AsReadOnly(query.Names.ToArray()),
            ExternalIdentities = Array.AsReadOnly(query.ExternalIdentities.ToArray()),
        };

    private static CandidateFactsLimits ValidateFactsLimits(CandidateFactsLimits limits)
    {
        if (
            limits.MaximumCandidates is < 1 or > 1024
            || limits.MaximumIdentityClosure is < 1 or > 10000
            || limits.MaximumAssertionsPerKind is < 1 or > 10000
            || limits.MaximumEvidencePerAssertion is < 1 or > 10000
            || limits.MaximumArtistContexts is < 1 or > 10000
            || limits.MaximumRoleBasisGroupsPerArtist is < 1 or > 128
            || limits.MaximumWitnessesPerArtist is < 0 or > 32
            || limits.MaximumMissingContextWitnesses is < 0 or > 64
            || limits.MaximumParentClosure is < 1 or > 10000
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(limits),
                "Candidate fact limits must be within the documented bounded ranges."
            );
        }

        return limits;
    }

    private static string FactsQueryHash(
        NormalizationCandidateQuery query,
        CandidateFactsLimits limits
    ) =>
        Convert.ToHexStringLower(
            SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(
                    new
                    {
                        version = FactsVersion,
                        query,
                        limits,
                    }
                )
            )
        );

    private static async Task<NormalizationCandidateUniverseDependency[]> ReadFactsUniversesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        NormalizationCandidateQuery query,
        CancellationToken ct
    )
    {
        NormalizationCandidateUniverseKey[] keys = query.Domain switch
        {
            IdentityMatchingDomain.People => [NormalizationCandidateUniverseKey.People],
            IdentityMatchingDomain.Venues => [NormalizationCandidateUniverseKey.Venues],
            IdentityMatchingDomain.Compositions => [NormalizationCandidateUniverseKey.Compositions],
            IdentityMatchingDomain.Repertoire =>
            [
                NormalizationCandidateUniverseKey.Compositions,
                NormalizationCandidateUniverseKey.ForRepertoire(query.ArtistUuid!.Value),
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(query)),
        };
        var result = new List<NormalizationCandidateUniverseDependency>();
        foreach (var key in keys)
        {
            var generation = await connection.QuerySingleOrDefaultAsync<long?>(
                new CommandDefinition(
                    "SELECT generation FROM normalization.candidate_universe_revisions WHERE domain=@Domain AND scope_uuid=@ScopeUuid",
                    key,
                    transaction,
                    cancellationToken: ct
                )
            );
            // A not-yet-created artist repertoire pool has the original zero observation.
            // Global pools are installed by migration 30 and cannot be invented by a reader.
            if (generation == null && key.Domain != "repertoire")
            {
                throw new InvalidOperationException(
                    "A global candidate generation is missing; candidate facts cannot be admitted."
                );
            }
            result.Add(new(key, generation ?? 0));
        }
        return result.ToArray();
    }
}
