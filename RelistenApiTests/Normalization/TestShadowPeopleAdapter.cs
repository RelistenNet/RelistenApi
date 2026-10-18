using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

/// <summary>Shadow relationships retain source ownership and do not grant identity authority.</summary>
[TestFixture]
public sealed class TestShadowPeopleAdapter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    [TestCase("Alice Vale (alice@example.test)", "Alice Vale (alice@example.test)")]
    [TestCase(
        "Alice Vale (https://archive.org/details/@night_owl)",
        "Alice Vale (https://archive.org/details/@night_owl)"
    )]
    [TestCase("Alice Vale aka Night Owl (alice@example.test)", "Night Owl (alice@example.test)")]
    public void AttributedAccountAndNameOrExplicitAliasProposeCrossArtistRelationship(
        string firstName,
        string secondName
    )
    {
        var first = Input(taper: firstName);
        var second = Input(taper: secondName);
        var adapter = Observing(first, second);

        var firstResults = adapter.Process(first).ToArray();
        var secondResults = adapter.Process(second).ToArray();
        var firstPerson = People(firstResults).People.Should().ContainSingle().Which;
        var secondPerson = People(secondResults).People.Should().ContainSingle().Which;
        firstPerson.EntityKey.Should().NotBe(secondPerson.EntityKey);
        var link = People(firstResults).Relationships.Should().ContainSingle().Which;
        link.Kind.Should().Be("proposed_same_person");
        link.ToEntityKey.Should().Be(secondPerson.EntityKey);
        link.TargetArtistUuid.Should().Be(second.Artist.Uuid);
        link.TargetSourceUuid.Should().Be(second.Uuid);
        link.TargetEvidence.Should().Contain(evidence => evidence.Field == "taper");
        firstResults
            .Single(result => result.Domain == "people")
            .EntityKeys.Should()
            .Contain(secondPerson.EntityKey);
        var credit = Credits(firstResults).Credits.Should().ContainSingle().Which;
        credit.PersonEntityKey.Should().Be(firstPerson.EntityKey);
        credit.SourceUuid.Should().Be(first.Uuid);
        credit.ArtistUuid.Should().Be(first.Artist.Uuid);
        credit.Role.Should().Be(RecordingCreditRole.Taper);
        credit.Evidence.Quote.Should().Be(firstName);
    }

    [Test]
    public void EqualNamesAloneRemainSeparateAndSharedAccountDoesNotJoinDifferentNames()
    {
        var first = Input(taper: "Alex Smith");
        var namesake = Input(taper: "Alex Smith");
        var sharedFirst = Input(taper: "Alice Vale (team@example.test)");
        var sharedSecond = Input(taper: "Bob Reed (team@example.test)");
        var adapter = Observing(first, namesake, sharedFirst, sharedSecond);

        var firstProposal = People(adapter.Process(first));
        firstProposal
            .Relationships.Should()
            .ContainSingle()
            .Which.Kind.Should()
            .Be("unresolved_name_match");
        firstProposal
            .IdentityDecisions!.Should()
            .ContainSingle()
            .Which.Reason.Should()
            .Be("name_requires_identity_evidence");
        firstProposal
            .IdentityDecisions!.Single()
            .QualifiedForAutomaticAcceptance.Should()
            .BeFalse();
        firstProposal
            .People.Single()
            .EntityKey.Should()
            .NotBe(People(adapter.Process(namesake)).People.Single().EntityKey);
        People(adapter.Process(sharedFirst))
            .Relationships.Should()
            .ContainSingle()
            .Which.Kind.Should()
            .Be("unresolved_shared_account");
    }

    [Test]
    public void ProjectWebsiteDoesNotBecomeAnIndividualIdentityLink()
    {
        var first = Input(taper: "Lantern Crew (https://lantern.example.test)");
        var second = Input(taper: "Lantern Crew (https://lantern.example.test)");
        var adapter = Observing(first, second);

        People(adapter.Process(first))
            .Relationships.Should()
            .ContainSingle()
            .Which.Kind.Should()
            .Be("unresolved_shared_account");
    }

    [Test]
    public void UnattributedNearbyContactDoesNotSupplyIdentityEvidence()
    {
        var first = Input(taper: "Alice Vale (contact Bob Reed: team@example.test)");
        var second = Input(taper: "Alice Vale (team@example.test)");
        var adapter = Observing(first, second);

        var comparison = People(adapter.Process(first))
            .Relationships.Should()
            .ContainSingle()
            .Which;
        comparison.Kind.Should().NotBe("proposed_same_person");
        comparison
            .Features.Should()
            .Contain(feature =>
                feature.Name == "actor_contact"
                && feature.Agreement == IdentityFeatureAgreement.Missing
            );
        Credits(adapter.Process(first)).Credits.Should().ContainSingle();
    }

    [Test]
    public void RosterAliasCreatesPeopleWithoutInventingTaperOrTransferrerCredits()
    {
        var input = Input(description: "The Band:\nAndy Moresi aka Hot Dog - Keyboard/Vocals");
        var results = Observing(input).Process(input).ToArray();

        var person = People(results).People.Should().ContainSingle().Which;
        person.Name.Should().Be("Andy Moresi");
        person.Aliases.Should().Equal("Hot Dog");
        Credits(results).Credits.Should().BeEmpty();
        results.Single(result => result.Domain == "credits").Outcome.Should().Be("unresolved");
    }

    [Test]
    public void MissingRoleClaimsStayUnknownAndContactOnlyTextIsNotAPublicName()
    {
        foreach (var input in new[] { Input(), Input(taper: "alice@example.test") })
        {
            var results = Observing(input).Process(input).ToArray();
            results.Select(result => result.Domain).Should().BeEquivalentTo("people", "credits");
            results.Should().OnlyContain(result => result.Outcome == "unresolved");
            People(results).People.Should().BeEmpty();
            Credits(results).Credits.Should().BeEmpty();
        }
    }

    [Test]
    public void RepeatedFirstPassRecordsDoNotDoubleLinksAndObservationOrderDoesNotChangeKeys()
    {
        var first = Input(taper: "Alice Vale (alice@example.test)");
        var second = Input(taper: "Alice Vale (alice@example.test)");
        var forward = Observing(first, second, first, second);
        var reverse = Observing(second, first);

        JsonSerializer
            .Serialize(People(forward.Process(first)), Json)
            .Should()
            .Be(JsonSerializer.Serialize(People(reverse.Process(first)), Json));
        People(forward.Process(first)).Relationships.Should().ContainSingle();
    }

    [Test]
    public void SharedAccountFanOutScoresEveryWitnessWhileBoundingOnlyDetailedEvidence()
    {
        var input = Input(taper: "Alice Vale (team@example.test)");
        var others = Enumerable
            .Range(0, 110)
            .Select(index => Input(taper: $"Taper Person{index} (team@example.test)"))
            .ToArray();
        var adapter = Observing(others.Prepend(input).ToArray());
        var results = adapter.Process(input).ToArray();
        var people = People(results);

        people.People.Should().ContainSingle();
        Credits(results).Credits.Should().ContainSingle();
        var coverage = people.RelationshipCoverage.Should().ContainSingle().Which;
        coverage.Complete.Should().BeTrue();
        coverage.AvailableAccounts.Should().Be(110);
        coverage.ComparedAccounts.Should().Be(110);
        coverage.DetailedRelationships.Should().Be(people.Relationships.Count);
        coverage.DetailedEvidenceComplete.Should().BeFalse();
        people.IdentityDecisions!.Single().CandidateOutcomes.Should().HaveCount(110);
        people.IdentityDecisions!.Single().CandidateComparisonsComplete.Should().BeTrue();
        var omitted = people
            .IdentityDecisions!.Single()
            .CandidateOutcomes.First(candidate =>
                people.Relationships.All(link => link.ToEntityKey != candidate.CandidateEntityKey)
            );
        results
            .Single(result => result.Domain == "people")
            .EntityKeys.Should()
            .Contain(omitted.CandidateEntityKey);
        people.Relationships.Should().OnlyContain(link => link.Kind == "unresolved_shared_account");
    }

    [Test]
    public void RawSourceFallbackUsesTheLocalExtractorsAndPreservesBothRoles()
    {
        var input = Input(description: "Recorded and transferred by Alice Vale") with
        {
            Extraction = null,
        };
        var results = new ShadowPeopleAdapter().Process(input).ToArray();

        results.Should().OnlyContain(result => result.Outcome == "proposed");
        Credits(results)
            .Credits.Select(credit => credit.Role)
            .Should()
            .BeEquivalentTo(new[] { RecordingCreditRole.Taper, RecordingCreditRole.Transferrer });
        People(results).People.Should().ContainSingle();
        People(results).GlobalIndexIncludesSource.Should().BeFalse();
        People(results)
            .IdentityDecisions!.Single()
            .Reason.Should()
            .Be("global_actor_index_unavailable");
        People(results).IdentityDecisions!.Single().CandidateComparisonsComplete.Should().BeFalse();
        Credits(results)
            .Credits.Select(credit => credit.PersonEntityKey)
            .Distinct()
            .Should()
            .ContainSingle();
    }

    [Test]
    public void ExtractionForAnotherSourceFailsBothDomainsWithoutPublishingItsClaims()
    {
        var first = Input(taper: "Alice Vale");
        var second = Input(taper: "Bob Reed");
        var results = new ShadowPeopleAdapter()
            .Process(first with { Extraction = second.Extraction })
            .ToArray();

        results
            .Should()
            .HaveCount(2)
            .And.OnlyContain(result => result.Outcome == "failed" && result.Proposal == null);
    }

    [Test]
    public void OrdinaryExplicitAliasesRetrieveAcrossArtistsWithoutInventingCreditsOrIdentity()
    {
        var roster = Input(description: "The Band:\nAndy Moresi aka Hot Dog - Keyboard/Vocals");
        var recording = Input(taper: "Hot Dog");
        var adapter = Observing(roster, recording);
        var people = People(adapter.Process(roster));

        var link = people.Relationships.Should().ContainSingle().Which;
        link.TargetArtistUuid.Should().Be(recording.Artist.Uuid);
        link.Kind.Should().Be("unresolved_name_match");
        people.IdentityDecisions!.Single().QualifiedForAutomaticAcceptance.Should().BeFalse();
        Credits(adapter.Process(roster)).Credits.Should().BeEmpty();
        adapter
            .Process(roster)
            .Single(result => result.Domain == "people")
            .EntityKeys.Intersect(
                adapter.Process(recording).Single(result => result.Domain == "people").EntityKeys
            )
            .Should()
            .Contain(key => key.StartsWith("person_name:", StringComparison.Ordinal));
    }

    [Test]
    public void CommonNameFanOutKeepsAccountSupportAndEveryCandidateOutcome()
    {
        var input = Input(taper: "Alice Vale (alice@example.test)");
        var supported = Input(taper: "Alice Vale (alice@example.test)");
        var namesakes = Enumerable.Range(0, 110).Select(_ => Input(taper: "Alice Vale")).ToArray();
        var adapter = Observing(namesakes.Concat(new[] { input, supported }).ToArray());
        var people = People(adapter.Process(input));

        people.RelationshipCoverage.Single().Complete.Should().BeTrue();
        people.RelationshipCoverage.Single().DetailedEvidenceComplete.Should().BeFalse();
        people.IdentityDecisions!.Single().CandidateOutcomes.Should().HaveCount(111);
        people
            .IdentityDecisions!.Single()
            .CandidateOutcomes.Should()
            .Contain(outcome =>
                outcome.SourceUuid == supported.Uuid && outcome.Outcome == "proposed_same_person"
            );
        people
            .Relationships.Should()
            .Contain(link =>
                link.TargetSourceUuid == supported.Uuid && link.Kind == "proposed_same_person"
            );
        people.People.Single().SourceUuid.Should().Be(input.Uuid);
    }

    [Test]
    public void TrainedModelRanksAllWitnessesWithoutBecomingAcceptanceAuthority()
    {
        WithModel(pin =>
        {
            var input = Input(taper: "Alice Vale (team@example.test)");
            var supported = Input(taper: "Alice Vale (team@example.test)");
            var sharedAccount = Input(taper: "Bob Reed (team@example.test)");
            var adapter = new ShadowPeopleAdapter(null, null, pin);
            Observe(adapter, input, supported, sharedAccount);
            var decision = People(adapter.Process(input)).IdentityDecisions!.Single();

            decision.CandidateOutcomes.Should().HaveCount(2);
            decision.Ranking!.Outcome.Should().Be("unique_ranked_witness");
            decision.Ranking.ScoreKind.Should().Be("raw_logit");
            decision.Ranking.HighestScore.Should().Be(7);
            decision.Ranking.RunnerUpMargin.Should().Be(3);
            decision
                .CandidateOutcomes.Single(outcome => outcome.SourceUuid == supported.Uuid)
                .RawScore.Should()
                .Be(7);
            decision.QualifiedForAutomaticAcceptance.Should().BeFalse();
            decision.Ranking.AcceptanceThresholdAvailable.Should().BeFalse();
            adapter.Version.Should().Contain(decision.Ranking.ModelSha256);
        });
    }

    [Test]
    public void RankedNamesakeTieRetainsEveryRivalWithoutChoosingAnIdentity()
    {
        WithModel(pin =>
        {
            var input = Input(taper: "Alex Smith");
            var first = Input(taper: "Alex Smith");
            var second = Input(taper: "Alex Smith");
            var adapter = new ShadowPeopleAdapter(null, null, pin);
            Observe(adapter, input, first, second);
            var decision = People(adapter.Process(input)).IdentityDecisions!.Single();

            decision.Ranking!.Outcome.Should().Be("tied_candidate_witnesses");
            decision.Ranking.HighestRankedCandidateKeys.Should().HaveCount(2);
            decision.Ranking.RunnerUpMargin.Should().Be(0);
            decision.Reason.Should().Be("name_requires_identity_evidence");
            decision.ProposedCandidateKeys.Should().BeEmpty();
        });
    }

    [Test]
    public void ChangedModelBytesCannotReuseTheirPinnedIdentity()
    {
        WithModel(pin =>
        {
            var path = pin[..pin.LastIndexOf("#sha256=", StringComparison.Ordinal)];
            File.AppendAllText(path, " ");
            var load = () => new ShadowPeopleAdapter(null, null, pin);
            load.Should().Throw<InvalidDataException>();
        });
    }

    [TestCase("feature_order")]
    [TestCase("runtime_mapping")]
    public void PinnedModelMustReproduceItsFeatureAndRuntimeContracts(string changedContract)
    {
        WithModel(pin =>
        {
            var path = pin[..pin.LastIndexOf("#sha256=", StringComparison.Ordinal)];
            var artifact = JsonNode.Parse(File.ReadAllText(path))!;
            var model = artifact["logistic"]!["final"]!;
            if (changedContract == "feature_order")
            {
                model["featureOrder"]![3] = "unsupported_feature";
            }
            else
            {
                model["runtimeScorerParameters"]!["contactAgreementWeight"] = 999;
            }
            File.WriteAllText(path, artifact.ToJsonString());
            var newPin =
                path
                + "#sha256="
                + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

            var load = () => new ShadowPeopleAdapter(null, null, newPin);
            load.Should().Throw<InvalidDataException>();
        });
    }

    [Test]
    public void EquivalentSameArtistSourcesRetrieveEachOtherInBothObservationOrders()
    {
        var artist = new ShadowCatalogArtist(
            1,
            Guid.NewGuid(),
            "Shared fixture artist",
            ["archive.org"]
        );
        var first = Input(taper: "Alice Vale (alice@example.test)", artistContext: artist);
        var second = Input(taper: "Alice Vale (alice@example.test)", artistContext: artist);
        var forward = Observing(first, second);
        var reverse = Observing(second, first);

        foreach (var (input, counterpart) in new[] { (first, second), (second, first) })
        {
            var proposal = People(forward.Process(input));
            var link = proposal.Relationships.Should().ContainSingle().Which;
            link.TargetSourceUuid.Should().Be(counterpart.Uuid);
            link.Kind.Should().Be("proposed_same_person");
            link.Features.Should()
                .Contain(feature =>
                    feature.Name == "artist_context"
                    && feature.Agreement == IdentityFeatureAgreement.Agrees
                );
            JsonSerializer
                .Serialize(proposal, Json)
                .Should()
                .Be(JsonSerializer.Serialize(People(reverse.Process(input)), Json));
        }
    }

    [Test]
    public void RepeatedSameArtistSourcesRemainOneCandidatePerMatchingFactBucket()
    {
        var artist = new ShadowCatalogArtist(
            1,
            Guid.NewGuid(),
            "Shared fixture artist",
            ["archive.org"]
        );
        var sources = Enumerable
            .Range(0, 120)
            .Select(_ => Input(taper: "Alice Vale", artistContext: artist))
            .ToArray();
        var adapter = Observing(sources);

        foreach (var input in sources)
        {
            var proposal = People(adapter.Process(input));
            proposal.RelationshipCoverage.Single().AvailableAccounts.Should().Be(1);
            proposal
                .Relationships.Should()
                .ContainSingle()
                .Which.TargetSourceUuid.Should()
                .NotBe(input.Uuid);
            proposal.IdentityDecisions!.Single().CandidateOutcomes.Should().ContainSingle();
        }
    }

    private static void WithModel(Action<string> test)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "relisten-people-model-" + Guid.NewGuid().ToString("N") + ".json"
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

    private static ShadowPeopleProposal People(IEnumerable<ShadowCatalogResult> results) =>
        (ShadowPeopleProposal)results.Single(result => result.Domain == "people").Proposal!;

    private static ShadowCreditsProposal Credits(IEnumerable<ShadowCatalogResult> results) =>
        (ShadowCreditsProposal)results.Single(result => result.Domain == "credits").Proposal!;

    private static ShadowCatalogInput Input(
        string? taper = null,
        string? description = null,
        ShadowCatalogArtist? artistContext = null
    )
    {
        var sourceUuid = Guid.NewGuid();
        var artist =
            artistContext
            ?? new ShadowCatalogArtist(1, Guid.NewGuid(), "Fixture artist", ["archive.org"]);
        var fields = new Dictionary<string, string?>
        {
            ["taper"] = taper,
            ["transferrer"] = null,
            ["description"] = description,
            ["source"] = null,
            ["lineage"] = null,
            ["taper_notes"] = null,
        };
        var row = new Dictionary<string, object?>
        {
            ["id"] = 42L,
            ["uuid"] = sourceUuid,
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
            sourceUuid,
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
}
