using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Relisten.Normalization;
using Relisten.Normalization.Songs;
using Relisten.Normalization.Titles;

namespace Relisten.NormalizationTests;

/// Older activity payloads contain text tokens. Keep them readable after the domain becomes typed.
public class TestSongPayloads
{
    [TestCase("jam", SongKind.Jam)]
    [TestCase(null, null)]
    public void ExistingSongPayloadsKeepTheirKinds(string? token, SongKind? expected)
    {
        var kindJson = JsonSerializer.Serialize(token);
        var json = $$"""
            {"Id":"song_01234567","ArtistUuid":"00000000-0000-0000-0000-000000000001","Name":"Dark Star","Kind":{{kindJson}},"OriginalArtist":null,"Confidence":0.95}
            """;

        var song = JsonSerializer.Deserialize<SongRecord>(json)!;

        song.Kind.Should().Be(expected);
        JsonNode
            .DeepEquals(JsonNode.Parse(json), JsonSerializer.SerializeToNode(song))
            .Should()
            .BeTrue();
    }

    [Test]
    public void ExistingTrackPartsKeepTheirQualifierTokens()
    {
        const string json = """
            {"TrackUuid":"00000000-0000-0000-0000-000000000001","Part":1,"SongId":"song_01234567","Text":"Dark Star","SegueAfter":true,"Qualifiers":["with_guest","tape_flip"],"Confidence":0.95,"Resolved":true,"Basis":"ruling","Notes":[]}
            """;

        var part = JsonSerializer.Deserialize<TrackPartRecord>(json)!;

        part.Qualifiers.Should().Equal(TrackQualifier.WithGuest, TrackQualifier.TapeFlip);
        JsonNode
            .DeepEquals(JsonNode.Parse(json), JsonSerializer.SerializeToNode(part))
            .Should()
            .BeTrue();
    }

    [Test]
    public void ExistingParsedTitleAndDecisionPayloadsKeepTheirQualifiers()
    {
        const string titleJson = """
            {"Raw":"Dark Star reprise","Parts":[{"Text":"Dark Star reprise","Key":"dark star reprise","Qualifiers":["partial"],"SegueAfter":false,"Hint":0,"Notes":[],"Bare":"dark star","BareQualifier":"reprise"}],"HasText":true}
            """;
        const string decisionJson = """
            {"Songs":["song_01234567"],"Confidence":0.95,"Basis":"ruling","Qualifier":"reprise","Text":null}
            """;

        var title = JsonSerializer.Deserialize<ParsedTitle>(titleJson)!;
        var decision = JsonSerializer.Deserialize<PartDecision>(decisionJson)!;

        title.Parts.Single().BareQualifier.Should().Be(TrackQualifier.Reprise);
        title.Parts.Single().Qualifiers.Should().Equal(TrackQualifier.Partial);
        decision.Qualifier.Should().Be(TrackQualifier.Reprise);
        JsonNode
            .DeepEquals(JsonNode.Parse(titleJson), JsonSerializer.SerializeToNode(title))
            .Should()
            .BeTrue();
        JsonNode
            .DeepEquals(JsonNode.Parse(decisionJson), JsonSerializer.SerializeToNode(decision))
            .Should()
            .BeTrue();
    }
}
