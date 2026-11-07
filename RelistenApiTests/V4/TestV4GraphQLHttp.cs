using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace RelistenApiTests.V4;

public sealed partial class TestV4CatalogHttp
{
    private async Task<JsonNode> Graph(string query, object? variables = null)
    {
        using var response = await http.PostAsJsonAsync(
            "/api/v4/graphql",
            new { query, variables }
        );
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["errors"].Should().BeNull(json.ToJsonString());
        return json["data"]!;
    }

    [Test]
    public async Task GraphQL_recording_and_parent_traversal_preserve_playback_identity()
    {
        var data = await Graph(
            """
            query($source: ID!, $track: ID!) {
              source(id: $source) {
                id show { id artist { id } }
                sets { id index tracks { id title mp3Url sourceId normalization { state } } }
              }
              sourceTrack(id: $track) { id source { id } sourceSet { id } show { id } }
            }
            """,
            new { source = CatalogId("source", 321), track = CatalogId("track", 341) }
        );
        var rest = await GetJson("/api/v4/sources/" + CatalogId("source", 321));
        var actual = data["source"]!["sets"]!
            .AsArray()
            .SelectMany(set => set!["tracks"]!.AsArray())
            .ToArray();
        var expected = rest["sets"]!
            .AsArray()
            .SelectMany(set => set!["tracks"]!.AsArray())
            .ToArray();
        actual
            .Select(t => t!["id"]!.GetValue<string>())
            .Should()
            .Equal(expected.Select(t => t!["id"]!.GetValue<string>()));
        for (var i = 0; i < actual.Length; i++)
        {
            actual[i]!["title"]!
                .GetValue<string>()
                .Should()
                .Be(expected[i]!["title"]!.GetValue<string>());
            JsonNode.DeepEquals(actual[i]!["mp3Url"], expected[i]!["mp3_url"]).Should().BeTrue();
        }
        data["sourceTrack"]!["source"]!["id"]!
            .GetValue<string>()
            .Should()
            .Be(CatalogId("source", 321));
    }

    [Test]
    public async Task GraphQL_cursor_pages_preserve_membership_and_filtered_counts()
    {
        const string query = """
            query($artist: ID!, $after: String) {
              artist(id: $artist) { shows(first: 2, after: $after) {
                nodes { id displayDate } edges { cursor node { id } } totalCount pageInfo { endCursor hasNextPage }
              } }
            }
            """;
        var ids = new List<string>();
        string? after = null;
        long? count = null;
        string? firstEdge = null;
        do
        {
            var data = await Graph(query, new { artist = CatalogId("artist", 301), after });
            var page = data["artist"]!["shows"]!;
            count ??= page["totalCount"]!.GetValue<long>();
            var edges = page["edges"]!.AsArray();
            edges
                .Select(edge => edge!["node"]!["id"]!.GetValue<string>())
                .Should()
                .Equal(page["nodes"]!.AsArray().Select(node => node!["id"]!.GetValue<string>()));
            if (edges.Count > 0)
            {
                firstEdge ??= edges[0]!["cursor"]!.GetValue<string>();
                page["pageInfo"]!["endCursor"]!
                    .GetValue<string>()
                    .Should()
                    .Be(edges[^1]!["cursor"]!.GetValue<string>());
            }
            ids.AddRange(page["nodes"]!.AsArray().Select(n => n!["id"]!.GetValue<string>()));
            after = page["pageInfo"]!["hasNextPage"]!.GetValue<bool>()
                ? page["pageInfo"]!["endCursor"]!.GetValue<string>()
                : null;
        } while (after is not null);
        var rest = await GetJson("/api/v4/artists/" + CatalogId("artist", 301) + "/shows");
        ids.Should().Equal(rest.AsArray().Select(n => n!["id"]!.GetValue<string>()));
        ids.Should().OnlyHaveUniqueItems();
        ids.Count.Should().Be((int)count!);
        var shifted = await Graph(
            query,
            new { artist = CatalogId("artist", 301), after = firstEdge }
        );
        shifted["artist"]!["shows"]!["nodes"]!
            .AsArray()
            .Select(node => node!["id"]!.GetValue<string>())
            .Should()
            .Equal(ids.Skip(1).Take(2));
    }

