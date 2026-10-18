using System.Text;
using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace RelistenApiTests.Normalization;

/// <summary>
/// Full source fields exercise roster context, not canonical identity or participation.
/// The retained real field belongs to the permitted exposed development cohort.
/// </summary>
[TestFixture]
public sealed class TestSourceActorRosterContinuation
{
    [Test]
    public void CompleteOriginalHtmlRosterPreservesLaterExplicitAliasAndConditionalContext()
    {
        var field = FullOriginalField();
        var result = Extract(field.Value);

        var assertion = result.Assertions.Should().ContainSingle().Which;
        assertion.Name.Should().Be("Jon Cunningham");
        assertion.Aliases.Should().ContainSingle().Which.Wording.Should().Be("Corn Mo");
        assertion.Context.Heading.Quote.Should().Be("Band line-up:");
        assertion
            .Context.Qualifiers.Should()
            .ContainSingle()
            .Which.Quote.Should()
            .Be("-- and some or all of the following --\u00a0");
        assertion
            .Context.Statement.Quote.Should()
            .Contain("Christy Stewart, Jon Cunningham (aka Corn Mo)");
        assertion
            .Relationships.Should()
            .OnlyContain(relationship =>
                relationship.Relationship == ActorReferenceRelationship.ExplicitAlias
                && relationship.Roles.Count == 0
            );
        result
            .Unresolved.Should()
            .Contain(issue =>
                issue.Evidence.Any(evidence =>
                    evidence.Quote.StartsWith("Tim DeLaughter -", StringComparison.Ordinal)
                )
            );
        result.Scopes.Single().Complete.Should().BeFalse();
        AssertCitations(field.Value, result);
    }

    [Test]
    public void OriginalFullFieldAlsoSurvivesRetainedProductionCoordinates()
    {
        var field = FullOriginalField();
        var input = new NormalizationInputRevision
        {
            Uuid = Guid.NewGuid(),
            Resource = new("archive.org", "item_metadata", "roster-continuation-fixture"),
            Payload = JsonSerializer.SerializeToUtf8Bytes(new { description = field.Value }),
            MediaType = "application/json",
            CaptureVersion = "archive-item-response-v1",
        };
        var result = new RetainedSourceActorAliasExtractor().Extract(
            input,
            [new(field.Field, new(field.Value, new(input.Uuid, "/description")))]
        );

        var assertion = result.Assertions.Should().ContainSingle().Which;
        assertion.Name.Should().Be("Jon Cunningham");
        assertion.Aliases.Should().ContainSingle().Which.Wording.Should().Be("Corn Mo");
        assertion.Context.Qualifiers.Should().ContainSingle();
        result.Scopes.Single().Complete.Should().BeFalse();
        using var document = NormalizationRetainedDocument.Open(input);
        foreach (
            var citation in new[]
            {
                assertion.NameEvidence,
                assertion.Context.Heading,
                assertion.Context.Statement,
            }
                .Concat(assertion.Context.Qualifiers)
                .Concat(assertion.Aliases.SelectMany(alias => alias.Evidence))
        )
        {
            document.ValidateEvidence(citation);
        }
    }

    [Test]
    public void UnknownCoordinatedItemsRetainContextWithoutEmittingTheUncertainRowsAlias()
    {
        const string qualifier = "-- and some or all of the following --";
        const string raw =
            "Personnel:\n"
            + qualifier
            + "\n"
            + "Alice Vale aka Lark - guitar, glass harmonica\n"
            + "Bob Reed aka Finch - vocals";
        var result = Extract(raw);

        var assertion = result.Assertions.Should().ContainSingle().Which;
        assertion.Name.Should().Be("Bob Reed");
        assertion.Aliases.Should().ContainSingle().Which.Wording.Should().Be("Finch");
        assertion.Context.Qualifiers.Should().ContainSingle().Which.Quote.Should().Be(qualifier);
        result
            .Unresolved.Should()
            .ContainSingle()
            .Which.Evidence.Single()
            .Quote.Should()
            .Be("Alice Vale aka Lark - guitar, glass harmonica");
        result.Scopes.Single().Complete.Should().BeFalse();
        AssertCitations(raw, result);
    }

    [Test]
    public void StandalonePedalSteelIsASupportedCompoundInstrument()
    {
        const string raw = "Personnel:\nAlice Vale - pedal steel\nBob Reed aka Finch - vocals";
        var result = Extract(raw);

        result.Assertions.Should().ContainSingle().Which.Name.Should().Be("Bob Reed");
        result.Unresolved.Should().BeEmpty();
        result.Scopes.Single().Complete.Should().BeTrue();
        AssertCitations(raw, result);
    }

    [TestCase("Alice Vale - glass harmonica")]
    [TestCase("Alice Vale - guitar recorded nearby")]
    [TestCase("Alice Vale - guitar, recorded nearby - vocals")]
    [TestCase("Recorded by Alice - vocals")]
    [TestCase("Digitally recorded by Alice - vocals")]
    [TestCase("Taper: Alice - piano")]
    [TestCase("The band played an encore - guitar")]
    [TestCase("An encore was played - guitar")]
    [TestCase("Notes about this recording - piano")]
    [TestCase("This recording - guitar, samples")]
    [TestCase("Some notes - guitar, samples")]
    [TestCase("Alice Vale - guitar, recorded nearby")]
    [TestCase("Alice Vale - guitar, not vocals")]
    [TestCase("Notes - piano, good memories")]
    [TestCase("Recording notes - guitar, samples")]
    [TestCase("Notes - piano")]
    [TestCase("Notes - piano, good memories - vocals")]
    [TestCase("Bonus material - guitar, samples")]
    [TestCase("Set 2 - piano, samples")]
    [TestCase("01. Song - piano, samples")]
    public void UnknownStandaloneItemsAndDefiniteProseOrSectionsCloseTheRoster(string boundary)
    {
        var raw = "Personnel:\n" + boundary + "\nSong aka Alternate Song - vocals";
        var result = Extract(raw);

        result.Assertions.Should().BeEmpty();
        result
            .Unresolved.Should()
            .Contain(issue =>
                issue.Evidence.Any(evidence => evidence.Quote == "Song aka Alternate Song - vocals")
            );
        result.Scopes.Single().Complete.Should().BeFalse();
        AssertCitations(raw, result);
    }

