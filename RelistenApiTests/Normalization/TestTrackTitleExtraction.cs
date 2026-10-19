using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace RelistenApiTests.Normalization;

/// <summary>
/// Real title examples come from the retained cross-artist corpus in
/// docs/research/2026-10-17-full-normalization/evaluation. These checks exercise
/// interpretation only; they do not establish composition or person identity.
/// </summary>
[TestFixture]
public partial class TestTrackTitleExtraction
{
    private readonly TrackTitleExtractor extractor = new();

    [TestCase("The Moma Dance", "The Moma Dance", Description = "Phish, track:13005808")]
    [TestCase(
        "...Driving Song >",
        "Driving Song",
        Description = "Widespread Panic, track:19483584"
    )]
    [TestCase(
        "Don't Ease Me In ->",
        "Don't Ease Me In",
        Description = "Grateful Dead, track:1790334"
    )]
    [TestCase(
        "Morph Dusseldorf ->",
        "Morph Dusseldorf",
        Description = "Disco Biscuits, track:187162"
    )]
    [TestCase("The Wind", "The Wind", Description = "Ween, track:819603")]
    [TestCase(
        "01 nothings alright in my life",
        "nothings alright in my life",
        Description = "Godspeed, track:524391"
    )]
    [TestCase(
        "Stop That Train (partial)",
        "Stop That Train",
        Description = "Jerry, local:track:35975434"
    )]
    public void RetainsSupportedSongMentionsInTheirArtistScope(string raw, string expected)
    {
        var artist = Guid.NewGuid();
        var result = extractor.Extract(artist, raw);

        result.ArtistUuid.Should().Be(artist);
        result.OriginalTitle.Should().Be(raw);
        result.IsResolved.Should().BeTrue();
        var segment = result.Segments.Should().ContainSingle().Subject;
        segment.Title.Should().Be(expected);
        segment.Kind.Should().Be(TrackContentKind.Song);
        segment.Basis.Should().Be("title_derived");
        segment.OccurrenceUuid.Should().BeNull();
        segment.StartMs.Should().BeNull();
        segment.EndMs.Should().BeNull();
        AssertEvidence(raw, segment.Evidence);
    }

    [Test]
    public void PreservesPartialCoverageAndSegueAsSeparateAssertions()
    {
        var result = extractor.Extract(Guid.NewGuid(), "...Driving Song >");

        var segment = result.Segments.Single();
        segment.Coverage.Should().Be(TitleCoverage.PartialMarker);
        segment.TransitionAfter.Should().Be(TitleTransition.SegueMarker);
        result
            .Qualifiers.Select(qualifier => qualifier.Kind)
            .Should()
            .BeEquivalentTo("partial_marker", "separator");
        result.Qualifiers.Should().OnlyContain(qualifier => qualifier.Evidence.Field == "title");

        var partial = extractor.Extract(Guid.NewGuid(), "Stop That Train (partial)");
        partial.Segments.Single().Coverage.Should().Be(TitleCoverage.Partial);
        partial.Segments.Single().Title.Should().Be("Stop That Train");
    }

    [TestCase("Only My Fear > Rilke", "Only My Fear", "Rilke", TitleTransition.SegueMarker)]
    [TestCase("BBF3 / The Sad Mafioso", "BBF3", "The Sad Mafioso", TitleTransition.Unknown)]
    public void KeepsMultipleTitleMentionsWithoutInventingTheirAudioBoundaries(
        string raw,
        string first,
        string second,
        TitleTransition transition
    )
    {
        var result = extractor.Extract(Guid.NewGuid(), raw);

        result.Segments.Select(segment => segment.Title).Should().Equal(first, second);
        result.Segments.Select(segment => segment.Position).Should().Equal(1, 2);
        result.Segments.First().TransitionAfter.Should().Be(transition);
        result.Sequence.Should().Be(TrackTitleSequence.TitleOrder);
        foreach (var segment in result.Segments)
        {
            AssertEvidence(raw, segment.Evidence);
            segment.StartMs.Should().BeNull();
            segment.EndMs.Should().BeNull();
            segment.OccurrenceUuid.Should().BeNull();
        }
    }

    [Test]
    public void RetainsRepeatedMentionsAndRepriseTitles()
    {
        var result = extractor.Extract(Guid.NewGuid(), "Tweezer > Tweezer Reprise > Tweezer");

        result
            .Segments.Select(segment => segment.Title)
            .Should()
            .Equal("Tweezer", "Tweezer Reprise", "Tweezer");
        result.Segments.Should().HaveCount(3);
    }

