using System.Text.Json.Nodes;
using FluentAssertions;

namespace RelistenApiTests.V4;

public sealed partial class TestV4CatalogHttp
{
    [Test]
    public async Task GraphQL_batched_song_tracks_keep_each_parent_limit_and_selection()
    {
        var data = await Graph(
            """
            {
              scarlet: song(id: "song_00000101") {
                shows(first: 50) { nodes {
                  show { id }
                  all: tracks(first: 1, selection: ALL) {
                    nodes { id } pageInfo { hasNextPage endCursor }
                  }
                  top: tracks(first: 50, selection: TOP) { nodes { id } }
                } }
              }
              fire: song(id: "song_00000102") {
                shows(first: 50) { nodes {
                  show { id } tracks(first: 50) { nodes { id } }
                } }
              }
            }
            """
        );
        var all = (await GetJson(DiscoverySongPath + "?include_tracks=all"))["shows"]!
            .AsArray()
            .ToDictionary(node => node!["show"]!["id"]!.GetValue<string>());
        var top = (await GetJson(DiscoverySongPath + "?include_tracks=top"))["shows"]!
            .AsArray()
            .ToDictionary(node => node!["show"]!["id"]!.GetValue<string>());

        var shows = data["scarlet"]!["shows"]!["nodes"]!.AsArray();
        shows.Count.Should().Be(all.Count);
        foreach (var node in shows)
        {
            var id = node!["show"]!["id"]!.GetValue<string>();
            var expected = TrackIds(all[id]!["tracks"]!);
            TrackIds(node["all"]!["nodes"]!).Should().Equal(expected.Take(1));
            node["all"]!["pageInfo"]!["hasNextPage"]!
                .GetValue<bool>()
                .Should()
                .Be(expected.Length > 1);
            TrackIds(node["top"]!["nodes"]!).Should().Equal(TrackIds(top[id]!["tracks"]!));
        }

        // Fire shares one multi-song file with Scarlet, but must not inherit Scarlet's
        // other show's files when both populations are materialized in the same batch.
        var fire = data["fire"]!["shows"]!["nodes"]!.AsArray();
        fire.Select(node => node!["show"]!["id"]!.GetValue<string>())
            .Should()
            .Equal(CatalogId("show", 311));
        TrackIds(fire[0]!["tracks"]!["nodes"]!).Should().Equal(CatalogId("track", 341));
    }

    private static string[] TrackIds(JsonNode nodes) =>
        nodes.AsArray().Select(node => node!["id"]!.GetValue<string>()).ToArray();
}