    [Test]
    public async Task GraphQL_root_inventories_preserve_artist_scope_and_canonical_links()
    {
        var data = await Graph(
            """
            query($artist:ID!,$songsArtist:ID!) {
              years(first:50,filter:{artistId:$artist}) { nodes { id } }
              tours(first:50,filter:{artistId:$artist}) { nodes { id } }
              sources(first:50,filter:{artistId:$artist}) { nodes { id } }
              sourceTracks(first:50,filter:{artistId:$artist}) { nodes { id } }
              songs(first:50,filter:{artistId:$songsArtist}) { nodes { id } }
              importedSongs(first:50,filter:{artistId:$songsArtist}) { nodes { id normalizedSong { id } } }
              importedVenues(first:50,filter:{artistId:$artist}) { nodes { id } }
              artistVenues(first:50,filter:{artistId:$artist}) { nodes { id } }
            }
            """,
            new { artist = BrowseArtistId, songsArtist = CatalogId("artist", 301) }
        );
        var expected = new Dictionary<string, string[]>
        {
            ["years"] = [CatalogId("year", 64), CatalogId("year", 160)],
            ["tours"] = [CatalogId("tour", 84)],
            ["sources"] = [CatalogId("source", 24), CatalogId("source", 120)],
            ["sourceTracks"] =
            [
                CatalogId("track", 44),
                CatalogId("track", 45),
                CatalogId("track", 46),
                CatalogId("track", 150),
            ],
            ["songs"] = ["song_00000101", "song_00000102", "song_00000103"],
            ["importedSongs"] = Enumerable
                .Range(351, 4)
                .Select(i => CatalogId("imported_song", i))
                .ToArray(),
            ["importedVenues"] = [CatalogId("imported_venue", 74)],
            ["artistVenues"] = (await GetJson(BrowseArtistPath + "/venues"))
                .AsArray()
                .Select(row => row!["artist_venue"]!["id"]!.GetValue<string>())
                .ToArray(),
        };
        foreach (var (field, ids) in expected)
        {
            data[field]!["nodes"]!
                .AsArray()
                .Select(row => row!["id"]!.GetValue<string>())
                .Should()
                .BeEquivalentTo(ids, field);
        }
        var imports = data["importedSongs"]!["nodes"]!
            .AsArray()
            .ToDictionary(row => row!["id"]!.GetValue<string>());
        imports[CatalogId("imported_song", 351)]!["normalizedSong"]!["id"]!
            .GetValue<string>()
            .Should()
            .Be("song_00000101");
        imports[CatalogId("imported_song", 352)]!["normalizedSong"].Should().BeNull();
    }

    [Test]
    public async Task GraphQL_song_browse_shares_rest_approval_policy()
    {
        var data = await Graph(
            "query($id: ID!) { artist(id: $id) { songs(first: 500) { nodes { kind song { id } importedSong { id normalizedSongId } } } } }",
            new { id = CatalogId("artist", 301) }
        );
        var rest = await GetJson("/api/v4/artists/" + CatalogId("artist", 301) + "/songs");
        string? Identity(JsonNode? node, string imported) =>
            node?["song"]?["id"]?.GetValue<string>() ?? node?[imported]?["id"]?.GetValue<string>();
        data["artist"]!["songs"]!["nodes"]!
            .AsArray()
            .Select(n => Identity(n, "importedSong"))
            .Should()
            .Equal(rest.AsArray().Select(n => Identity(n, "imported_song")));
    }