    [Test]
    public void PeelsOnlyTheRecognizedFilenameMetadataPattern()
    {
        const string raw =
            "1970-04-03 - A Pair From 1970 - Köln & Sussex - 101 No Opportunity Necessary, No Experience Needed.mp3";
        var result = extractor.Extract(Guid.NewGuid(), raw);

        result
            .Segments.Single()
            .Title.Should()
            .Be("No Opportunity Necessary, No Experience Needed");
        result.OriginalTitle.Should().Be(raw);
        result
            .Qualifiers.Select(qualifier => qualifier.Kind)
            .Should()
            .Equal("filename_metadata", "audio_extension");
        foreach (var qualifier in result.Qualifiers)
        {
            AssertEvidence(raw, qualifier.Evidence);
        }

        extractor.Extract(Guid.NewGuid(), "1979").Segments.Single().Title.Should().Be("1979");
        extractor
            .Extract(Guid.NewGuid(), "99 Problems")
            .Segments.Single()
            .Title.Should()
            .Be("99 Problems");
        extractor
            .Extract(Guid.NewGuid(), "AC/DC Bag")
            .Segments.Single()
            .Title.Should()
            .Be("AC/DC Bag");
    }

    [TestCase("TimmyNodarMattEllis_2008-09-15_t01")]
    [TestCase("Track 01.flac")]
    [TestCase("d1t02.mp3")]
    [TestCase("gd1977-05-08d1t01.flac")]
    [TestCase("10 (unknown)")]
    [TestCase("Intro")]
    [TestCase("intros")]
    public void LeavesUnknownContentsWithoutAProvisionalSongIdentity(string raw)
    {
        var result = extractor.Extract(Guid.NewGuid(), raw);

        result.IsResolved.Should().BeFalse();
        result.Reason.Should().NotBeNullOrEmpty();
        result.Segments.Should().OnlyContain(segment => segment.Title == null);
    }

    [TestCase("banter", TrackContentKind.Speech)]
    [TestCase("crowd & tuning", TrackContentKind.Nonmusic)]
    [TestCase("tuning", TrackContentKind.Nonmusic)]
    [TestCase("Jam >", TrackContentKind.Jam)]
    public void RecognizesNonSongContentsWithoutDiscardingThem(
        string raw,
        TrackContentKind expected
    )
    {
        var result = extractor.Extract(Guid.NewGuid(), raw);

        result.IsResolved.Should().BeTrue();
        result.Segments.Single().Kind.Should().Be(expected);
        result.Segments.Single().Title.Should().BeNull();
    }

    [Test]
    public void SlashLabelsDoNotClaimAnOrderedPerformanceSequence()
    {
        var result = extractor.Extract(Guid.NewGuid(), "banter/jam");

        result
            .Segments.Select(segment => segment.Kind)
            .Should()
            .Equal(TrackContentKind.Speech, TrackContentKind.Jam);
        result.Sequence.Should().Be(TrackTitleSequence.Unresolved);
    }

    [Test]
    public void EvidenceSpansCountUnicodeCodePointsRatherThanUtf16Units()
    {
        const string raw = "🎹 Jam > Café 🌙 (partial)";
        var result = extractor.Extract(Guid.NewGuid(), raw);

        var second = result.Segments[1];
        second.Title.Should().Be("Café 🌙");
        second.Evidence.StartCodePoint.Should().Be(8);
        second.Evidence.EndCodePoint.Should().Be(14);
        foreach (var segment in result.Segments)
        {
            AssertEvidence(raw, segment.Evidence);
        }

        foreach (var qualifier in result.Qualifiers)
        {
            AssertEvidence(raw, qualifier.Evidence);
        }
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase(" > ")]
    [TestCase("... (partial)")]
    public void EmptyAndSyntaxOnlyInputsDoNotBecomeSongs(string raw)
    {
        var result = extractor.Extract(Guid.NewGuid(), raw);

        result.IsResolved.Should().BeFalse();
        result.Segments.Should().BeEmpty();
    }

    private static void AssertEvidence(string raw, NormalizationTextEvidence evidence)
    {
        var recovered = string.Concat(
            raw.EnumerateRunes()
                .Skip(evidence.StartCodePoint)
                .Take(evidence.EndCodePoint - evidence.StartCodePoint)
                .Select(rune => rune.ToString())
        );
        recovered.Should().Be(evidence.Quote);
        evidence.Field.Should().Be("title");
        evidence.JsonPointer.Should().Be("/title");
    }
}
