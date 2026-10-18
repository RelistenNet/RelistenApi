using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;

namespace RelistenApiTests.Services;

/// <summary>
/// Pure proposal evaluation over synthetic original text and actual matcher comparisons.
/// These fixtures supply no production evaluation evidence or accepted identity authority.
/// </summary>
[TestFixture]
public sealed class TestSourceActorAliasPolicyEvaluator
{
    private readonly PeopleResolutionPolicyEvaluator evaluator = new();
    private static readonly Guid Artist = Guid.Parse("88888888-8888-4888-8888-888888888888");

    [TestCase("person")]
    [TestCase("group")]
    public void ExplicitAliasCanMatchAnExistingPersonOrGroupWithoutInventingActorKind(string kind)
    {
        var input = Input(Candidate("Alice Vale", kind));
        var projection = PeopleActorProjection.Project(input.Assertion, Artist);

        var result = evaluator.EvaluateSourceActorAlias(input, Policy());

        result.Action.Should().Be(SourceActorAliasPolicyAction.MatchExisting);
        result.AssertionUuid.Should().Be(input.Assertion.AssertionUuid);
        result.PersonUuid.Should().Be(input.Candidates.Facts.Single().Hit.Uuid);
        result.WinnerScore.Should().Be(3);
        projection.Record.Kind.Should().BeNull();
        projection.Record.ActorContext.Should().BeNull();
        projection.Record.ExternalIdentities.Should().BeEmpty();
        input
            .Assertion.Relationships.Should()
            .OnlyContain(relationship => relationship.Roles.Count == 0);
        input
            .Comparisons.Single()
            .Features.Single(feature => feature.Name == "person_kind")
            .Agreement.Should()
            .Be(IdentityFeatureAgreement.Missing);
    }

    [Test]
    public void ExplicitAliasParticipatesInBothRetrievalAndActualPairComparison()
    {
        var input = Input(Candidate("Different canonical heading", aliases: ["Lark"]));

        var result = evaluator.EvaluateSourceActorAlias(input, Policy());

        input.Candidates.Query.Names.Should().Equal("Alice Vale", "Lark");
        input.Candidates.Query.PeopleHints.Should().BeNull();
        input.Candidates.Query.ArtistUuid.Should().BeNull();
        input
            .Comparisons.Single()
            .Features.Single(feature => feature.Name == "name")
            .Agreement.Should()
            .Be(IdentityFeatureAgreement.Agrees);
        result.Action.Should().Be(SourceActorAliasPolicyAction.MatchExisting);
        result.WinnerScore.Should().Be(3);
    }

    [Test]
    public void SourceInputCannotCreateAnIdentityEvenWhenTheArtifactAlsoEvaluatedCreditCreation()
    {
        var input = Input();
        var policy = Policy(creation: true);

        var result = evaluator.EvaluateSourceActorAlias(input, policy);

        policy.Artifact.Creation.Should().NotBeNull();
        result.Action.Should().Be(SourceActorAliasPolicyAction.Review);
        result.Reason.Should().Be("source_identity_unresolved");
        result.PersonUuid.Should().BeNull();
        result.Candidates.Should().BeEmpty();
    }

    [Test]
    public void EquallyRankedNamesakesRequireReviewDespiteCompleteSourceAliasEvidence()
    {
        var input = Input(Candidate("Alice Vale"), Candidate("Alice Vale"));

        var result = evaluator.EvaluateSourceActorAlias(input, Policy());

        result.Action.Should().Be(SourceActorAliasPolicyAction.Review);
        result.Reason.Should().Be("ambiguous_candidate_margin");
        result.PersonUuid.Should().BeNull();
        result.Candidates.Should().HaveCount(2);
    }

