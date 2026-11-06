using System.Text.Json.Nodes;
using FluentAssertions;

namespace RelistenApiTests.V4;

public sealed partial class TestV4CatalogHttp
{
    [Test]
    public async Task GraphQL_scoped_show_batches_preserve_each_owners_membership()
    {
        var venue = await GetJson(BrowseArtistPath + "/venues/venue_00000001");
        var data = await Graph(
            """
            query($artist: ID!, $year: ID!, $tour: ID!, $rawVenue: ID!, $rawSong: ID!, $relationship: ID!) {
              artist(id: $artist) { shows(first: 50) { nodes { id } totalCount } }
              year(id: $year) { shows(first: 50) { nodes { id } totalCount } }
              tour(id: $tour) { shows(first: 50) { nodes { id } totalCount } }
              venue(id: "venue_00000001") { shows(first: 50) { nodes { id } totalCount } }
              importedVenue(id: $rawVenue) { shows(first: 50) { nodes { id } totalCount } }
              importedSong(id: $rawSong) { shows(first: 50) { nodes { id } totalCount } }
              artistVenue(id: $relationship) { shows(first: 50) { nodes { id } totalCount } }
              filtered: shows(first: 50, filter: {artistId: $artist}) { nodes { id } totalCount }
              intersection: artist(id: $artist) {
                shows(first: 50, filter: {songId: "song_00000101"}) { nodes { id } totalCount }
              }
            }
            """,
            new
            {
                artist = CatalogId("artist", 14),
                year = CatalogId("year", 64),
                tour = CatalogId("tour", 84),
                rawVenue = CatalogId("imported_venue", 74),
                rawSong = CatalogId("imported_song", 351),
                relationship = venue["id"]!.GetValue<string>(),
            }
        );
        foreach (
            var (field, path) in new[]
            {
                ("artist", BrowseArtistPath + "/shows"),
                ("year", BrowseArtistPath + "/years/" + CatalogId("year", 64)),
                ("tour", TourPath(84)),
                ("venue", CanonicalVenuePath),
                ("importedVenue", ImportedVenuePath(74)),
                ("importedSong", ImportedSongPath(351)),
                ("artistVenue", "/api/v4/artist-venues/" + venue["id"]!.GetValue<string>()),
            }
        )
        {
            var rest = await GetJson(path);
            var expected = ResourceIds(rest is JsonArray ? rest : rest["shows"]!);
            var page = data[field]!["shows"]!;
            ResourceIds(page["nodes"]!).Should().Equal(expected, field);
            page["totalCount"]!.GetValue<int>().Should().Be(expected.Length, field);
        }

        ResourceIds(data["filtered"]!["nodes"]!)
            .Should()
            .Equal(ResourceIds(data["artist"]!["shows"]!["nodes"]!));
        data["intersection"]!["shows"]!["nodes"]!.AsArray().Should().BeEmpty();
        data["intersection"]!["shows"]!["totalCount"]!.GetValue<int>().Should().Be(0);
    }

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
            var expected = ResourceIds(all[id]!["tracks"]!);
            ResourceIds(node["all"]!["nodes"]!).Should().Equal(expected.Take(1));
            node["all"]!["pageInfo"]!["hasNextPage"]!
                .GetValue<bool>()
                .Should()
                .Be(expected.Length > 1);
            ResourceIds(node["top"]!["nodes"]!).Should().Equal(ResourceIds(top[id]!["tracks"]!));
        }

        // Fire shares one multi-song file with Scarlet, but must not inherit Scarlet's
        // other show's files when both populations are materialized in the same batch.
        var fire = data["fire"]!["shows"]!["nodes"]!.AsArray();
        fire.Select(node => node!["show"]!["id"]!.GetValue<string>())
            .Should()
            .Equal(CatalogId("show", 311));
        ResourceIds(fire[0]!["tracks"]!["nodes"]!).Should().Equal(CatalogId("track", 341));
    }

    private static string[] ResourceIds(JsonNode nodes) =>
        nodes.AsArray().Select(node => node!["id"]!.GetValue<string>()).ToArray();
}