    [TestCase("review.provider", null)]
    [TestCase("phish.in", "PHISHIN")]
    public async Task GraphQL_artist_metadata_shares_compact_features_and_providers(
        string provider,
        string? expectedProvider
    )
    {
        await using var connection = await MigratedTestDatabase.OpenAsync();
        try
        {
            await connection.ExecuteAsync(
                "UPDATE upstream_sources SET name=@provider WHERE id=910010",
                new { provider }
            );
            var graph = await Graph(
                """
                query($id: ID!) {
                  artist(id: $id) { name showCount features upstreamSources { provider upstreamIdentifier } }
                  __type(name: "ArtistProviderResource") { fields { name } }
                }
                """,
                new { id = CatalogId("artist", 14) }
            );
            var rest = await GetJson(BrowseArtistPath);
            graph["artist"]!["name"]!
                .GetValue<string>()
                .Should()
                .Be(rest["name"]!.GetValue<string>());
            graph["artist"]!["showCount"]!
                .GetValue<int>()
                .Should()
                .Be(rest["show_count"]!.GetValue<int>());
            graph["artist"]!["features"]!
                .GetValue<string>()
                .Should()
                .Be(rest["features"]!.GetValue<string>());
            var assignment = graph["artist"]!["upstreamSources"]!.AsArray().Single()!;
            var actualProvider = assignment["provider"]?.GetValue<string>();
            actualProvider.Should().Be(expectedProvider);
            assignment["upstreamIdentifier"]!
                .GetValue<string>()
                .Should()
                .Be(rest["upstream_sources"]![0]!["upstream_identifier"]!.GetValue<string>());
            graph["__type"]!["fields"]!
                .AsArray()
                .Select(field => field!["name"]!.GetValue<string>())
                .Should()
                .BeEquivalentTo("provider", "upstreamIdentifier");
        }
        finally
        {
            await connection.ExecuteAsync(
                "UPDATE upstream_sources SET name='review.provider' WHERE id=910010"
            );
        }
    }

    [TestCase(910001, 11, "venues")]
    [TestCase(925003, 303, "songs")]
    public async Task GraphQL_artist_browse_switches_to_empty_canonical_catalog_when_approved(
        long artistId,
        int suffix,
        string collection
    )
    {
        var query =
            "query($id: ID!) { artist(id: $id) { "
            + collection
            + "(first: 100) { nodes { kind } totalCount } } }";
        var variables = new { id = CatalogId("artist", suffix) };
        var original = (await Graph(query, variables))["artist"]![collection]!;
        original["nodes"]!.AsArray().Should().NotBeEmpty();
        await using var connection = await MigratedTestDatabase.OpenAsync();
        try
        {
            await connection.ExecuteAsync(
                "UPDATE features SET norm_catalogue=true WHERE artist_id=@artistId",
                new { artistId }
            );
            var approved = (await Graph(query, variables))["artist"]![collection]!;
            approved["nodes"]!.AsArray().Should().BeEmpty();
            approved["totalCount"]!.GetValue<int>().Should().Be(0);
            (await GetJson(ArtistPath(suffix) + "/" + collection)).AsArray().Should().BeEmpty();
        }
        finally
        {
            await connection.ExecuteAsync(
                "UPDATE features SET norm_catalogue=false WHERE artist_id=@artistId",
                new { artistId }
            );
        }
        JsonNode
            .DeepEquals((await Graph(query, variables))["artist"]![collection], original)
            .Should()
            .BeTrue();
    }

    [Test]
    public async Task GraphQL_resolves_saved_references_and_bulk_tracks_in_input_order()
    {
        var first = CatalogId("track", 341);
        var second = CatalogId("track", 343);
        var missing = CatalogId("track", 999);
        var ids = new[] { first, missing, second, first };
        var data = await Graph(
            """
            query($ids:[ID!]!,$refs:[CatalogGraphReferenceInput!]!) {
              sourceTracksByIds(ids:$ids) { id }
              resolve(references:$refs) { id target { ... on SourceTrack { id } } }
            }
            """,
            new { ids, refs = ids.Select(id => new { kind = "TRACK", id }).ToArray() }
        );
        string?[] expected = [first, null, second, first];
        data["sourceTracksByIds"]!
            .AsArray()
            .Select(row => row?["id"]?.GetValue<string>())
            .Should()
            .Equal(expected);
        data["resolve"]!
            .AsArray()
            .Select(row => row!["id"]!.GetValue<string>())
            .Should()
            .Equal(ids);
        data["resolve"]!
            .AsArray()
            .Select(row => row!["target"]?["id"]?.GetValue<string>())
            .Should()
            .Equal(expected);
    }

