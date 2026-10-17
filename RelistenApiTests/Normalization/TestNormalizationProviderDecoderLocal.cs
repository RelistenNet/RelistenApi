using System.Text;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace RelistenApiTests.Normalization;

[TestFixture]
public sealed class TestNormalizationProviderDecoderLocal
{
    private readonly NormalizationProviderDecoder decoder = new();

    [Test]
    public void RootKeepsDirectoriesAndUnknownDatesSeparate()
    {
        var input = Input(
            "metadata_root",
            "catalog-a",
            """
            {"data":{"1970-xx-xx":[
              {"dir":"early","date":"1970-xx-xx","venue":"Old Hall","city":"Montréal","state":"QC","taper_notes":"Taped by Zoë"},
              {"dir":"late","date":"1970-xx-xx","venue":"New Hall","taper_notes":"Transferred by Pat"}
            ]}}
            """
        );
        var result = decoder.Decode(input);

        result.Recordings.Should().HaveCount(2);
        result
            .Recordings.Select(recording => recording.LiteralDate!.Value)
            .Should()
            .Equal("1970-xx-xx", "1970-xx-xx");
        result
            .Recordings.Select(recording => recording.Origin.Item.ProviderKey)
            .Should()
            .Equal("[\"catalog-a\",\"early\"]", "[\"catalog-a\",\"late\"]");
        result
            .Recordings.Select(recording => recording.Venue!.Names.Single().Value)
            .Should()
            .Equal("Old Hall", "New Hall");
        result.Recordings[0].Venue!.City!.Value.Should().Be("Montréal");
        result
            .Recordings[0]
            .Text.Single()
            .Text.Evidence.JsonPointer.Should()
            .Be("/data/1970-xx-xx/0/taper_notes");
        result
            .Recordings.Should()
            .OnlyContain(recording =>
                recording.Origin.Scope == ProviderObservationScope.CatalogMember
            );
        result.Setlists.Should().BeEmpty();
        result.Issues.Should().BeEmpty();
    }

    [Test]
    public void DirectoryKeepsFilesNotesAndRawLocatorsWithoutAssigningADate()
    {
        var input = Input(
            "directory_metadata",
            "[\"catalog-a\",\"disc-one\"]",
            """
            {"txts":["Taped by Zoë","Transferred by Pat"],"tracks":[
              {"mp3":"song-2.mp3","title":"different title","tags":{"title":"Scarlet > Fire","track":{"no":2}}},
              {"mp3":"song-1.mp3","tags":{"title":"Scarlet","track":{"no":1}}}
            ]}
            """
        );
        var result = decoder.Decode(input);
        var recording = result.Recordings.Should().ContainSingle().Subject;

        recording.LiteralDate.Should().BeNull();
        recording
            .Text.Select(field => field.Text.Value)
            .Should()
            .Equal("Taped by Zoë", "Transferred by Pat");
        recording
            .Text.Select(field => field.Text.Evidence.JsonPointer)
            .Should()
            .Equal("/txts/0", "/txts/1");
        recording
            .Files.Select(file => file.PlaybackLocator!.Value)
            .Should()
            .Equal("song-2.mp3", "song-1.mp3");
        recording.Files.Select(file => file.Position!.Value).Should().Equal(2, 1);
        recording.Files[0].Title!.Value.Should().Be("Scarlet > Fire");
        recording.Files[0].Text.Single().Text.Value.Should().Be("different title");
        recording.Files[0].Title!.Evidence.JsonPointer.Should().Be("/tracks/0/tags/title");
        result.Issues.Should().ContainSingle(issue => issue.Code == "conflicting_file_title");
        result.Setlists.Should().BeEmpty();
        using var document = NormalizationRetainedDocument.Open(input);
        foreach (var field in recording.Text)
        {
            document.ValidateEvidence(field.Text.Evidence);
            document
                .Resolve(field.Text.Evidence.JsonPointer)
                .GetString()
                .Should()
                .Be(field.Text.Value);
        }
    }

