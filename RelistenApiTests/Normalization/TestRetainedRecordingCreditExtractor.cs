using System.Text;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace RelistenApiTests.Normalization;

[TestFixture]
public sealed class TestRetainedRecordingCreditExtractor
{
    private readonly RetainedRecordingCreditExtractor extractor = new();

    [Test]
    public void ArchiveArrayCreditsRetainOriginalFieldAndRole()
    {
        var input = Input(
            """
            {"metadata":{"identifier":"recording-a","taper":["Zoë","Pat"],"transferer":"François","description":"Taped by Lee<br>Transferred by Sam"},"files":[]}
            """,
            "application/json"
        );
        var decoded = new NormalizationProviderDecoder().Decode(input);
        var result = extractor.Extract(input, decoded.Recordings.Single().Text);

        result
            .Credits.Select(credit => (credit.Name, credit.Role))
            .Should()
            .Equal(
                ("Zoë", RecordingCreditRole.Taper),
                ("Pat", RecordingCreditRole.Taper),
                ("François", RecordingCreditRole.Transferrer),
                ("Lee", RecordingCreditRole.Taper),
                ("Sam", RecordingCreditRole.Transferrer)
            );
        result
            .Credits.Take(3)
            .Select(credit => credit.Evidence.JsonPointer)
            .Should()
            .Equal("/metadata/taper/0", "/metadata/taper/1", "/metadata/transferer");
        result.Credits.Last().Evidence.Quote.Should().Be("Transferred by Sam");
        result.Unresolved.Should().BeEmpty();
        using var document = NormalizationRetainedDocument.Open(input);
        foreach (var credit in result.Credits)
        {
            document.ValidateEvidence(credit.Evidence);
        }
    }

    [Test]
    public void NarrowHtmlFieldMapsUnicodeOffsetsIntoOriginalDocument()
    {
        const string prefix = "<header>🎵</header><section>";
        const string prose = "Taped by Zoë &amp; François<br>Transferred by Pat";
        var input = Input(prefix + prose + "</section>", "text/html");
        var start = prefix.EnumerateRunes().Count();
        var field = new ProviderTextObservation(
            "description",
            new(prose, new(input.Uuid, "", start, start + prose.EnumerateRunes().Count(), prose))
        );
        var result = extractor.Extract(input, [field]);

        result.Credits.Select(credit => credit.Name).Should().Equal("Zoë", "François", "Pat");
        result.Credits[0].Evidence.StartCodePoint.Should().Be(start);
        result.Credits[0].Evidence.Quote.Should().Be("Taped by Zoë &amp; François");
        result
            .Credits.Last()
            .Evidence.StartCodePoint.Should()
            .Be(start + "Taped by Zoë &amp; François<br>".Length);
        using var document = NormalizationRetainedDocument.Open(input);
        foreach (var credit in result.Credits)
        {
            document.ValidateEvidence(credit.Evidence);
        }
    }

    [Test]
    public void EquipmentAndUnknownCreditsRemainUnresolvedAndNoncreditFieldsAreIgnored()
    {
        var input = Input(
            "{\"taper\":\"Schoeps CMC6\",\"transferrer\":\"unknown\",\"title\":\"Taped by Someone\"}",
            "application/json"
        );
        var result = extractor.Extract(
            input,
            [
                Field(input, "taper", "Schoeps CMC6"),
                Field(input, "transferrer", "unknown"),
                Field(input, "title", "Taped by Someone"),
            ]
        );
        result.Credits.Should().BeEmpty();
        result
            .Unresolved.Select(issue => issue.Reason)
            .Should()
            .Equal("unparsed_credit", "unknown_credit");
        result
            .Unresolved.Select(issue => issue.Evidence.JsonPointer)
            .Should()
            .Equal("/taper", "/transferrer");
    }

    [Test]
    public void PhishTagNotesCanSupplyExplicitCreditProse()
    {
        var input = new NormalizationInputRevision
        {
            Uuid = Guid.NewGuid(),
            Resource = new("phish.in", "show", "12"),
            Payload = Encoding.UTF8.GetBytes(
                """{"id":12,"date":"1990-01-01","tags":[{"notes":"Taped by Zoë"}],"tracks":[]}"""
            ),
            MediaType = "application/json",
            CaptureVersion = "phishin-show-object-v1",
        };
        var decoded = new NormalizationProviderDecoder().Decode(input);
        var result = extractor.Extract(input, decoded.Recordings.Single().Text);

        var credit = result.Credits.Should().ContainSingle().Subject;
        credit.Name.Should().Be("Zoë");
        credit.Role.Should().Be(RecordingCreditRole.Taper);
        credit.Evidence.JsonPointer.Should().Be("/tags/0/notes");
        credit.Evidence.Quote.Should().Be("Taped by Zoë");
    }

    [Test]
    public void ChangedTextAndForeignRevisionCannotProduceAClaim()
    {
        var input = Input("{\"taper\":\"Zoë\"}", "application/json");
        var changed = () => extractor.Extract(input, [Field(input, "taper", "Pat")]);
        changed.Should().Throw<ArgumentException>();
        var foreign = () =>
            extractor.Extract(input, [new("taper", new("Zoë", new(Guid.NewGuid(), "/taper")))]);
        foreign.Should().Throw<ArgumentException>();
    }

    private static ProviderTextObservation Field(
        NormalizationInputRevision input,
        string field,
        string value
    ) => new(field, new(value, new(input.Uuid, "/" + field)));

    private static NormalizationInputRevision Input(string payload, string mediaType) =>
        new()
        {
            Uuid = Guid.NewGuid(),
            Resource = new("archive.org", "item_metadata", "recording-a"),
            Payload = Encoding.UTF8.GetBytes(payload),
            MediaType = mediaType,
            CaptureVersion = "archive-item-response-v1",
        };
}