    [TestCase("Scarlet", 301)]
    [TestCase("Historical", 14)]
    [TestCase("Fixture taper", 14)]
    [TestCase("Sc", 14)]
    public async Task GraphQL_search_categories_preserve_rest_membership(string term, int artist)
    {
        var id = CatalogId("artist", artist);
        var data = await Graph(
            """
            query($q: String!, $artist: ID!) { search(q: $q,artistId: $artist) {
              artists(first: 20) { nodes { id } }
              shows(first: 20) { nodes { id } }
              sources(first: 20) { nodes { id } }
              tours(first: 20) { nodes { id } }
              songs(first: 20) { nodes { song { id } importedSong { id } } }
              venues(first: 20) { nodes { artistVenue { id } importedVenue { id } } }
            } }
            """,
            new { q = term, artist = id }
        );
        var rest = await GetJson(
            "/api/v4/search?q=" + Uri.EscapeDataString(term) + "&artist_id=" + id
        );
        foreach (var category in new[] { "artists", "shows", "sources", "tours" })
        {
            data["search"]![category]!["nodes"]!
                .AsArray()
                .Select(n => n!["id"]!.GetValue<string>())
                .Should()
                .Equal(rest[category]!.AsArray().Select(n => n!["id"]!.GetValue<string>()));
        }
        string? Entry(JsonNode? n, params string[] names) =>
            names
                .Select(name => n?[name]?["id"]?.GetValue<string>())
                .FirstOrDefault(id => id is not null);
        data["search"]!["songs"]!["nodes"]!
            .AsArray()
            .Select(n => Entry(n, "song", "importedSong"))
            .Should()
            .Equal(rest["songs"]!.AsArray().Select(n => Entry(n, "song", "imported_song")));
        data["search"]!["venues"]!["nodes"]!
            .AsArray()
            .Select(n => Entry(n, "artistVenue", "importedVenue"))
            .Should()
            .Equal(
                rest["venues"]!.AsArray().Select(n => Entry(n, "artist_venue", "imported_venue"))
            );
    }

    [Test]
    public async Task GraphQL_expensive_queries_are_rejected_before_catalog_reads()
    {
        long commands = 0;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (i, l) =>
        {
            if (i.Name == "catalog.read.commands")
            {
                l.EnableMeasurementEvents(i);
            }
        };
        listener.SetMeasurementEventCallback<long>(
            (_, value, _, _) => Interlocked.Add(ref commands, value)
        );
        listener.Start();
        using var response = await http.PostAsJsonAsync(
            "/api/v4/graphql",
            new
            {
                query = "{ artists(first: 500) { nodes { shows(first: 500) { nodes { sources(first: 500) { nodes { id } } } } } } }",
            }
        );
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["errors"]!
            .AsArray()
            .Should()
            .Contain(e => e!["extensions"]!["code"]!.GetValue<string>() == "HC0047");
        commands.Should().Be(0);
    }

    [Test]
    public async Task GraphQL_rejects_cross_scope_cursors_and_keeps_independent_fields()
    {
        var page = await Graph(
            "query($id: ID!) { artist(id: $id) { shows(first: 1) { pageInfo { endCursor } } } }",
            new { id = CatalogId("artist", 301) }
        );
        using var response = await http.PostAsJsonAsync(
            "/api/v4/graphql",
            new
            {
                query = "query($id: ID!, $after: String) { artist(id: $id) { shows(first: 1,after: $after) { nodes { id } } } bad: sourceTrack(id: \"wrong\") { id } good: artist(id: $id) { id } }",
                variables = new
                {
                    id = CatalogId("artist", 14),
                    after = page["artist"]!["shows"]!["pageInfo"]!["endCursor"]!.GetValue<string>(),
                },
            }
        );
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["data"].Should().NotBeNull(json.ToJsonString());
        json["data"]!["good"]!["id"]!.GetValue<string>().Should().Be(CatalogId("artist", 14));
        json["errors"]!
            .AsArray()
            .Should()
            .Contain(e => e!["extensions"]!["code"]!.GetValue<string>() == "invalid_cursor");
        json["errors"]!
            .AsArray()
            .Should()
            .Contain(e => e!["extensions"]!["code"]!.GetValue<string>() == "invalid_argument");
    }

    [Test]
    public async Task GraphQL_venue_natural_lookups_preserve_the_artist_relationship()
    {
        var data = await Graph(
            """
            query($artist: ID!) {
              pair: artistVenueFor(artistId: $artist,venueId: "venue_00000001") { id artistShowCount shows(first:2) { nodes { id } } }
              slug: artistVenueBySlug(artistId: $artist,slug:"review-arena") { id artistShowCount }
            }
            """,
            new { artist = BrowseArtistId }
        );
        var rest = await GetJson(BrowseArtistPath + "/venues/venue_00000001");
        data["pair"]!["id"]!.GetValue<string>().Should().Be(rest["id"]!.GetValue<string>());
        data["slug"]!["id"]!.GetValue<string>().Should().Be(rest["id"]!.GetValue<string>());
        data["pair"]!["artistShowCount"]!
            .GetValue<int>()
            .Should()
            .Be(rest["artist_show_count"]!.GetValue<int>());
    }

