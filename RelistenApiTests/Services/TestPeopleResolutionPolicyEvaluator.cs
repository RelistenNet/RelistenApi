using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;

namespace RelistenApiTests.Services;

/// <summary>Whole-pool decisions remain separate from policy authority and accepted publication.</summary>
[TestFixture]
public sealed class TestPeopleResolutionPolicyEvaluator
{
    private readonly PeopleResolutionPolicyEvaluator evaluator = new();

    [Test]
    public void PriorPeopleFactsAttestationCannotAuthorizeTheCurrentAggregateRecipe()
    {
        var policy = Policy();
        policy = policy with
        {
            Artifact = policy.Artifact with
            {
                CandidateFactsVersion = NormalizationCandidateService.FactsVersion,
            },
        };

        var result = Evaluate(
            Claim("Dave Sorochty", Pair(1, exact: true, sharedArtist: true)),
            policy
        );

        result.Action.Should().Be(PeopleResolutionAction.Unresolved);
        result.Reason.Should().Be("unsupported_policy_applicability");
    }

    [Test]
    public void CandidateCannotReplaceItsCompleteActorAggregateWithRawWitnesses()
    {
        var claim = Claim("Dave Sorochty", Pair(1, exact: true, sharedArtist: true));
        claim = claim with
        {
            Candidates = claim.Candidates! with
            {
                Facts = claim
                    .Candidates.Facts.Select(fact => fact with { ActorMatchingFacts = null })
                    .ToArray(),
            },
        };

        var result = Evaluate(claim);

        result.Action.Should().Be(PeopleResolutionAction.Unresolved);
        result.Reason.Should().Be("inconsistent_candidate_projection");
    }

    [TestCase("names_absent")]
    [TestCase("completeness_absent")]
    [TestCase("incomplete_names")]
    public void V5AcceptanceRequiresTheCompleteAliasAggregate(string missingPart)
    {
        var claim = Claim("Fixture Person", Pair(1, exact: true, sharedArtist: true));
        claim = claim with
        {
            Candidates = claim.Candidates! with
            {
                Facts = claim
                    .Candidates.Facts.Select(fact =>
                        fact with
                        {
                            AcceptedAliasMatchingNames = missingPart == "names_absent" ? null : [],
                            AliasMatchingNamesComplete = missingPart switch
                            {
                                "completeness_absent" => null,
                                "incomplete_names" => false,
                                _ => true,
                            },
                        }
                    )
                    .ToArray(),
            },
        };

        var result = Evaluate(claim);

        result.Action.Should().Be(PeopleResolutionAction.Unresolved);
        result.Reason.Should().Be("inconsistent_candidate_projection");
    }

    [Test]
    public void ClearWinnerCanMatchDespiteMultipleRetrievedCandidates()
    {
        var winner = Pair(1, exact: true, sharedArtist: true);
        var rival = Pair(1, exact: true);
        var claim = Claim("Dave Sorochty", winner, rival);

        var result = Evaluate(claim);

        result.Action.Should().Be(PeopleResolutionAction.MatchExisting);
        result.PersonUuid.Should().Be(winner.CandidateUuid);
        result.Candidates.Should().HaveCount(2);
        result.RunnerUpMargin.Should().Be(1);
        claim.Decision.AutomaticAcceptanceAuthorized.Should().BeFalse();
    }

    [TestCase(0)]
    [TestCase(0.5)]
    public void EquallyRankedIdentitiesRequireReviewEvenWithZeroConfiguredMargin(
        double minimumMargin
    )
    {
        var policy = Policy();
        policy = policy with
        {
            Artifact = policy.Artifact with
            {
                Scorer = policy.Artifact.Scorer with { MinimumRunnerUpMargin = minimumMargin },
            },
        };

        var result = Evaluate(
            Claim(
                "Dave Sorochty",
                Pair(1, exact: true, sharedArtist: true),
                Pair(1, exact: true, sharedArtist: true)
            ),
            policy
        );

        result.Action.Should().Be(PeopleResolutionAction.Review);
        result.Reason.Should().Be("ambiguous_candidate_margin");
        result.PersonUuid.Should().BeNull();
    }

