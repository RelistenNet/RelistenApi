using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationWorkService
{
    private static NormalizationReviewReadSet? NormalizeReviewReadSet(
        NormalizationReviewReadSet? readSet
    )
    {
        if (readSet == null)
        {
            return null;
        }

        var inputs = (readSet.Inputs ?? [])
            .Distinct()
            .OrderBy(value => value.ResourceUuid)
            .ToArray();
        var dependencies = (readSet.Dependencies ?? [])
            .Distinct()
            .OrderBy(value => value.SubjectUuid)
            .ToArray();
        var artists = NormalizeCatalogInputs(readSet.CatalogInputs);
        var identifiers = NormalizeSourceInputs(readSet.SourceIdentities);
        var observations = NormalizeOwnerObservations(readSet.OwnerObservations);
        if (
            inputs.Any(value =>
                value.ResourceUuid == Guid.Empty
                || value.RevisionUuid == Guid.Empty
                || value.Generation < 1
            )
            || inputs.GroupBy(value => value.ResourceUuid).Any(group => group.Count() != 1)
            || artists.Any(value => value.ArtistUuid == Guid.Empty || value.Generation < 0)
            || identifiers.Any(value => value.Generation < 0)
            || readSet.MappingRegistry is { Generation: < 0 }
        )
        {
            throw new ArgumentException(
                "Review facts require stable keys and consistent original generations."
            );
        }

        var frozen = new NormalizationReviewReadSet(
            FreezeReviewFacts(inputs),
            FreezeReviewFacts(dependencies),
            FreezeReviewFacts(artists),
            FreezeReviewFacts(identifiers),
            readSet.MappingRegistry,
            FreezeReviewFacts(NormalizeImportedVenues(readSet.ImportedVenues)),
            FreezeReviewFacts(NormalizeCandidateUniverses(readSet.CandidateUniverses)),
            FreezeReviewFacts(observations),
            FreezeReviewFacts(NormalizeSourceUuidInputs(readSet.SourceUuids))
        );
        // Dependencies are one-off observed stamps, not Work graph edges. Check their own
        // validity and consistency with the same owner's explicit observations before hashing.
        ReviewOwnerObservations(frozen);
        return frozen;
    }

    private static IReadOnlyList<T>? FreezeReviewFacts<T>(T[] values) =>
        values.Length == 0 ? null : Array.AsReadOnly(values);

    private static NormalizationSubjectObservation[] ReviewOwnerObservations(
        NormalizationReviewReadSet? readSet
    ) =>
        NormalizeOwnerObservations(
            (readSet?.OwnerObservations ?? [])
                .Concat(
                    (readSet?.Dependencies ?? []).Select(
                        value => new NormalizationSubjectObservation(
                            value.SubjectUuid,
                            value.DesiredGeneration,
                            value.EditorialRevision,
                            value.PublishedWorkUuid
                        )
                    )
                )
                .ToArray()
        );

    /// <summary>Take only declared fact locks, in the same order as publication, before any owners.</summary>
    private static async Task LockReviewFactReadSet(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        NormalizationReviewReadSet? readSet,
        IReadOnlyList<NormalizationCandidateUniverseKey> writeScopes,
        CancellationToken ct
    )
    {
        if (readSet != null)
        {
            var catalog = await LockCatalogReadSet(
                connection,
                transaction,
                (readSet.CatalogInputs ?? []).Select(value => value.ArtistUuid).ToArray(),
                (readSet.SourceIdentities ?? [])
                    .Select(value => value.UpstreamIdentifier)
                    .ToArray(),
                (readSet.SourceUuids ?? []).Select(value => value.SourceUuid).ToArray(),
                (readSet.ImportedVenues ?? []).Select(value => value.VenueUuid).ToArray(),
                readSet.MappingRegistry != null,
                initializeMissingClocks: false,
                ct
            );
            RequireCatalogReadSet(
                catalog,
                readSet.CatalogInputs ?? [],
                readSet.SourceIdentities ?? [],
                readSet.SourceUuids ?? [],
                readSet.ImportedVenues ?? [],
                readSet.MappingRegistry
            );
        }

        var candidates = await LockCandidateUniverses(
            connection,
            transaction,
            (readSet?.CandidateUniverses ?? []).Select(value => value.Universe).ToArray(),
            writeScopes,
            initializeConsumed: false,
            ct
        );
        RequireCandidateUniverses(candidates, readSet?.CandidateUniverses ?? []);

        var expectedInputs = readSet?.Inputs ?? [];
        if (expectedInputs.Count > 0)
        {
            var inputs = await LockInputUuids(
                connection,
                transaction,
                expectedInputs.Select(value => value.ResourceUuid).ToArray(),
                ct
            );
            if (!inputs.OrderBy(value => value.ResourceUuid).SequenceEqual(expectedInputs))
            {
                throw new NormalizationConflictException(
                    "Review inputs changed after they were read; prepare the review again."
                );
            }
        }
    }

    private static void RequireReviewOwnerObservations(
        IReadOnlyList<NormalizationSubjectObservation> observations,
        IReadOnlyList<SubjectRow> subjects
    )
    {
        var byUuid = subjects.ToDictionary(value => value.Uuid);
        if (
            observations.Any(value =>
                !byUuid.TryGetValue(value.SubjectUuid, out var owner)
                || !MatchesObservation(value, owner)
            )
        )
        {
            // Unlike publication admission, review never reconfigures its own owner. A self
            // observation must match too; the editorial CAS alone does not fence publications.
            throw new NormalizationConflictException(
                "Observed review owners changed; prepare the review again."
            );
        }
    }
}
