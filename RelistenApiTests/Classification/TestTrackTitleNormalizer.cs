using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using Relisten.Services.Classification;

namespace RelistenApiTests.Classification;

[TestFixture]
public class TestTrackTitleNormalizer
{
    #region Basic Normalization

    [Test]
    public void NormalizeTitle_SimpleSongTitle_ReturnsSingleSegment()
    {
        var result = TrackTitleNormalizer.NormalizeTitle("Scarlet Begonias");
        result.Should().HaveCount(1);
        result[0].NormalizedName.Should().Be("Scarlet Begonias");
        result[0].TrackType.Should().Be("song");
        result[0].IsSegue.Should().BeFalse();
    }

    [Test]
    public void NormalizeTitle_EmptyString_ReturnsEmpty()
    {
        TrackTitleNormalizer.NormalizeTitle("").Should().BeEmpty();
        TrackTitleNormalizer.NormalizeTitle(null!).Should().BeEmpty();
        TrackTitleNormalizer.NormalizeTitle("   ").Should().BeEmpty();
    }

    #endregion

    #region Segue Splitting

    [Test]
    public void NormalizeTitle_SegueArrow_SplitsIntoMultipleSegments()
    {
        var result = TrackTitleNormalizer.NormalizeTitle("Scarlet Begonias > Fire on the Mountain");
        result.Should().HaveCount(2);
        result[0].NormalizedName.Should().Be("Scarlet Begonias");
        result[0].Position.Should().Be(0);
        result[0].IsSegue.Should().BeTrue();
        result[1].NormalizedName.Should().Be("Fire on the Mountain");
        result[1].Position.Should().Be(1);
        result[1].IsSegue.Should().BeTrue();
    }

    [Test]
    public void NormalizeTitle_DashArrow_SplitsCorrectly()
    {
        var result = TrackTitleNormalizer.NormalizeTitle("China Cat Sunflower -> I Know You Rider");
        result.Should().HaveCount(2);
        result[0].NormalizedName.Should().Be("China Cat Sunflower");
        result[1].NormalizedName.Should().Be("I Know You Rider");
    }

    [Test]
    public void NormalizeTitle_UnicodeArrow_SplitsCorrectly()
    {
        var result = TrackTitleNormalizer.NormalizeTitle("Help on the Way → Slipknot!");
        result.Should().HaveCount(2);
        result[0].NormalizedName.Should().Be("Help on the Way");
        result[1].NormalizedName.Should().Be("Slipknot!");
    }

    [Test]
    public void NormalizeTitle_DoubleArrow_SplitsCorrectly()
    {
        var result = TrackTitleNormalizer.NormalizeTitle("Playing in the Band >> Uncle John's Band");
        result.Should().HaveCount(2);
        result[0].NormalizedName.Should().Be("Playing in the Band");
        result[1].NormalizedName.Should().Be("Uncle John's Band");
    }

    #endregion

    #region Prefix Stripping

    [Test]
    public void NormalizeTitle_TrackNumber_StripsPrefix()
    {
        var result = TrackTitleNormalizer.NormalizeTitle("01 Scarlet Begonias");
        result.Should().HaveCount(1);
        result[0].NormalizedName.Should().Be("Scarlet Begonias");
    }

    [Test]
    public void NormalizeTitle_TrackNumberWithDot_StripsPrefix()
    {
        var result = TrackTitleNormalizer.NormalizeTitle("03. Fire on the Mountain");
        result.Should().HaveCount(1);
        result[0].NormalizedName.Should().Be("Fire on the Mountain");
    }

    [Test]
    public void NormalizeTitle_DiscTrackPrefix_StripsPrefix()
    {
        var result = TrackTitleNormalizer.NormalizeTitle("d1t05 Eyes of the World");
        result.Should().HaveCount(1);
        result[0].NormalizedName.Should().Be("Eyes of the World");
    }

    [Test]
    public void NormalizeTitle_SetPrefix_StripsPrefix()
    {
        var result = TrackTitleNormalizer.NormalizeTitle("Set I: Bertha");
        result.Should().HaveCount(1);
        result[0].NormalizedName.Should().Be("Bertha");
    }

    [Test]
    public void NormalizeTitle_EncorePrefix_StripsPrefix()
    {
        var result = TrackTitleNormalizer.NormalizeTitle("E: Brokedown Palace");
        result.Should().HaveCount(1);
        result[0].NormalizedName.Should().Be("Brokedown Palace");
    }

    #endregion

    #region Suffix Stripping

    [Test]
    public void NormalizeTitle_InstrumentalSuffix_StripsSuffix()
    {
        var result = TrackTitleNormalizer.NormalizeTitle("Morning Dew (instrumental)");
        result.Should().HaveCount(1);
        result[0].NormalizedName.Should().Be("Morning Dew");
    }

