using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

internal static partial class SourceActorResolutionCheckpoint
{
    // The ordinal pool list still owns queries, retrieval results and freshness. Only exact
    // complete fact bodies and owner lists are local shared blocks; these indexes are not IDs.
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record StoredPool(
        NormalizationCandidateFactsSnapshot<PersonCandidateFacts> Value,
        int OwnerBlockRef,
        IReadOnlyList<int> FactRefs,
        IReadOnlyList<NormalizationCandidateHit> FactHits
    );

    private static NormalizationCandidateHit NeutralHit(NormalizationCandidateHit hit) =>
        hit with
        {
            NameSimilarity = 0,
            Reasons = [],
        };

    private sealed class CandidateEncoder(bool enforceCapacity)
    {
        private long uniqueBytes;
        private readonly CandidateBlockTable<PersonCandidateFacts> facts = new();
        private readonly CandidateBlockTable<
            IReadOnlyList<NormalizationSubjectObservation>
        > owners = new();

        public IReadOnlyList<PersonCandidateFacts> Facts => facts.Values;
        public IReadOnlyList<IReadOnlyList<NormalizationSubjectObservation>> OwnerBlocks =>
            owners.Values;

        public StoredPool Pool(NormalizationCandidateFactsSnapshot<PersonCandidateFacts> value)
        {
            Action<int>? reserve = enforceCapacity ? ReserveUniqueBytes : null;
            var factRefs = value
                .Facts.Select(fact =>
                    facts.Add(fact with { Hit = NeutralHit(fact.Hit) }, fact, reserve)
                )
                .ToArray();
            var ownerRef = owners.Add(
                value.ReadSet.OwnerObservations,
                value.ReadSet.OwnerObservations,
                reserve
            );
            return new(
                value with
                {
                    Facts = [],
                    ReadSet = value.ReadSet with { OwnerObservations = [] },
                },
                ownerRef,
                factRefs,
                value.Facts.Select(fact => fact.Hit).ToArray()
            );
        }

        private void ReserveUniqueBytes(int count)
        {
            // Unique blocks alone cannot exceed a complete bounded envelope. Refuse before
            // retaining more than that budget; equality intentionally does not call this gate.
            if (uniqueBytes + count > MaximumCheckpointBytes)
            {
                throw new SourceActorResolutionCapacityException("checkpoint_capacity_exceeded");
            }
            uniqueBytes += count;
        }
    }

    private sealed class CandidateDecoder(
        IReadOnlyList<PersonCandidateFacts> facts,
        IReadOnlyList<IReadOnlyList<NormalizationSubjectObservation>> owners
    )
    {
        private readonly bool[] usedFacts = new bool[facts.Count];
        private readonly bool[] usedOwners = new bool[owners.Count];

        public NormalizationCandidateFactsSnapshot<PersonCandidateFacts> Pool(StoredPool stored)
        {
            RequirePoolShape(stored);
            var ownerList = At(owners, stored.OwnerBlockRef);
            if (ownerList.Any(owner => owner == null))
            {
                throw Invalid("Source actor candidate owner blocks require complete observations.");
            }
            usedOwners[stored.OwnerBlockRef] = true;
            var restored = new PersonCandidateFacts[stored.FactRefs.Count];
            for (var index = 0; index < restored.Length; index++)
            {
                var reference = stored.FactRefs[index];
                var body = At(facts, reference);
                var hit = stored.FactHits[index];
                if (
                    body.Hit == null
                    || body.Header == null
                    || hit == null
                    || hit.Reasons == null
                    || body.Hit.NameSimilarity != 0
                    || body.Hit.Reasons == null
                    || body.Hit.Reasons.Count != 0
                    || !Same(NeutralHit(hit), body.Hit)
                    || hit.Uuid != body.Header.Uuid
                    || hit.Revision != body.Header.Revision
                    || hit.DisplayText != body.Header.DisplayText
                    || hit.OwnerSubjectUuid != body.Header.OwnerSubjectUuid
                )
                {
                    throw Invalid(
                        "Source actor candidate hit differs from its complete shared fact body."
                    );
                }
                usedFacts[reference] = true;
                restored[index] = body with { Hit = hit };
            }
            return stored.Value with
            {
                Facts = restored,
                ReadSet = stored.Value.ReadSet with { OwnerObservations = ownerList },
            };
        }

        public void RequireAllReferenced()
        {
            if (usedFacts.Any(used => !used) || usedOwners.Any(used => !used))
            {
                throw Invalid("Unreferenced source actor candidate fact or owner block.");
            }
        }

        internal static void RequirePoolShape(StoredPool stored)
        {
            if (
                stored == null
                || stored.Value == null
                || stored.Value.Selection == null
                || stored.Value.Issues == null
                || stored.Value.ReadSet == null
                || stored.Value.Facts == null
                || stored.Value.Facts.Count != 0
                || stored.Value.ReadSet.OwnerObservations == null
                || stored.Value.ReadSet.OwnerObservations.Count != 0
                || stored.FactRefs == null
                || stored.FactHits == null
                || stored.FactRefs.Count != stored.FactHits.Count
            )
            {
                throw Invalid(
                    "Source actor candidate pool contains hidden or inconsistent block data."
                );
            }
        }

        private static T At<T>(IReadOnlyList<T> values, int index)
            where T : class =>
            (uint)index < (uint)values.Count && values[index] != null
                ? values[index]
                : throw Invalid("Invalid source actor candidate block reference.");
    }

    /// <summary>
    /// Content equality preserves independent equal copies without treating a digest or UUID
    /// as identity. Encoded bytes are retained only for unique blocks in this one checkpoint.
    /// </summary>
    private sealed class CandidateBlockTable<T>
        where T : class
    {
        private readonly List<T> values = [];
        private readonly List<byte[]> encoded = [];
        private readonly Dictionary<string, List<int>> hashes = new(StringComparer.Ordinal);
        private readonly Dictionary<object, int> references = new(
            ReferenceEqualityComparer.Instance
        );

        public IReadOnlyList<T> Values => values;

        public int Add(T value, object originalReference, Action<int>? reserveBytes)
        {
            if (references.TryGetValue(originalReference, out var existing))
            {
                return existing;
            }
            // Production writes also bound each encoded block before allocating its full bytes.
            // Semantic equality can compare valid old history without a new format's write cap.
            using MemoryStream stream =
                reserveBytes == null ? new MemoryStream() : new CheckpointStream();
            JsonSerializer.Serialize(stream, value);
            var bytes = stream.ToArray();
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            if (!hashes.TryGetValue(hash, out var indexes))
            {
                indexes = [];
                hashes.Add(hash, indexes);
            }
            foreach (var index in indexes)
            {
                if (bytes.AsSpan().SequenceEqual(encoded[index]))
                {
                    references.Add(originalReference, index);
                    return index;
                }
            }
            reserveBytes?.Invoke(bytes.Length);
            var added = values.Count;
            values.Add(value);
            encoded.Add(bytes);
            indexes.Add(added);
            references.Add(originalReference, added);
            return added;
        }
    }
}
