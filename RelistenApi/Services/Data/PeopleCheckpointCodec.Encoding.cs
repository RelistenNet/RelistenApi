using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

internal static partial class PeopleCheckpointCodec
{
    // One encoder owns one immutable checkpoint. Sharing cannot cross a later stage
    // that deliberately rereads derived facts with different original observations.
    private sealed class Encoder
    {
        private readonly ValuePool<IReadOnlyList<CandidateActorEvidenceFact>> actorEvidence;
        private readonly ValuePool<NormalizationSubjectObservation> observations;
        private readonly ValuePool<IReadOnlyList<int>> observationLists;
        private readonly ValuePool<StoredSnapshot> snapshots;
        private readonly Dictionary<
            NormalizationCandidateFactsSnapshot<PersonCandidateFacts>,
            int
        > snapshotReferences = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<
            IReadOnlyList<NormalizationSubjectObservation>,
            int
        > observationListReferences = new(ReferenceEqualityComparer.Instance);

        public Encoder(bool enforceWriteBudget)
        {
            var budget = enforceWriteBudget ? new PoolWriteBudget() : null;
            actorEvidence = new(budget);
            observations = new(budget);
            observationLists = new(budget);
            snapshots = new(budget);
        }

        public Envelope<T> Wrap<T>(string kind, T value) =>
            new(
                FormatVersion,
                kind,
                value,
                actorEvidence.Values,
                observations.Values,
                observationLists.Values,
                snapshots.Values
            );

        public StoredPreparation Preparation(NormalizationPeopleResolutionPreparation value) =>
            new(
                value.ExtractionObservation,
                new(
                    value.CreditContext with
                    {
                        IdentityOwnerObservations = [],
                    },
                    ObservationList(value.CreditContext.IdentityOwnerObservations)
                ),
                value.ExpectedConfigurationGeneration,
                value.ExpectedContributions,
                value
                    .Inputs.Select(input => new StoredInput(
                        input.OriginalInput,
                        input.ExpectedContributionRevision,
                        input.Claims.Select(Claim).ToArray(),
                        input.UnresolvedCredits,
                        input.DecodeIssues
                    ))
                    .ToArray()
            );

        public StoredClaim Claim(NormalizationPeopleResolutionClaim value) =>
            new(
                value with
                {
                    Candidates = null,
                },
                value.Candidates == null ? null : Snapshot(value.Candidates)
            );

        public StoredBatch Batch(NormalizationPeopleResolutionBatch value) =>
            new(
                value.AttemptUuid,
                value.SourceUuid,
                value.ExtractionAttemptUuid,
                value.ProcessingVersion,
                Preparation(value.Preparation),
                Ticket(value.Ticket)
            );

        public StoredPlan Plan(PeopleAcceptancePlan value) =>
            new(
                Batch(value.Proposal),
                value.Policy,
                value.Decisions,
                value.PolicyState,
                value.NegativeReason
            );

        public StoredStage Stage(PeopleAcceptanceStage value) =>
            new(
                value.WorkUuid,
                value.PersonUuid,
                value.Policy,
                Preparation(value.Facts),
                value.Decisions,
                value.Contributions.Select(Contribution).ToArray(),
                value.ExpectedConfigurationGeneration,
                value.ExpectedContributions,
                value.Identity,
                value.Reconciliation,
                value.Ticket == null ? null : Ticket(value.Ticket),
                value.Receipt,
                value.ScopeCoverage,
                value.PolicyState,
                value.DecisionOnly,
                value.PeerTicket == null ? null : Ticket(value.PeerTicket)
            );

        private int Snapshot(NormalizationCandidateFactsSnapshot<PersonCandidateFacts> value)
        {
            if (snapshotReferences.TryGetValue(value, out var existing))
            {
                return existing;
            }
            var evidenceRefs = value
                .Facts.Select(fact =>
                    fact.ActorEvidence == null ? (int?)null : actorEvidence.Add(fact.ActorEvidence)
                )
                .ToArray();
            var skeleton = value with
            {
                Facts = value.Facts.Select(fact => fact with { ActorEvidence = null }).ToArray(),
                ReadSet = value.ReadSet with { OwnerObservations = [] },
            };
            var index = snapshots.Add(
                new(skeleton, ObservationList(value.ReadSet.OwnerObservations), evidenceRefs)
            );
            snapshotReferences.Add(value, index);
            return index;
        }

        private StoredContribution Contribution(NormalizationPreparedInputContribution value) =>
            new(
                value with
                {
                    ExpectedOwnerObservations = null,
                },
                value.ExpectedOwnerObservations == null
                    ? null
                    : ObservationList(value.ExpectedOwnerObservations)
            );

        private StoredTicket Ticket(NormalizationWorkTicket value) =>
            new(
                value with
                {
                    OwnerObservations = null,
                },
                value.OwnerObservations == null ? null : ObservationList(value.OwnerObservations)
            );

        private int ObservationList(IReadOnlyList<NormalizationSubjectObservation> value)
        {
            if (observationListReferences.TryGetValue(value, out var existing))
            {
                return existing;
            }
            var index = observationLists.Add(value.Select(observations.Add).ToArray());
            observationListReferences.Add(value, index);
            return index;
        }
    }

    // The pooled values are literal parts of the final envelope. Their total byte count is
    // a lower bound on its size, so reject unique growth before retaining unbounded caches.
    // Final serialization still enforces the exact root budget, including all descriptors.
    private sealed class PoolWriteBudget
    {
        private long retainedBytes;

        public void AddUnique(int bytes)
        {
            if (retainedBytes + bytes > MaximumCheckpointBytes)
            {
                throw new ArgumentException(
                    "People checkpoint unique facts exceed 32 MiB; no evidence or observations were truncated."
                );
            }
            retainedBytes += bytes;
        }
    }

    // Digests accelerate lookup only. Captured-byte equality decides sharing, including
    // every support/revision, original stamp and list order. UUIDs and QueryHash cannot do so.
    private sealed class ValuePool<T>(PoolWriteBudget? budget)
        where T : class
    {
        // Checkpoints are immutable for this operation. Repeated references need only one
        // serialization; distinct objects still require complete captured-byte equality.
        private readonly Dictionary<T, int> references = new(ReferenceEqualityComparer.Instance);
        private readonly List<T> values = [];
        private readonly List<byte[]> bytes = [];
        private readonly Dictionary<string, List<int>> hashes = new(StringComparer.Ordinal);

        public IReadOnlyList<T> Values => values;

        public int Add(T value)
        {
            if (references.TryGetValue(value, out var existing))
            {
                return existing;
            }
            var serialized =
                budget == null
                    ? JsonSerializer.SerializeToUtf8Bytes(value)
                    : SerializeBoundedBytes(value);
            var digest = Convert.ToHexStringLower(SHA256.HashData(serialized));
            if (hashes.TryGetValue(digest, out var candidates))
            {
                foreach (var index in candidates)
                {
                    if (serialized.AsSpan().SequenceEqual(bytes[index]))
                    {
                        references.Add(value, index);
                        return index;
                    }
                }
            }
            else
            {
                candidates = [];
                hashes.Add(digest, candidates);
            }
            budget?.AddUnique(serialized.Length);
            var next = values.Count;
            values.Add(value);
            bytes.Add(serialized);
            candidates.Add(next);
            references.Add(value, next);
            return next;
        }
    }
}