    [TestCase("recentlyPerformedShows", "recently-performed")]
    [TestCase("recentlyUpdatedShows", "recently-updated")]
    [TestCase("recentlyAddedShows", "recently-added")]
    public async Task GraphQL_recent_default_windows_retain_rest_membership(
        string field,
        string route
    )
    {
        var data = await Graph(
            "query($artist:ID!) { "
                + field
                + "(first:25,filter:{artistId:$artist}) { nodes { id } } }",
            new { artist = BrowseArtistId }
        );
        var rest = await GetJson(BrowseArtistPath + "/shows/" + route);
        data[field]!["nodes"]!
            .AsArray()
            .Select(n => n!["id"]!.GetValue<string>())
            .Should()
            .Equal(rest.AsArray().Select(n => n!["id"]!.GetValue<string>()));
    }

    [Test]
    public async Task GraphQL_partial_filters_apply_and_unsupported_combinations_fail()
    {
        var data = await Graph(
            """
            query($artist:ID!) {
              soundboards: sources(first:50,filter:{artistId:$artist,soundboard:true}) { nodes { id isSoundboard } }
              sources(first: 2,filter: {from: "2050-01-01T00:00:00Z"}) { nodes { id } }
              sourceTracks(first: 2,filter: {through: "0001-01-01T00:00:00Z"}) { nodes { id } }
            }
            """,
            new { artist = BrowseArtistId }
        );
        var soundboard = data["soundboards"]!["nodes"]!.AsArray().Should().ContainSingle().Which!;
        soundboard["id"]!.GetValue<string>().Should().Be(CatalogId("source", 24));
        soundboard["isSoundboard"]!.GetValue<bool>().Should().BeTrue();
        data["sources"]!["nodes"]!.AsArray().Should().BeEmpty();
        data["sourceTracks"]!["nodes"]!.AsArray().Should().BeEmpty();
        using var response = await http.PostAsJsonAsync(
            "/api/v4/graphql",
            new
            {
                query = "{ artists(filter:{soundboard:true}) { nodes { id } } years(orderBy:UPDATED) { nodes { id } } }",
            }
        );
        var errors = JsonNode.Parse(await response.Content.ReadAsStringAsync())![
            "errors"
        ]!.AsArray();
        errors.Should().HaveCount(2);
        errors
            .Should()
            .OnlyContain(e => e!["extensions"]!["code"]!.GetValue<string>() == "invalid_argument");
    }

