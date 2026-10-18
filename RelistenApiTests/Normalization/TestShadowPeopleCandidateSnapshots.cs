using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

/// <summary>Offline artifacts retain public reader results without inventing missing queries.</summary>
[TestFixture]
public sealed class TestShadowPeopleCandidateSnapshotFiles
{
    private static readonly JsonSerializerOptions SourceJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
    private static readonly Guid Person = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Rival = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid Artist = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private static readonly Guid OtherArtist = Guid.Parse("00000000-0000-0000-0000-000000000004");
    private static readonly Guid Owner = Guid.Parse("00000000-0000-0000-0000-000000000005");
    private string directory = null!;

    [SetUp]
    public void SetUp() => directory = Directory.CreateTempSubdirectory().FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(directory, recursive: true);

    [Test]
    public void RoundTripPreservesCanonicalIdentityAndCompleteAggregatesWithBoundedWitnesses()
    {
        var original = Snapshot("Alex Example", Candidate(Person));
        var bytes = ShadowPeopleCandidateSnapshots.Serialize([original]);
        var artifact = Load(bytes);

        var lookup = artifact.Find(original.Query);

        lookup.Found.Should().BeTrue();
        lookup.Snapshot.Should().BeEquivalentTo(original);
        lookup.Snapshot!.Complete.Should().BeTrue();
        lookup
            .Locator.Should()
            .Be(
                new ShadowPeopleCandidateSnapshotLocator(
                    Convert.ToHexStringLower(SHA256.HashData(bytes)),
                    PeopleActorProjection.QueryFingerprint(original.Query),
                    original.QueryHash
                )
            );

        var facts = lookup.Snapshot.Facts.Single();
        facts.Aliases.Should().ContainSingle();
        facts.AliasAssertionCount.Should().Be(30);
        facts.AliasEvidenceTruncated.Should().BeTrue();
        facts.ActorEvidence.Should().BeEmpty();
        facts.ActorEvidenceTruncated.Should().BeTrue();
        var projected = PeopleActorProjection.Candidate(facts);
        projected.Uuid.Should().Be(Person);
        projected
            .Names.Select(name => name.Wording)
            .Should()
            .Contain([
                "Alex Example",
                "A. Example",
                "Alexander Example",
                "Alex at the Board",
                "Alex E.",
            ]);
        projected.ArtistUuids.Should().BeEquivalentTo(new[] { Artist, OtherArtist });
        projected
            .ActorContext!.Contacts.Should()
            .ContainSingle()
            .Which.LookupKey.Should()
            .Be("alex@example.test");
        facts.ArtistContexts.Select(context => context.AcceptedCreditCount).Should().Equal(12, 8);
        facts
            .ArtistContexts.Should()
            .OnlyContain(context => context.Witnesses.Count == 0 && context.WitnessesTruncated);
    }

    [Test]
    public void SameNameRivalsKeepDistinctResolvedPersonUuids()
    {
        var snapshot = Snapshot("Alex Example", Candidate(Person), Candidate(Rival));

        var lookup = Load(ShadowPeopleCandidateSnapshots.Serialize([snapshot]))
            .Find(snapshot.Query);

        lookup
            .Snapshot!.Facts.Select(PeopleActorProjection.Candidate)
            .Select(candidate => candidate.Uuid)
            .Should()
            .Equal(Person, Rival);
        lookup.Snapshot.Selection.Items.Select(hit => hit.Uuid).Should().Equal(Person, Rival);
    }

    [Test]
    public void MissingQueryIsUnknownAndDiffersFromAnExportedCompleteEmptyPool()
    {
        var empty = Snapshot("Nobody Found");
        var artifact = Load(ShadowPeopleCandidateSnapshots.Serialize([empty]));

        var found = artifact.Find(empty.Query);
        var missing = artifact.Find(empty.Query with { NameLimit = empty.Query.NameLimit + 1 });

        found.Found.Should().BeTrue();
        found.Snapshot!.Complete.Should().BeTrue();
        found.Snapshot.Facts.Should().BeEmpty();
        found.Locator.Should().NotBeNull();
        missing.Found.Should().BeFalse();
        missing.Snapshot.Should().BeNull();
        missing.Locator.Should().BeNull();
        missing.QueryFingerprint.Should().NotBe(found.QueryFingerprint);
    }

