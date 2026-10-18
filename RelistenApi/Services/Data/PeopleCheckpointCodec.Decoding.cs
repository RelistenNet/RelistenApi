using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

internal static partial class PeopleCheckpointCodec
{
    // Hydrate each shared block once, validate every reference, then return the existing
    // public values. Decoding never reads current DB state or substitutes owner clocks.
    private sealed class Decoder
    {
        private readonly IReadOnlyList<IReadOnlyList<CandidateActorEvidenceFact>> actorEvidence;
        private readonly IReadOnlyList<NormalizationSubjectObservation> observations;
        private readonly IReadOnlyList<IReadOnlyList<int>> observationLists;
        private readonly IReadOnlyList<StoredSnapshot> snapshots;
        private readonly IReadOnlyList<CandidateActorEvidenceFact>?[] decodedEvidence;
        private readonly IReadOnlyList<NormalizationSubjectObservation>?[] decodedLists;
        private readonly NormalizationCandidateFactsSnapshot<PersonCandidateFacts>?[] decodedSnapshots;
        private readonly bool[] usedObservations;

        public Decoder(
            IReadOnlyList<IReadOnlyList<CandidateActorEvidenceFact>> actorEvidence,
            IReadOnlyList<NormalizationSubjectObservation> observations,
            IReadOnlyList<IReadOnlyList<int>> observationLists,
            IReadOnlyList<StoredSnapshot> snapshots
        )
        {
            this.actorEvidence = actorEvidence ?? throw Invalid("Missing actor evidence table.");
            this.observations = observations ?? throw Invalid("Missing owner observation table.");
            this.observationLists = observationLists ?? throw Invalid("Missing owner list table.");
            this.snapshots = snapshots ?? throw Invalid("Missing candidate snapshot table.");
            decodedEvidence = new IReadOnlyList<CandidateActorEvidenceFact>?[actorEvidence.Count];
            decodedLists = new IReadOnlyList<NormalizationSubjectObservation>?[
                observationLists.Count
            ];
            decodedSnapshots = new NormalizationCandidateFactsSnapshot<PersonCandidateFacts>?[
                snapshots.Count
            ];
            usedObservations = new bool[observations.Count];
        }

        public NormalizationPeopleResolutionPreparation Preparation(StoredPreparation value)
        {
            Require(
                value != null && value.CreditContext != null && value.CreditContext.Value != null,
                "Missing people preparation or credit context."
            );
            Require(
                value!.CreditContext.Value.IdentityOwnerObservations is { Count: 0 },
                "Credit context contains inline owner observations."
            );
            Require(
                value.Inputs != null && value.ExpectedContributions != null,
                "Missing preparation inputs or contribution revisions."
            );
            return new(
                value.ExtractionObservation,
                value.CreditContext.Value with
                {
                    IdentityOwnerObservations = ObservationList(
                        value.CreditContext.OwnerObservationListRef
                    ),
                },
                value.ExpectedConfigurationGeneration,
                value.ExpectedContributions,
                Array.AsReadOnly(value.Inputs!.Select(Input).ToArray())
            );
        }

        public NormalizationPeopleResolutionBatch Batch(StoredBatch value)
        {
            Require(
                value != null && value.Preparation != null && value.Ticket != null,
                "Missing people batch preparation or ticket."
            );
            return new(
                value!.AttemptUuid,
                value.SourceUuid,
                value.ExtractionAttemptUuid,
                value.ProcessingVersion,
                Preparation(value.Preparation),
                Ticket(value.Ticket)
            );
        }

        public PeopleAcceptancePlan Plan(StoredPlan value)
        {
            Require(
                value != null && value.Proposal != null && value.Decisions != null,
                "Missing people acceptance plan fields."
            );
            return new(
                Batch(value!.Proposal),
                value.Policy,
                value.Decisions,
                value.PolicyState,
                value.NegativeReason
            );
        }

