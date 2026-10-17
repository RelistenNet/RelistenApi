using System.Text;
using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace RelistenApiTests.Normalization;

[TestFixture]
public sealed class TestNormalizationProviderDecoderArchive
{
    private readonly NormalizationProviderDecoder decoder = new();

    [Test]
    public void ArraysPreserveSeparateCreatorsVenueNamesAndRoleSpecificWording()
    {
        var input = Input(
            """
            {"metadata":{"identifier":"item","creator":["Artist A","Artist B"],
              "date":"1975-03-16","venue":["Macky Auditorium","University of Colorado"],
              "coverage":["Boulder, CO","Colorado"],"taper":["Pat 🎤","François"],
              "transferer":["Avery","Robin"],"source":"AUD > DAT","lineage":"DAT > WAV",
              "description":"<p>Original &amp; exact</p>","notes":" Set 1\nSet 2 "},"files":[]}
            """
        );

        var result = decoder.Decode(input);
        var recording = result.Recordings.Should().ContainSingle().Which;
        recording
            .Performers.Select(person => person.Name!.Value)
            .Should()
            .Equal("Artist A", "Artist B");
        recording
            .Venue!.Names.Select(name => name.Value)
            .Should()
            .Equal("Macky Auditorium", "University of Colorado");
        recording.Venue.OriginalLocality.Should().BeNull();
        recording
            .Text.Where(text => text.Field == "coverage")
            .Select(text => text.Text.Value)
            .Should()
            .Equal("Boulder, CO", "Colorado");
        recording
            .Text.Where(text => text.Field == "taper")
            .Select(text => text.Text.Value)
            .Should()
            .Equal("Pat 🎤", "François");
        recording
            .Text.Where(text => text.Field == "transferrer")
            .Select(text => text.Text.Value)
            .Should()
            .Equal("Avery", "Robin");
        recording
            .Text.Single(text => text.Field == "description")
            .Text.Value.Should()
            .Be("<p>Original &amp; exact</p>");
        recording
            .Text.Single(text => text.Field == "notes")
            .Text.Value.Should()
            .Be(" Set 1\nSet 2 ");
        result.Issues.Select(issue => issue.Code).Should().Equal("multiple_venue_localities");
        result.Repertoire.Should().BeEmpty();
        result.Setlists.Should().BeEmpty();
        AssertEvidence(input, result);
    }

    [Test]
    public void FilesRetainTheirNamesAndOriginalReferencesWithoutInventingTitlesOrPlayback()
    {
        var input = Input(
            """
            {"metadata":{"identifier":"item"},"files":[
              {"name":"folder/a::b.mp3","original":"master.flac","format":"VBR MP3","track":"01",
               "taper":"Pat","transferer":["Avery","Robin"]},
              {"name":"master.flac","title":"Song A > Song B","format":"Flac","track":2}]}
            """
        );

        var result = decoder.Decode(input);
        var files = result.Recordings.Single().Files;
        files.Should().HaveCount(2);
        files[0].Title.Should().BeNull();
        files[0].OriginalFilename!.Value.Should().Be("master.flac");
        files[0].PlaybackLocator.Should().BeNull();
        files[0].Position!.Value.Should().Be(1);
        files[1].Position!.Value.Should().Be(2);
        files[1].Title!.Value.Should().Be("Song A > Song B");
        files[0].Origin.Item.Should().Be(input.Resource);
        files[0].Origin.Evidence.JsonPointer.Should().Be("/files/0");
        var identity = files[0].Identities.Single().Identity;
        identity.Provider.Should().Be("archive.org");
        identity.ResourceType.Should().Be("item_file");
        JsonSerializer
            .Deserialize<string[]>(identity.ProviderKey)
            .Should()
            .Equal("item", "folder/a::b.mp3");
        files[0]
            .Text.Where(text => text.Field == "transferrer")
            .Select(text => text.Text.Value)
            .Should()
            .Equal("Avery", "Robin");
        files[0].Text.Single(text => text.Field == "format").Text.Value.Should().Be("VBR MP3");
        result.Issues.Should().BeEmpty();
        AssertEvidence(input, result);
    }

    [Test]
    public void MissingAndDuplicateFilenamesStayVisibleWithoutPositionalIdentities()
    {
        var input = Input(
            """
            {"metadata":{"identifier":"item"},"files":[
              {"name":"same.mp3","title":"First"},{"name":"same.mp3","title":"Second"},
              {"title":"Unbound","track":"1/10"}]}
            """
        );

        var result = decoder.Decode(input);
        var files = result.Recordings.Single().Files;
        files.Should().HaveCount(3);
        files[0].Identities.Single().Identity.Should().Be(files[1].Identities.Single().Identity);
        files[2].Identities.Should().BeEmpty();
        files[2].Position.Should().BeNull();
        files[2].Text.Single().Text.Value.Should().Be("1/10");
        result
            .Issues.Select(issue => issue.Code)
            .Should()
            .Equal("duplicate_file_identity", "missing_file_identity", "unstructured_track_number");
        AssertEvidence(input, result);
    }