    [Test]
    public void IncompletePoolsRetainTheirLimitsSelectionFlagsIssuesAndPartialAggregates()
    {
        var facts = Candidate(Person) with
        {
            AcceptedAliasMatchingNames = [],
            AliasMatchingNamesComplete = false,
        };
        var original = Snapshot("Alex Example", facts);
        original = original with
        {
            Selection = original.Selection with
            {
                NameCandidatesTruncated = true,
                ContextCandidatesTruncated = true,
            },
            Limits = original.Limits with { MaximumAliasMatchingNames = 1 },
            Issues =
            [
                new(CandidateFactsIssueKind.AssertionLimit, Person, "person_alias_matching_names"),
            ],
        };

        var loaded = Load(ShadowPeopleCandidateSnapshots.Serialize([original]))
            .Find(original.Query);

        loaded.Found.Should().BeTrue();
        loaded.Snapshot.Should().BeEquivalentTo(original);
        loaded.Snapshot!.Complete.Should().BeFalse();
        loaded.Snapshot.Facts.Single().AliasMatchingNamesComplete.Should().BeFalse();
    }

    [Test]
    public void BytePinAndProjectedQueryBothProtectLookupIdentity()
    {
        var original = Snapshot("Alex Example", Candidate(Person));
        var bytes = ShadowPeopleCandidateSnapshots.Serialize([original]);
        var pin = Write(bytes);
        var document = JsonNode.Parse(bytes)!;
        document["entries"]![0]!["snapshot"]!["query"]!["names"]![0] = "Someone Else";
        var modified = JsonSerializer.SerializeToUtf8Bytes(document);
        File.WriteAllBytes(pin[..pin.LastIndexOf("#sha256=", StringComparison.Ordinal)], modified);

        Action brokenPin = () => new ShadowPeopleCandidateSnapshots(pin);
        Action replacedPin = () => Load(modified);

        brokenPin.Should().Throw<InvalidDataException>();
        replacedPin.Should().Throw<InvalidDataException>();
    }

    [TestCase("version")]
    [TestCase("domain")]
    [TestCase("projectionVersion")]
    [TestCase("snapshotVersion")]
    public void UnsupportedContractsAreRejectedEvenWithAValidBytePin(string field)
    {
        var document = JsonNode.Parse(
            ShadowPeopleCandidateSnapshots.Serialize([Snapshot("Alex Example", Candidate(Person))])
        )!;
        if (field == "snapshotVersion")
        {
            document["entries"]![0]!["snapshot"]!["version"] = "people-candidate-facts-v4";
        }
        else
        {
            document[field] = field == "domain" ? "venues" : "unsupported-version";
        }

        Action load = () => Load(JsonSerializer.SerializeToUtf8Bytes(document));

        load.Should().Throw<InvalidDataException>();
    }

    [TestCase("duplicateSelection")]
    [TestCase("duplicateFacts")]
    [TestCase("missingFacts")]
    [TestCase("unselectedFacts")]
    [TestCase("wrongHeader")]
    [TestCase("changedHit")]
    [TestCase("missingAliasAggregate")]
    [TestCase("missingActorAggregate")]
    public void InvalidCandidateCorrespondenceOrMissingAggregatesAreRejected(string fault)
    {
        var document = JsonNode.Parse(
            ShadowPeopleCandidateSnapshots.Serialize([Snapshot("Alex Example", Candidate(Person))])
        )!;
        var snapshot = document["entries"]![0]!["snapshot"]!;
        var selected = snapshot["selection"]!["items"]!.AsArray();
        var facts = snapshot["facts"]!.AsArray();
        switch (fault)
        {
            case "duplicateSelection":
                selected.Add(selected[0]!.DeepClone());
                break;
            case "duplicateFacts":
                facts.Add(facts[0]!.DeepClone());
                break;
            case "missingFacts":
                facts.Clear();
                break;
            case "unselectedFacts":
                facts[0]!["hit"]!["uuid"] = Rival.ToString();
                break;
            case "wrongHeader":
                facts[0]!["header"]!["uuid"] = Rival.ToString();
                break;
            case "changedHit":
                facts[0]!["hit"]!["revision"] = 9;
                break;
            case "missingAliasAggregate":
                facts[0]!.AsObject().Remove("acceptedAliasMatchingNames");
                break;
            case "missingActorAggregate":
                facts[0]!.AsObject().Remove("actorMatchingFacts");
                break;
        }

        Action load = () => Load(JsonSerializer.SerializeToUtf8Bytes(document));

        load.Should().Throw<InvalidDataException>();
    }