    [TestCase("guitar, an unrecognized instrument item with many words")]
    [TestCase("guitar, glass harmonica, ")]
    [TestCase("samples, various noises")]
    public void AContinuationNeedsBoundedNonemptyItemsAndACompleteKnownAnchor(string suffix)
    {
        var raw = "Personnel:\nAlice Vale - " + suffix + "\nBob Reed aka Finch - vocals";
        var result = Extract(raw);

        result.Assertions.Should().BeEmpty();
        result.Scopes.Single().Complete.Should().BeFalse();
        AssertCitations(raw, result);
    }

    [TestCase("guitar, , vocals")]
    [TestCase("guitar,,")]
    [TestCase("guitar and and vocals")]
    public void MalformedSeparatorsDoNotProduceAnApparentlyCompleteAlias(string suffix)
    {
        var raw = "Personnel:\nAlice Vale aka Lark - " + suffix;
        var result = Extract(raw);

        result.Assertions.Should().BeEmpty();
        result.Unresolved.Should().NotBeEmpty();
        result.Scopes.Single().Complete.Should().BeFalse();
        AssertCitations(raw, result);
    }

    [TestCase("Alice Vale aka \"Night - Day\"")]
    [TestCase("Alice Vale (aka Night - Day)")]
    [TestCase("Alice Vale aka 'Night - Day'")]
    [TestCase("Alice Vale aka “Night - Day”")]
    public void DashesInsideExplicitAliasBoundariesDoNotBecomeInstrumentDelimiters(string actor)
    {
        var raw = "Personnel:\n" + actor + " - vocals";
        var result = Extract(raw);

        result
            .Assertions.Should()
            .ContainSingle()
            .Which.Aliases.Should()
            .ContainSingle()
            .Which.Wording.Should()
            .Be("Night - Day");
        AssertCitations(raw, result);
    }

    [TestCase("Recorded by the Moon")]
    [TestCase("The band played an encore")]
    public void ProseWordsInsideAnExplicitQuotedAliasRemainLiteralNameEvidence(string wording)
    {
        var raw = "Personnel:\nAlice Vale aka \"" + wording + "\" - vocals";
        var result = Extract(raw);

        result
            .Assertions.Should()
            .ContainSingle()
            .Which.Aliases.Should()
            .ContainSingle()
            .Which.Wording.Should()
            .Be(wording);
        AssertCitations(raw, result);
    }

    [Test]
    public void WordsInsideAnUncertainRowsQuotedAliasDoNotBecomeProsePrefixes()
    {
        const string raw =
            "Personnel:\nAlice Vale aka \"Recorded by the Moon\" - guitar, samples\n"
            + "Bob Reed aka Finch - vocals";
        var result = Extract(raw);

        result.Assertions.Should().ContainSingle().Which.Name.Should().Be("Bob Reed");
        result.Unresolved.Should().ContainSingle();
        result.Scopes.Single().Complete.Should().BeFalse();
        AssertCitations(raw, result);
    }

    private static SourceActorAliasExtraction<NormalizationTextEvidence> Extract(string raw) =>
        new SourceActorAliasExtractor().Extract(
            new Dictionary<string, string?> { ["description"] = raw }
        );

    private static (string Field, string Value) FullOriginalField()
    {
        var path = Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "Fixtures",
            "normalization",
            "source-actor-roster-3601258.json"
        );
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        return (
            document.RootElement.GetProperty("field").GetString()!,
            document.RootElement.GetProperty("value").GetString()!
        );
    }

    private static void AssertCitations(
        string raw,
        SourceActorAliasExtraction<NormalizationTextEvidence> result
    )
    {
        var points = raw.EnumerateRunes().Select(rune => rune.ToString()).ToArray();
        var citations = result
            .Assertions.SelectMany(assertion =>
                new[]
                {
                    assertion.NameEvidence,
                    assertion.Context.Heading,
                    assertion.Context.Statement,
                }
                    .Concat(assertion.Context.Qualifiers)
                    .Concat(assertion.Aliases.SelectMany(alias => alias.Evidence))
                    .Concat(
                        assertion.Relationships.SelectMany(relationship => relationship.Evidence)
                    )
            )
            .Concat(result.Unresolved.SelectMany(issue => issue.Evidence))
            .Concat(result.Scopes.SelectMany(scope => scope.Evidence));
        foreach (var evidence in citations)
        {
            evidence.Field.Should().Be("description");
            evidence.JsonPointer.Should().Be("/description");
            evidence.StartCodePoint.Should().BeGreaterThanOrEqualTo(0);
            evidence.EndCodePoint.Should().BeInRange(evidence.StartCodePoint, points.Length);
            string.Concat(points[evidence.StartCodePoint..evidence.EndCodePoint])
                .Should()
                .Be(evidence.Quote);
        }
    }
}
