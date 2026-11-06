using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Relisten;
using Relisten.GraphQL;

namespace RelistenApiTests.V4;

public sealed partial class TestV4CatalogHttp
{
    [Test]
    public async Task GraphQL_source_headers_do_not_consume_the_complete_recording_budget()
    {
        var options = app.Services.GetRequiredService<CatalogGraphOptions>();
        var original = options.MaxMaterializedRows;
        options.MaxMaterializedRows = 2;
        try
        {
            var shallow = await Graph(
                "query($show: ID!) { show(id: $show) { sources(first: 1) { nodes { id } } } }",
                new { show = CatalogId("show", 4) }
            );
            shallow["show"]!["sources"]!["nodes"]!.AsArray().Should().ContainSingle();

            // A source, set and track already exceed this budget. Header-only reads
            // must succeed without paying for those complete recording children.
            using var response = await http.PostAsJsonAsync(
                "/api/v4/graphql",
                new
                {
                    query = "query($show: ID!) { show(id: $show) { sources(first: 1) { nodes { id sets { id tracks { id } } } } } }",
                    variables = new { show = CatalogId("show", 4) },
                }
            );
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            json["errors"].Should().NotBeNull(json.ToJsonString());
            json["errors"]!
                .AsArray()
                .Should()
                .Contain(error =>
                    error!["extensions"]!["code"]!.GetValue<string>() == "catalog_row_limit"
                );
        }
        finally
        {
            options.MaxMaterializedRows = original;
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task GraphQL_song_timings_follow_fragments_aliases_and_conditional_selection(
        bool include
    )
    {
        await using var connection = await MigratedTestDatabase.OpenAsync();
        var original = await connection.QuerySingleAsync<double?>(
            "SELECT duration FROM source_tracks WHERE id=925042"
        );
        try
        {
            await connection.ExecuteAsync("UPDATE source_tracks SET duration=300 WHERE id=925042");
            var data = await Graph(
                """
                query($include: Boolean!) {
                  song(id: "song_00000101") {
                    compact: shows(first: 50) { nodes {
                      show { id }
                      ...TimingFields @include(if: $include)
                      tracks(first: 1) { nodes { id } }
                    } }
                    detailed: shows(first: 50) { edges { node {
                      show { id }
                      details: timings { singleSong { median sourceCount } }
                    } } }
                  }
                }
                fragment TimingFields on SongShow {
                  measurements: timings { singleSong { median sourceCount } }
                }
                """,
                new { include }
            );

            var compact = data["song"]!["compact"]!["nodes"]!.AsArray();
            var detailed = data["song"]!["detailed"]!["edges"]!.AsArray();
            detailed[0]!["node"]!["details"]!["singleSong"]!["median"]!
                .GetValue<double>()
                .Should()
                .Be(300);
            for (var i = 0; i < compact.Count; i++)
            {
                compact[i]!["tracks"]!["nodes"]!.AsArray().Should().NotBeEmpty();
                if (include)
                {
                    JsonNode
                        .DeepEquals(compact[i]!["measurements"], detailed[i]!["node"]!["details"])
                        .Should()
                        .BeTrue();
                }
                else
                {
                    compact[i]!.AsObject().ContainsKey("measurements").Should().BeFalse();
                }
            }
        }
        finally
        {
            await connection.ExecuteAsync(
                "UPDATE source_tracks SET duration=@original WHERE id=925042",
                new { original }
            );
        }
    }

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