    [Test]
    public void IdenticalRepeatedQueriesCollapseButConflictingSnapshotsAreRejected()
    {
        var original = Snapshot("Alex Example", Candidate(Person));
        var document = JsonNode.Parse(
            ShadowPeopleCandidateSnapshots.Serialize([original, original])
        )!;
        var entries = document["entries"]!.AsArray();
        entries.Should().ContainSingle();
        entries.Add(entries[0]!.DeepClone());
        Load(JsonSerializer.SerializeToUtf8Bytes(document))
            .Find(original.Query)
            .Snapshot.Should()
            .BeEquivalentTo(original);
        entries[1]!["snapshot"]!["queryHash"] = new string('b', 64);

        Action load = () => Load(JsonSerializer.SerializeToUtf8Bytes(document));

        load.Should().Throw<InvalidDataException>();
    }

    [Test]
    public void QueryOrderDoesNotChangeBytesOrLookupLocators()
    {
        var first = Snapshot("Alex Example", Candidate(Person));
        var second = Snapshot("Another Person", Candidate(Rival));
        var forward = ShadowPeopleCandidateSnapshots.Serialize([first, second]);
        var reverse = ShadowPeopleCandidateSnapshots.Serialize([second, first]);

        reverse.Should().Equal(forward);
        var artifact = Load(reverse);
        artifact.Find(second.Query).Snapshot.Should().BeEquivalentTo(second);
        artifact.Find(first.Query).Snapshot.Should().BeEquivalentTo(first);
        artifact.Find(first.Query).Locator!.ArtifactSha256.Should().Be(artifact.Identity);
    }

    [TestCase("Alex Example (alex@example.test)")]
    [TestCase("Alex Example (https://archive.org/details/@alex_example)")]
    public void AdapterProposesCanonicalIdentityFromAttributedAccountWithoutGrantingAcceptance(
        string taper
    )
    {
        var (input, projection) = SourceActor(taper);
        var supported = CandidateFor(projection);
        var rival = Candidate(Rival) with
        {
            ActorMatchingFacts = new(false, [], [], [], [], [], false, false),
        };
        var snapshot = Snapshot("Alex Example", supported, rival) with
        {
            Query = projection.Query!,
        };
        var bytes = ShadowPeopleCandidateSnapshots.Serialize([snapshot]);
        var adapter = new ShadowPeopleAdapter(null, null, acceptedCandidatesPath: Write(bytes));

        var results = adapter.Process(input).ToArray();
        var decision = CanonicalDecision(results);

        decision.Outcome.Should().Be("proposed");
        decision.SnapshotComplete.Should().BeTrue();
        decision.ProjectionComplete.Should().BeTrue();
        decision.QualifiedForAutomaticAcceptance.Should().BeFalse();
        decision
            .Snapshot.Should()
            .Be(
                new ShadowPeopleCandidateSnapshotLocator(
                    Convert.ToHexStringLower(SHA256.HashData(bytes)),
                    PeopleActorProjection.QueryFingerprint(projection.Query!),
                    snapshot.QueryHash
                )
            );
        decision
            .Candidates.Select(candidate => candidate.PersonUuid)
            .Should()
            .BeEquivalentTo(new[] { Person, Rival });
        var proposal = decision
            .Candidates.Should()
            .ContainSingle(candidate => candidate.SupportedIdentityEvidence)
            .Which;
        proposal.PersonUuid.Should().Be(Person);
        proposal.Comparison.CandidateUuid.Should().Be(Person);
        proposal.Comparison.Disposition.Should().NotBe(IdentityMatchDisposition.AutomaticMatch);
        decision
            .Candidates.Single(candidate => candidate.PersonUuid == Rival)
            .SupportedIdentityEvidence.Should()
            .BeFalse();
        results
            .Single(result => result.Domain == "people")
            .EntityKeys.Should()
            .Contain(proposal.EntityKey);
    }

