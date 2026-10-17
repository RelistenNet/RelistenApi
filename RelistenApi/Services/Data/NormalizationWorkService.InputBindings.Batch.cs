using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationWorkService
{
    /// <summary>
    /// Atomically replaces several contributors and admits their complete resulting union.
    /// Each preparation pins every resource it owns and retains its original catalog clocks.
    /// Unmentioned peers remain owned. Their bindings appear in the returned ticket, which
    /// callers must consume completely before publication. A peer with observed-owner keys
    /// must also be explicitly prepared: admission cannot invent its original owner stamps.
    /// Conflicting producer observations require a new
    /// preparation, never selecting the newest generation. Single-contributor replay hashes
    /// retain their existing contract; this method has a distinct batch request identity.
    /// Complete consumers must supply expectedContributionRevisions from their saved snapshot;
    /// an empty list fences an empty membership, while null permits unmentioned peers. The
    /// membership check also catches new peers whose resource union is otherwise identical.
    /// </summary>
    public Task<NormalizationWorkTicket> BeginContributedBatchAsync(
        Guid workUuid,
        NormalizationSubjectKey subject,
        string policyVersion,
        IReadOnlyList<NormalizationPreparedInputContribution> contributions,
        long? expectedConfigurationGeneration = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<NormalizationInputContributionRevision>? expectedContributionRevisions = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyVersion);
        if (contributions.Count is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(contributions),
                "Prepare between 1 and 1000 contributors."
            );
        }
        var prepared = contributions
            .Select(PrepareReplacement)
            .OrderBy(entry => entry.Replacement.Contributor.Provider, StringComparer.Ordinal)
            .ThenBy(entry => entry.Replacement.Contributor.ResourceType, StringComparer.Ordinal)
            .ThenBy(entry => entry.Replacement.Contributor.ProviderKey, StringComparer.Ordinal)
            .ToArray();
        if (
            prepared.Select(entry => entry.Replacement.Contributor).Distinct().Count()
            != prepared.Length
        )
        {
            throw new ArgumentException(
                "A batch replaces each contributor exactly once.",
                nameof(contributions)
            );
        }
        var expected = MergePreparedExpectations(prepared);
        var membership =
            expectedContributionRevisions == null
                ? null
                : NormalizeContributionRevisions(expectedContributionRevisions);
        var requestHash = Hash(
            JsonSerializer.SerializeToElement(
                new
                {
                    mode = "contributed_inputs_batch_v1",
                    subject,
                    policyVersion,
                    contributions = prepared,
                    expectedConfigurationGeneration,
                    expectedContributionRevisions = membership,
                }
            )
        );
        return InTransaction(
            async (connection, transaction) =>
            {
                var replay = await ReadWorkReplay(
                    connection,
                    transaction,
                    workUuid,
                    requestHash,
                    cancellationToken
                );
                if (replay != null)
                {
                    return replay;
                }
                await LockConfiguration(connection, transaction, subject, cancellationToken);
                var (union, change) = await PrepareContributions(
                    connection,
                    transaction,
                    subject,
                    prepared.Select(entry => entry.Replacement).ToArray(),
                    cancellationToken,
                    membership
                );
                var request = new WorkStartRequest(
                    workUuid,
                    subject,
                    policyVersion,
                    union,
                    expectedConfigurationGeneration,
                    requestHash,
                    expected.CatalogInputs,
                    expected.SourceIdentities,
                    expected.MappingRegistry,
                    expected.ExpectedInputRevisions,
                    expected.ImportedVenues,
                    expected.CandidateUniverses,
                    expected.OwnerObservations,
                    expected.SourceUuids,
                    change
                );
                return await StartConfiguredWork(
                    connection,
                    transaction,
                    request,
                    cancellationToken
                );
            },
            cancellationToken
        );
    }

    private static NormalizationInputContributionRevision[] NormalizeContributionRevisions(
        IEnumerable<NormalizationInputContributionRevision> revisions
    )
    {
        var normalized = revisions
            .Distinct()
            .OrderBy(input => input.Contributor.Provider, StringComparer.Ordinal)
            .ThenBy(input => input.Contributor.ResourceType, StringComparer.Ordinal)
            .ThenBy(input => input.Contributor.ProviderKey, StringComparer.Ordinal)
            .ToArray();
        foreach (var input in normalized)
        {
            ValidateContributor(input.Contributor);
            ArgumentOutOfRangeException.ThrowIfNegative(input.Revision);
        }
        if (normalized.GroupBy(input => input.Contributor).Any(group => group.Count() != 1))
        {
            throw new ArgumentException(
                "Contributor membership requires one original revision per contributor."
            );
        }
        return normalized;
    }

    private static PreparedContributionReplacement PrepareReplacement(
        NormalizationPreparedInputContribution prepared
    )
    {
        var contribution = prepared.Contribution;
        ValidateContributor(contribution.Contributor);
        ArgumentOutOfRangeException.ThrowIfNegative(contribution.ExpectedRevision);
        var catalog = NormalizeCatalogInputs(prepared.CatalogInputs);
        var identifiers = NormalizeSourceInputs(prepared.SourceIdentities);
        var venues = NormalizeImportedVenues(prepared.ImportedVenues);
        var candidates = NormalizeCandidateUniverses(prepared.CandidateUniverses);
        var observations = NormalizeOwnerObservations(prepared.ExpectedOwnerObservations);
        var sources = NormalizeSourceUuidInputs(prepared.SourceUuids);
        var bindings = WorkBindingSet.Create(
            contribution.Resources,
            contribution.DependencySubjectUuids,
            catalog.Select(input => input.ArtistUuid),
            identifiers.Select(input => input.UpstreamIdentifier),
            prepared.MappingRegistry != null,
            venues.Select(input => input.VenueUuid),
            candidates.Select(input => input.Universe),
            contribution.OwnerObservationSubjectUuids,
            sources.Select(input => input.SourceUuid)
        );
        var inputs = NormalizeExpectedInputRevisions(
            prepared.ExpectedInputRevisions,
            bindings.Resources
        );
        if (inputs.Length != bindings.Resources.Length)
        {
            throw new ArgumentException(
                "Every prepared contribution resource requires its original input revision."
            );
        }
        if (
            !observations
                .Select(input => input.SubjectUuid)
                .SequenceEqual(bindings.ObservedSubjects ?? [])
        )
        {
            throw new ArgumentException(
                "Each prepared contributor requires its complete original owner observations."
            );
        }
        return new(
            new(contribution.Contributor, contribution.ExpectedRevision, bindings),
            inputs,
            catalog,
            identifiers,
            prepared.MappingRegistry,
            venues,
            candidates,
            observations,
            sources
        );
    }

    private static PreparedContributionExpectations MergePreparedExpectations(
        PreparedContributionReplacement[] prepared
    )
    {
        var inputs = prepared.SelectMany(entry => entry.ExpectedInputRevisions).ToArray();
        var catalog = prepared.SelectMany(entry => entry.CatalogInputs).ToArray();
        var identifiers = prepared.SelectMany(entry => entry.SourceIdentities).ToArray();
        var venues = prepared.SelectMany(entry => entry.ImportedVenues).ToArray();
        var candidates = prepared.SelectMany(entry => entry.CandidateUniverses).ToArray();
        var observations = prepared.SelectMany(entry => entry.OwnerObservations).ToArray();
        var sources = prepared.SelectMany(entry => entry.SourceUuids).ToArray();
        var registry = prepared
            .Select(entry => entry.MappingRegistry)
            .OfType<NormalizationMappingRegistryDependency>()
            .Distinct()
            .ToArray();
        RequireCoherentObservations(inputs, input => input.Resource);
        RequireCoherentObservations(catalog, input => input.ArtistUuid);
        RequireCoherentObservations(identifiers, input => input.UpstreamIdentifier);
        RequireCoherentObservations(venues, input => input.VenueUuid);
        RequireCoherentObservations(candidates, input => input.Universe);
        RequireCoherentObservations(observations, input => input.SubjectUuid);
        RequireCoherentObservations(sources, input => input.SourceUuid);
        if (registry.Length > 1)
        {
            throw IncompatiblePreparations();
        }
        return new(
            NormalizeExpectedInputRevisions(
                inputs,
                prepared
                    .SelectMany(entry => entry.Replacement.Bindings.Resources)
                    .Distinct()
                    .ToArray()
            ),
            NormalizeCatalogInputs(catalog),
            NormalizeSourceInputs(identifiers),
            registry.SingleOrDefault(),
            NormalizeImportedVenues(venues),
            NormalizeCandidateUniverses(candidates),
            NormalizeOwnerObservations(observations),
            NormalizeSourceUuidInputs(sources)
        );
    }

    private static void RequireCoherentObservations<T, TKey>(
        IEnumerable<T> observations,
        Func<T, TKey> key
    )
    {
        if (observations.GroupBy(key).Any(group => group.Distinct().Count() > 1))
        {
            throw IncompatiblePreparations();
        }
    }

    private static NormalizationConflictException IncompatiblePreparations() =>
        new(
            "Prepared contributors observed different revisions of a shared input or clock; prepare their complete read set again."
        );

    private sealed record PreparedContributionReplacement(
        ContributionReplacement Replacement,
        NormalizationBindingInputReference[] ExpectedInputRevisions,
        NormalizationCatalogDependency[] CatalogInputs,
        NormalizationSourceIdentityDependency[] SourceIdentities,
        NormalizationMappingRegistryDependency? MappingRegistry,
        NormalizationImportedVenueDependency[] ImportedVenues,
        NormalizationCandidateUniverseDependency[] CandidateUniverses,
        NormalizationSubjectObservation[] OwnerObservations,
        NormalizationSourceUuidDependency[] SourceUuids
    );

    private sealed record PreparedContributionExpectations(
        NormalizationBindingInputReference[] ExpectedInputRevisions,
        NormalizationCatalogDependency[] CatalogInputs,
        NormalizationSourceIdentityDependency[] SourceIdentities,
        NormalizationMappingRegistryDependency? MappingRegistry,
        NormalizationImportedVenueDependency[] ImportedVenues,
        NormalizationCandidateUniverseDependency[] CandidateUniverses,
        NormalizationSubjectObservation[] OwnerObservations,
        NormalizationSourceUuidDependency[] SourceUuids
    );
}
