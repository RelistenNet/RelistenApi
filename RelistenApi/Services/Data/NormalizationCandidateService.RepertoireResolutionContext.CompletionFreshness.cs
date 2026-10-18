using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationCandidateService
{
    private static async Task<CandidateFactsReadSet> ReadRepertoireCompletionFreshness(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid attemptUuid,
        Guid? artistUuid,
        NormalizationSubjectState? finalArtist,
        CandidateFactsReadSet current,
        CancellationToken ct
    )
    {
        const string projection = """
            jsonb_build_object('CandidateUniverses',s.stage->'Context'->'ReadSet'->'CandidateUniverses',
                'OwnerObservations',s.stage->'Context'->'ReadSet'->'OwnerObservations',
                'CatalogInputs',s.stage->'Context'->'ReadSet'->'CatalogInputs')
            """;
        var bytes = await c.ExecuteScalarAsync<long>(
            new CommandDefinition(
                $"""
                SELECT COALESCE(sum(octet_length(({projection})::text)
                    +COALESCE(octet_length((s.stage->'Context'->'ArtistOwner'->'Membership'->'Owner')::text),0)
                    +COALESCE(octet_length(s.own_effects::text),0)),0)::bigint
                FROM normalization.repertoire_resolution_stages s WHERE s.attempt_uuid=@attemptUuid
                """,
                new { attemptUuid },
                t,
                cancellationToken: ct
            )
        );
        if (bytes > 32 * 1024 * 1024)
        {
            throw new InvalidOperationException(
                "The complete repertoire freshness proof exceeds its metadata limit."
            );
        }
        var rows = (
            await c.QueryAsync<RepertoireFreshnessEvent>(
                new CommandDefinition(
                    $"""
                    SELECT (s.stage->>'Kind')::integer AS "Kind",({projection})::text AS "ReadSet",
                        (s.stage->'Context'->'ArtistOwner'->'Membership'->'Owner')::text AS "BeforeArtist",
                        s.own_effects::text AS "OwnEffects",s.receipt IS NOT NULL AS "Published",s.superseded AS "Superseded"
                    FROM normalization.repertoire_resolution_stages s WHERE s.attempt_uuid=@attemptUuid ORDER BY s.ordinal
                    """,
                    new { attemptUuid },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        var expected = new RepertoireCompletionExpectations(artistUuid);
        foreach (var row in rows)
        {
            var before = row.BeforeArtist is null or "null"
                ? null
                : JsonSerializer.Deserialize<NormalizationSubjectState>(row.BeforeArtist);
            if (row.Kind == RepertoireResolutionStageKind.DecisionPage)
            {
                expected.ObserveArtist(before);
                expected.Observe(JsonSerializer.Deserialize<CandidateFactsReadSet>(row.ReadSet)!);
            }
            else if (
                row.Kind
                is RepertoireResolutionStageKind.AcceptedWrite
                    or RepertoireResolutionStageKind.RetireArtistContribution
            )
            {
                if (row.Superseded && !row.Published)
                {
                    continue;
                }
                if (!row.Published || row.OwnEffects == null)
                {
                    throw new NormalizationConflictException(
                        "A repertoire stage has no committed freshness effect."
                    );
                }
                expected.ObserveArtist(before);
                expected.Apply(
                    JsonSerializer.Deserialize<RepertoireResolutionOwnEffects>(row.OwnEffects)!,
                    row.Kind
                );
            }
        }
        expected.ObserveArtist(finalArtist);
        expected.Observe(current);
        var read = expected.ReadSet();
        // Foreign candidate owners may not appear in the empty completion projection. Read
        // their current stamps here, then retain the original expectations as watcher keys.
        var ownerIds = read.OwnerObservations.Select(value => value.SubjectUuid).ToArray();
        var owners = (
            await c.QueryAsync<NormalizationSubjectObservation>(
                new CommandDefinition(
                    """
                    SELECT uuid AS "SubjectUuid",desired_generation AS "DesiredGeneration",editorial_revision AS "EditorialRevision",
                        published_work_uuid AS "PublishedWorkUuid" FROM normalization.work_subjects WHERE uuid=ANY(@ownerIds)
                    """,
                    new { ownerIds },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        if (!owners.ToHashSet().SetEquals(read.OwnerObservations))
        {
            throw new NormalizationConflictException(
                "A repertoire decision owner changed before source acknowledgment."
            );
        }
        var artistIds = read.CatalogInputs.Select(value => value.ArtistUuid).ToArray();
        var catalog = (
            await c.QueryAsync<NormalizationCatalogDependency>(
                new CommandDefinition(
                    """
                    SELECT artist_uuid AS "ArtistUuid",generation AS "Generation"
                    FROM normalization.catalog_artist_revisions WHERE artist_uuid=ANY(@artistIds)
                    """,
                    new { artistIds },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        if (!catalog.ToHashSet().SetEquals(read.CatalogInputs))
        {
            throw new NormalizationConflictException(
                "A repertoire decision catalog context changed before source acknowledgment."
            );
        }
        return read;
    }

    /// <summary>
    /// Pages add factual reads; they cannot replace an earlier observation. The sole permitted
    /// advances are the exact effects recorded under this attempt's own publication locks.
    /// In particular, a later empty projection cannot settle an older negative against a new pool.
    /// </summary>
    private sealed class RepertoireCompletionExpectations(Guid? artistUuid)
    {
        private readonly Dictionary<
            NormalizationCandidateUniverseKey,
            NormalizationCandidateUniverseDependency
        > pools = [];
        private readonly Dictionary<Guid, NormalizationSubjectObservation> owners = [];
        private readonly Dictionary<Guid, NormalizationCatalogDependency> catalog = [];
        private bool artistObserved;
        private NormalizationSubjectObservation? artist;

        public void ObserveArtist(NormalizationSubjectState? before)
        {
            var observed =
                before == null
                    ? null
                    : new NormalizationSubjectObservation(
                        before.SubjectUuid,
                        before.DesiredGeneration,
                        before.EditorialRevision,
                        before.PublishedWorkUuid
                    );
            if (artistObserved && artist != observed)
            {
                throw new NormalizationConflictException(
                    "The repertoire artist owner changed between decision stages."
                );
            }
            artistObserved = true;
            artist = observed;
        }

        public void Observe(CandidateFactsReadSet read)
        {
            foreach (var value in read.CandidateUniverses)
            {
                Observe(pools, value.Universe, value);
            }
            foreach (var value in read.OwnerObservations)
            {
                Observe(owners, value.SubjectUuid, value);
            }
            foreach (var value in read.CatalogInputs)
            {
                Observe(catalog, value.ArtistUuid, value);
            }
        }

        public void Apply(RepertoireResolutionOwnEffects effect, RepertoireResolutionStageKind kind)
        {
            var allowed = artistUuid is { } uuid
                ? new[]
                {
                    NormalizationCandidateUniverseKey.Compositions,
                    NormalizationCandidateUniverseKey.ForRepertoire(uuid),
                }
                : [];
            if (
                artistUuid == null
                || artist != null && artist.SubjectUuid != effect.ArtistOwner.SubjectUuid
                || kind == RepertoireResolutionStageKind.RetireArtistContribution
                    && effect.CandidateUniverses.Count != 0
                || kind == RepertoireResolutionStageKind.AcceptedWrite
                    && !effect
                        .CandidateUniverses.Select(value => value.Universe)
                        .ToHashSet()
                        .SetEquals(allowed)
            )
            {
                throw new NormalizationConflictException(
                    "The recorded repertoire stage effects do not belong to its artist."
                );
            }
            foreach (var value in effect.CandidateUniverses)
            {
                if (
                    !pools.TryGetValue(value.Universe, out var previous)
                    || value.Generation < previous.Generation
                )
                {
                    throw new NormalizationConflictException(
                        "A repertoire write cannot replace an unobserved candidate pool."
                    );
                }
                pools[value.Universe] = value;
            }
            artist = effect.ArtistOwner;
            owners[artist.SubjectUuid] = artist;
        }

        public CandidateFactsReadSet ReadSet() =>
            new(pools.Values.ToArray(), owners.Values.ToArray(), catalog.Values.ToArray());

        private static void Observe<TKey, TValue>(
            Dictionary<TKey, TValue> observations,
            TKey key,
            TValue value
        )
            where TKey : notnull
        {
            if (
                observations.TryGetValue(key, out var previous)
                && !EqualityComparer<TValue>.Default.Equals(previous, value)
            )
            {
                throw new NormalizationConflictException(
                    "A repertoire decision observation changed outside this attempt's recorded effects."
                );
            }
            observations[key] = value;
        }
    }

    private sealed record RepertoireFreshnessEvent(
        RepertoireResolutionStageKind Kind,
        string ReadSet,
        string? BeforeArtist,
        string? OwnEffects,
        bool Published,
        bool Superseded
    );
}