    [TestCase("incompletePool")]
    [TestCase("attachmentUncertainty")]
    [TestCase("contradictoryKind")]
    public void AdapterRetainsCanonicalRivalsWhenEvidenceCannotSupportAProposedIdentity(string hold)
    {
        var (input, projection) = SourceActor("Alex Example (alex@example.test)");
        var candidate = CandidateFor(projection);
        candidate = candidate with
        {
            ActorMatchingFacts = candidate.ActorMatchingFacts! with
            {
                HasAttachmentUncertainty = hold == "attachmentUncertainty",
                HasConflictingKind = hold == "contradictoryKind",
            },
        };
        var rival = Candidate(Rival) with
        {
            ActorMatchingFacts = new(false, [], [], [], [], [], false, false),
        };
        var snapshot = Snapshot("Alex Example", candidate, rival) with
        {
            Query = projection.Query!,
            Issues = hold == "incompletePool" ? [new(CandidateFactsIssueKind.CandidateLimit)] : [],
        };
        var adapter = new ShadowPeopleAdapter(
            null,
            null,
            acceptedCandidatesPath: Write(ShadowPeopleCandidateSnapshots.Serialize([snapshot]))
        );

        var decision = CanonicalDecision(adapter.Process(input));

        decision.Outcome.Should().Be("unresolved");
        decision.SnapshotComplete.Should().Be(hold != "incompletePool");
        decision.Snapshot.Should().NotBeNull();
        decision
            .Candidates.Select(value => value.PersonUuid)
            .Should()
            .BeEquivalentTo(new[] { Person, Rival });
        decision.Candidates.Should().OnlyContain(value => !value.SupportedIdentityEvidence);
        decision.QualifiedForAutomaticAcceptance.Should().BeFalse();
        if (hold == "contradictoryKind")
        {
            decision
                .Candidates.Single(value => value.PersonUuid == Person)
                .Comparison.Features.Should()
                .Contain(feature =>
                    feature.Name == "actor_kind_conflict"
                    && feature.Agreement == IdentityFeatureAgreement.Conflicts
                );
        }
    }

    [Test]
    public void AdapterDistinguishesUnavailableAndUnexportedQueriesFromACompleteEmptyPool()
    {
        var (input, projection) = SourceActor("Alex Example (alex@example.test)");
        var unknown = Snapshot("Different Query");
        var empty = Snapshot("Alex Example") with { Query = projection.Query! };
        var withoutFile = new ShadowPeopleAdapter();
        var withoutQuery = new ShadowPeopleAdapter(
            null,
            null,
            acceptedCandidatesPath: Write(ShadowPeopleCandidateSnapshots.Serialize([unknown]))
        );
        var withEmptyPool = new ShadowPeopleAdapter(
            null,
            null,
            acceptedCandidatesPath: Write(ShadowPeopleCandidateSnapshots.Serialize([empty]))
        );

        var unavailable = CanonicalDecision(withoutFile.Process(input));
        var unexported = CanonicalDecision(withoutQuery.Process(input));
        var completeEmpty = CanonicalDecision(withEmptyPool.Process(input));

        unavailable.Reason.Should().Be("accepted_candidate_snapshot_unavailable");
        unexported.Reason.Should().Be("accepted_candidate_query_not_exported");
        new[] { unavailable, unexported }
            .Should()
            .OnlyContain(decision =>
                !decision.SnapshotComplete
                && decision.Snapshot == null
                && decision.Candidates.Count == 0
            );
        completeEmpty.Reason.Should().Be("no_accepted_candidates");
        completeEmpty.SnapshotComplete.Should().BeTrue();
        completeEmpty.Snapshot.Should().NotBeNull();
        completeEmpty.Candidates.Should().BeEmpty();
        new[] { unavailable, unexported, completeEmpty }
            .Should()
            .OnlyContain(decision =>
                decision.Outcome == "unresolved" && !decision.QualifiedForAutomaticAcceptance
            );
    }

    private static ShadowCanonicalPeopleDecision CanonicalDecision(
        IEnumerable<ShadowCatalogResult> results
    ) =>
        ((ShadowPeopleProposal)results.Single(result => result.Domain == "people").Proposal!)
            .CanonicalIdentityDecisions.Should()
            .ContainSingle()
            .Which;

