using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;

namespace RelistenApiTests.Normalization;

public partial class TestTrackTitleExtraction
{
    [TestCase("09.Scarlet", "Scarlet", TrackTitleStructuralKind.TrackNumber)]
    [TestCase("(Set2) Scarlet", "Scarlet", TrackTitleStructuralKind.SetLabel)]
    [TestCase("Scarlet (Set 2)", "Scarlet", TrackTitleStructuralKind.SetLabel)]
    [TestCase("Scarlet [12:34]", "Scarlet", TrackTitleStructuralKind.Duration)]
    [TestCase("Scarlet (1:02:03)", "Scarlet", TrackTitleStructuralKind.Duration)]
    [TestCase("Scarlet (cut)", "Scarlet", TrackTitleStructuralKind.PartialCoverage)]
    [TestCase("Scarlet (aborted)", "Scarlet", TrackTitleStructuralKind.Aborted)]
    [TestCase("Scarlet (filler)", "Scarlet", TrackTitleStructuralKind.Filler)]
    [TestCase(
        "1977-05-08 d1t09 Scarlet.flac",
        "Scarlet",
        TrackTitleStructuralKind.FilenameMetadata
    )]
    [TestCase("1977-05-08 09.Scarlet.flac", "Scarlet", TrackTitleStructuralKind.FilenameMetadata)]
    [TestCase(
        "gd76-06-15 s2t10 Scarlet Begonias",
        "Scarlet Begonias",
        TrackTitleStructuralKind.FilenameMetadata
    )]
    [TestCase(
        "gd78-04-16d1t09_Scarlet Begonias",
        "Scarlet Begonias",
        TrackTitleStructuralKind.FilenameMetadata
    )]
    [TestCase(
        "gd1981-08-12 Set 2. Track04. Scarlet Begonias",
        "Scarlet Begonias",
        TrackTitleStructuralKind.FilenameMetadata
    )]
    public void PeelsOnlyExplicitStructuralQualifiers(
        string raw,
        string expected,
        TrackTitleStructuralKind kind
    )
    {
        var result = extractor.Extract(Guid.NewGuid(), raw);

        result.OriginalTitle.Should().Be(raw);
        result.Segments.Single().Title.Should().Be(expected);
        result.Qualifiers.Should().Contain(qualifier => qualifier.StructuralKind == kind);
        AssertExtractionEvidence(result);
    }

    [Test]
    public void KeepsStackedStructureAndPartialCoverageSeparateFromMusicalVariants()
    {
        const string raw = "09.(Set 2) Scarlet Jam (cut) [12:34]";
        var result = extractor.Extract(Guid.NewGuid(), raw);
        var segment = result.Segments.Single();

        segment.Title.Should().Be("Scarlet Jam");
        segment.Coverage.Should().Be(TitleCoverage.Partial);
        segment.CoreTitle!.Title.Should().Be("Scarlet");
        segment.CoreTitle.Variants.Single().Kind.Should().Be(TrackTitleVariantKind.Jam);
        result
            .Qualifiers.Select(qualifier => qualifier.StructuralKind)
            .Should()
            .BeEquivalentTo(
                new TrackTitleStructuralKind?[]
                {
                    TrackTitleStructuralKind.TrackNumber,
                    TrackTitleStructuralKind.SetLabel,
                    TrackTitleStructuralKind.PartialCoverage,
                    TrackTitleStructuralKind.Duration,
                }
            );
        AssertExtractionEvidence(result);
    }

    [TestCase("Scarlet Jam", "Scarlet", TrackTitleVariantKind.Jam)]
    [TestCase("Mountain Jam", "Mountain", TrackTitleVariantKind.Jam)]
    [TestCase("Scarlet (tease)", "Scarlet", TrackTitleVariantKind.Tease)]
    [TestCase("Tweezer Reprise", "Tweezer", TrackTitleVariantKind.Reprise)]
    [TestCase("Scarlet [practice]", "Scarlet", TrackTitleVariantKind.Practice)]
    public void RetainsTheWholeNamedVariantAndOffersOnlyAnAlternativeCoreCandidate(
        string raw,
        string core,
        TrackTitleVariantKind kind
    )
    {
        var result = extractor.Extract(Guid.NewGuid(), raw);
        var segment = result.Segments.Single();

        segment.Title.Should().Be(raw);
        segment.Evidence.Quote.Should().Be(raw);
        segment.CoreTitle!.Title.Should().Be(core);
        segment.CoreTitle.Variants.Single().Kind.Should().Be(kind);
        result.Qualifiers.Should().BeEmpty();
        AssertExtractionEvidence(result);
    }

    [TestCase("Scarlet-Fire")]
    [TestCase("Scarlet + Fire")]
    [TestCase("99 Problems")]
    [TestCase("46 Days")]
    [TestCase("3.14159")]
    [TestCase("1979")]
    [TestCase("Jam on the River")]
    [TestCase("Scarlet (Part 2)")]
    [TestCase("Scarlet (12:99)")]
    [TestCase("1977-02-30 d1t09 Scarlet")]
    [TestCase("2024-01-019 Scarlet")]
    [TestCase("gd81-03-05 Blues")]
    [TestCase("gd81-02-30 s2t10 Scarlet Begonias")]
    [TestCase("gd1900-02-29d1t09_Scarlet Begonias")]
    [TestCase("10 Scarlet")]
    public void RetainsLiteralNamesAndUnrecognizedSyntax(string raw)
    {
        var result = extractor.Extract(Guid.NewGuid(), raw);

        result.Segments.Single().Title.Should().Be(raw);
        result.Segments.Single().CoreTitle.Should().BeNull();
        result.Qualifiers.Should().BeEmpty();
    }

    [Test]
    public void VariantAndStructuralEvidenceRetainsUnicodeCodePointCoordinates()
    {
        const string raw = "09.🎹 Café 🌙 Jam (Set2)";
        var result = extractor.Extract(Guid.NewGuid(), raw);
        var segment = result.Segments.Single();

        segment.Title.Should().Be("🎹 Café 🌙 Jam");
        segment.CoreTitle!.Title.Should().Be("🎹 Café 🌙");
        segment.Evidence.StartCodePoint.Should().Be(3);
        segment.CoreTitle.Evidence.StartCodePoint.Should().Be(3);
        AssertExtractionEvidence(result);
    }

    [Test]
    public void MissingOptionalSyntaxFactsRemainAbsentWhenReadingHistoricalResults()
    {
        var original = extractor.Extract(Guid.NewGuid(), "Scarlet").Segments.Single();
        var json = JsonSerializer.Serialize(original);

        json.Should().NotContain("CoreTitle");
        JsonSerializer.Deserialize<TrackTitleSegment>(json)!.CoreTitle.Should().BeNull();
        JsonSerializer
            .Serialize(new TrackTitleQualifier("separator", original.Evidence))
            .Should()
            .NotContain("StructuralKind");
    }

    private static void AssertExtractionEvidence(TrackTitleExtraction result)
    {
        foreach (var segment in result.Segments)
        {
            AssertEvidence(result.OriginalTitle, segment.Evidence);
            if (segment.CoreTitle is { } core)
            {
                AssertEvidence(result.OriginalTitle, core.Evidence);
                foreach (var variant in core.Variants)
                {
                    AssertEvidence(result.OriginalTitle, variant.Evidence);
                }
            }
        }

        foreach (var qualifier in result.Qualifiers)
        {
            AssertEvidence(result.OriginalTitle, qualifier.Evidence);
        }
    }
}
