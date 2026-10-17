using System.Text.Json;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Relisten.Ingestion;
using Relisten.Ingestion.Providers.TapersSection;

namespace RelistenApiTests.TapersSection;

[TestFixture]
public class TestTapersSectionEvidence
{
    private static readonly Guid CandidateId = Guid.Parse("b7fed85c-96d2-421b-bc9c-674c492e6ac2");
    private static readonly Guid ArtistUuid = Guid.Parse("aaf72a2a-cb1e-4d18-b2eb-21bd86edaa15");

    [Test]
    public void CapturePreservesFieldsAndHashesRelevantContentIndependentlyOfObjectOrder()
    {
        var first = ArchiveObservationProjector.Project(
            new ArchiveItemId("test"),
            """
            {"metadata":{"identifier":"test","creator":["One","Two"],"title":"2025-01-02","date":null,"subject":[],"uploader":"private"},
             "files":[{"name":"track.mp3","format":"VBR MP3","original":"track.flac","md5":"abc","title":["One","Two"]}],"reviews":[{"reviewer":"unneeded"}]}
            """
        );
        var reordered = ArchiveObservationProjector.Project(
            new ArchiveItemId("test"),
            """
            {"files":[{"md5":"abc","title":["One","Two"],"original":"track.flac","format":"VBR MP3","name":"track.mp3"}],
             "metadata":{"subject":[],"date":null,"title":"2025-01-02","creator":["One","Two"],"identifier":"test","uploader":"changed"}}
            """
        );
        first.ContentHash.Should().Be(reordered.ContentHash);
        var payload = JObject.Parse(first.PayloadJson);
        payload["metadata"]!["creator"]!.Type.Should().Be(JTokenType.Array);
        payload["metadata"]!["date"]!.Type.Should().Be(JTokenType.Null);
        payload["metadata"]!["year"].Should().BeNull();
        payload["metadata"]!["subject"]!.Should().BeEmpty();
        payload["metadata"]!["uploader"].Should().BeNull();
        payload["reviews"].Should().BeNull();
        payload["files"]![0]!["original"]!.Value<string>().Should().Be("track.flac");
    }

    [Test]
    public void ChangedScalarArrayOrFileRelationshipCreatesDifferentObservationHash()
    {
        var raw = Raw("2025-01-02", "Performer Live at Venue on 2025-01-02");
        var first = Project(raw);
        var array = JObject.Parse(raw);
        array["metadata"]!["creator"] = new JArray("Performer");
        Project(array.ToString()).ContentHash.Should().NotBe(first.ContentHash);
        var files = JObject.Parse(raw);
        files["files"]![0]!["original"] = "other.flac";
        Project(files.ToString()).ContentHash.Should().NotBe(first.ContentHash);
    }

    [TestCase("{\"metadata\":{\"identifier\":\"wrong\"}}")]
    [TestCase("{\"metadata\":{\"identifier\":\"test\"},\"files\":{}}")]
    public void RejectsInvalidContentInsteadOfInventingAnObservation(string raw)
    {
        var act = () => Project(raw);
        act.Should().Throw<InvalidDataException>();
    }

    [Test]
    public void AvailabilityResponseIsRetainedWithoutBecomingValidContent()
    {
        var observation = Project("{\"errcode\":104,\"error\":\"item unavailable\"}");
        observation.IsAvailabilityOnly.Should().BeTrue();
        Evaluate(observation).Reasons.Should().Contain(TapersSectionReason.UpstreamUnavailable);
    }

    [Test]
    public void ClearApprovedArtistHasAStableExplicitAssignment()
    {
        var result = Evaluate(Project(Raw("2025-01-02", "Performer Live at Venue on 2025-01-02")));
        result.IsEligible.Should().BeTrue();
        result.CandidateId.Should().Be(CandidateId);
        result.DisplayDate.Should().Be("2025-01-02");
        result
            .Evidence.Should()
            .Contain(value =>
                value.Locator == "metadata.date" && value.Rule == "complete_structured_date"
            );
    }

    [Test]
    public void IsoTimestampRemainsAJsonStringAndSuppliesItsCompleteCalendarDate()
    {
        var result = Evaluate(Project(Raw("2025-01-02T00:00:00Z", "Performer")));
        result.IsEligible.Should().BeTrue();
        result.DisplayDate.Should().Be("2025-01-02");
    }