    [Test]
    public async Task GraphQL_song_filters_intersect_parent_membership_and_top_source_selection()
    {
        var data = await Graph(
            """
            { song(id:"song_00000101") {
                tracks(filter:{songId:"song_00000102"}) { nodes { id } }
                shows(filter:{songId:"song_00000102"}) { nodes { show { id } tracks(selection: TOP) { nodes { id } } } }
              }
            }
            """
        );
        data["song"]!["tracks"]!["nodes"]!
            .AsArray()
            .Select(n => n!["id"]!.GetValue<string>())
            .Should()
            .Equal(CatalogId("track", 341));
        var shows = data["song"]!["shows"]!["nodes"]!.AsArray();
        shows
            .Select(n => n!["show"]!["id"]!.GetValue<string>())
            .Should()
            .Equal(CatalogId("show", 311));
        shows[0]!["tracks"]!["nodes"]!
            .AsArray()
            .Select(n => n!["id"]!.GetValue<string>())
            .Should()
            .Equal(CatalogId("track", 341));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task GraphQL_artist_browse_keeps_rest_autocreated_policy(bool includeAuto)
    {
        var data = await Graph(
            "query($include: Boolean!) { artists(first:500,filter:{includeAutoCreated:$include}) { nodes { id } } }",
            new { include = includeAuto }
        );
        var rest = await GetJson(
            "/api/v4/artists?include_autocreated=" + includeAuto.ToString().ToLowerInvariant()
        );
        data["artists"]!["nodes"]!
            .AsArray()
            .Select(n => n!["id"]!.GetValue<string>())
            .Should()
            .Equal(rest.AsArray().Select(n => n!["id"]!.GetValue<string>()));
    }

    [TestCase("/api/v4/graphql")]
    [TestCase("/api/v4/graphql/")]
    [TestCase("/API/V4/GRAPHQL")]
    public async Task GraphQL_transport_limits_cover_both_url_forms(string path)
    {
        using var response = await http.PostAsJsonAsync(path, new { query = "{ __typename }" });
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        using var large = await http.PostAsync(
            path,
            new StringContent(
                System.Text.Json.JsonSerializer.Serialize(
                    new { query = "{ __typename }" + new string(' ', 270000) }
                ),
                System.Text.Encoding.UTF8,
                "application/json"
            )
        );
        large.StatusCode.Should().Be(System.Net.HttpStatusCode.RequestEntityTooLarge);
    }

    [Test]
    public async Task GraphQL_documented_operations_execute_against_the_catalog()
    {
        await Graph(
            """
            query($artist: ID!) {
              showsOnDate(month: 12,day: 31,first: 2) { nodes { id } }
              showsToday(first: 2) { nodes { id } }
              recentlyPerformedShows(first: 2) { nodes { id } }
              recentlyAddedShows(first: 2) { nodes { id } }
              recentlyUpdatedShows(first: 2) { nodes { id } }
              topShows(first: 2) { nodes { id } }
              popularArtists(limit: 2) { rank node { id } metrics { momentumScore } }
              popularShows(limit: 2) { rank node { id popularity { momentumScore } } metrics { momentumScore } }
              trendingYears(limit: 2) { rank node { id } }
              artistRankings(artistIds: [$artist],limit: 2) { artistId popularShows { rank node { id } } }
              momentumShows(artistIds: [$artist],limit: 2) { rank node { id } }
              liveHistory(limit: 2) { sequence track { id } }
              liveSummary(showsLimit: 2,artistsLimit: 2) { totalPlays }
            }
            """,
            new { artist = CatalogId("artist", 14) }
        );

        var variables = new
        {
            id = "song_00000101",
            scarlet = "song_00000101",
            drums = "song_00000102",
            recording = CatalogId("source", 321),
            artist = CatalogId("artist", 301),
            show = CatalogId("show", 311),
            venue = "venue_00000001",
            q = "Scarlet",
            after = (string?)null,
            ids = new[] { CatalogId("track", 341) },
        };
        foreach (
            var path in Directory.GetFiles(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "graphql-operations"),
                "*.graphql"
            )
        )
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v4/graphql")
            {
                Content = JsonContent.Create(
                    new { query = await File.ReadAllTextAsync(path), variables }
                ),
            };
            request.Headers.Add("GraphQL-Cost", "report");
            using var response = await http.SendAsync(request);
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            json["errors"].Should().BeNull(Path.GetFileName(path) + ": " + json.ToJsonString());
            json["extensions"]!["operationCost"].Should().NotBeNull();
        }
    }

    [Test]
    public async Task GraphQL_search_category_scope_is_preserved_and_conflicts_are_rejected()
    {
        var data = await Graph(
            "query($artist: ID!) { search(q: \"Scarlet\") { songs(filter:{artistId:$artist}) { nodes { song { artistId } importedSong { artistId } } } } }",
            new { artist = CatalogId("artist", 301) }
        );
        var entries = data["search"]!["songs"]!["nodes"]!.AsArray();
        entries.Should().NotBeEmpty();
        entries
            .Should()
            .OnlyContain(n =>
                (n!["song"] ?? n["importedSong"])!["artistId"]!.GetValue<string>()
                == CatalogId("artist", 301)
            );
        using var response = await http.PostAsJsonAsync(
            "/api/v4/graphql",
            new
            {
                query = "query($a:ID!,$b:ID!) { search(q: \"Scarlet\",artistId:$a) { songs(filter:{artistId:$b}) { nodes {kind} } } }",
                variables = new { a = CatalogId("artist", 301), b = BrowseArtistId },
            }
        );
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!["errors"]![0]!["extensions"]![
            "code"
        ]!
            .GetValue<string>()
            .Should()
            .Be("invalid_argument");
    }

    [TestCase("LEGACY_UUID", "invalid", false)]
    [TestCase("LEGACY_INTEGER", "not-a-number", true)]
    [TestCase("LEGACY_SLUG", "scarlet-begonias", false)]
    public async Task GraphQL_legacy_reference_syntax_and_owner_are_validated(
        string scheme,
        string id,
        bool owner
    )
    {
        using var response = await http.PostAsJsonAsync(
            "/api/v4/graphql",
            new
            {
                query = "query($id:String!,$artist:String){ resolve(references:[{kind:SHOW,scheme:"
                    + scheme
                    + ",id:$id,artistId:$artist}]) { availability } }",
                variables = new { id, artist = owner ? BrowseArtistId : null },
            }
        );
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!["errors"]![0]!["extensions"]![
            "code"
        ]!
            .GetValue<string>()
            .Should()
            .Be("invalid_argument");
    }

    [Test]
    public async Task GraphQL_grouped_rankings_budget_only_selected_series()
    {
        var options = app.Services.GetRequiredService<Relisten.GraphQL.CatalogGraphOptions>();
        var original = options.MaxMaterializedRows;
        var artists = new[] { BrowseArtistId, CatalogId("artist", 11) };
        string Query(string fields) =>
            "query($artists:[ID!]!) { artistRankings(artistIds:$artists,limit:1) { "
            + fields
            + " } }";
        options.MaxMaterializedRows = 4;
        try
        {
            var headers = await Graph(Query("artistId"), new { artists });
            headers["artistRankings"]!
                .AsArray()
                .Select(row => row!["artistId"]!.GetValue<string>())
                .Should()
                .BeEquivalentTo(artists);
            var popular = await Graph(
                Query("artistId selected:popularShows { node { id } }"),
                new { artists }
            );
            popular["artistRankings"]!.AsArray().Should().HaveCount(artists.Length);
            using var response = await http.PostAsJsonAsync(
                "/api/v4/graphql",
                new
                {
                    query = Query(
                        "artistId popularShows { node { id } } trendingShows { node { id } }"
                    ),
                    variables = new { artists },
                }
            );
            JsonNode.Parse(await response.Content.ReadAsStringAsync())!["errors"]!
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

    [TestCase(0)]
    [TestCase(501)]
    public async Task GraphQL_rejects_invalid_page_sizes(int first)
    {
        using var response = await http.PostAsJsonAsync(
            "/api/v4/graphql",
            new
            {
                query = "query($first:Int!) { artists(first:$first) { nodes { id } } }",
                variables = new { first },
            }
        );
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!["errors"]!
            .AsArray()
            .Should()
            .NotBeEmpty();
    }

    [Test]
    public async Task GraphQL_admin_explorer_uses_the_admin_login_and_embedded_assets()
    {
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = http.BaseAddress,
        };
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
        using var anonymous = await client.GetAsync("/relisten-admin/graphql/");
        anonymous.StatusCode.Should().Be(System.Net.HttpStatusCode.Redirect);
        anonymous.Headers.Location!.OriginalString.Should().Contain("/relisten-admin/login");
        using var login = await client.PostAsync("/test-admin-login", null);
        login.EnsureSuccessStatusCode();
        using var editor = await client.GetAsync("/relisten-admin/graphql/");
        editor.EnsureSuccessStatusCode();
        editor.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        var html = await editor.Content.ReadAsStringAsync();
        var script = System
            .Text.RegularExpressions.Regex.Match(html, "<script[^>]*src=\"([^\"]+)\"")
            .Groups[1]
            .Value;
        script.Should().StartWith("./assets/");
        using var asset = await client.GetAsync(
            new Uri(new Uri(client.BaseAddress!, "/relisten-admin/graphql/"), script)
        );
        asset.EnsureSuccessStatusCode();
        asset.Content.Headers.ContentType!.MediaType.Should().Contain("javascript");
        using var config = await client.GetAsync("/relisten-admin/graphql/nitro-config.json");
        config.EnsureSuccessStatusCode();
        var settings = JsonNode.Parse(await config.Content.ReadAsStringAsync())!;
        settings["endpoint"]!.GetValue<string>().Should().Be("/api/v4/graphql");
        settings["useGet"]!.GetValue<bool>().Should().BeFalse();
    }
}