    private static PersonCandidateFacts CandidateFor(PeopleActorProjectionResult projection)
    {
        var context = projection.Record.ActorContext!;
        return Candidate(Person) with
        {
            ActorMatchingFacts = new(
                true,
                ["Alex Example"],
                [],
                context.Contacts,
                context.Profiles,
                [],
                false,
                false
            ),
        };
    }

    private static (ShadowCatalogInput Input, PeopleActorProjectionResult Projection) SourceActor(
        string taper
    )
    {
        var sourceUuid = Guid.Parse("00000000-0000-0000-0000-000000000009");
        var artist = new ShadowCatalogArtist(1, Artist, "Fixture artist", ["archive.org"]);
        var fields = new Dictionary<string, string?> { ["taper"] = taper };
        var credits = new RecordingCreditExtractor().Extract(fields);
        var credit = credits.Credits.Should().ContainSingle().Which;
        var projection = PeopleActorProjection.Project(credit, Artist);
        projection.Complete.Should().BeTrue();
        projection.Query.Should().NotBeNull();
        var extraction = new NormalizationBatchExtraction(
            "source_sample",
            42,
            sourceUuid,
            artist.Id,
            Artist,
            null,
            fields,
            null,
            credits,
            null
        );
        var record = JsonSerializer.SerializeToElement(
            new
            {
                kind = "source_sample",
                row = new
                {
                    id = 42L,
                    uuid = sourceUuid,
                    artist_id = artist.Id,
                    taper,
                },
            },
            SourceJson
        );
        return (
            new(
                record,
                JsonSerializer.SerializeToElement(extraction, SourceJson),
                artist,
                "archive.org"
            ),
            projection
        );
    }

    private ShadowPeopleCandidateSnapshots Load(byte[] bytes) => new(Write(bytes));

    private string Write(byte[] bytes)
    {
        var path = Path.Combine(directory, Guid.NewGuid() + ".json");
        File.WriteAllBytes(path, bytes);
        return path + "#sha256=" + Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static NormalizationCandidateFactsSnapshot<PersonCandidateFacts> Snapshot(
        string name,
        params PersonCandidateFacts[] facts
    ) =>
        new(
            NormalizationCandidateService.PeopleFactsVersion,
            new string('a', 64),
            new(IdentityMatchingDomain.People, [name], []),
            new() { MaximumAssertionsPerKind = 1, MaximumWitnessesPerArtist = 0 },
            new(IdentityMatchingDomain.People, facts.Select(fact => fact.Hit).ToArray(), false),
            facts,
            new(
                [new(NormalizationCandidateUniverseKey.People, 17)],
                [new(Owner, 4, 3, Guid.Parse("00000000-0000-0000-0000-000000000006"))],
                [new(Artist, 9), new(OtherArtist, 11)]
            ),
            []
        );

    private static PersonCandidateFacts Candidate(Guid uuid)
    {
        var hit = new NormalizationCandidateHit(uuid, 2, "Alex Example", Owner, 0.8, ["name"]);
        var provenance = new CandidateAssertionProvenance(
            Guid.Parse("00000000-0000-0000-0000-000000000007"),
            uuid,
            Owner,
            2,
            "reviewed",
            [new(Guid.Parse("00000000-0000-0000-0000-000000000008"), "/alias", Quote: "A. Example")]
        );
        var header = new CandidateIdentityHeader(uuid, hit.DisplayText, 2, Owner, "person");
        return new()
        {
            Hit = hit,
            Header = header,
            IdentityClosure = [header],
            Aliases = [new(provenance, "A. Example")],
            AcceptedAliasMatchingNames = ["A. Example", "Alexander Example", "Alex at the Board"],
            AliasMatchingNamesComplete = true,
            AliasAssertionCount = 30,
            AliasSupportCount = 14,
            AliasEvidenceTruncated = true,
            ActorEvidence = [],
            ActorMatchingFacts = new(
                true,
                ["Alex Example"],
                ["Alex E."],
                [new("email", "alex@example.test")],
                [],
                ["person"],
                false,
                false
            ),
            ActorContextCount = 22,
            ActorEvidenceTruncated = true,
            ArtistContexts =
            [
                new(Artist, 12, [new("taper", "reviewed", 12)], [], true),
                new(OtherArtist, 8, [new("transferrer", "provider_assertion", 8)], [], true),
            ],
        };
    }
}