    [Test]
    public void IrrelevantRejectedRivalDoesNotBlockWinnerButPlausibleContradictionDoes()
    {
        var winner = Pair(1, exact: true, sharedArtist: true);
        var irrelevant = Pair(0.1, contradiction: true);
        var clear = Evaluate(Claim("Dave Sorochty", winner, irrelevant));
        clear.Action.Should().Be(PeopleResolutionAction.MatchExisting);
        clear
            .Candidates.Single(candidate => candidate.PersonUuid == irrelevant.CandidateUuid)
            .RejectionReasons.Should()
            .Contain("contradictory_facts");

        var plausible = Pair(1, exact: true, sharedArtist: true, contradiction: true);
        var uncertain = Evaluate(Claim("Dave Sorochty", winner, plausible));
        uncertain.Action.Should().Be(PeopleResolutionAction.Review);
        uncertain.Reason.Should().Be("relevant_candidate_conflict");
    }

    [Test]
    public void OverflowingScoreCannotBecomeAWinner()
    {
        var policy = Policy();
        policy = policy with
        {
            Artifact = policy.Artifact with
            {
                Scorer = policy.Artifact.Scorer with
                {
                    NameSimilarityWeight = double.MaxValue,
                    ExactNameWeight = double.MaxValue,
                },
            },
        };
        var result = Evaluate(
            Claim("Dave Sorochty", Pair(1, exact: true), Pair(1, exact: true)),
            policy
        );
        result.Action.Should().Be(PeopleResolutionAction.Unresolved);
        result.Reason.Should().Be("non_finite_candidate_score");
    }

    [Test]
    public void EqualScoringContradictionStillNeedsReviewWithZeroMargin()
    {
        var policy = Policy();
        policy = policy with
        {
            Artifact = policy.Artifact with
            {
                Scorer = policy.Artifact.Scorer with { MinimumRunnerUpMargin = 0 },
            },
        };
        var result = Evaluate(
            Claim(
                "Dave Sorochty",
                Pair(1, exact: true, sharedArtist: true),
                Pair(1, exact: true, sharedArtist: true, contradiction: true)
            ),
            policy
        );
        result.Action.Should().Be(PeopleResolutionAction.Review);
        result.Reason.Should().Be("relevant_candidate_conflict");
    }

    [Test]
    public void IncompletePoolCannotMatchOrCreate()
    {
        var claim = Claim("Dave Sorochty", Pair(1, exact: true, sharedArtist: true));
        claim = claim with
        {
            Candidates = claim.Candidates! with
            {
                Issues = [new(CandidateFactsIssueKind.CandidateLimit)],
            },
        };

        var result = Evaluate(claim, Policy(creation: true));

        result.Action.Should().Be(PeopleResolutionAction.Unresolved);
        result.Reason.Should().Be("incomplete_candidate_facts");
    }

    [Test]
    public void ReviewedClaimAndUnboundFileScopeCannotBecomeAutomaticCredits()
    {
        var claim = Claim("Dave Sorochty", Pair(1, exact: true, sharedArtist: true));
        Evaluate(
            claim with
            {
                Decision = new(PeopleResolutionAction.Review, "reviewed_credit_claim"),
            }
        )
            .Action.Should()
            .Be(PeopleResolutionAction.Review);
        Evaluate(claim with { ScopeIssue = "missing_file_binding" })
            .Reason.Should()
            .Be("unbound_file_scope");
    }

    [Test]
    public void UnsupportedInputAndCandidateProjectionStayUnresolved()
    {
        var claim = Claim("Dave Sorochty", Pair(1, exact: true, sharedArtist: true));
        evaluator
            .Evaluate(claim, Policy(), 1, "unfamiliar_format")
            .Reason.Should()
            .Be("unsupported_policy_applicability");
        Evaluate(claim with { Comparisons = [] })
            .Reason.Should()
            .Be("inconsistent_candidate_projection");
    }

    [Test]
    public void EmptyPoolAloneDoesNotAuthorizeCreation()
    {
        var result = Evaluate(Claim("Dave Sorochty"));
        result.Action.Should().Be(PeopleResolutionAction.Review);
        result.Reason.Should().Be("creation_capability_unavailable");
        result.Creation.Should().BeNull();
    }

    [Test]
    public void SeparatelyEvaluatedSingleFullNameCreationRetainsExplicitKindAndCheckpointIds()
    {
        var person = Guid.NewGuid();
        var group = Guid.NewGuid();
        var result = evaluator.Evaluate(
            Claim("Dave Sorochty"),
            Policy(creation: true),
            1,
            "retained_credit",
            person,
            group
        );

        result.Action.Should().Be(PeopleResolutionAction.CreateIdentity);
        result.PersonUuid.Should().Be(person);
        result
            .Creation.Should()
            .Be(new PeopleAcceptanceCreationPlan(person, "person", "Dave Sorochty", group));
    }

