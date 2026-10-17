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
    [TestCase("Performer: Best of 2025", TapersSectionReason.Compilation)]
    [TestCase("A Tribute to Jerry Garcia", TapersSectionReason.TributeContext)]
    [TestCase("Performer memorial concert", TapersSectionReason.TributeContext)]
    [TestCase(
        "Performer — A Jerry Garcia Tribute — 2025-01-02",
        TapersSectionReason.TributeContext
    )]
    [TestCase(
        "Bill Frisell & Ron Carter joint performance",
        TapersSectionReason.MultiplePerformers
    )]
    public void RecognizedAttributionRisksRemainReviewCases(
        string title,
        TapersSectionReason reason
    )
    {
        var result = Evaluate(Project(Raw("2025-01-02", title)));
        result.IsEligible.Should().BeFalse();
        result.Reasons.Should().Contain(reason);
        result.Evidence.Should().Contain(value => value.Rule.StartsWith("explicit_"));
    }

    [TestCase("title")]
    [TestCase("description")]
    public void SameDayEarlyAndLateCoverageRetainsEvidenceWithoutHoldingPublication(string field)
    {
        var raw = JObject.Parse(Raw("2025-01-02", "Performer Live at Venue on 2025-01-02"));
        raw["metadata"]![field] = "Performer early and late shows";
        var result = Evaluate(Project(raw.ToString()));
        result.IsEligible.Should().BeTrue();
        result.DisplayDate.Should().Be("2025-01-02");
        result
            .Evidence.Should()
            .Contain(value =>
                value.Rule == "ambiguous_early_late_billing"
                && value.Locator == "metadata." + field
                && value.Value == "early and late"
            );
    }

    [Test]
    public void TransferRepairNotesDoNotClassifyARecordingAsACompilation()
    {
        var raw = JObject.Parse(Raw("2025-01-02", "Performer Live at Venue on 2025-01-02"));
        raw["metadata"]!["description"] = "Most has been fixed to the best of my abilities";

        Evaluate(Project(raw.ToString())).IsEligible.Should().BeTrue();
    }

    [Test]
    public void ExplicitTributeCreatorLabelStillRequiresReview()
    {
        var raw = JObject.Parse(Raw("2025-01-02", "Live at Venue on 2025-01-02"));
        raw["metadata"]!["creator"] = "Jerry Garcia Tribute";

        var result = new TapersSectionEligibilityEvaluator().Evaluate(
            Project(raw.ToString()),
            [new(CandidateId, true, ["Jerry Garcia Tribute"], ArtistUuid)]
        );

        result.Reasons.Should().Contain(TapersSectionReason.TributeContext);
        result
            .Evidence.Should()
            .Contain(value => value.Locator == "metadata.creator" && value.Value == "Tribute");
    }

    [Test]
    public void MemorialInAVenueNameDoesNotClassifyARecordingAsATribute()
    {
        var raw = JObject.Parse(
            Raw("2025-01-02", "Performer Live at Robert F. Kennedy Memorial Stadium on 2025-01-02")
        );
        raw["metadata"]!["description"] = "Venue: Robert F. Kennedy Memorial Stadium";

        Evaluate(Project(raw.ToString())).IsEligible.Should().BeTrue();
    }

    [TestCase("A memorial performance for the late musician", TapersSectionReason.TributeContext)]
    [TestCase("A Jerry Garcia tribute", TapersSectionReason.TributeContext)]
    [TestCase("The best of performances from several shows", TapersSectionReason.Compilation)]
    [TestCase("A compilation of concert recordings", TapersSectionReason.Compilation)]
    public void ExplicitDescriptionPerformanceContextStillRequiresReview(
        string description,
        TapersSectionReason reason
    )
    {
        var raw = JObject.Parse(Raw("2025-01-02", "Performer Live at Venue on 2025-01-02"));
        raw["metadata"]!["description"] = description;

        var result = Evaluate(Project(raw.ToString()));
        result.Reasons.Should().Contain(reason);
        result.Evidence.Should().Contain(value => value.Locator == "metadata.description");
    }

    [TestCase(
        "2001-05-12",
        "Gov't Mule 2001-05-12 Live at Lupo's Heartbreak Hotel in Providence, RI"
    )]
    [TestCase("2025-09-06", "Gov't Mule 2025-09-06 Live at Xfinity Center in Mansfield, MA")]
    public void ReviewedArtistWithPerformanceDateBeforeLiveAtIsNotADifferentPerformer(
        string date,
        string title
    )
    {
        var raw = JObject.Parse(Raw(date, title));
        raw["metadata"]!["creator"] = "Gov't Mule";

        var result = new TapersSectionEligibilityEvaluator().Evaluate(
            Project(raw.ToString()),
            [new(CandidateId, true, ["Gov't Mule"], ArtistUuid)]
        );

        result.IsEligible.Should().BeTrue();
        result.DisplayDate.Should().Be(date);
        result.Evidence.Should().NotContain(value => value.Rule == "title_performer_conflict");
    }

    [TestCase("Other Artist 2025-01-02 Live at Venue", TapersSectionReason.AmbiguousCreator)]
    [TestCase("Performer 1999 Live at Venue", TapersSectionReason.AmbiguousCreator)]
    [TestCase("Performer 2025-99-99 Live at Venue", TapersSectionReason.AmbiguousCreator)]
    [TestCase("Performer 2025-01-03 Live at Venue", TapersSectionReason.ConflictingDate)]
    public void DateBeforeLiveAtCannotHideAnUnreviewedPerformerOrConflictingDate(
        string title,
        TapersSectionReason expected
    )
    {
        var result = Evaluate(Project(Raw("2025-01-02", title)));

        result.IsEligible.Should().BeFalse();
        result.Reasons.Should().Contain(expected);
    }

    [TestCase("<div>* w/ In Memory of Elizabeth Reed Jam</div>")]
    [TestCase("<div>Kind of Bird has an In Memory of Elizabeth Reed tease.</div>")]
    [TestCase("Kind of Bird has an In Memory of Elizabeth Reed tease.")]
    [TestCase("* w/ In Memory of Another Song Jam")]
    public void ExplicitDescriptionSongJamAndTeaseNotesDoNotDescribeAMemorialEvent(
        string description
    )
    {
        var raw = JObject.Parse(Raw("2025-01-02", "Performer Live at Venue on 2025-01-02"));
        raw["metadata"]!["description"] = description;

        var result = Evaluate(Project(raw.ToString()));

        result.IsEligible.Should().BeTrue();
        result.Reasons.Should().NotContain(TapersSectionReason.TributeContext);
    }

    [Test]
    public void NumberedSongEntryNeedsItsRetainedFileTitleAsCorroboration()
    {
        var raw = JObject.Parse(Raw("2019-07-03", "Performer Live at Venue on 2019-07-03"));
        raw["metadata"]!["description"] =
            "<div>03. Kind Of Bird &gt;</div><div>04. In Memory Of Elizabeth Reed &gt;</div>";
        raw["files"]![0]!["title"] = "In Memory Of Elizabeth Reed >";

        var result = Evaluate(Project(raw.ToString()));

        result.IsEligible.Should().BeTrue();
    }

    [TestCase("<div>04. In Memory Of Elizabeth Reed &gt;</div>")]
    [TestCase("<div>Set 2 - In Memory Of Elizabeth Reed</div>")]
    [TestCase("<div>In Memory Of Dickey Betts</div>")]
    [TestCase("<div>Set 2 - Tribute To Levon Helm #</div>")]
    public void NumberingOrASongNameAloneDoesNotExcuseMemorialContext(string description)
    {
        var raw = JObject.Parse(Raw("2025-01-02", "Performer Live at Venue on 2025-01-02"));
        raw["metadata"]!["description"] = description;

        var result = Evaluate(Project(raw.ToString()));

        result.Reasons.Should().Contain(TapersSectionReason.TributeContext);
        result.Evidence.Should().Contain(value => value.Rule == "explicit_tribute_context");
    }

    [Test]
    public void SkippingASongReferenceDoesNotHideALaterTributeSetHeading()
    {
        var raw = JObject.Parse(Raw("2012-06-02", "Performer Live at Venue on 2012-06-02"));
        raw["metadata"]!["description"] =
            "<div>* w/ In Memory of Elizabeth Reed Jam</div><div>Set 2 - Tribute To Levon Helm #</div>";

        var result = Evaluate(Project(raw.ToString()));

        result.Reasons.Should().Contain(TapersSectionReason.TributeContext);
        result
            .Evidence.Should()
            .Contain(value => value.Locator == "metadata.description" && value.Value == "Tribute");
    }

    [Test]
    public void AMatchingSongFileCannotExcuseATributeSetHeading()
    {
        var raw = JObject.Parse(Raw("2025-01-02", "Performer Live at Venue on 2025-01-02"));
        raw["metadata"]!["description"] = "<div>Set 2 - In Memory Of Elizabeth Reed</div>";
        raw["files"]![0]!["title"] = "In Memory Of Elizabeth Reed";

        Evaluate(Project(raw.ToString()))
            .Reasons.Should()
            .Contain(TapersSectionReason.TributeContext);
    }

    [Test]
    public void CurrentMetadataMarch6WinsOverImmutableJune6Identifier()
    {
        const string identifier = "pavement1994-06-06";
        var raw = JObject.Parse(Raw("1994-03-06", "Performer Live at Venue on 1994-03-06"));
        raw["metadata"]!["identifier"] = identifier;

        var result = Evaluate(ArchiveObservationProjector.Project(new(identifier), raw.ToString()));
        result.IsEligible.Should().BeTrue();
        result.DisplayDate.Should().Be("1994-03-06");
        result
            .DateDetails!.Notes.Should()
            .Contain(note => note.Code == TapersSectionDateNoteCode.IdentifierDateDisagreement);
    }

    [TestCase("2025-09-06", false)]
    [TestCase("2025-10-20", true)]
    public void AudioFormatIdentifierSuffixCannotInventAYearButItsIsoDateStillSuppliesDiagnostics(
        string metadataDate,
        bool identifierDisagrees
    )
    {
        // A real Gov't Mule identifier ends in .2496 for 24-bit/96 kHz audio.
        const string identifier = "govtmule2025-09-06.mk21.cmc1k.ad8.sxr4.2496";
        var raw = JObject.Parse(Raw(metadataDate, "Performer Live at Venue on " + metadataDate));
        raw["metadata"]!["identifier"] = identifier;

        var result = Evaluate(ArchiveObservationProjector.Project(new(identifier), raw.ToString()));

        result.IsEligible.Should().BeTrue();
        result.DisplayDate.Should().Be(metadataDate);
        result
            .DateDetails!.Notes.Any(note =>
                note.Code == TapersSectionDateNoteCode.IdentifierDateDisagreement
            )
            .Should()
            .Be(identifierDisagrees);
        result
            .Evidence.Where(value => value.Locator == "item_identifier")
            .Select(value => value.Value)
            .Should()
            .Equal("2025-09-06");
    }

    [Test]
    public void PartialIsoIdentifierDateStillSuppliesDiagnosticsWithoutAStandaloneYearLimit()
    {
        const string identifier = "performer7316-07.audio.2496";
        var raw = JObject.Parse(Raw("7316-08-XX", "Performer"));
        raw["metadata"]!["identifier"] = identifier;

        var result = Evaluate(ArchiveObservationProjector.Project(new(identifier), raw.ToString()));

        result.IsEligible.Should().BeTrue();
        result.DisplayDate.Should().Be("7316-08-XX");
        result
            .DateDetails!.Notes.Should()
            .Contain(note => note.Code == TapersSectionDateNoteCode.IdentifierDateDisagreement);
        result
            .Evidence.Where(value => value.Locator == "item_identifier")
            .Select(value => value.Value)
            .Should()
            .Equal("7316-07");
    }

    [Test]
    public void AHyphenBeforeTheIdentifierDateRetainsItsNonblockingDisagreement()
    {
        const string identifier = "pavement-1999-12-31";
        var raw = JObject.Parse(Raw("2001-04-05", "Performer Live at Venue on 2001-04-05"));
        raw["metadata"]!["identifier"] = identifier;

        var result = Evaluate(ArchiveObservationProjector.Project(new(identifier), raw.ToString()));

        result.IsEligible.Should().BeTrue();
        result.DisplayDate.Should().Be("2001-04-05");
        result
            .Evidence.Should()
            .Contain(value => value.Locator == "item_identifier" && value.Value == "1999-12-31");
        result
            .DateDetails!.Notes.Should()
            .Contain(note => note.Code == TapersSectionDateNoteCode.IdentifierDateDisagreement);
    }

    [TestCase("1992-00-00", "1992-01-01", "1992-XX-XX", TapersSectionDatePrecision.Year)]
    [TestCase("1992-03-00", "1992-03-01", "1992-03-XX", TapersSectionDatePrecision.Month)]
    [TestCase("1992-00-06", "1992-01-06", "1992-XX-06", TapersSectionDatePrecision.Partial)]
    [TestCase("1992-XX-XX", "1992-01-01", "1992-XX-XX", TapersSectionDatePrecision.Year)]
    [TestCase("1992-??-??", "1992-01-01", "1992-XX-XX", TapersSectionDatePrecision.Year)]
    public void PartialPerformanceTitleExplainsStructuredFirstDayPlaceholder(
        string partial,
        string structured,
        string expected,
        TapersSectionDatePrecision precision
    )
    {
        var identifier = "pavement" + partial;
        var raw = JObject.Parse(
            Raw(structured, "Performer: " + partial + " KSSU Radio?, Sacremento, CA")
        );
        raw["metadata"]!["identifier"] = identifier;

        var result = Evaluate(ArchiveObservationProjector.Project(new(identifier), raw.ToString()));

        result.IsEligible.Should().BeTrue();
        result.DisplayDate.Should().Be(expected);
        result.DateDetails!.Precision.Should().Be(precision);
        result.DateDetails.SelectedEvidence.Locator.Should().Be("metadata.title");
        result
            .DateDetails.Notes.Should()
            .Contain(note => note.Code == TapersSectionDateNoteCode.PartialDate);
        result
            .DateDetails.Notes.Should()
            .Contain(note => note.Code == TapersSectionDateNoteCode.MetadataDateDisagreement);
        result
            .Evidence.Should()
            .Contain(value => value.Locator == "metadata.title" && value.Value == partial);
        result
            .Evidence.Should()
            .Contain(value => value.Locator == "item_identifier" && value.Value == partial);
    }

    [TestCase("1992", "1992-XX-XX", TapersSectionDatePrecision.Year)]
    [TestCase("1992-03", "1992-03-XX", TapersSectionDatePrecision.Month)]
    [TestCase("1992-03-00", "1992-03-XX", TapersSectionDatePrecision.Month)]
    [TestCase("1992-xx-14", "1992-XX-14", TapersSectionDatePrecision.Partial)]
    public void StructuredKnownYearPartialDatesPreserveTheirPrecision(
        string supplied,
        string expected,
        TapersSectionDatePrecision precision
    )
    {
        var result = Evaluate(Project(Raw(supplied, "Performer")));

        result.IsEligible.Should().BeTrue();
        result.DisplayDate.Should().Be(expected);
        result.DateDetails!.Precision.Should().Be(precision);
        result.DateDetails.SelectedEvidence.Value.Should().Be(supplied);
        result
            .DateDetails.Notes.Should()
            .Contain(note => note.Code == TapersSectionDateNoteCode.PartialDate);
    }

    [Test]
    public void ImmutablePartialIdentifierCannotDowngradeConfirmedJanuary1()
    {
        const string identifier = "performer1992-00-00";
        var raw = JObject.Parse(Raw("1992-01-01", "Performer Live at Venue on 1992-01-01"));
        raw["metadata"]!["identifier"] = identifier;

        var result = Evaluate(ArchiveObservationProjector.Project(new(identifier), raw.ToString()));

        result.IsEligible.Should().BeTrue();
        result.DisplayDate.Should().Be("1992-01-01");
        result.DateDetails!.Precision.Should().Be(TapersSectionDatePrecision.Day);
    }

    [Test]
    public void CompatibleSupportedTitleCanRefineStructuredPartialDate()
    {
        var result = Evaluate(Project(Raw("2025-XX-XX", "Performer Live at Venue on 2025-07-20")));

        result.IsEligible.Should().BeTrue();
        result.DisplayDate.Should().Be("2025-07-20");
        result.DateDetails!.SelectedEvidence.Locator.Should().Be("metadata.title");
    }

    [Test]
    public void CompatibleIdentifierCannotRefineStructuredPartialDate()
    {
        const string identifier = "performer1992-03-06";
        var raw = JObject.Parse(Raw("1992-XX-XX", "Performer"));
        raw["metadata"]!["identifier"] = identifier;

        var result = Evaluate(ArchiveObservationProjector.Project(new(identifier), raw.ToString()));

        result.IsEligible.Should().BeTrue();
        result.DisplayDate.Should().Be("1992-XX-XX");
    }

    [TestCase("Performer abc7316def live")]
    [TestCase("Performer abcd1234abcd live")]
    public void FourDigitSequencesInsideNamesAreNotPerformanceYears(string title)
    {
        var result = Evaluate(Project(Raw("2000-01-01", title)));

        result.IsEligible.Should().BeTrue();
        result.DisplayDate.Should().Be("2000-01-01");
        result.Evidence.Should().NotContain(value => value.Locator == "metadata.title");
    }

    [TestCase("Band 1999 live")]
    [TestCase("Band 1999 Live at Venue on 2000-01-01")]
    public void AYearInTheReviewedPerformerNameDoesNotConflictWithItsPerformanceDate(string title)
    {
        var raw = JObject.Parse(Raw("2000-01-01", title));
        raw["metadata"]!["creator"] = "Band 1999";

        var result = new TapersSectionEligibilityEvaluator().Evaluate(
            Project(raw.ToString()),
            [new(CandidateId, true, ["Band 1999"], ArtistUuid)]
        );

        result.IsEligible.Should().BeTrue();
        result.DisplayDate.Should().Be("2000-01-01");
        result
            .Evidence.Should()
            .NotContain(value => value.Locator == "metadata.title" && value.Value == "1999");
    }

    [Test]
    public void AReviewedArtistAndYearHeadingUsesSyntaxRatherThanAFutureYearCutoff()
    {
        var result = Evaluate(Project(Raw(null, "Performer - 7316")));

        result.IsEligible.Should().BeTrue();
        result.DisplayDate.Should().Be("7316-XX-XX");
        result.DateDetails!.Precision.Should().Be(TapersSectionDatePrecision.Year);
    }

    [Test]
    public void IdentifierAloneCannotSupplyMissingMetadataDate()
    {
        const string identifier = "performer1992-03-06";
        var raw = JObject.Parse(Raw(null, "Performer"));
        raw["metadata"]!["identifier"] = identifier;

        var result = Evaluate(ArchiveObservationProjector.Project(new(identifier), raw.ToString()));

        result.Reasons.Should().Contain(TapersSectionReason.MissingDate);
        result.DisplayDate.Should().BeNull();
        result.Evidence.Should().Contain(value => value.Locator == "item_identifier");
    }

    [TestCase("Performer - 1992", "1992-XX-XX")]
    [TestCase("Performer - 1992-07", "1992-07-XX")]
    [TestCase("Performer Live at Venue on 1992-00-00", "1992-XX-XX")]
    public void SupportedPartialTitleCanSupplyMissingStructuredDate(string title, string expected)
    {
        var result = Evaluate(Project(Raw(null, title)));

        result.IsEligible.Should().BeTrue();
        result.DisplayDate.Should().Be(expected);
        result.DateDetails!.SelectedEvidence.Locator.Should().Be("metadata.title");
    }

    [Test]
    public void PartialTitleCannotHideConflictingCurrentStructuredDates()
    {
        var raw = JObject.Parse(Raw("1992-01-01", "Performer - 1992-XX-XX"));
        raw["metadata"]!["date"] = new JArray("1992-01-01", "1992-07-20");

        var result = Evaluate(Project(raw.ToString()));

        result.IsEligible.Should().BeFalse();
        result.Reasons.Should().Contain(TapersSectionReason.ConflictingDate);
    }

    [Test]
    public void NonblockingPartialDateDetailsRoundTripWithStableWireValues()
    {
        var result = Evaluate(Project(Raw("1992-03-XX", "Performer")));

        var json = JsonSerializer.Serialize(result);
        var restored = JsonSerializer.Deserialize<TapersSectionInterpretation>(json)!;

        restored.Should().BeEquivalentTo(result);
        restored.IsEligible.Should().BeTrue();
        using var parsed = JsonDocument.Parse(json);
        parsed
            .RootElement.GetProperty("DateDetails")
            .GetProperty("Precision")
            .GetString()
            .Should()
            .Be("month");
        parsed
            .RootElement.GetProperty("DateDetails")
            .GetProperty("Notes")[0]
            .GetProperty("Code")
            .GetString()
            .Should()
            .Be("partial_date");
    }

    [TestCase("XXXX-XX-XX")]
    [TestCase("0000-00-00")]
    [TestCase("2025-13-01")]
    public void UnknownYearAndUnusableCalendarDatesRemainHeld(string supplied)
    {
        var result = Evaluate(Project(Raw(supplied, "Performer")));

        result.IsEligible.Should().BeFalse();
        result.Reasons.Should().Contain(TapersSectionReason.MissingDate);
        result.DateDetails.Should().BeNull();
    }

    [Test]
    public void CompleteJanuary1PerformanceDateRemainsEligibleDespiteUncertainTransferDate()
    {
        const string identifier = "performer1992-01-01";
        var raw = JObject.Parse(Raw("1992-01-01", "Performer Live at Venue on 1992-01-01"));
        raw["metadata"]!["identifier"] = identifier;
        raw["metadata"]!["description"] = "Transfer date: 2001-00-00";

        var result = Evaluate(ArchiveObservationProjector.Project(new(identifier), raw.ToString()));

        result.IsEligible.Should().BeTrue();
        result.DisplayDate.Should().Be("1992-01-01");
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
    public void CorrectedMetadataDateCanUpdateTheSamePublishedArtist()
    {
        var result = new TapersSectionEligibilityEvaluator().Evaluate(
            Project(Raw("2025-01-02", "Performer")),
            [new(CandidateId, true, ["Performer"], ArtistUuid)],
            new(ArtistUuid, "2025-01-01")
        );
        result.IsEligible.Should().BeTrue();
        result.DisplayDate.Should().Be("2025-01-02");
    }

    [Test]
    public void PublishedArtistOwnershipStillHoldsAChangedPerformer()
    {
        var result = new TapersSectionEligibilityEvaluator().Evaluate(
            Project(Raw("2025-01-02", "Performer")),
            [new(CandidateId, true, ["Performer"], ArtistUuid)],
            new(Guid.NewGuid(), "2025-01-02")
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
    public void PersistedInterpretationRetainsItsAssignmentAndConflict()
    {
        // Existing PostgreSQL evidence must remain readable after a worker update.
        var persisted = """
            {"RulesVersion":1,"CreatorKeys":["PERFORMER"],"CandidateId":"11111111-1111-1111-1111-111111111111",
             "DisplayDate":"2024-07-20","Evidence":[],"Reasons":["published_assignment_conflict"]}
            """;
        var interpretation = JsonSerializer.Deserialize<TapersSectionInterpretation>(persisted)!;

        interpretation.CandidateId.Should().Be(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        interpretation.DisplayDate.Should().Be("2024-07-20");
        interpretation.Reasons.Should().Equal(TapersSectionReason.PublishedAssignmentConflict);
        interpretation.IsEligible.Should().BeFalse();
        interpretation.DateDetails.Should().BeNull();
        var restored = JsonSerializer.Deserialize<TapersSectionInterpretation>(
            JsonSerializer.Serialize(interpretation)
        )!;
        restored.Should().BeEquivalentTo(interpretation);
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
