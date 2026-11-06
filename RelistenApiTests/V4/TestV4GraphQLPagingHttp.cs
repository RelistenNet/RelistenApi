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
    [TestCase("source")]
    [TestCase("connection")]
    [TestCase("resolve")]
    public async Task GraphQL_recording_selection_respects_budgets_fragments_and_directives(
        string entry
    )
    {
        var selection = entry switch
        {
            "source" => "recording: source(id:$id) { id ...Recording }",
            "connection" => "show(id:$id) { sources(first:1) { nodes { id ...Recording } } }",
            _ =>
                "resolve(references:[{kind:SOURCE,id:$id}]) { target { ... on Source { id ...Recording } } }",
        };
        var query =
            "query($id:"
            + (entry == "resolve" ? "String!" : "ID!")
            + ",$full:Boolean!) { "
            + selection
            + " } "
            + "fragment Recording on Source { complete: sets @include(if:$full) { tracks { id } } }";
        var id = entry == "connection" ? CatalogId("show", 311) : CatalogId("source", 321);
        JsonNode Recording(JsonNode data) =>
            entry switch
            {
                "source" => data["recording"]!,
                "connection" => data["show"]!["sources"]!["nodes"]![0]!,
                _ => data["resolve"]![0]!["target"]!,
            };
        var options = app.Services.GetRequiredService<CatalogGraphOptions>();
        var original = options.MaxMaterializedRows;
        options.MaxMaterializedRows = 2;
        try
        {
            var shallow = Recording(await Graph(query, new { id, full = false }));
            shallow["id"]!.GetValue<string>().Should().Be(CatalogId("source", 321));
            shallow.AsObject().ContainsKey("complete").Should().BeFalse();
            using var response = await http.PostAsJsonAsync(
                "/api/v4/graphql",
                new { query, variables = new { id, full = true } }
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
        var complete = Recording(await Graph(query, new { id, full = true }));
        var rest = await GetJson("/api/v4/sources/" + CatalogId("source", 321));
        complete["complete"]!
            .AsArray()
            .SelectMany(set => set!["tracks"]!.AsArray())
            .Select(track => track!["id"]!.GetValue<string>())
            .Should()
            .Equal(
                rest["sets"]!
                    .AsArray()
                    .SelectMany(set => set!["tracks"]!.AsArray())
                    .Select(track => track!["id"]!.GetValue<string>())
            );
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task GraphQL_song_timings_follow_fragments_aliases_and_conditional_selection(
        bool include
    )
    {
        await using var connection = await MigratedTestDatabase.OpenAsync();
        var original = (
            await connection.QueryAsync<(long Id, double? Duration)>(
                "SELECT id,duration FROM source_tracks WHERE id IN(925041,925042)"
            )
        ).ToArray();
        try
        {
            await connection.ExecuteAsync(
                "UPDATE source_tracks SET duration=CASE id WHEN 925041 THEN 600 ELSE 300 END WHERE id IN(925041,925042)"
            );
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
                      details: timings { singleSong { median sourceCount } multiSong { median sourceCount } }
                    } } }
                  }
                }
                fragment TimingFields on SongShow {
                  measurements: timings { singleSong { median sourceCount } multiSong { median sourceCount } }
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
            detailed[0]!["node"]!["details"]!["multiSong"]!["median"]!
                .GetValue<double>()
                .Should()
                .Be(600);
            var rest = await GetJson(DiscoverySongPath);
            for (var i = 0; i < detailed.Count; i++)
            {
                foreach (
                    var (graphName, restName) in new[]
                    {
                        ("singleSong", "single_song"),
                        ("multiSong", "multi_song"),
                    }
                )
                {
                    JsonNode
                        .DeepEquals(
                            detailed[i]!["node"]!["details"]![graphName]?["median"],
                            rest["shows"]![i]!["timings"]![restName]?["median"]
                        )
                        .Should()
                        .BeTrue();
                }
            }
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
                "UPDATE source_tracks SET duration=@duration WHERE id=@id",
                original.Select(row => new { id = row.Id, duration = row.Duration })
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