    [Test]
    public void VerifiedCreationCorrespondenceCanLinkWithoutAnExistingArtistCredit()
    {
        var created = Pair(1, exact: true);
        var result = evaluator.Evaluate(
            Claim("Dave Sorochty", created),
            Policy(creation: true),
            1,
            "retained_credit",
            createdPersonUuid: created.CandidateUuid
        );

        result.Action.Should().Be(PeopleResolutionAction.MatchExisting);
        result.Reason.Should().Be("evaluated_creation_correspondence");
        result.PersonUuid.Should().Be(created.CandidateUuid);
        result.WinnerScore.Should().BeLessThan(2.5);
        result.Creation.Should().BeNull();
    }

    [Test]
    public void FreshNamesakeBlocksCreationCorrespondenceEvenWithoutArtistOverlap()
    {
        var created = Pair(1, exact: true);
        var namesake = Pair(1, exact: true);
        var result = evaluator.Evaluate(
            Claim("Dave Sorochty", created, namesake),
            Policy(creation: true),
            1,
            "retained_credit",
            createdPersonUuid: created.CandidateUuid
        );

        result.Action.Should().Be(PeopleResolutionAction.Review);
        result.Reason.Should().Be("creation_correspondence_rival");
        result.PersonUuid.Should().BeNull();
        result.Candidates.Should().HaveCount(2);
    }

    [Test]
    public void WeakUnrelatedRivalDoesNotBlockCreationCorrespondence()
    {
        var created = Pair(1, exact: true);
        var unrelated = Pair(0.1);
        var result = evaluator.Evaluate(
            Claim("Dave Sorochty", created, unrelated),
            Policy(creation: true),
            1,
            "retained_credit",
            createdPersonUuid: created.CandidateUuid
        );

        result.Action.Should().Be(PeopleResolutionAction.MatchExisting);
        result.PersonUuid.Should().Be(created.CandidateUuid);
        result.Candidates.Should().HaveCount(2);
    }

    [TestCase("group", "Dave Sorochty")]
    [TestCase("person", "Changed Name")]
    public void ChangedCreatedIdentityKindOrNameRequiresReview(string kind, string name)
    {
        var created = Pair(1, exact: true);
        var claim = Claim("Dave Sorochty", created);
        var fact = claim.Candidates!.Facts.Single();
        claim = claim with
        {
            Candidates = claim.Candidates with
            {
                Facts =
                [
                    fact with
                    {
                        Header = fact.Header with { Kind = kind, DisplayText = name },
                    },
                ],
            },
        };
        var result = evaluator.Evaluate(
            claim,
            Policy(creation: true),
            1,
            "retained_credit",
            createdPersonUuid: created.CandidateUuid
        );

        result.Action.Should().Be(PeopleResolutionAction.Review);
        result.Reason.Should().Be("creation_correspondence_changed");
    }

    [Test]
    public void MissingCreatedIdentityAndReviewedClaimsCannotUseCorrespondence()
    {
        var created = Pair(1, exact: true);
        evaluator
            .Evaluate(
                Claim("Dave Sorochty"),
                Policy(creation: true),
                1,
                "retained_credit",
                createdPersonUuid: created.CandidateUuid
            )
            .Reason.Should()
            .Be("created_identity_missing");
        var reviewed = Claim("Dave Sorochty", created) with
        {
            Decision = new(PeopleResolutionAction.Review, "reviewed_credit_claim"),
        };
        evaluator
            .Evaluate(
                reviewed,
                Policy(creation: true),
                1,
                "retained_credit",
                createdPersonUuid: created.CandidateUuid
            )
            .Reason.Should()
            .Be("reviewed_credit_claim");
    }

    [TestCase("tapehead")]
    [TestCase("D. Sorochty")]
    public void UnsupportedActorNamesOrUnknownGroupingRequireReview(string name)
    {
        var result = evaluator.Evaluate(
            Claim(name),
            Policy(creation: true),
            1,
            "retained_credit",
            Guid.NewGuid(),
            Guid.NewGuid()
        );
        result.Action.Should().NotBe(PeopleResolutionAction.CreateIdentity);
    }

    [Test]
    public void RepeatedNameDoesNotEstablishCreationCoreference()
    {
        var result = evaluator.Evaluate(
            Claim("Dave Sorochty"),
            Policy(creation: true),
            2,
            "retained_credit",
            Guid.NewGuid(),
            Guid.NewGuid()
        );
        result.Action.Should().Be(PeopleResolutionAction.Review);
        result.Reason.Should().Be("creation_coreference_unresolved");
    }