    [Test]
    public void ApprovedAmpersandBandRemainsOneReviewedIdentity()
    {
        var raw = JObject.Parse(Raw("2025-01-02", "A & B Live at Venue on 2025-01-02"));
        raw["metadata"]!["creator"] = "A & B";
        var result = new TapersSectionEligibilityEvaluator().Evaluate(
            Project(raw.ToString()),
            [new(CandidateId, true, ["A & B"], ArtistUuid)]
        );
        result.IsEligible.Should().BeTrue();
        result.CreatorKeys.Should().Equal("A & B");
    }

    [Test]
    public void DuplicateCreatorSpellingsCollapseButSeparateCreatorsRemainAmbiguous()
    {
        var raw = JObject.Parse(Raw("2025-01-02", "Performer Live at Venue on 2025-01-02"));
        raw["metadata"]!["creator"] = new JArray("Performer", " PERFORMER  ");
        Evaluate(Project(raw.ToString())).IsEligible.Should().BeTrue();
        raw["metadata"]!["creator"] = new JArray("Performer", "Another performer");
        Evaluate(Project(raw.ToString()))
            .Reasons.Should()
            .Contain(TapersSectionReason.AmbiguousCreator);
    }

    [TestCase("Dear Jerry: A Celebration of Jerry Garcia", TapersSectionReason.TributeContext)]
    [TestCase("In Memory Of Dickey Betts", TapersSectionReason.TributeContext)]
    [TestCase("1989: The Songs Of Taylor Swift", TapersSectionReason.TributeContext)]
    [TestCase("Garcia compilation 2025 remaster", TapersSectionReason.Compilation)]
    [TestCase(
        "Bill Frisell & Ron Carter joint performance",
        TapersSectionReason.MultiplePerformers
    )]
    [TestCase("Performer early and late shows", TapersSectionReason.AmbiguousDate)]
    public void RecognizedAttributionRisksRemainReviewCases(
        string title,
        TapersSectionReason reason
    )
    {
        var result = Evaluate(Project(Raw("2025-01-02", title)));
        result.IsEligible.Should().BeFalse();
        result.Reasons.Should().Contain(reason);
        result
            .Evidence.Should()
            .Contain(value =>
                value.Rule.StartsWith("explicit_") || value.Rule == "ambiguous_early_late_billing"
            );
    }

    [Test]
    public void StructuredJuly21CannotOverrideJuly20TitleAndIdentifier()
    {
        var raw = Raw("2024-07-21", "Performer Live at Venue on 2024-07-20");
        var result = Evaluate(
            ArchiveObservationProjector.Project(
                new("performer2024-07-20"),
                raw.Replace("\"test\"", "\"performer2024-07-20\"")
            )
        );
        result.Reasons.Should().Contain(TapersSectionReason.ConflictingDate);
    }

    [TestCase("Performer Live at Venue on July 20, 2024", "2024-07-20")]
    [TestCase("Performer Live at Venue on 2024-07-20", "2024-07-20")]
    [TestCase("Performer - 2024-07-20", "2024-07-20")]
    public void MissingStructuredDateCanUseOneFullYearTitleDate(string title, string expected)
    {
        var result = Evaluate(Project(Raw(null, title)));
        result.IsEligible.Should().BeTrue();
        result.DisplayDate.Should().Be(expected);
        result.Evidence.Should().Contain(value => value.Rule == "full_year_title_date");
    }

    [TestCase("2025-XX-XX", "Performer Live at Venue on 2025-01-02")]
    [TestCase(null, "Performer on 01/02/25")]
    [TestCase(null, "Performer uploaded 2025-01-02")]
    [TestCase(null, "Performer album released on 2025-01-02")]
    public void PlaceholderUnsupportedAndUploadDatesCannotSupplyPerformanceDate(
        string? date,
        string title
    )
    {
        Evaluate(Project(Raw(date, title)))
            .Reasons.Should()
            .Contain(TapersSectionReason.MissingDate);
    }

    [Test]
    public void DescriptionCannotSupplyMissingDateAndTitleCannotChangeReviewedPerformer()
    {
        var raw = JObject.Parse(Raw(null, "Other artist Live at Venue"));
        raw["metadata"]!["description"] = "Date: 2025-01-02";
        var result = Evaluate(Project(raw.ToString()));
        result.Reasons.Should().Contain(TapersSectionReason.MissingDate);
        result.Reasons.Should().Contain(TapersSectionReason.AmbiguousCreator);
    }