    [Test]
    public void NormalizeTitle_RepriseSuffix_StripsSuffix()
    {
        var result = TrackTitleNormalizer.NormalizeTitle("Playing in the Band (reprise)");
        result.Should().HaveCount(1);
        result[0].NormalizedName.Should().Be("Playing in the Band");
    }

    [Test]
    public void NormalizeTitle_FileExtension_StripsExtension()
    {
        var result = TrackTitleNormalizer.NormalizeTitle("Dark Star.mp3");
        result.Should().HaveCount(1);
        result[0].NormalizedName.Should().Be("Dark Star");
    }

    [Test]
    public void NormalizeTitle_FlacExtension_StripsExtension()
    {
        var result = TrackTitleNormalizer.NormalizeTitle("01 Tweezer.flac");
        result.Should().HaveCount(1);
        result[0].NormalizedName.Should().Be("Tweezer");
    }

    #endregion

    #region Non-Song Detection

    [Test]
    public void NormalizeTitle_Banter_DetectsAsBanter()
    {
        var result = TrackTitleNormalizer.NormalizeTitle("Banter");
        result.Should().HaveCount(1);
        result[0].TrackType.Should().Be("banter");
    }

    [Test]
    public void NormalizeTitle_StageBanter_DetectsAsBanter()
    {
        var result = TrackTitleNormalizer.NormalizeTitle("Stage Banter");
        result.Should().HaveCount(1);
        result[0].TrackType.Should().Be("banter");
    }

    [Test]
    public void NormalizeTitle_Tuning_DetectsAsTuning()
    {
        var result = TrackTitleNormalizer.NormalizeTitle("Tuning");
        result.Should().HaveCount(1);
        result[0].TrackType.Should().Be("tuning");
    }

    [Test]
    public void NormalizeTitle_Crowd_DetectsAsCrowd()
    {
        var result = TrackTitleNormalizer.NormalizeTitle("Crowd");
        result.Should().HaveCount(1);
        result[0].TrackType.Should().Be("crowd");
    }

    [Test]
    public void NormalizeTitle_Soundcheck_DetectsAsSoundcheck()
    {
        var result = TrackTitleNormalizer.NormalizeTitle("Soundcheck");
        result.Should().HaveCount(1);
        result[0].TrackType.Should().Be("soundcheck");
    }

    [Test]
    public void NormalizeTitle_DrumsSpace_DetectsAsJam()
    {
        var result = TrackTitleNormalizer.NormalizeTitle("Drums/Space");
        result.Should().HaveCount(1);
        result[0].TrackType.Should().Be("jam");
    }

    [Test]
    public void NormalizeTitle_ActualSongTitle_NotDetectedAsNonSong()
    {
        // "Dark Star" should NOT be mistaken for a non-song
        var result = TrackTitleNormalizer.NormalizeTitle("Dark Star");
        result.Should().HaveCount(1);
        result[0].TrackType.Should().Be("song");
    }

    #endregion

    #region Slug Generation

    [Test]
    public void NormalizeTitle_GeneratesCorrectSlug()
    {
        var result = TrackTitleNormalizer.NormalizeTitle("Uncle John's Band");
        result.Should().HaveCount(1);
        result[0].Slug.Should().Be("uncle-johns-band");
    }

    [Test]
    public void NormalizeTitle_SegueGeneratesSlugsForEach()
    {
        var result = TrackTitleNormalizer.NormalizeTitle("Scarlet Begonias > Fire on the Mountain");
        result[0].Slug.Should().Be("scarlet-begonias");
        result[1].Slug.Should().Be("fire-on-the-mountain");
    }

    #endregion

    #region Complex Titles

    [Test]
    public void NormalizeTitle_ComplexTitle_HandlesEverything()
    {
        var result = TrackTitleNormalizer.NormalizeTitle("d2t01 Set II: Scarlet Begonias > Fire on the Mountain (jam).flac");
        result.Should().HaveCount(2);
        result[0].NormalizedName.Should().Be("Scarlet Begonias");
        result[1].NormalizedName.Should().Be("Fire on the Mountain");
        result[0].TrackType.Should().Be("song");
        result[1].TrackType.Should().Be("song");
    }

    [Test]
    public void NormalizeTitle_TrackNumberWithDash_StripsCorrectly()
    {
        var result = TrackTitleNormalizer.NormalizeTitle("1 - Morning Dew");
        result.Should().HaveCount(1);
        result[0].NormalizedName.Should().Be("Morning Dew");
    }

    #endregion
}