    [TestCase("name_pool")]
    [TestCase("context_pool")]
    [TestCase("blocking_issue")]
    [TestCase("missing_alias_aggregate")]
    [TestCase("incomplete_alias_aggregate")]
    [TestCase("missing_actor_aggregate")]
    [TestCase("old_facts")]
    public void IncompletePoolsOrSemanticAggregatesCannotProduceASourceWinner(string fault)
    {
        var input = Input(Candidate("Alice Vale"));
        var snapshot = input.Candidates;
        var fact = snapshot.Facts.Single();
        snapshot = fault switch
        {
            "name_pool" => snapshot with
            {
                Selection = snapshot.Selection with { NameCandidatesTruncated = true },
            },
            "context_pool" => snapshot with
            {
                Selection = snapshot.Selection with { ContextCandidatesTruncated = true },
            },
            "blocking_issue" => snapshot with
            {
                Issues = [new(CandidateFactsIssueKind.EvidenceLimit)],
            },
            "missing_alias_aggregate" => snapshot with
            {
                Facts = [fact with { AcceptedAliasMatchingNames = null }],
            },
            "incomplete_alias_aggregate" => snapshot with
            {
                Facts = [fact with { AliasMatchingNamesComplete = false }],
            },
            "missing_actor_aggregate" => snapshot with
            {
                Facts = [fact with { ActorMatchingFacts = null }],
            },
            "old_facts" => snapshot with { Version = NormalizationCandidateService.FactsVersion },
            _ => throw new AssertionException("Unknown fixture fault"),
        };

        var result = evaluator.EvaluateSourceActorAlias(
            input with
            {
                Candidates = snapshot,
            },
            Policy()
        );

        result.Action.Should().Be(SourceActorAliasPolicyAction.Unresolved);
        result.PersonUuid.Should().BeNull();
    }

    [TestCase("missing")]
    [TestCase("duplicate")]
    [TestCase("foreign_candidate")]
    [TestCase("foreign_domain")]
    [TestCase("null_candidate")]
    [TestCase("changed_similarity")]
    [TestCase("changed_features")]
    public void EveryComparisonMustBeTheActualCompleteMatcherInventory(string fault)
    {
        var input = Input(
            Candidate("Alice Vale"),
            Candidate("Unrelated identity", sharedArtist: false)
        );
        var pair = input.Comparisons[0];
        IReadOnlyList<IdentityCandidateComparison> comparisons = fault switch
        {
            "missing" => [pair],
            "duplicate" => [pair, pair],
            "foreign_candidate" =>
            [
                pair with
                {
                    CandidateUuid = Guid.NewGuid(),
                },
                input.Comparisons[1],
            ],
            "foreign_domain" =>
            [
                pair with
                {
                    Domain = IdentityMatchingDomain.Compositions,
                },
                input.Comparisons[1],
            ],
            "null_candidate" => [pair with { CandidateUuid = null }, input.Comparisons[1]],
            "changed_similarity" =>
            [
                pair with
                {
                    NameTokenSimilarity = .123,
                },
                input.Comparisons[1],
            ],
            "changed_features" => [pair with { Features = [] }, input.Comparisons[1]],
            _ => throw new AssertionException("Unknown fixture fault"),
        };

        var result = evaluator.EvaluateSourceActorAlias(
            input with
            {
                Comparisons = comparisons,
            },
            Policy()
        );

        result.Action.Should().Be(SourceActorAliasPolicyAction.Unresolved);
        result.PersonUuid.Should().BeNull();
    }

    [TestCase("missing_selection")]
    [TestCase("duplicate_selection")]
    [TestCase("changed_hit")]
    [TestCase("duplicate_fact")]
    [TestCase("foreign_header")]
    [TestCase("unknown_kind")]
    public void CandidateSelectionAndFactsMustDescribeTheSameExactIdentities(string fault)
    {
        var input = Input(Candidate("Alice Vale"));
        var snapshot = input.Candidates;
        var hit = snapshot.Selection.Items.Single();
        var fact = snapshot.Facts.Single();
        snapshot = fault switch
        {
            "missing_selection" => snapshot with
            {
                Selection = snapshot.Selection with { Items = [] },
            },
            "duplicate_selection" => snapshot with
            {
                Selection = snapshot.Selection with { Items = [hit, hit] },
            },
            "changed_hit" => snapshot with
            {
                Selection = snapshot.Selection with
                {
                    Items = [hit with { Revision = hit.Revision + 1 }],
                },
            },
            "duplicate_fact" => snapshot with { Facts = [fact, fact] },
            "foreign_header" => snapshot with
            {
                Facts = [fact with { Header = fact.Header with { Uuid = Guid.NewGuid() } }],
            },
            "unknown_kind" => snapshot with
            {
                Facts = [fact with { Header = fact.Header with { Kind = "unknown" } }],
            },
            _ => throw new AssertionException("Unknown fixture fault"),
        };

        var result = evaluator.EvaluateSourceActorAlias(
            input with
            {
                Candidates = snapshot,
            },
            Policy()
        );

        result.Action.Should().Be(SourceActorAliasPolicyAction.Unresolved);
    }