    [TestCase(false, false, TapersSectionReason.UnapprovedArtist)]
    [TestCase(true, true, TapersSectionReason.ExistingArtist)]
    public void ApprovalDoesNotOverrideExistingArtistDeferral(
        bool approved,
        bool existing,
        TapersSectionReason expected
    )
    {
        var result = new TapersSectionEligibilityEvaluator().Evaluate(
            Project(Raw("2025-01-02", "Performer")),
            [new(CandidateId, approved, ["Performer"], ArtistUuid, existing)]
        );
        result.Reasons.Should().Contain(expected);
    }

    [Test]
    public void MultipleRegistryMatchesCannotBePublishedOrDeferredAsOneKnownArtist()
    {
        var result = new TapersSectionEligibilityEvaluator().Evaluate(
            Project(Raw("2025-01-02", "Performer")),
            [new(CandidateId, true, ["Performer"], null, true, true)]
        );
        result.Reasons.Should().Contain(TapersSectionReason.AmbiguousCreator);
        result.Reasons.Should().NotContain(TapersSectionReason.ExistingArtist);
    }

    [Test]
    public void ExplicitDescriptionDateCanHoldAConflictingAssignmentButCannotSupplyOne()
    {
        var raw = JObject.Parse(Raw("2025-01-02", "Performer"));
        raw["metadata"]!["description"] = "<div>Date: 2025-01-03</div>";
        var result = Evaluate(Project(raw.ToString()));
        result.Reasons.Should().Contain(TapersSectionReason.ConflictingDate);
        result.Evidence.Should().Contain(value => value.Rule == "explicit_description_date");
    }

    [Test]
    public void PublishedAssignmentConflictHoldsAnOtherwiseClearUpdate()
    {
        var result = new TapersSectionEligibilityEvaluator().Evaluate(
            Project(Raw("2025-01-02", "Performer")),
            [new(CandidateId, true, ["Performer"], ArtistUuid)],
            new(ArtistUuid, "2025-01-01")
        );
        result.Reasons.Should().Contain(TapersSectionReason.PublishedAssignmentConflict);
    }

    [TestCase("MP3")]
    [TestCase("Flac")]
    public void OriginalMp3AndFlacDoNotBroadenLegacyPlaybackEligibility(string format)
    {
        var raw = JObject.Parse(Raw("2025-01-02", "Performer"));
        raw["files"]![0]!["format"] = format;
        Evaluate(Project(raw.ToString())).Reasons.Should().Contain(TapersSectionReason.NoVbrMp3);
    }

    [TestCase(null)]
    [TestCase("bad")]
    [TestCase("-30")]
    [TestCase("NaN")]
    [TestCase("999999999999999999999")]
    public void UnusableDurationCannotPublishAnEmptyOrInvalidRecording(string? length)
    {
        var raw = JObject.Parse(Raw("2025-01-02", "Performer"));
        raw["files"]![0]!["length"] = length;
        Evaluate(Project(raw.ToString())).Reasons.Should().Contain(TapersSectionReason.NoVbrMp3);
    }

    [Test]
    public void RestrictedFilesCannotBeAutomaticallyPublished()
    {
        var raw = JObject.Parse(Raw("2025-01-02", "Performer"));
        raw["files"]![0]!["private"] = "true";
        Evaluate(Project(raw.ToString()))
            .Reasons.Should()
            .Contain(TapersSectionReason.UpstreamUnavailable);
    }

    [Test]
    public void ReasonWireValuesRemainReadableAndStable()
    {
        // This wire contract is stored in PostgreSQL interpretation JSON and Temporal payloads.
        JsonSerializer
            .Serialize(TapersSectionReason.PublishedAssignmentConflict)
            .Should()
            .Be("\"published_assignment_conflict\"");
    }

    private static ArchiveObservationProjection Project(string raw) =>
        ArchiveObservationProjector.Project(new("test"), raw);

    private static TapersSectionInterpretation Evaluate(ArchiveObservationProjection observation) =>
        new TapersSectionEligibilityEvaluator().Evaluate(
            observation,
            [new(CandidateId, true, ["Performer"], ArtistUuid)]
        );

    private static string Raw(string? date, string title) =>
        new JObject
        {
            ["metadata"] = new JObject
            {
                ["identifier"] = "test",
                ["mediatype"] = "audio",
                ["creator"] = "Performer",
                ["title"] = title,
                ["date"] = date == null ? null : JValue.CreateString(date),
            },
            ["files"] = new JArray(
                new JObject
                {
                    ["name"] = "one.mp3",
                    ["length"] = "120",
                    ["format"] = "VBR MP3",
                    ["original"] = "one.flac",
                }
            ),
        }.ToString();
}
