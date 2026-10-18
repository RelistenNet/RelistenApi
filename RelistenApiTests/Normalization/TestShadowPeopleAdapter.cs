using System.Text.Json;
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
        firstProposal.Relationships.Should().BeEmpty();
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

        People(adapter.Process(first)).Relationships.Should().BeEmpty();
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
    public void SharedAccountFanOutIsExplicitlyIncompleteWithoutDroppingCreditsOrPeople()
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
        coverage.Complete.Should().BeFalse();
        coverage.AvailableAccounts.Should().Be(110);
        coverage.ComparedAccounts.Should().Be(people.Relationships.Count);
        coverage.ComparedAccounts.Should().BeLessThan(coverage.AvailableAccounts);
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

    private static ShadowPeopleAdapter Observing(params ShadowCatalogInput[] inputs)
    {
        var adapter = new ShadowPeopleAdapter();
        foreach (var input in inputs)
        {
            adapter.Observe(input.Record);
            if (input.Extraction is { } extraction)
            {
                adapter.Observe(extraction);
            }
        }
        return adapter;
    }

    private static ShadowPeopleProposal People(IEnumerable<ShadowCatalogResult> results) =>
        (ShadowPeopleProposal)results.Single(result => result.Domain == "people").Proposal!;

    private static ShadowCreditsProposal Credits(IEnumerable<ShadowCatalogResult> results) =>
        (ShadowCreditsProposal)results.Single(result => result.Domain == "credits").Proposal!;

    private static ShadowCatalogInput Input(string? taper = null, string? description = null)
    {
        var sourceUuid = Guid.NewGuid();
        var artist = new ShadowCatalogArtist(1, Guid.NewGuid(), "Fixture artist", ["archive.org"]);
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
