using FluentAssertions;
using NUnit.Framework;
using Relisten.Api.Models;
using Relisten.Services.Classification;

namespace RelistenApiTests.Classification;

[TestFixture]
public class TestRecordingTypeExtensions
{
    [TestCase(RecordingType.Soundboard, "soundboard")]
    [TestCase(RecordingType.Audience, "audience")]
    [TestCase(RecordingType.Matrix, "matrix")]
    [TestCase(RecordingType.UltraMatrix, "ultra_matrix")]
    [TestCase(RecordingType.PreFm, "pre_fm")]
    [TestCase(RecordingType.Fm, "fm")]
    [TestCase(RecordingType.Webcast, "webcast")]
    [TestCase(RecordingType.Unknown, "unknown")]
    public void ToDbString_AllValues_ProduceCorrectSnakeCase(RecordingType type, string expected)
    {
        type.ToDbString().Should().Be(expected);
    }

    [Test]
    public void ToDbString_UltraMatrix_NotJustToLower()
    {
        // This was the original bug: ToString().ToLowerInvariant() produces "ultramatrix"
        RecordingType.UltraMatrix.ToDbString().Should().NotBe("ultramatrix");
        RecordingType.UltraMatrix.ToDbString().Should().Be("ultra_matrix");
    }

    [Test]
    public void ToDbString_PreFm_NotJustToLower()
    {
        // Same bug: ToString().ToLowerInvariant() produces "prefm"
        RecordingType.PreFm.ToDbString().Should().NotBe("prefm");
        RecordingType.PreFm.ToDbString().Should().Be("pre_fm");
    }
}

[TestFixture]
public class TestLevenshteinSimilarity
{
    [Test]
    public void CalculateSimilarity_IdenticalStrings_ReturnsOne()
    {
        TrackSongMatcher.CalculateSimilarity("scarlet begonias", "scarlet begonias")
            .Should().BeApproximately(1.0f, 0.001f);
    }

    [Test]
    public void CalculateSimilarity_CompletelyDifferent_ReturnsLow()
    {
        TrackSongMatcher.CalculateSimilarity("dark star", "playing in the band")
            .Should().BeLessThan(0.3f);
    }

    [Test]
    public void CalculateSimilarity_SmallTypo_ReturnsHigh()
    {
        // "Scarlet Begonias" vs "Scarlet Begonais" (transposition)
        TrackSongMatcher.CalculateSimilarity("scarlet begonias", "scarlet begonais")
            .Should().BeGreaterThan(0.85f);
    }

    [Test]
    public void CalculateSimilarity_CaseInsensitive_ReturnsOne()
    {
        TrackSongMatcher.CalculateSimilarity("Dark Star", "dark star")
            .Should().BeApproximately(1.0f, 0.001f);
    }

    [Test]
    public void CalculateSimilarity_EmptyStrings_ReturnsZero()
    {
        TrackSongMatcher.CalculateSimilarity("", "something").Should().Be(0.0f);
        TrackSongMatcher.CalculateSimilarity("something", "").Should().Be(0.0f);
    }

    [Test]
    public void CalculateSimilarity_Abbreviation_ReturnsModerate()
    {
        // "Not Fade Away" vs "NFA" — very different lengths, low similarity
        TrackSongMatcher.CalculateSimilarity("not fade away", "nfa")
            .Should().BeLessThan(0.5f);
    }
}
