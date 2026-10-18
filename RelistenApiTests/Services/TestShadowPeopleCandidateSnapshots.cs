using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;
using Relisten.Normalization.Batch;
using CatalogSource = RelistenApiTests.Services.NormalizationDomainFixture.CatalogSource;

namespace RelistenApiTests.Services;

/// <summary>
/// Offline shadow comparisons consume accepted candidate facts with their original clocks.
/// Bounded evidence examples must not replace complete matching names or hide rival people.
/// </summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestShadowPeopleCandidateSnapshots
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizedPeopleService people = null!;
    private NormalizationCandidateService candidates = null!;
    private DirectoryInfo directory = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        people = new(fixture.Db, fixture.Work);
        candidates = new(fixture.Db);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [SetUp]
    public void CreateOutputDirectory() => directory = Directory.CreateTempSubdirectory();

    [TearDown]
    public void RemoveOutputDirectory() => directory?.Delete(true);

    [Test]
    public async Task ExportPreservesAcceptedFactsAndOriginalReadSetAcrossLaterPublication()
    {
        var person = await Person(UniqueName());
        var firstAlias = UniqueName();
        var secondAlias = UniqueName();
        await Alias(person, firstAlias);
        await Alias(person, secondAlias);
        var firstSource = await fixture.Source();
        var secondSource = await fixture.Source();
        await Credit(person, firstSource, "taper");
        await Credit(person, secondSource, "transferrer");
        var query = Query(person.DisplayName);
        var limits = new CandidateFactsLimits { MaximumAssertionsPerKind = 1 };
        var before = await candidates.FindPeopleFactsAsync(query, limits);
        var originalFacts = before.Facts.Should().ContainSingle().Which;
        before.Complete.Should().BeTrue();
        originalFacts.AliasEvidenceTruncated.Should().BeTrue();
        originalFacts
            .AcceptedAliasMatchingNames.Should()
            .BeEquivalentTo(new[] { person.DisplayName, firstAlias, secondAlias });
        originalFacts.ArtistContexts.Should().HaveCount(2);

        var bytes = await ShadowPeopleCandidateSnapshots.ExportAsync(candidates, [query], limits);
        var retainedPath = Retain(bytes, "before.json");
        var retained = new ShadowPeopleCandidateSnapshots(retainedPath);
        var lookup = retained.Find(query);

        lookup.Found.Should().BeTrue();
        lookup.Snapshot.Should().BeEquivalentTo(before);
        lookup.Locator!.ArtifactSha256.Should().Be(retained.Identity);
        lookup.Locator.QueryHash.Should().Be(before.QueryHash);
        var projected = PeopleActorProjection.Candidate(lookup.Snapshot!.Facts.Single());
        projected.Uuid.Should().Be(person.Uuid);
        projected.Names.Select(name => name.Wording).Should().Contain([firstAlias, secondAlias]);
        projected
            .ArtistUuids.Should()
            .BeEquivalentTo(originalFacts.ArtistContexts.Select(context => context.ArtistUuid));

        // Append-published credits supply artist membership, not invented actor accounts.
        originalFacts.ActorMatchingFacts!.HasActorContext.Should().BeFalse();
        lookup
            .Snapshot.Facts.Single()
            .ActorMatchingFacts.Should()
            .BeEquivalentTo(originalFacts.ActorMatchingFacts);

        var laterAlias = UniqueName();
        await Alias(person, laterAlias);
        var afterBytes = await ShadowPeopleCandidateSnapshots.ExportAsync(
            candidates,
            [query],
            limits
        );
        var current = new ShadowPeopleCandidateSnapshots(Retain(afterBytes, "after.json"))
            .Find(query)
            .Snapshot!;
        current.Facts.Single().AcceptedAliasMatchingNames.Should().Contain(laterAlias);
        current
            .ReadSet.CandidateUniverses.Single()
            .Generation.Should()
            .BeGreaterThan(before.ReadSet.CandidateUniverses.Single().Generation);
        current
            .Facts.Single()
            .Header.Revision.Should()
            .BeGreaterThan(originalFacts.Header.Revision);
        var owner = originalFacts.Header.OwnerSubjectUuid;
        current
            .ReadSet.OwnerObservations.Single(value => value.SubjectUuid == owner)
            .Should()
            .NotBe(before.ReadSet.OwnerObservations.Single(value => value.SubjectUuid == owner));

        retained.Find(query).Snapshot.Should().BeEquivalentTo(before);
        new ShadowPeopleCandidateSnapshots(retainedPath)
            .Find(query)
            .Snapshot.Should()
            .BeEquivalentTo(before);
        File.ReadAllBytes(Path.Combine(directory.FullName, "before.json")).Should().Equal(bytes);
        retained
            .Find(query)
            .Snapshot!.Facts.Single()
            .AcceptedAliasMatchingNames.Should()
            .NotContain(laterAlias);
    }

    [Test]
    public async Task CanonicalNamesakeRivalsRemainDistinctWhenAliasWitnessesAreOmitted()
    {
        var sharedName = UniqueName();
        var sharedAlias = UniqueName();
        var first = await Person(sharedName);
        var second = await Person(sharedName);
        await Alias(first, sharedAlias);
        await Alias(second, sharedAlias);
        var source = await fixture.Source();
        var input = await Input(source, sharedAlias);
        var extraction = new RecordingCreditExtractor().Extract(
            new Dictionary<string, string?> { ["taper"] = sharedAlias }
        );
        var query = PeopleActorProjection
            .Project(extraction.Credits.Single(), input.Artist.Uuid)
            .Query!;
        var limits = new CandidateFactsLimits { MaximumAliasEvidenceBytes = 1 };
        var before = await candidates.FindPeopleFactsAsync(query, limits);
        before.Complete.Should().BeTrue();
        before
            .Facts.Select(fact => fact.Hit.Uuid)
            .Should()
            .BeEquivalentTo([first.Uuid, second.Uuid]);
        before.Facts.Should().OnlyContain(fact => fact.Aliases.Count == 0);
        before
            .Facts.Should()
            .OnlyContain(fact => fact.AcceptedAliasMatchingNames!.Contains(sharedAlias));
        var pinnedPath = Retain(
            await ShadowPeopleCandidateSnapshots.ExportAsync(candidates, [query], limits),
            "rivals.json"
        );
        var retained = new ShadowPeopleCandidateSnapshots(pinnedPath);
        retained.Find(query).Snapshot.Should().BeEquivalentTo(before);

        var adapter = new ShadowPeopleAdapter(null, null, acceptedCandidatesPath: pinnedPath);
        var proposal = (ShadowPeopleProposal)
            adapter.Process(input).Single(result => result.Domain == "people").Proposal!;
        var decision = proposal.CanonicalIdentityDecisions.Should().ContainSingle().Which;

        decision.SnapshotComplete.Should().BeTrue();
        decision.QualifiedForAutomaticAcceptance.Should().BeFalse();
        decision.Snapshot!.ArtifactSha256.Should().Be(retained.Identity);
        decision.Snapshot.QueryHash.Should().Be(before.QueryHash);
        decision.Candidates.Should().HaveCount(2);
        decision
            .Candidates.Select(candidate => candidate.PersonUuid)
            .Should()
            .BeEquivalentTo(new[] { first.Uuid, second.Uuid });
        decision
            .Candidates.Should()
            .OnlyContain(candidate =>
                candidate.Comparison.CandidateUuid == candidate.PersonUuid
                && candidate.Comparison.NameTokenSimilarity == 1
            );
    }

    [Test]
    public async Task IncompleteMatchingNamesRemainIncompleteAfterExportAndOfflineLoad()
    {
        var person = await Person(UniqueName());
        await Alias(person, UniqueName());
        var source = await fixture.Source();
        var input = await Input(source, person.DisplayName);
        var extraction = new RecordingCreditExtractor().Extract(
            new Dictionary<string, string?> { ["taper"] = person.DisplayName }
        );
        var query = PeopleActorProjection
            .Project(extraction.Credits.Single(), input.Artist.Uuid)
            .Query!;
        var limits = new CandidateFactsLimits { MaximumAliasMatchingNames = 1 };
        var before = await candidates.FindPeopleFactsAsync(query, limits);
        before.Complete.Should().BeFalse();
        before.Facts.Single().AliasMatchingNamesComplete.Should().BeFalse();
        before.Issues.Should().Contain(issue => issue.PreventsAutomaticAcceptance);
        var pinnedPath = Retain(
            await ShadowPeopleCandidateSnapshots.ExportAsync(candidates, [query], limits),
            "incomplete.json"
        );
        var retained = new ShadowPeopleCandidateSnapshots(pinnedPath);

        var loaded = retained.Find(query).Snapshot!;
        loaded.Should().BeEquivalentTo(before);
        loaded.Complete.Should().BeFalse();
        loaded.Facts.Single().AliasMatchingNamesComplete.Should().BeFalse();

        var adapter = new ShadowPeopleAdapter(null, null, acceptedCandidatesPath: pinnedPath);
        var proposal = (ShadowPeopleProposal)
            adapter.Process(input).Single(result => result.Domain == "people").Proposal!;
        var decision = proposal.CanonicalIdentityDecisions.Should().ContainSingle().Which;
        decision.SnapshotComplete.Should().BeFalse();
        decision.QualifiedForAutomaticAcceptance.Should().BeFalse();
        decision.Snapshot!.QueryHash.Should().Be(before.QueryHash);
    }

    private async Task<CanonicalPerson> Person(string name)
    {
        var person = new CanonicalPerson(Guid.NewGuid(), name);
        var input = await fixture.Capture(name, "people");
        (await fixture.PublishPeople(people, input, new([person], [], [], [])))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        return person;
    }

    private async Task Alias(CanonicalPerson person, string wording)
    {
        var input = await fixture.Capture(wording, "people");
        (
            await fixture.PublishPeople(
                people,
                input,
                new(
                    [],
                    [new PersonAlias(Guid.NewGuid(), person.Uuid, wording, input.Reference)],
                    [],
                    []
                )
            )
        )
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
    }

    private async Task Credit(CanonicalPerson person, CatalogSource source, string role)
    {
        var input = await fixture.Capture(person.DisplayName, "people");
        var credit = new RecordingCredit(
            Guid.NewGuid(),
            source.SourceUuid,
            role,
            person.DisplayName,
            person.Uuid,
            null,
            [input.Reference]
        );
        (await fixture.PublishPeople(people, input, new([], [], [], [credit])))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
    }

    private async Task<ShadowCatalogInput> Input(CatalogSource source, string taper)
    {
        var artistUuid = await fixture.Db.WithWriteConnection(connection =>
            connection.QuerySingleAsync<Guid>("SELECT uuid FROM artists WHERE id=@ArtistId", source)
        );
        var record = JsonSerializer.SerializeToElement(
            new
            {
                kind = "source_sample",
                row = new
                {
                    id = source.SourceId,
                    uuid = source.SourceUuid,
                    artist_id = source.ArtistId,
                    taper,
                },
            }
        );
        return new(
            record,
            null,
            new(source.ArtistId, artistUuid, "Fixture artist", ["archive.org"]),
            "archive.org"
        );
    }

    private string Retain(byte[] bytes, string name)
    {
        var path = Path.Combine(directory.FullName, name);
        File.WriteAllBytes(path, bytes);
        return path + "#sha256=" + Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static NormalizationCandidateQuery Query(string name) =>
        new(IdentityMatchingDomain.People, [name], [], MinimumNameSimilarity: .99);

    private static string UniqueName() =>
        string.Join(
            " ",
            Enumerable
                .Range(0, 2)
                .Select(_ =>
                    string.Concat(
                        Guid.NewGuid().ToByteArray().Select(value => (char)('a' + value % 26))
                    )
                )
        );
}
