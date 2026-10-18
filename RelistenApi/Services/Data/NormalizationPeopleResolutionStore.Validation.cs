using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationPeopleResolutionStore
{
    public const int MaximumClaims = 1000;

    private static async Task ValidatePreparation(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        NormalizationPeopleResolutionAttempt attempt,
        NormalizationPeopleResolutionPreparation preparation,
        CancellationToken ct
    )
    {
        var json = await c.QuerySingleAsync<string>(
            new CommandDefinition(
                """
                SELECT batch::text FROM normalization.recording_processing_attempts
                WHERE attempt_uuid=@ExtractionAttemptUuid AND disposition='published'
                """,
                attempt,
                t,
                cancellationToken: ct
            )
        );
        var extraction = JsonSerializer.Deserialize<NormalizationRecordingProposalBatch>(json)!;
        var original = NormalizationRecordingReadSet.Inputs(extraction.Preparation);
        ValidatePreparationHeader(attempt, preparation, extraction, original.Length);
        var decisions = new NormalizationPeopleResolutionDecisionService(new());
        foreach (var input in preparation.Inputs)
        {
            ct.ThrowIfCancellationRequested();
            ValidateInput(input, preparation, extraction, original, decisions);
        }
    }

    private static void ValidatePreparationHeader(
        NormalizationPeopleResolutionAttempt attempt,
        NormalizationPeopleResolutionPreparation preparation,
        NormalizationRecordingProposalBatch extraction,
        int inputCount
    )
    {
        var stamp = new NormalizationSubjectObservation(
            extraction.Ticket.SubjectUuid,
            extraction.Ticket.DesiredGeneration,
            extraction.Ticket.EditorialRevision,
            extraction.AttemptUuid
        );
        if (
            preparation.ExtractionObservation != stamp
            || preparation.ExpectedConfigurationGeneration is < 0
            || preparation.Inputs.Count != inputCount
            || preparation
                .Inputs.Select(input => input.OriginalInput.Contributor)
                .Distinct()
                .Count() != inputCount
            || preparation.ExpectedContributions.Any(value => value.Revision < 0)
            || preparation
                .ExpectedContributions.Select(value => value.Contributor)
                .Distinct()
                .Count() != preparation.ExpectedContributions.Count
            || preparation.Inputs.Sum(input => (long)input.Claims.Count) > MaximumClaims
            || preparation.CreditContext.SourceUuid != attempt.SourceUuid
            || preparation.CreditContext.PeopleUniverse.Universe
                != NormalizationCandidateUniverseKey.People
            || preparation.CreditContext.PeopleUniverse.Generation < 0
        )
        {
            throw new ArgumentException(
                "People resolution requires the complete extraction, membership and original people scope."
            );
        }
    }

    private static void ValidateInput(
        NormalizationPeopleResolutionInput input,
        NormalizationPeopleResolutionPreparation preparation,
        NormalizationRecordingProposalBatch extraction,
        NormalizationRecordingPreparedInput[] original,
        NormalizationPeopleResolutionDecisionService decisions
    )
    {
        var saved = original.SingleOrDefault(value =>
            value.Contributor == input.OriginalInput.Contributor
        );
        var membership = preparation.ExpectedContributions.SingleOrDefault(value =>
            value.Contributor == input.OriginalInput.Contributor
        );
        if (
            saved == null
            || !Equal(saved, input.OriginalInput)
            || input.ExpectedContributionRevision != (membership?.Revision ?? 0)
        )
        {
            throw new ArgumentException(
                "Resolution inputs must retain their original extraction binding and contribution revision."
            );
        }
        var group =
            saved.Binding.InputRevisionUuid == extraction.Preparation.Binding.InputRevisionUuid
                ? extraction.Extraction
                : extraction
                    .AdditionalExtractions!.Single(value =>
                        value.InputRevisionUuid == saved.Binding.InputRevisionUuid
                    )
                    .Extraction;
        if (
            input.Claims.Count != group.Credits.Count
            || input.Claims.Select(claim => claim.Proposal.ProposalUuid).Distinct().Count()
                != input.Claims.Count
            || !Equal(input.UnresolvedCredits, group.UnresolvedCredits)
            || !Equal(input.DecodeIssues, group.DecodeIssues)
        )
        {
            throw new ArgumentException(
                "Resolution must preserve every original credit, unresolved passage and decoder issue."
            );
        }
        foreach (var claim in input.Claims)
        {
            var proposal = group.Credits.SingleOrDefault(value =>
                value.ProposalUuid == claim.Proposal.ProposalUuid
            );
            if (proposal == null || !Equal(proposal, claim.Proposal))
            {
                throw new ArgumentException(
                    "Resolution credits must retain the original extraction proposal."
                );
            }
            ValidateCandidateQuery(claim.Candidates, proposal);
            // Reproduce only this version's deterministic comparison proposal. Caller-supplied
            // policy objects, selected identities and automatic authority cannot enter storage.
            if (
                !PeopleCheckpointCodec.EquivalentClaim(
                    claim,
                    decisions.Compare(
                        proposal,
                        saved.Binding,
                        claim.Candidates,
                        preparation.CreditContext
                    )
                )
            )
            {
                throw new ArgumentException(
                    "People resolution decisions must reproduce the policy-free baseline and checked file scope."
                );
            }
        }
    }

    private static void ValidateCandidateQuery(
        NormalizationCandidateFactsSnapshot<PersonCandidateFacts>? snapshot,
        NormalizationRecordingCreditProposal proposal
    )
    {
        var projection = PeopleActorProjection.Project(proposal.Mention, Guid.Empty);
        var querySupported = projection.Query != null;
        if (snapshot == null)
        {
            if (querySupported)
            {
                throw new ArgumentException(
                    "A supported credit name requires its candidate query result."
                );
            }
            return;
        }
        if (
            !querySupported
            || snapshot.Version != NormalizationCandidateService.PeopleFactsVersion
            || snapshot.Query.Domain != IdentityMatchingDomain.People
            || snapshot.Query.ArtistUuid != null
            || !Equal(snapshot.Query, projection.Query)
            || snapshot.ReadSet.CandidateUniverses.Count != 1
            || snapshot.ReadSet.CandidateUniverses[0].Universe
                != NormalizationCandidateUniverseKey.People
            || snapshot.ReadSet.CandidateUniverses[0].Generation < 0
            || snapshot.Facts.Select(fact => fact.Hit.Uuid).Distinct().Count()
                != snapshot.Facts.Count
            || snapshot.Facts.Any(fact =>
                fact.ActorMatchingFacts == null
                || fact.AcceptedAliasMatchingNames == null
                || fact.AliasMatchingNamesComplete == null
            )
            || snapshot.Complete
                && snapshot.Facts.Any(fact => fact.AliasMatchingNamesComplete != true)
            || snapshot.Complete
                && !snapshot
                    .Facts.Select(fact => fact.Hit.Uuid)
                    .ToHashSet()
                    .SetEquals(snapshot.Selection.Items.Select(hit => hit.Uuid))
        )
        {
            throw new ArgumentException(
                "People candidate proposals must retain their original complete typed query result."
            );
        }
    }

    private static bool Equal<T>(T first, T second) =>
        JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(first),
            JsonSerializer.SerializeToElement(second)
        );
}
