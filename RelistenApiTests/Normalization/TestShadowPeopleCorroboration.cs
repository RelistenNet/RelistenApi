using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

/// <summary>Direct corroboration remains separate from accepted people and ranking.</summary>
[TestFixture]
public sealed class TestShadowPeopleCorroboration
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    [TestCase("Alice Vale (alice@example.test)", "Alice Vale (alice@example.test)")]
    [TestCase("Alice Vale aka Night Owl (alice@example.test)", "Night Owl (alice@example.test)")]
    public void DirectCrossArtistSupportCreatesOnlyAnUnacceptedAnchoredHypothesis(
        string anchorName,
        string supportingName
    )
    {
        var anchor = Input(taper: anchorName);
        var support = Input(taper: supportingName);
        var adapter = Observing(anchor, support);

        var people = People(adapter, anchor);
        var decision = people.IdentityDecisions!.Single();
        decision.Hypothesis.Should().NotBeNull();
        var hypothesis = decision.Hypothesis!;
        hypothesis.AnchorEntityKey.Should().Be(people.People.Single().EntityKey);
        hypothesis.ProposalHandle.Should().NotBeNullOrWhiteSpace();
        hypothesis.ExtractionIdentitySha256.Should().MatchRegex("^[0-9a-f]{64}$");
        hypothesis.Method.Should().Be("direct_name_or_explicit_alias_and_attributed_account");
        hypothesis.DirectSupportedMatchingFactCount.Should().Be(1);
        hypothesis.DistinctSupportingSourceCount.Should().Be(1);
        hypothesis.DistinctSupportingArtistCount.Should().Be(1);
        hypothesis.MatchingFactComparisonsComplete.Should().BeTrue();
        hypothesis.SourceMembershipComplete.Should().BeTrue();
        hypothesis.DirectEvidenceExamplesComplete.Should().BeTrue();
        var example = hypothesis.DirectEvidenceExamples.Should().ContainSingle().Which;
        example.FromEntityKey.Should().Be(hypothesis.AnchorEntityKey);
        example.TargetSourceUuid.Should().Be(support.Uuid);
        example.TargetArtistUuid.Should().Be(support.Artist.Uuid);
        example.Kind.Should().Be("proposed_same_person");
        example.TargetEvidence.Should().NotBeEmpty();
        hypothesis.AcceptedIdentity.Should().BeFalse();
        hypothesis.QualifiedForAutomaticAcceptance.Should().BeFalse();
        decision.QualifiedForAutomaticAcceptance.Should().BeFalse();
        people.People.Single().SourceUuid.Should().Be(anchor.Uuid);
    }

    [Test]
    public void CompleteSourceMembershipSurvivesCompactWitnessesDuplicateRolesAndRepeatedObserve()
    {
        WithModel(pin =>
        {
            var artist = Artist();
            const string name = "Alice Vale (alice@example.test)";
            var anchor = Input(taper: name, artist: artist);
            var supports = Enumerable
                .Range(0, 120)
                .Select(_ => Input(taper: name, transferrer: name, artist: artist))
                .ToArray();
            var adapter = new ShadowPeopleAdapter(null, null, pin);
            Observe(adapter, anchor, supports[0]);
            var initial = Decision(adapter, anchor);

            Observe(adapter, supports);
            Observe(adapter, supports);
            Observe(adapter, anchor);
            var decision = Decision(adapter, anchor);
            var hypothesis = decision.Hypothesis!;

            decision.CandidateOutcomes.Should().ContainSingle();
            People(adapter, anchor).RelationshipCoverage.Single().ComparedAccounts.Should().Be(1);
            hypothesis.DirectSupportedMatchingFactCount.Should().Be(1);
            hypothesis.DistinctSupportingSourceCount.Should().Be(120);
            hypothesis.DistinctSupportingArtistCount.Should().Be(1);
            hypothesis.SourceMembershipComplete.Should().BeTrue();
            hypothesis.MatchingFactComparisonsComplete.Should().BeTrue();
            hypothesis.DirectEvidenceExamples.Count.Should().BeInRange(1, 8);
            hypothesis
                .DirectEvidenceExamples.Select(example => example.TargetSourceUuid)
                .Should()
                .OnlyHaveUniqueItems();
            hypothesis
                .DirectEvidenceExamples.Should()
                .OnlyContain(example =>
                    example.TargetSourceUuid != anchor.Uuid
                    && supports.Any(source => source.Uuid == example.TargetSourceUuid)
                );
            hypothesis.DirectEvidenceExamplesComplete.Should().BeFalse();
            hypothesis.ProposalHandle.Should().Be(initial.Hypothesis!.ProposalHandle);
            decision.Ranking!.HighestScore.Should().Be(initial.Ranking!.HighestScore);
            decision.Ranking.RunnerUpMargin.Should().Be(initial.Ranking.RunnerUpMargin);
            decision.Ranking.Outcome.Should().Be("unique_ranked_witness");
            decision.Ranking.AcceptanceThresholdAvailable.Should().BeFalse();
            hypothesis.AcceptedIdentity.Should().BeFalse();
            hypothesis.QualifiedForAutomaticAcceptance.Should().BeFalse();
        });
    }

    [TestCase("Alex Smith", "Alex Smith")]
    [TestCase("Alice Vale (team@example.test)", "Bob Reed (team@example.test)")]
    [TestCase(
        "Lantern Crew (https://lantern.example.test)",
        "Lantern Crew (https://lantern.example.test)"
    )]
    public void NameOnlySharedAccountAndProjectProfilesDoNotCreateHypotheses(
        string anchorName,
        string otherName
    )
    {
        var anchor = Input(taper: anchorName);
        var other = Input(taper: otherName);
        var decision = Decision(Observing(anchor, other), anchor);

        decision.CandidateOutcomes.Should().ContainSingle();
        decision.ProposedCandidateKeys.Should().BeEmpty();
        decision.Hypothesis.Should().BeNull();
        decision.QualifiedForAutomaticAcceptance.Should().BeFalse();
    }

    [Test]
    public void AliasBridgeDoesNotTransitivelyAddAnUnsupportedNamesake()
    {
        var alice = Input(taper: "Alice Vale (team@example.test)");
        var bridge = Input(taper: "Alice Vale aka Bob Reed (team@example.test)");
        var bob = Input(taper: "Bob Reed (team@example.test)");
        var adapter = Observing(alice, bridge, bob);

        var aliceDecision = Decision(adapter, alice);
        aliceDecision.Hypothesis!.DistinctSupportingSourceCount.Should().Be(1);
        aliceDecision
            .Hypothesis.DirectEvidenceExamples.Should()
            .OnlyContain(example => example.TargetSourceUuid == bridge.Uuid);
        aliceDecision
            .CandidateOutcomes.Single(candidate => candidate.SourceUuid == bob.Uuid)
            .Outcome.Should()
            .Be("unresolved_shared_account");
        Decision(adapter, bridge).Hypothesis!.DistinctSupportingSourceCount.Should().Be(2);
        Decision(adapter, bob).Hypothesis!.DistinctSupportingSourceCount.Should().Be(1);
        aliceDecision.Hypothesis.AcceptedIdentity.Should().BeFalse();
    }

    [Test]
    public void SupportedHypothesisRetainsTheUnresolvedNamesakeRival()
    {
        var anchor = Input(taper: "Alice Vale (alice@example.test)");
        var support = Input(taper: "Alice Vale (alice@example.test)");
        var namesake = Input(taper: "Alice Vale");
        var adapter = Observing(anchor, support, namesake);

        var decision = Decision(adapter, anchor);
        decision.CandidateOutcomes.Should().HaveCount(2);
        decision
            .CandidateOutcomes.Single(candidate => candidate.SourceUuid == namesake.Uuid)
            .Outcome.Should()
            .Be("unresolved_name_match");
        decision.Hypothesis!.DistinctSupportingSourceCount.Should().Be(1);
        decision
            .Hypothesis.DirectEvidenceExamples.Should()
            .OnlyContain(example => example.TargetSourceUuid == support.Uuid);
        decision.QualifiedForAutomaticAcceptance.Should().BeFalse();
    }

    [Test]
    public void HandleSurvivesObservationOrderReplayAndReplacementOfTheCompactRepresentative()
    {
        var artist = Artist();
        const string name = "Alice Vale (alice@example.test)";
        var anchor = Input(taper: name, artist: artist);
        var supports = new[]
        {
            Input(taper: name, artist: artist),
            Input(taper: name, artist: artist),
        }
            .OrderBy(
                input => People(new ShadowPeopleAdapter(), input).People.Single().EntityKey,
                StringComparer.Ordinal
            )
            .ToArray();
        var adapter = Observing(anchor, supports[1]);
        var original = Decision(adapter, anchor);
        original.CandidateOutcomes.Single().SourceUuid.Should().Be(supports[1].Uuid);

        Observe(adapter, supports[0]);
        var expanded = Decision(adapter, anchor);
        expanded.CandidateOutcomes.Single().SourceUuid.Should().Be(supports[0].Uuid);
        expanded.Hypothesis!.DistinctSupportingSourceCount.Should().Be(2);
        expanded.Hypothesis.ProposalHandle.Should().Be(original.Hypothesis!.ProposalHandle);
        expanded
            .Hypothesis.ExtractionIdentitySha256.Should()
            .Be(original.Hypothesis.ExtractionIdentitySha256);
        var replay = JsonSerializer.Serialize(expanded, Json);
        JsonSerializer.Serialize(Decision(adapter, anchor), Json).Should().Be(replay);

        var reversed = Observing(supports[0], supports[1], anchor);
        Decision(reversed, anchor).Hypothesis.Should().BeEquivalentTo(expanded.Hypothesis);
    }

    [Test]
    public void HandleBindsTheCompleteExtractionAndAdapterVersion()
    {
        WithModel(pin =>
        {
            const string name = "Alice Vale (alice@example.test)";
            var anchor = Input(taper: name);
            var support = Input(taper: name);
            var original = Decision(Observing(anchor, support), anchor).Hypothesis!;
            var changed = Input(
                taper: name,
                description: "An additional retained source note.",
                artist: anchor.Artist,
                sourceUuid: anchor.Uuid
            );
            var changedHypothesis = Decision(Observing(changed, support), changed).Hypothesis!;

            changedHypothesis.AnchorEntityKey.Should().Be(original.AnchorEntityKey);
            changedHypothesis
                .ExtractionIdentitySha256.Should()
                .NotBe(original.ExtractionIdentitySha256);
            changedHypothesis.ProposalHandle.Should().NotBe(original.ProposalHandle);
            var ranked = new ShadowPeopleAdapter(null, null, pin);
            Observe(ranked, anchor, support);
            var rankedHypothesis = Decision(ranked, anchor).Hypothesis!;
            rankedHypothesis
                .ExtractionIdentitySha256.Should()
                .Be(original.ExtractionIdentitySha256);
            rankedHypothesis.ProposalHandle.Should().NotBe(original.ProposalHandle);
        });
    }

    [Test]
    public void SupportSummaryIncludesEveryComparedBucketBeyondTheDetailedRelationshipLimit()
    {
        const string name = "Alice Vale (alice@example.test)";
        var anchor = Input(taper: name);
        var supports = Enumerable.Range(0, 110).Select(_ => Input(taper: name)).ToArray();
        var adapter = Observing(supports.Prepend(anchor).ToArray());

        var people = People(adapter, anchor);
        var decision = people.IdentityDecisions!.Single();
        people.Relationships.Should().HaveCount(100);
        people.RelationshipCoverage.Single().DetailedEvidenceComplete.Should().BeFalse();
        decision.CandidateOutcomes.Should().HaveCount(110);
        var hypothesis = decision.Hypothesis!;
        hypothesis.DirectSupportedMatchingFactCount.Should().Be(110);
        hypothesis.DistinctSupportingSourceCount.Should().Be(110);
        hypothesis.DistinctSupportingArtistCount.Should().Be(110);
        hypothesis.MatchingFactComparisonsComplete.Should().BeTrue();
        hypothesis.SourceMembershipComplete.Should().BeTrue();
        hypothesis.DirectEvidenceExamples.Count.Should().BeInRange(1, 8);
        hypothesis.DirectEvidenceExamplesComplete.Should().BeFalse();
        hypothesis.AcceptedIdentity.Should().BeFalse();
        hypothesis.QualifiedForAutomaticAcceptance.Should().BeFalse();
    }

    private static ShadowPeopleIdentityDecision Decision(
        ShadowPeopleAdapter adapter,
        ShadowCatalogInput input
    ) => People(adapter, input).IdentityDecisions!.Single();

    private static ShadowPeopleProposal People(
        ShadowPeopleAdapter adapter,
        ShadowCatalogInput input
    ) =>
        (ShadowPeopleProposal)
            adapter.Process(input).Single(result => result.Domain == "people").Proposal!;

    private static void Observe(ShadowPeopleAdapter adapter, params ShadowCatalogInput[] inputs)
    {
        foreach (var input in inputs)
        {
            adapter.Observe(input.Record);
            if (input.Extraction is { } extraction)
            {
                adapter.Observe(extraction);
            }
        }
    }

    private static ShadowPeopleAdapter Observing(params ShadowCatalogInput[] inputs)
    {
        var adapter = new ShadowPeopleAdapter();
        Observe(adapter, inputs);
        return adapter;
    }

    private static ShadowCatalogArtist Artist() =>
        new(1, Guid.NewGuid(), "Fixture artist", ["archive.org"]);

    private static ShadowCatalogInput Input(
        string? taper = null,
        string? transferrer = null,
        string? description = null,
        ShadowCatalogArtist? artist = null,
        Guid? sourceUuid = null
    )
    {
        artist ??= Artist();
        var source = sourceUuid ?? Guid.NewGuid();
        var fields = new Dictionary<string, string?>
        {
            ["taper"] = taper,
            ["transferrer"] = transferrer,
            ["description"] = description,
            ["source"] = null,
            ["lineage"] = null,
            ["taper_notes"] = null,
        };
        var row = new Dictionary<string, object?>
        {
            ["id"] = 42L,
            ["uuid"] = source,
            ["artist_id"] = artist.Id,
        };
        foreach (var field in fields)
        {
            row.Add(field.Key, field.Value);
        }
        var record = JsonSerializer.SerializeToElement(new { kind = "source_sample", row }, Json);
        var extraction = new NormalizationBatchExtraction(
            "source_sample",
            42,
            source,
            artist.Id,
            artist.Uuid,
            null,
            fields,
            null,
            new RecordingCreditExtractor().Extract(fields),
            null,
            new SourceActorAliasExtractor().Extract(fields)
        );
        return new(
            record,
            JsonSerializer.SerializeToElement(extraction, Json),
            artist,
            "archive.org"
        );
    }

    private static void WithModel(Action<string> test)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "relisten-corroboration-model-" + Guid.NewGuid().ToString("N") + ".json"
        );
        try
        {
            File.WriteAllText(
                path,
                JsonSerializer.Serialize(
                    new
                    {
                        logistic = new
                        {
                            final = new
                            {
                                version = "people-five-feature-logistic-v1",
                                projectionVersion = "people-mentions-source-context-five-v1",
                                scoreKind = "raw_logit",
                                classes = new[] { 0, 1 },
                                featureOrder = new[]
                                {
                                    "name_token_similarity",
                                    "exact_name",
                                    "artist_overlap",
                                    "contact_agreement",
                                    "profile_agreement",
                                },
                                coefficients = new[] { 1d, 2, 3, 4, 5 },
                                intercept = 0,
                                runtimeScorerParameters = new
                                {
                                    intercept = 0,
                                    nameSimilarityWeight = 1,
                                    exactNameWeight = 2,
                                    artistOverlapWeight = 3,
                                    contactAgreementWeight = 4,
                                    profileAgreementWeight = 5,
                                },
                            },
                        },
                    }
                )
            );
            test(
                path
                    + "#sha256="
                    + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))
            );
        }
        finally
        {
            File.Delete(path);
        }
    }
}