        public PeopleAcceptanceStage Stage(StoredStage value)
        {
            Require(
                value != null
                    && value.Facts != null
                    && value.Decisions != null
                    && value.Contributions != null
                    && value.ExpectedContributions != null,
                "Missing people acceptance stage fields."
            );
            return new(
                value!.WorkUuid,
                value.PersonUuid,
                value.Policy,
                Preparation(value.Facts),
                value.Decisions,
                Array.AsReadOnly(value.Contributions.Select(Contribution).ToArray()),
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
        }

        public void RequireAllReferenced()
        {
            if (
                decodedEvidence.Any(value => value == null)
                || decodedLists.Any(value => value == null)
                || decodedSnapshots.Any(value => value == null)
                || usedObservations.Any(value => !value)
            )
            {
                throw Invalid("People checkpoint contains unreferenced table entries.");
            }
        }

        private NormalizationPeopleResolutionInput Input(StoredInput value)
        {
            Require(
                value != null
                    && value.OriginalInput != null
                    && value.Claims != null
                    && value.UnresolvedCredits != null
                    && value.DecodeIssues != null,
                "Missing people input fields."
            );
            return new(
                value!.OriginalInput,
                value.ExpectedContributionRevision,
                Array.AsReadOnly(value.Claims.Select(Claim).ToArray()),
                value.UnresolvedCredits,
                value.DecodeIssues
            );
        }

        private NormalizationPeopleResolutionClaim Claim(StoredClaim value)
        {
            Require(
                value != null && value.Value != null && value.Value.Candidates == null,
                "People claim contains inline candidates or is missing its value."
            );
            return value!.Value with
            {
                Candidates = value.CandidateSnapshotRef is { } index ? Snapshot(index) : null,
            };
        }

        private NormalizationCandidateFactsSnapshot<PersonCandidateFacts> Snapshot(int index)
        {
            var stored = At(snapshots, index, "candidate snapshot");
            if (decodedSnapshots[index] is { } saved)
            {
                return saved;
            }
            Require(
                stored != null
                    && stored.Value != null
                    && stored.Value.Facts != null
                    && stored.ActorEvidenceRefs != null
                    && stored.Value.ReadSet != null,
                "Missing candidate snapshot fields."
            );
            var value = stored!.Value;
            Require(
                value.ReadSet.OwnerObservations is { Count: 0 }
                    && stored.ActorEvidenceRefs.Count == value.Facts.Count,
                "Candidate snapshot has inline observations or mismatched evidence references."
            );
            var facts = value
                .Facts.Select(
                    (fact, position) =>
                    {
                        Require(
                            fact != null && fact.ActorEvidence == null,
                            "Candidate facts contain inline actor evidence."
                        );
                        return fact! with
                        {
                            ActorEvidence = stored.ActorEvidenceRefs[position] is { } evidenceRef
                                ? ActorEvidence(evidenceRef)
                                : null,
                        };
                    }
                )
                .ToArray();
            var result = value with
            {
                Facts = Array.AsReadOnly(facts),
                ReadSet = value.ReadSet with
                {
                    OwnerObservations = ObservationList(stored.OwnerObservationListRef),
                },
            };
            decodedSnapshots[index] = result;
            return result;
        }

        private IReadOnlyList<CandidateActorEvidenceFact> ActorEvidence(int index)
        {
            var value = At(actorEvidence, index, "actor evidence");
            if (decodedEvidence[index] is { } saved)
            {
                return saved;
            }
            Require(
                value != null && value.All(fact => fact != null),
                "Missing actor evidence block or fact."
            );
            var result = Array.AsReadOnly(value!.ToArray());
            decodedEvidence[index] = result;
            return result;
        }

        private IReadOnlyList<NormalizationSubjectObservation> ObservationList(int index)
        {
            var indexes = At(observationLists, index, "owner observation list");
            if (decodedLists[index] is { } saved)
            {
                return saved;
            }
            Require(indexes != null, "Missing owner observation list.");
            var result = Array.AsReadOnly(
                indexes!
                    .Select(ownerIndex =>
                    {
                        var observation = At(observations, ownerIndex, "owner observation");
                        Require(observation != null, "Missing original owner observation.");
                        usedObservations[ownerIndex] = true;
                        return observation;
                    })
                    .ToArray()
            );
            decodedLists[index] = result;
            return result;
        }

        private NormalizationPreparedInputContribution Contribution(StoredContribution value)
        {
            Require(
                value != null
                    && value.Value != null
                    && value.Value.ExpectedOwnerObservations == null,
                "Contribution contains inline original observations or is missing its value."
            );
            return value!.Value with
            {
                ExpectedOwnerObservations = value.OwnerObservationListRef is { } index
                    ? ObservationList(index)
                    : null,
            };
        }

        private NormalizationWorkTicket Ticket(StoredTicket value)
        {
            Require(
                value != null && value.Value != null && value.Value.OwnerObservations == null,
                "Ticket contains inline original observations or is missing its value."
            );
            return value!.Value with
            {
                OwnerObservations = value.OwnerObservationListRef is { } index
                    ? ObservationList(index)
                    : null,
            };
        }

        private static T At<T>(IReadOnlyList<T> values, int index, string name)
        {
            if (index < 0 || index >= values.Count)
            {
                throw Invalid($"Invalid {name} reference.");
            }
            return values[index];
        }

        private static void Require([DoesNotReturnIf(false)] bool condition, string message)
        {
            if (!condition)
            {
                throw Invalid(message);
            }
        }
    }
}