    [Test]
    public void DateKeyFallbackCitesItsContainingMapAndMetadataDateWinsConflict()
    {
        var result = decoder.Decode(
            Input(
                "metadata_root",
                "catalog-a",
                """
                {"data":{"1970-xx-xx":[{"dir":"a"}],"1970-01-01":[{"dir":"b","date":"1970-01-02"}]}}
                """
            )
        );

        result.Recordings[0].LiteralDate!.Value.Should().Be("1970-xx-xx");
        result.Recordings[0].LiteralDate!.Evidence.JsonPointer.Should().Be("/data");
        result.Recordings[1].LiteralDate!.Value.Should().Be("1970-01-02");
        result.Issues.Should().ContainSingle(issue => issue.Code == "conflicting_directory_date");
    }

    [Test]
    public void UnsupportedEntriesKeepValidNeighborsAndExactIssueLocators()
    {
        var result = decoder.Decode(
            Input(
                "metadata_root",
                "catalog-a",
                """
                {"data":{"1970-01-01":[false,{"dir":"a","venue":"Hall"},{}],"1970-01-02":{}}}
                """
            )
        );
        result.Recordings.Should().ContainSingle();
        result
            .Issues.Select(issue => issue.Code)
            .Should()
            .Equal(
                "expected_directory_metadata",
                "missing_directory_identity",
                "expected_directory_list"
            );
        result
            .Issues.Select(issue => issue.Evidence.JsonPointer)
            .Should()
            .Equal("/data/1970-01-01/0", "/data/1970-01-01/2", "/data/1970-01-02");
    }

    [TestCase("metadata_root", "catalog-a", "{\"data\":[]}")]
    [TestCase("directory_metadata", "catalog-a", "{\"tracks\":[],\"txts\":[]}")]
    [TestCase("directory_metadata", "[\"a\",\"b\"]", "{\"tracks\":null,\"txts\":[]}")]
    public void InvalidRepresentationDoesNotReturnPartialDomainAssertions(
        string type,
        string key,
        string json
    )
    {
        var act = () => decoder.Decode(Input(type, key, json));
        act.Should().ThrowExactly<System.IO.InvalidDataException>();
    }

    [Test]
    public void MalformedTagObjectsKeepFilesButReportExactUnsupportedFields()
    {
        var result = decoder.Decode(
            Input(
                "directory_metadata",
                "[\"a\",\"b\"]",
                """
                {"txts":[],"tracks":[
                  {"mp3":"one.mp3","tags":[]},
                  {"mp3":"two.mp3","tags":{"track":false}},
                  {"mp3":"three.mp3","tags":{"title":"Song","track":{"no":3}}}
                ]}
                """
            )
        );
        result.Recordings.Single().Files.Should().HaveCount(3);
        result
            .Issues.Select(issue => issue.Evidence.JsonPointer)
            .Should()
            .Equal("/tracks/0/tags", "/tracks/1/tags/track");
        result.Recordings.Single().Files.Last().Position!.Value.Should().Be(3);
    }

    [Test]
    public void UnknownCaptureVersionAndCancelledRunDoNotDecode()
    {
        var input = Input("metadata_root", "catalog-a", "{\"data\":{}}");
        var unsupported = () =>
            decoder.Decode(
                new NormalizationInputRevision
                {
                    Uuid = input.Uuid,
                    Resource = input.Resource,
                    Payload = Encoding.UTF8.GetBytes(
                        "future representation with a different grammar"
                    ),
                    MediaType = input.MediaType,
                    CaptureVersion = "future-format",
                }
            );
        unsupported.Should().Throw<NotSupportedException>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = () => decoder.Decode(input, cancellation.Token);
        cancelled.Should().Throw<OperationCanceledException>();
    }

    private static NormalizationInputRevision Input(string type, string key, string json) =>
        new()
        {
            Uuid = Guid.NewGuid(),
            Resource = new("local", type, key),
            Payload = Encoding.UTF8.GetBytes(json),
            MediaType = "application/json",
            CaptureVersion = "local-http-json-v1",
        };
}