    [TestCase("dropped_alias")]
    [TestCase("extra_name")]
    [TestCase("artist_filter")]
    [TestCase("wrong_domain")]
    public void TheSnapshotQueryMustReproduceTheRoleFreeProjection(string fault)
    {
        var input = Input(Candidate("Alice Vale"));
        var query = input.Candidates.Query;
        query = fault switch
        {
            "dropped_alias" => query with { Names = ["Alice Vale"] },
            "extra_name" => query with { Names = [.. query.Names, "Another actor"] },
            "artist_filter" => query with { ArtistUuid = Artist },
            "wrong_domain" => query with { Domain = IdentityMatchingDomain.Compositions },
            _ => throw new AssertionException("Unknown fixture fault"),
        };

        var result = evaluator.EvaluateSourceActorAlias(
            input with
            {
                Candidates = input.Candidates with { Query = query },
            },
            Policy()
        );

        result.Action.Should().Be(SourceActorAliasPolicyAction.Unresolved);
    }

    [TestCase("unresolved")]
    [TestCase("unqueryable_alias")]
    [TestCase("too_many_aliases")]
    [TestCase("assertion_version")]
    public void ProjectionUncertaintyCannotBeRepairedBySupplyingAnOtherwiseWinningComparison(
        string fault
    )
    {
        var input = Input(Candidate("Alice Vale"));
        var assertion = input.Assertion;
        var alias = assertion.Aliases.Single();
        assertion = fault switch
        {
            "unresolved" => assertion with
            {
                Unresolved = [new("ambiguous_alias_boundary", [], [assertion.Context.Statement])],
            },
            "unqueryable_alias" => assertion with
            {
                Aliases = [alias with { Wording = new string('a', 201) }],
            },
            "too_many_aliases" => assertion with
            {
                Aliases = Enumerable
                    .Range(0, 17)
                    .Select(index => alias with { Wording = "Alias " + index })
                    .ToArray(),
            },
            "assertion_version" => assertion with { Version = "prior-source-assertion" },
            _ => throw new AssertionException("Unknown fixture fault"),
        };

        var result = evaluator.EvaluateSourceActorAlias(
            input with
            {
                Assertion = assertion,
            },
            Policy()
        );

        result.Action.Should().Be(SourceActorAliasPolicyAction.Unresolved);
        result.Reason.Should().Be("incomplete_actor_projection");
    }

    [TestCase("input_form")]
    [TestCase("producer")]
    [TestCase("extraction")]
    [TestCase("assertion")]
    [TestCase("projection")]
    [TestCase("facts")]
    [TestCase("retrieval")]
    [TestCase("scorer")]
    public void IndependentlyHashedApplicabilityMustUseEveryExactCurrentSourceContract(string fault)
    {
        var policy = Policy();
        var applicability = policy.Artifact.SourceActorAliasApplicability!;
        applicability = fault switch
        {
            "input_form" => applicability with { InputForm = "retained_credit" },
            "producer" => applicability with { RecordingProcessingVersion = "prior-producer" },
            "extraction" => applicability with { ExtractionVersion = "prior-extraction" },
            "assertion" => applicability with { AssertionVersion = "prior-assertion" },
            "projection" => applicability with
            {
                ProjectionVersion = PeopleActorProjection.Version,
            },
            "facts" => applicability with
            {
                CandidateFactsVersion = NormalizationCandidateService.FactsVersion,
            },
            "retrieval" => applicability with { RetrievalVersion = "prior-retrieval" },
            "scorer" => applicability with { ScorerVersion = "prior-scorer" },
            _ => throw new AssertionException("Unknown fixture fault"),
        };
        // Reseal every outer hash so failure concerns capability compatibility, not tampering.
        policy = Seal(
            policy with
            {
                Artifact = policy.Artifact with { SourceActorAliasApplicability = applicability },
            }
        );

        var result = evaluator.EvaluateSourceActorAlias(Input(Candidate("Alice Vale")), policy);

        result.Action.Should().Be(SourceActorAliasPolicyAction.Unresolved);
        result.Reason.Should().Be("unsupported_policy_applicability");
    }

