using FluentAssertions;
using NUnit.Framework;
using Relisten.Api.Models;
using Relisten.Services.Classification;

namespace RelistenApiTests.Classification;

/// <summary>
/// Tests for RecordingTypeClassifier.ClassifyWithRules() — the synchronous, rule-based layer.
/// No mocking needed since this is pure logic with no external dependencies.
/// </summary>
[TestFixture]
public class TestRecordingTypeClassifier
{
    // We can't instantiate the classifier directly (it needs LLM + logger),
    // but ClassifyWithRules is a public instance method.
    // Use a minimal wrapper to test the rules.
    private RecordingTypeClassifier _classifier = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        // ClassifyWithRules doesn't use the LLM or logger, so null is safe here.
        _classifier = new RecordingTypeClassifier(null!, null!);
    }

    #region Soundboard Detection

    [Test]
    public void ClassifyWithRules_ExplicitSbd_ReturnsSoundboard()
    {
        var meta = new SourceMetadataForClassification
        {
            Lineage = "SBD > DAT > CD > FLAC"
        };
        var result = _classifier.ClassifyWithRules(meta);
        result.RecordingType.Should().Be(RecordingType.Soundboard);
        result.Confidence.Should().BeGreaterThanOrEqualTo(0.7f);
    }

    [Test]
    public void ClassifyWithRules_SoundboardInLineage_ReturnsSoundboard()
    {
        var meta = new SourceMetadataForClassification
        {
            Lineage = "Soundboard > DAT > FLAC"
        };
        var result = _classifier.ClassifyWithRules(meta);
        result.RecordingType.Should().Be(RecordingType.Soundboard);
    }

    [Test]
    public void ClassifyWithRules_DirectBoardFeed_ReturnsSoundboardHighConfidence()
    {
        var meta = new SourceMetadataForClassification
        {
            Source = "board feed"
        };
        var result = _classifier.ClassifyWithRules(meta);
        result.RecordingType.Should().Be(RecordingType.Soundboard);
        result.Confidence.Should().BeGreaterThanOrEqualTo(0.9f);
    }

    [Test]
    public void ClassifyWithRules_ConsolRecording_ReturnsSoundboard()
    {
        var meta = new SourceMetadataForClassification
        {
            Source = "console recording"
        };
        var result = _classifier.ClassifyWithRules(meta);
        result.RecordingType.Should().Be(RecordingType.Soundboard);
        result.Confidence.Should().BeGreaterThanOrEqualTo(0.9f);
    }

    [Test]
    public void ClassifyWithRules_SbdInIdentifier_ReturnsHighConfidenceSbd()
    {
        var meta = new SourceMetadataForClassification
        {
            Identifier = "gd1977-05-08.sbd.miller.97543.flac16",
            Source = "SBD"
        };
        var result = _classifier.ClassifyWithRules(meta);
        result.RecordingType.Should().Be(RecordingType.Soundboard);
        result.Confidence.Should().BeGreaterThanOrEqualTo(0.9f);
    }

    #endregion

    #region SBD False Positive Detection (The Core Use Case)

    [Test]
    public void ClassifyWithRules_NearTheSoundboard_ReturnsAudience()
    {
        var meta = new SourceMetadataForClassification
        {
            TaperNotes = "Recorded near the soundboard with Schoeps MK4V"
        };
        var result = _classifier.ClassifyWithRules(meta);
        result.RecordingType.Should().Be(RecordingType.Audience);
    }

    [Test]
    public void ClassifyWithRules_BehindSbd_ReturnsAudience()
    {
        var meta = new SourceMetadataForClassification
        {
            TaperNotes = "Taped from behind the SBD"
        };
        var result = _classifier.ClassifyWithRules(meta);
        result.RecordingType.Should().Be(RecordingType.Audience);
    }

    [Test]
    public void ClassifyWithRules_ThreeRowsFromSbd_ReturnsAudience()
    {
        var meta = new SourceMetadataForClassification
        {
            TaperNotes = "Set up 3 rows from the soundboard"
        };
        var result = _classifier.ClassifyWithRules(meta);
        // Should NOT be classified as soundboard
        result.RecordingType.Should().NotBe(RecordingType.Soundboard);
    }

    [Test]
    public void ClassifyWithRules_NextToBoard_ReturnsAudience()
    {
        var meta = new SourceMetadataForClassification
        {
            TaperNotes = "Mics placed next to the board"
        };
        var result = _classifier.ClassifyWithRules(meta);
        result.RecordingType.Should().NotBe(RecordingType.Soundboard);
    }

    #endregion

    #region Matrix Detection

    [Test]
    public void ClassifyWithRules_MatrixKeyword_ReturnsMatrix()
    {
        var meta = new SourceMetadataForClassification
        {
            Source = "Matrix of SBD and AUD sources"
        };
        var result = _classifier.ClassifyWithRules(meta);
        result.RecordingType.Should().Be(RecordingType.Matrix);
    }

    [Test]
    public void ClassifyWithRules_SbdPlusAud_ReturnsMatrix()
    {
        var meta = new SourceMetadataForClassification
        {
            Lineage = "SBD + AUD > mix > FLAC"
        };
        var result = _classifier.ClassifyWithRules(meta);
        result.RecordingType.Should().Be(RecordingType.Matrix);
    }

    [Test]
    public void ClassifyWithRules_BoardPlusMics_ReturnsMatrix()
    {
        var meta = new SourceMetadataForClassification
        {
            Source = "board + mics blend"
        };
        var result = _classifier.ClassifyWithRules(meta);
        result.RecordingType.Should().Be(RecordingType.Matrix);
    }

    [Test]
    public void ClassifyWithRules_UltraMatrix_ReturnsUltraMatrix()
    {
        var meta = new SourceMetadataForClassification
        {
            Source = "Ultra-matrix of 3 sources"
        };
        var result = _classifier.ClassifyWithRules(meta);
        result.RecordingType.Should().Be(RecordingType.UltraMatrix);
    }

    #endregion

    #region FM/Pre-FM Detection

    [Test]
    public void ClassifyWithRules_PreFm_ReturnsPreFm()
    {
        var meta = new SourceMetadataForClassification
        {
            Source = "Pre-FM master tape"
        };
        var result = _classifier.ClassifyWithRules(meta);
        result.RecordingType.Should().Be(RecordingType.PreFm);
    }

    [Test]
    public void ClassifyWithRules_FmBroadcast_ReturnsFm()
    {
        var meta = new SourceMetadataForClassification
        {
            Source = "FM broadcast from WNEW"
        };
        var result = _classifier.ClassifyWithRules(meta);
        result.RecordingType.Should().Be(RecordingType.Fm);
    }

    [Test]
    public void ClassifyWithRules_RadioBroadcast_ReturnsFm()
    {
        var meta = new SourceMetadataForClassification
        {
            Source = "radio broadcast recording"
        };
        var result = _classifier.ClassifyWithRules(meta);
        result.RecordingType.Should().Be(RecordingType.Fm);
    }

    #endregion

    #region Webcast Detection

    [Test]
    public void ClassifyWithRules_Webcast_ReturnsWebcast()
    {
        var meta = new SourceMetadataForClassification
        {
            Source = "webcast from venue website"
        };
        var result = _classifier.ClassifyWithRules(meta);
        result.RecordingType.Should().Be(RecordingType.Webcast);
    }

    [Test]
    public void ClassifyWithRules_NugsNet_ReturnsWebcast()
    {
        var meta = new SourceMetadataForClassification
        {
            Source = "nugs.net live stream"
        };
        var result = _classifier.ClassifyWithRules(meta);
        result.RecordingType.Should().Be(RecordingType.Webcast);
    }

    #endregion

    #region Unknown / Default

    [Test]
    public void ClassifyWithRules_NoIndicators_ReturnsUnknown()
    {
        var meta = new SourceMetadataForClassification
        {
            Description = "Great show, everyone had a good time"
        };
        var result = _classifier.ClassifyWithRules(meta);
        result.RecordingType.Should().Be(RecordingType.Unknown);
        result.Confidence.Should().BeLessThan(0.5f);
    }

    [Test]
    public void ClassifyWithRules_AllFieldsNull_ReturnsUnknown()
    {
        var meta = new SourceMetadataForClassification();
        var result = _classifier.ClassifyWithRules(meta);
        result.RecordingType.Should().Be(RecordingType.Unknown);
        result.Method.Should().Be("rule");
    }

    #endregion

    #region Priority Ordering

    [Test]
    public void ClassifyWithRules_WebcastBeforeSbd_PrefersWebcast()
    {
        // If something is described as a webcast with SBD quality, webcast wins
        var meta = new SourceMetadataForClassification
        {
            Source = "webcast SBD quality stream"
        };
        var result = _classifier.ClassifyWithRules(meta);
        result.RecordingType.Should().Be(RecordingType.Webcast);
    }

    [Test]
    public void ClassifyWithRules_MatrixBeforeSbd_PrefersMatrix()
    {
        // A matrix contains SBD references but should be classified as matrix
        var meta = new SourceMetadataForClassification
        {
            Source = "matrix of SBD and audience"
        };
        var result = _classifier.ClassifyWithRules(meta);
        result.RecordingType.Should().Be(RecordingType.Matrix);
    }

    #endregion
}