    [Test]
    public void ConflictingDatesAndUnexpectedArrayMembersRetainSupportedEntries()
    {
        var input = Input(
            """
            {"metadata":{"identifier":"item","date":["1975-03-16","1975-03-17"],
              "creator":["Artist",{"name":"Unstructured"}],"taper":["Pat",42,null]}}
            """
        );

        var result = decoder.Decode(input);
        var recording = result.Recordings.Single();
        recording.LiteralDate.Should().BeNull();
        recording.Performers.Single().Name!.Value.Should().Be("Artist");
        recording
            .Text.Where(text => text.Field == "date")
            .Select(text => text.Text.Value)
            .Should()
            .Equal("1975-03-16", "1975-03-17");
        result
            .Issues.Should()
            .Contain(issue =>
                issue.Code == "expected_text" && issue.Evidence.JsonPointer == "/metadata/creator/1"
            );
        result
            .Issues.Should()
            .Contain(issue =>
                issue.Code == "expected_text" && issue.Evidence.JsonPointer == "/metadata/taper/1"
            );
        result.Issues.Should().Contain(issue => issue.Code == "multiple_recording_dates");
        AssertEvidence(input, result);
    }

    [Test]
    public void ArbitraryWorkFieldsRemainUnclassifiedRatherThanAssertingAuthorship()
    {
        var input = Input(
            """
            {"metadata":{"identifier":"item","creator":"The Band","composer":["Pat","Robin"]},
             "files":[{"name":"song.mp3","title":"Song","writer":{"name":"Avery"}}]}
            """
        );

        var result = decoder.Decode(input);
        result
            .Recordings.Single()
            .Text.Where(text => text.Field == "provider_field:composer")
            .Select(text => text.Text.Value)
            .Should()
            .Equal("Pat", "Robin");
        result.Recordings.Single().Files.Single().WorkCredits.Should().BeEmpty();
        result.Repertoire.Should().BeEmpty();
        result.Issues.Count(issue => issue.Code == "unsupported_work_credit_field").Should().Be(2);
        AssertEvidence(input, result);
    }

    [TestCase("{\"is_dark\":true}")]
    [TestCase("{\"is_dark\":\"TRUE\",\"metadata\":{\"identifier\":\"item\"}}")]
    [TestCase("{\"is_dark\":1}")]
    public void UnavailableItemsDoNotCreateRecordingAssertions(string json)
    {
        var input = Input(json);
        var result = decoder.Decode(input);
        result.Recordings.Should().BeEmpty();
        result.Issues.Single().Code.Should().Be("resource_unavailable");
        AssertEvidence(input, result);
    }

    [TestCase("{\"metadata\":{\"identifier\":\"different\"}}")]
    [TestCase("{\"is_dark\":true,\"metadata\":{\"identifier\":\"different\"}}")]
    [TestCase("{\"metadata\":{\"identifier\":[\"item\"]}}")]
    [TestCase("{\"metadata\":{}}")]
    [TestCase("{\"errcode\":104}")]
    [TestCase("{\"error\":\"not available\"}")]
    [TestCase("{\"metadata\":[]}")]
    [TestCase("[]")]
    public void InvalidOrMismatchedEnvelopesFailWithoutAssigningTheRequestedIdentity(string json)
    {
        var decode = () => decoder.Decode(Input(json));
        decode.Should().Throw<ArgumentException>();
    }

    [Test]
    public void MalformedFileEntriesProduceCitedIssuesWithoutLosingValidFiles()
    {
        var input = Input(
            """{"metadata":{"identifier":"item"},"files":["unexpected",{"name":"valid.mp3"}]}"""
        );
        var result = decoder.Decode(input);
        result.Recordings.Single().Files.Single().Filename!.Value.Should().Be("valid.mp3");
        result.Issues.Single().Evidence.JsonPointer.Should().Be("/files/0");
        AssertEvidence(input, result);
    }

    [Test]
    public void RealAadamCollectionMetadataPreservesPerformerAndTaperAsDifferentAssertions()
    {
        // Exact selected fields from retained observation 5d41b4db-d560-4c7e-92e5-dab14f02f0e1
        // in relisten_jerry_20261017. This fixture is a metadata projection, not the full HTTP body.
        var input = Input(
            """
            {"metadata":{"date":"2017-03-04","taper":"Aadam Jacobs",
             "title":"Kool Keith Live at Empty Bottle on 2017-03-04","venue":"Empty Bottle",
             "creator":"Kool Keith","subject":"Aadam Jacobs;AJC Project","mediatype":"audio",
             "collection":"aadamjacobs","identifier":"ajc01969_koolkeith_2017-03-04"}}
            """,
            "ajc01969_koolkeith_2017-03-04"
        );

        var result = decoder.Decode(input);
        var recording = result.Recordings.Single();
        recording.Performers.Single().Name!.Value.Should().Be("Kool Keith");
        recording.Text.Single(text => text.Field == "taper").Text.Value.Should().Be("Aadam Jacobs");
        recording.Venue!.Names.Single().Value.Should().Be("Empty Bottle");
        recording.Files.Should().BeEmpty();
        AssertEvidence(input, result);
    }

    private static NormalizationInputRevision Input(string json, string identifier = "item") =>
        new()
        {
            Uuid = Guid.NewGuid(),
            Resource = new("archive.org", "item_metadata", identifier),
            CaptureVersion = "archive-item-response-v1",
            MediaType = "application/json; charset=utf-8",
            Payload = Encoding.UTF8.GetBytes(json),
        };

    private static void AssertEvidence(
        NormalizationInputRevision input,
        ProviderDecodeResult result
    )
    {
        using var document = NormalizationRetainedDocument.Open(input);
        Visit(JsonSerializer.SerializeToElement(result));

        void Visit(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (value.TryGetProperty("JsonPointer", out _))
                {
                    document.ValidateEvidence(value.Deserialize<NormalizedEvidenceReference>()!);
                }
                foreach (var property in value.EnumerateObject())
                {
                    Visit(property.Value);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    Visit(item);
                }
            }
        }
    }
}