    [TestCase("credit_only")]
    [TestCase("missing_source_evaluation")]
    [TestCase("wrong_applicability_hash")]
    public void ASourceInputFormStringOrCreditAttestationCannotReplaceIndependentSourceEvaluation(
        string fault
    )
    {
        var policy = Policy();
        policy = fault switch
        {
            "credit_only" => policy with
            {
                Artifact = policy.Artifact with { SourceActorAliasApplicability = null },
                Evaluation = policy.Evaluation with { SourceActorAliasEvaluation = null },
            },
            "missing_source_evaluation" => policy with
            {
                Evaluation = policy.Evaluation with { SourceActorAliasEvaluation = null },
            },
            "wrong_applicability_hash" => policy with
            {
                Evaluation = policy.Evaluation with
                {
                    SourceActorAliasEvaluation = policy.Evaluation.SourceActorAliasEvaluation! with
                    {
                        ApplicabilityHash = new string('f', 64),
                    },
                },
            },
            _ => throw new AssertionException("Unknown fixture fault"),
        };
        policy = Seal(policy, refreshSourceApplicabilityHash: false);

        var result = evaluator.EvaluateSourceActorAlias(Input(Candidate("Alice Vale")), policy);

        result.Action.Should().Be(SourceActorAliasPolicyAction.Unresolved);
        result.Reason.Should().Be("unsupported_policy_applicability");
    }