    private PeopleAcceptanceClaimDecision Evaluate(
        NormalizationPeopleResolutionClaim claim,
        RegisteredPeopleAcceptancePolicy? policy = null
    ) => evaluator.Evaluate(claim, policy ?? Policy(), 1, "retained_credit");

    private static IdentityCandidateComparison Pair(
        double similarity,
        bool exact = false,
        bool sharedArtist = false,
        bool contradiction = false
    ) =>
        new(
            Guid.NewGuid(),
            IdentityMatchingDomain.People,
            contradiction
                ? IdentityMatchDisposition.Review
                : IdentityMatchDisposition.NeedsEvidence,
            true,
            similarity,
            [
                new(
                    "name",
                    exact ? IdentityFeatureAgreement.Agrees : IdentityFeatureAgreement.Differs,
                    "fixture"
                ),
                new(
                    "artist_context",
                    sharedArtist
                        ? IdentityFeatureAgreement.Agrees
                        : IdentityFeatureAgreement.Differs,
                    "fixture"
                ),
                new(
                    "person_kind",
                    contradiction
                        ? IdentityFeatureAgreement.Conflicts
                        : IdentityFeatureAgreement.Missing,
                    "fixture"
                ),
            ],
            [],
            null,
            null,
            null
        );

    private static NormalizationPeopleResolutionClaim Claim(
        string name,
        params IdentityCandidateComparison[] pairs
    )
    {
        var hits = pairs
            .Select(pair => new NormalizationCandidateHit(
                pair.CandidateUuid!.Value,
                1,
                name,
                Guid.NewGuid(),
                pair.NameTokenSimilarity,
                ["name"]
            ))
            .ToArray();
        var facts = hits.Select(hit => new PersonCandidateFacts
            {
                Hit = hit,
                Header = new(hit.Uuid, name, 1, hit.OwnerSubjectUuid, "person"),
                ActorMatchingFacts = new(false, [], [], [], [], [], false, false),
                AcceptedAliasMatchingNames = [],
                AliasMatchingNamesComplete = true,
            })
            .ToArray();
        var snapshot = new NormalizationCandidateFactsSnapshot<PersonCandidateFacts>(
            NormalizationCandidateService.PeopleFactsVersion,
            "fixture-query-hash",
            new(IdentityMatchingDomain.People, [name], []),
            new(),
            new(IdentityMatchingDomain.People, hits, false),
            facts,
            new([], [], []),
            []
        );
        return new(
            new(
                Guid.NewGuid(),
                new(name, RecordingCreditRole.Taper, new(Guid.NewGuid(), "/taper", Quote: name))
            ),
            Guid.NewGuid(),
            null,
            null,
            snapshot,
            pairs,
            new(PeopleResolutionAction.Review, "competing_candidates")
        );
    }

    private static RegisteredPeopleAcceptancePolicy Policy(bool creation = false)
    {
        var artifact = new PeopleAcceptancePolicyArtifact(
            Guid.NewGuid(),
            "fixture-v1",
            "",
            PeopleResolutionPolicyEvaluator.ScorerVersion,
            PeopleResolutionPolicyEvaluator.RetrievalVersion,
            NormalizationCandidateService.PeopleFactsVersion,
            NormalizationRecordingExtractor.Version,
            NormalizedPeopleService.CreditContextVersion,
            ["retained_credit"],
            new(0, 1, 1, 1, 2.5, 0.5),
            creation ? new(PeopleResolutionPolicyEvaluator.CreationClassifierVersion, 0.25) : null,
            FixtureOnly: true
        );
        artifact = artifact with
        {
            ContentHash = PeopleResolutionPolicyService.ComputeArtifactHash(artifact),
        };
        var receipt = new PeopleAcceptanceEvaluationReceipt(
            Guid.NewGuid(),
            artifact.Uuid,
            artifact.ContentHash,
            "",
            new string('a', 64),
            new string('b', 64),
            new string('c', 64),
            "fixture-evaluation-v1",
            "independent-fixture-labels",
            "fixture-operator",
            new(20, 0),
            creation ? new(10, 0) : null,
            creation ? new(10, 0) : null,
            FixtureOnly: true
        );
        receipt = receipt with
        {
            ReceiptHash = PeopleResolutionPolicyService.ComputeEvaluationHash(receipt),
        };
        return new(artifact, receipt);
    }
}
