using System.Text;
using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace RelistenApiTests.Normalization;

/// <summary>
/// Historical negative review must reproduce old evidence and local IDs, rather than
/// stamping current interpretations with an earlier producer version.
/// </summary>
[TestFixture]
public sealed class TestSourceActorAliasCompatibility
{
    [Test]
    public void LegacyRosterMappingReproducesTheActualPublishedV2Fixture()
    {
        var path = Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "Fixtures",
            "normalization",
            "source-actor-v1-public-fixture.json"
        );
        using var capture = JsonDocument.Parse(File.ReadAllText(path));
        var original = capture
            .RootElement.GetProperty("Published")
            .GetProperty("Attempt")
            .GetProperty("Preparation")
            .GetProperty("Inputs")[0]
            .GetProperty("Scopes")[0];
        var scope = original.GetProperty("OriginalScope");
        var field = original.GetProperty("Fields")[0];
        var raw = field.GetProperty("OriginalText").GetString()!;
        var reference = field.GetProperty("Evidence").Deserialize<NormalizedEvidenceReference>()!;
        var expected = scope
            .GetProperty("Extraction")
            .Deserialize<SourceActorAliasExtraction<NormalizedEvidenceReference>>()!;

        // This is an in-memory mapping test against a genuine old public output. The full
        // historical producer/header and current-head authority are tested by restored DBs.
        var input = new NormalizationInputRevision
        {
            Uuid = scope.GetProperty("InputRevisionUuid").GetGuid(),
            Resource = scope.GetProperty("Resource").Deserialize<NormalizationResourceKey>()!,
            Payload = Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new { source = new { description = raw } })
            ),
            MediaType = "application/json",
            CaptureVersion = "legacy-recording-snapshot-v2",
        };
        var extractor = new RetainedSourceActorAliasExtractor(SourceActorAliasGrammar.LegacyV2);
        var actual = extractor.Extract(input, [new("description", new(raw, reference))]);

        JsonElement
            .DeepEquals(
                JsonSerializer.SerializeToElement(expected),
                JsonSerializer.SerializeToElement(actual)
            )
            .Should()
            .BeTrue(
                "the genuine v2 assertion, coverage, citations and every local UUID must survive"
            );
        actual.Assertions.Should().NotBeEmpty();
        scope.GetProperty("ProducerVersion").GetString().Should().Be(extractor.ProducerVersion);
    }

    [Test]
    public void HistoricalGrammarDoesNotReinterpretANewlySupportedByline()
    {
        const string raw = "Recorded, Mastered and Posted by Alice Vale AKA Night Owl";
        var fields = new Dictionary<string, string?> { ["description"] = raw };

        var historical = new SourceActorAliasExtractor(SourceActorAliasGrammar.LegacyV2).Extract(
            fields
        );
        var current = new SourceActorAliasExtractor().Extract(fields);

        historical.Assertions.Should().BeEmpty();
        historical
            .Unresolved.Should()
            .ContainSingle()
            .Which.Reason.Should()
            .Be("unsupported_alias_context");
        historical.Scopes.Should().ContainSingle().Which.Complete.Should().BeFalse();
        current.Assertions.Should().ContainSingle().Which.Name.Should().Be("Alice Vale");
        current.Scopes.Should().ContainSingle().Which.Complete.Should().BeTrue();
    }

    [Test]
    public void HistoricalRosterKeepsItsOriginalIncompleteContactInterpretation()
    {
        const string raw = "The band:\nAlice Vale (alice at gmail) AKA Night Owl - guitar";
        var fields = new Dictionary<string, string?> { ["description"] = raw };

        var current = new SourceActorAliasExtractor().Extract(fields);
        current.Assertions.Should().ContainSingle().Which.Name.Should().Be("Alice Vale");
        foreach (
            var grammar in new[]
            {
                SourceActorAliasGrammar.LegacyV2,
                SourceActorAliasGrammar.LegacyV3,
            }
        )
        {
            var historical = new SourceActorAliasExtractor(grammar).Extract(fields);
            var assertion = historical.Assertions.Should().ContainSingle().Which;
            assertion.Name.Should().Be("Alice Vale (alice at gmail)");
            assertion.Aliases.Should().ContainSingle().Which.Wording.Should().Be("Night Owl");
            assertion
                .Relationships.Should()
                .OnlyContain(relationship => relationship.Roles.Count == 0);
        }
        current.Unresolved.Should().Contain(issue => issue.Reason == "incomplete_actor_contact");
    }

    [Test]
    public void LegacyV3KeepsItsPublishedBylinesWithoutAdoptingNewActionForms()
    {
        var historical = new SourceActorAliasExtractor(SourceActorAliasGrammar.LegacyV3);
        const string original = "Recorded, Mastered and Posted by Pat Myers AKA DATPAT";
        var originalResult = historical.Extract(
            new Dictionary<string, string?> { ["description"] = original }
        );

        var assertion = originalResult.Assertions.Should().ContainSingle().Which;
        assertion.Name.Should().Be("Pat Myers");
        assertion.NameEvidence.Quote.Should().Be("Pat Myers");
        assertion.Context.Heading.Quote.Should().Be("Recorded, Mastered and Posted by");
        assertion.Aliases.Should().ContainSingle().Which.Wording.Should().Be("DATPAT");
        assertion.Relationships.Should().OnlyContain(relationship => relationship.Roles.Count == 0);

        var fields = new Dictionary<string, string?>
        {
            ["description"] = "Transfer by Alice Vale AKA Night Owl",
        };
        historical.Extract(fields).Assertions.Should().BeEmpty();
        new SourceActorAliasExtractor()
            .Extract(fields)
            .Assertions.Should()
            .ContainSingle()
            .Which.Name.Should()
            .Be("Alice Vale");
    }

    [Test]
    public void RetainedLegacyV3BylineHasItsOwnProducerNamespaceAndOriginalCoordinates()
    {
        const string raw = "🎵<div>Recorded, Mastered and Posted by Zoë AKA Lark</div>";
        var input = new NormalizationInputRevision
        {
            Uuid = Guid.Parse("3081eecf-a5c8-4847-815a-2c81b1d06ac2"),
            Resource = new("archive.org", "item_metadata", "historical-byline"),
            Payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { description = raw })),
            MediaType = "application/json",
            CaptureVersion = "archive-item-response-v1",
        };
        ProviderTextObservation[] fields =
        [
            new("description", new(raw, new(input.Uuid, "/description"))),
        ];

        var historical = new RetainedSourceActorAliasExtractor(
            SourceActorAliasGrammar.LegacyV3
        ).Extract(input, fields);
        var current = new RetainedSourceActorAliasExtractor().Extract(input, fields);

        var oldAssertion = historical.Assertions.Should().ContainSingle().Which;
        var currentAssertion = current.Assertions.Should().ContainSingle().Which;
        oldAssertion.Name.Should().Be("Zoë");
        oldAssertion.NameEvidence.Should().Be(currentAssertion.NameEvidence);
        oldAssertion.Context.Should().BeEquivalentTo(currentAssertion.Context);
        oldAssertion.AssertionUuid.Should().NotBe(currentAssertion.AssertionUuid);
        oldAssertion.ActorReferenceUuid.Should().NotBe(currentAssertion.ActorReferenceUuid);
        oldAssertion
            .Aliases.Single()
            .ReferenceUuid.Should()
            .NotBe(currentAssertion.Aliases.Single().ReferenceUuid);
        using var document = NormalizationRetainedDocument.Open(input);
        document.ValidateEvidence(oldAssertion.NameEvidence);
        foreach (var evidence in oldAssertion.Aliases.Single().Evidence)
        {
            document.ValidateEvidence(evidence);
        }
    }

    [Test]
    public void CurrentAndHistoricalMappingsKeepDistinctProducerNamespacesForTheSameRoster()
    {
        const string raw = "The band:\nAlice Vale AKA Night Owl - guitar";
        var input = new NormalizationInputRevision
        {
            Uuid = Guid.Parse("ea459754-c510-4ff6-bfd3-8c0353007b93"),
            Resource = new("archive.org", "item_metadata", "versioned-roster"),
            Payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { description = raw })),
            MediaType = "application/json",
            CaptureVersion = "archive-item-response-v1",
        };
        ProviderTextObservation[] fields =
        [
            new("description", new(raw, new(input.Uuid, "/description"))),
        ];

        var historical = new RetainedSourceActorAliasExtractor(
            SourceActorAliasGrammar.LegacyV2
        ).Extract(input, fields);
        var current = new RetainedSourceActorAliasExtractor().Extract(input, fields);

        var oldAssertion = historical.Assertions.Should().ContainSingle().Which;
        var currentAssertion = current.Assertions.Should().ContainSingle().Which;
        oldAssertion.Name.Should().Be(currentAssertion.Name);
        oldAssertion.NameEvidence.Should().Be(currentAssertion.NameEvidence);
        oldAssertion.FieldSha256.Should().Be(currentAssertion.FieldSha256);
        oldAssertion.AssertionUuid.Should().NotBe(currentAssertion.AssertionUuid);
        oldAssertion.ActorReferenceUuid.Should().NotBe(currentAssertion.ActorReferenceUuid);
        oldAssertion
            .Aliases.Single()
            .ReferenceUuid.Should()
            .NotBe(currentAssertion.Aliases.Single().ReferenceUuid);
        oldAssertion.Context.Should().BeEquivalentTo(currentAssertion.Context);
        oldAssertion.Relationships.Should().OnlyContain(r => r.Roles.Count == 0);
        currentAssertion.Relationships.Should().OnlyContain(r => r.Roles.Count == 0);
    }

    [TestCase("Recorded by Alice Vale AKA \"Night & Day\"", "Night & Day")]
    [TestCase("Recorded by Alice Vale (AKA \"Night & Day\")", "Night & Day")]
    [TestCase("Recorded by Alice Vale AKA “Night & Day”", "Night & Day")]
    [TestCase("Recorded by Alice Vale AKA 'Night & Day'", "Night & Day")]
    public void AQuotedBylineMustKeepAnExactBoundedAlias(string raw, string expectedAlias)
    {
        var result = new SourceActorAliasExtractor().Extract(
            new Dictionary<string, string?> { ["description"] = raw }
        );

        var assertion = result.Assertions.Should().ContainSingle().Which;
        assertion.Name.Should().Be("Alice Vale");
        assertion.Aliases.Should().ContainSingle().Which.Wording.Should().Be(expectedAlias);
        assertion.Relationships.Should().OnlyContain(relationship => relationship.Roles.Count == 0);
        result.Unresolved.Should().BeEmpty();
        result.Scopes.Should().ContainSingle().Which.Complete.Should().BeTrue();
        foreach (var evidence in assertion.Aliases.Single().Evidence.Append(assertion.NameEvidence))
        {
            var quote = string.Concat(
                raw.EnumerateRunes()
                    .Skip(evidence.StartCodePoint)
                    .Take(evidence.EndCodePoint - evidence.StartCodePoint)
                    .Select(rune => rune.ToString())
            );
            quote.Should().Be(evidence.Quote);
        }
    }

    [TestCase("Recorded by Alice Vale AKA \"Night Owl")]
    [TestCase("Recorded by Alice Vale AKA “Night Owl\"")]
    [TestCase("Recorded by Alice Vale AKA \"Night Owl\" with help from Bob Reed")]
    [TestCase("Recorded by Alice Vale AKA \"Night Owl\" and Bob Reed")]
    [TestCase("Recorded by Alice Vale AKA \"Night Owl\"; Bob Reed")]
    [TestCase("Recorded by Alice Vale AKA \"Night Owl\".")]
    [TestCase("Recorded by Alice Vale AKA \"Night Owl\")")]
    [TestCase("Recorded by Alice Vale (AKA \"Night Owl\") with help from Bob Reed")]
    public void AnUnclosedQuoteOrTrailingTextCannotBecomeAliasWording(string raw)
    {
        var result = new SourceActorAliasExtractor().Extract(
            new Dictionary<string, string?> { ["description"] = raw }
        );

        result.Assertions.Should().BeEmpty();
        result
            .Unresolved.Should()
            .ContainSingle()
            .Which.Reason.Should()
            .Be("ambiguous_byline_alias");
        result.Unresolved.Single().Evidence.Should().ContainSingle().Which.Quote.Should().Be(raw);
        result.Scopes.Should().ContainSingle().Which.Complete.Should().BeFalse();
    }
}