    [Test]
    public void OldArtifactAndEvaluationKeepIdenticalBytesAndHashesWhenSourceCapabilityIsAbsent()
    {
        var policy = Policy(source: false);
        var artifact = policy.Artifact;
        var oldArtifact = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                artifact.Uuid,
                artifact.Version,
                ContentHash = "",
                artifact.ScorerVersion,
                artifact.RetrievalVersion,
                artifact.CandidateFactsVersion,
                artifact.ExtractionVersion,
                artifact.CreditContextVersion,
                artifact.InputForms,
                artifact.Scorer,
                artifact.Creation,
                artifact.FixtureOnly,
            }
        );
        JsonSerializer
            .SerializeToUtf8Bytes(artifact with { ContentHash = "" })
            .Should()
            .Equal(oldArtifact);
        Hash(oldArtifact).Should().Be(artifact.ContentHash);
        JsonSerializer.Serialize(artifact).Should().NotContain("SourceActorAlias");

        var receipt = policy.Evaluation;
        var oldReceipt = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                receipt.Uuid,
                receipt.ArtifactUuid,
                receipt.ArtifactHash,
                ReceiptHash = "",
                receipt.DatasetHash,
                receipt.SplitHash,
                receipt.AdjudicationHash,
                receipt.EvaluatorVersion,
                receipt.IndependentEvaluator,
                receipt.ApprovedBy,
                receipt.Matching,
                receipt.Creation,
                receipt.ActorKind,
                receipt.FixtureOnly,
            }
        );
        JsonSerializer
            .SerializeToUtf8Bytes(receipt with { ReceiptHash = "" })
            .Should()
            .Equal(oldReceipt);
        Hash(oldReceipt).Should().Be(receipt.ReceiptHash);
        JsonSerializer.Serialize(receipt).Should().NotContain("SourceActorAlias");
    }

    [Test]
    public void SourceAndLegacyCreditInputsUseTheSameActualNumericalMatcherAndScorer()
    {
        var input = Input(
            Candidate("Alice Vale"),
            Candidate("Different Name", sharedArtist: false)
        );
        var mention = new RetainedCreditMention(
            input.Assertion.Name,
            RecordingCreditRole.Taper,
            input.Assertion.NameEvidence
        );
        var creditProjection = PeopleActorProjection.Project(mention, Artist);
        var matcher = new IdentityCandidateMatcher();
        var creditPairs = input
            .Candidates.Facts.Select(fact =>
                matcher.ComparePeople(
                    creditProjection.Record,
                    PeopleActorProjection.Candidate(fact)
                )
            )
            .ToArray();
        var creditSnapshot = input.Candidates with { Query = creditProjection.Query! };
        var claim = new NormalizationPeopleResolutionClaim(
            new(Guid.NewGuid(), mention),
            Artist,
            null,
            null,
            creditSnapshot,
            creditPairs,
            new(PeopleResolutionAction.Review, "competing_candidates")
        );
        var policy = Policy();

        var source = evaluator.EvaluateSourceActorAlias(input, policy);
        var credit = evaluator.Evaluate(claim, policy, 1, "retained_credit");

        source.Action.Should().Be(SourceActorAliasPolicyAction.MatchExisting);
        credit.Action.Should().Be(PeopleResolutionAction.MatchExisting);
        source.PersonUuid.Should().Be(credit.PersonUuid);
        source.WinnerScore.Should().Be(credit.WinnerScore);
        source.RunnerUpMargin.Should().Be(credit.RunnerUpMargin);
        source
            .Candidates.Should()
            .BeEquivalentTo(credit.Candidates, options => options.WithStrictOrdering());
    }

    [Test]
    public void CombinedArtifactCannotRouteSourceAssertionsThroughTheLegacyCreditCreationBranch()
    {
        var input = Input();
        // Deliberately probe an unsupported caller route. Valid source evaluation never
        // fabricates a credit; only the separate legacy-credit control below has a role.
        var mention = new RetainedCreditMention(
            input.Assertion.Name,
            RecordingCreditRole.Taper,
            input.Assertion.NameEvidence
        );
        var claim = new NormalizationPeopleResolutionClaim(
            new(Guid.NewGuid(), mention),
            Artist,
            null,
            null,
            input.Candidates,
            [],
            new(PeopleResolutionAction.Review, "no_candidates")
        );
        var policy = Policy(creation: true);
        var person = Guid.NewGuid();
        var coReference = Guid.NewGuid();

        var refused = evaluator.Evaluate(
            claim,
            policy,
            1,
            "source_actor_alias_assertion",
            person,
            coReference
        );
        var legacy = evaluator.Evaluate(claim, policy, 1, "retained_credit", person, coReference);

        refused.Action.Should().Be(PeopleResolutionAction.Unresolved);
        refused.Reason.Should().Be("unsupported_policy_applicability");
        refused.Creation.Should().BeNull();
        legacy.Action.Should().Be(PeopleResolutionAction.CreateIdentity);
        legacy
            .Creation.Should()
            .Be(new PeopleAcceptanceCreationPlan(person, "person", "Alice Vale", coReference));
    }

    private static SourceActorAliasPolicyInput Input(params PersonCandidateFacts[] facts)
    {
        var assertion = Assertion();
        var projection = PeopleActorProjection.Project(assertion, Artist);
        var snapshot = new NormalizationCandidateFactsSnapshot<PersonCandidateFacts>(
            NormalizationCandidateService.PeopleFactsVersion,
            "fixture-source-query",
            projection.Query!,
            new()
            {
                MaximumAliasMatchingNames = 5000,
                MaximumAliasMatchingBytes = 4 * 1024 * 1024,
                MaximumAliasEvidenceBytes = 4 * 1024 * 1024,
                MaximumActorMatchingBytes = 4 * 1024 * 1024,
            },
            new(IdentityMatchingDomain.People, facts.Select(fact => fact.Hit).ToArray(), false),
            facts,
            new([new(NormalizationCandidateUniverseKey.People, 1)], [], []),
            []
        );
        var matcher = new IdentityCandidateMatcher();
        var comparisons = facts
            .Select(fact =>
                matcher.ComparePeople(projection.Record, PeopleActorProjection.Candidate(fact))
            )
            .ToArray();
        return new(assertion, Artist, snapshot, comparisons);
    }

    private static PersonCandidateFacts Candidate(
        string heading,
        string kind = "person",
        string[]? aliases = null,
        bool sharedArtist = true
    )
    {
        var uuid = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var header = new CandidateIdentityHeader(uuid, heading, 1, owner, kind);
        return new()
        {
            Hit = new(uuid, 1, heading, owner, 1, ["name"]),
            Header = header,
            IdentityClosure = [header],
            AcceptedAliasMatchingNames = aliases ?? [],
            AliasMatchingNamesComplete = true,
            AliasAssertionCount = aliases?.Length ?? 0,
            AliasSupportCount = 0,
            AliasEvidenceTruncated = false,
            ActorMatchingFacts = new(false, [], [], [], [], [], false, false),
            ArtistContexts = sharedArtist ? [new(Artist, 1, [], [], false)] : [],
        };
    }

    private static SourceActorAliasAssertion<NormalizedEvidenceReference> Assertion()
    {
        const string raw = "Personnel:\nAlice Vale aka Lark - vocals";
        var retained = new NormalizationInputRevision
        {
            Uuid = Guid.NewGuid(),
            Resource = new("fixture", "source_text", Guid.NewGuid().ToString()),
            Payload = JsonSerializer.SerializeToUtf8Bytes(new { description = raw }),
            MediaType = "application/json",
            CaptureVersion = "fixture-source-alias-v1",
        };
        return new RetainedSourceActorAliasExtractor()
            .Extract(retained, [new("description", new(raw, new(retained.Uuid, "/description")))])
            .Assertions.Single();
    }

    private static RegisteredPeopleAcceptancePolicy Policy(
        bool source = true,
        bool creation = false
    )
    {
        var applicability = new SourceActorAliasPolicyApplicability(
            "source_actor_alias_assertion",
            NormalizationRecordingProcessor.Version,
            NormalizationRecordingExtractor.Version,
            SourceActorAliasExtractor.Version,
            PeopleActorProjection.SourceAliasVersion,
            NormalizationCandidateService.PeopleFactsVersion,
            PeopleResolutionPolicyEvaluator.RetrievalVersion,
            PeopleResolutionPolicyEvaluator.ScorerVersion
        );
        var artifact = new PeopleAcceptancePolicyArtifact(
            Guid.NewGuid(),
            "fixture-source-policy-v1",
            "",
            PeopleResolutionPolicyEvaluator.ScorerVersion,
            PeopleResolutionPolicyEvaluator.RetrievalVersion,
            NormalizationCandidateService.PeopleFactsVersion,
            NormalizationRecordingExtractor.Version,
            NormalizedPeopleService.CreditContextVersion,
            source ? ["retained_credit", "source_actor_alias_assertion"] : ["retained_credit"],
            new(0, 1, 1, 1, 2.5, .5),
            creation ? new(PeopleResolutionPolicyEvaluator.CreationClassifierVersion, .25) : null,
            FixtureOnly: true,
            SourceActorAliasApplicability: source ? applicability : null
        );
        var receipt = new PeopleAcceptanceEvaluationReceipt(
            Guid.NewGuid(),
            artifact.Uuid,
            "",
            "",
            new string('a', 64),
            new string('b', 64),
            new string('c', 64),
            "fixture-credit-evaluation-v1",
            "independent-fixture-credit-labels",
            "fixture-operator",
            new(20, 0),
            creation ? new(10, 0) : null,
            creation ? new(10, 0) : null,
            FixtureOnly: true,
            SourceActorAliasEvaluation: source
                ? new(
                    "",
                    new string('d', 64),
                    new string('e', 64),
                    new string('f', 64),
                    "fixture-source-evaluation-v1",
                    "independent-fixture-source-labels",
                    new(20, 0)
                )
                : null
        );
        return Seal(new(artifact, receipt));
    }

    private static RegisteredPeopleAcceptancePolicy Seal(
        RegisteredPeopleAcceptancePolicy policy,
        bool refreshSourceApplicabilityHash = true
    )
    {
        var artifact = policy.Artifact with
        {
            ContentHash = PeopleResolutionPolicyService.ComputeArtifactHash(policy.Artifact),
        };
        var source = policy.Evaluation.SourceActorAliasEvaluation;
        if (
            refreshSourceApplicabilityHash
            && source != null
            && artifact.SourceActorAliasApplicability != null
        )
        {
            source = source with
            {
                ApplicabilityHash =
                    PeopleResolutionPolicyService.ComputeSourceActorApplicabilityHash(
                        artifact.SourceActorAliasApplicability
                    ),
            };
        }
        var receipt = policy.Evaluation with
        {
            ArtifactHash = artifact.ContentHash,
            SourceActorAliasEvaluation = source,
        };
        receipt = receipt with
        {
            ReceiptHash = PeopleResolutionPolicyService.ComputeEvaluationHash(receipt),
        };
        return new(artifact, receipt);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
