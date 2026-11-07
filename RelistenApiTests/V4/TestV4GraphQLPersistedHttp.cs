using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using HotChocolate.Execution;
using HotChocolate.Language;
using Microsoft.Extensions.DependencyInjection;
using Relisten.GraphQL;

namespace RelistenApiTests.V4;

public sealed partial class TestV4CatalogHttp
{
    [Test]
    public async Task GraphQL_persisted_get_preserves_post_results_variables_and_cursor_pages()
    {
        var file = PersistedFile("Catalog/artist-v1.graphql");
        var query = await File.ReadAllTextAsync(file);
        var variables = new { id = CatalogId("artist", 14), first = 1 };
        using var response = await PersistedGet("Catalog/artist-v1.graphql", variables);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertEdgeCache(response, 300);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        response.Headers.GetValues("Access-Control-Allow-Origin").Should().Contain("*");
        response
            .Headers.Vary.Should()
            .Contain(["Accept", "GraphQL-Cost", "Authorization", "Cookie"]);
        var data = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        data["errors"].Should().BeNull();
        using var post = await http.PostAsJsonAsync("/api/v4/graphql", new { query, variables });
        AssertEdgeCache(post, null);
        var postData = JsonNode.Parse(await post.Content.ReadAsStringAsync())!;
        postData["errors"].Should().BeNull();
        JsonNode.DeepEquals(data["data"], postData["data"]).Should().BeTrue();
        var page = data["data"]!["artist"]!["shows"]!;
        page["pageInfo"]!["hasNextPage"]!.GetValue<bool>().Should().BeTrue();
        var cursor = page["pageInfo"]!["endCursor"]!.GetValue<string>();
        using var next = await PersistedGet(
            "Catalog/artist-v1.graphql",
            new
            {
                variables.id,
                first = 1,
                after = cursor,
            }
        );
        var nextData = JsonNode.Parse(await next.Content.ReadAsStringAsync())!;
        nextData["errors"].Should().BeNull();
        nextData["data"]!["artist"]!["shows"]!["nodes"]![0]!["id"]!
            .GetValue<string>()
            .Should()
            .NotBe(page["nodes"]![0]!["id"]!.GetValue<string>());
        using var other = await PersistedGet(
            "Catalog/artist-v1.graphql",
            new { id = CatalogId("artist", 11) }
        );
        var otherData = JsonNode.Parse(await other.Content.ReadAsStringAsync())!;
        otherData["data"]!["artist"]!["id"]!
            .GetValue<string>()
            .Should()
            .Be(CatalogId("artist", 11));
    }

    [Test]
    public async Task GraphQL_persisted_get_cannot_execute_an_unregistered_post_warmed_document()
    {
        const string query = "query NotRegistered($id: ID!) { artist(id: $id) { id slug } }";
        var variables = new { id = CatalogId("artist", 14) };
        await Graph(query, variables);
        var executor = await app
            .Services.GetRequiredService<IRequestExecutorProvider>()
            .GetExecutorAsync();
        var hash = executor
            .Schema.Services.GetRequiredService<IDocumentHashProvider>()
            .ComputeHash(Encoding.UTF8.GetBytes(query))
            .Value;
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            CatalogGraphRegistration.PersistedPath
                + "/"
                + hash
                + "?variables="
                + Uri.EscapeDataString(JsonSerializer.Serialize(variables))
        );
        request.Headers.Add("Accept", "application/json");
        using var response = await http.SendAsync(request);
        AssertEdgeCache(response, null);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["errors"]
            .Should()
            .NotBeNull("POST document caching must not register a persisted operation");
        json["data"].Should().BeNull();
    }

    [TestCase("Rankings/artists-v1.graphql", 30)]
    [TestCase("Live/summary-v1.graphql", null)]
    public async Task GraphQL_persisted_cache_policy_follows_the_registered_operation(
        string file,
        int? cacheSeconds
    )
    {
        using var response = await PersistedGet(file);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["errors"].Should().BeNull();
        AssertEdgeCache(response, cacheSeconds);
    }

    [TestCase("GraphQL-Cost", "report")]
    [TestCase("GraphQL-Cost", "validate")]
    [TestCase("Authorization", "Bearer diagnostic")]
    [TestCase("Cookie", "diagnostic=1")]
    [TestCase("Accept", "application/graphql-response+json")]
    public async Task GraphQL_persisted_diagnostics_and_variant_requests_are_not_cached(
        string header,
        string value
    )
    {
        using var response = await PersistedGet(
            "Catalog/artist-v1.graphql",
            new { id = CatalogId("artist", 14) },
            header,
            value
        );
        AssertEdgeCache(response, null);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["errors"].Should().BeNull();
        if (header == "GraphQL-Cost")
        {
            json["extensions"]!["operationCost"].Should().NotBeNull();
            if (value == "validate")
            {
                json["data"].Should().BeNull();
            }
        }
    }

    [Test]
    public async Task GraphQL_persisted_unknown_invalid_and_partial_results_remain_uncached()
    {
        using var unknown = await http.GetAsync(
            CatalogGraphRegistration.PersistedPath + "/" + new string('0', 64)
        );
        AssertEdgeCache(unknown, null);
        JsonNode.Parse(await unknown.Content.ReadAsStringAsync())!["errors"]!
            .AsArray()
            .Should()
            .NotBeEmpty();
        using var invalid = await PersistedGet("Catalog/artist-v1.graphql", new { first = 1 });
        AssertEdgeCache(invalid, null);
        JsonNode.Parse(await invalid.Content.ReadAsStringAsync())!["errors"]!
            .AsArray()
            .Should()
            .NotBeEmpty();
        var limits = app.Services.GetRequiredService<CatalogGraphOptions>();
        var original = limits.MaxMaterializedRows;
        try
        {
            limits.MaxMaterializedRows = 1;
            using var partial = await PersistedGet(
                "Catalog/artist-v1.graphql",
                new { id = CatalogId("artist", 14) }
            );
            AssertEdgeCache(partial, null);
            var json = JsonNode.Parse(await partial.Content.ReadAsStringAsync())!;
            json["errors"]![0]!["extensions"]!["code"]!
                .GetValue<string>()
                .Should()
                .Be("catalog_row_limit");
            json["data"]!["artist"]!["id"]!.GetValue<string>().Should().Be(CatalogId("artist", 14));
        }
        finally
        {
            limits.MaxMaterializedRows = original;
        }
        using var recovered = await PersistedGet(
            "Catalog/artist-v1.graphql",
            new { id = CatalogId("artist", 14) }
        );
        AssertEdgeCache(recovered, 300);
    }

    private static void AssertEdgeCache(HttpResponseMessage response, int? seconds)
    {
        var policy = response.Headers.CacheControl;
        policy.Should().NotBeNull();
        if (seconds is null)
        {
            policy!.NoStore.Should().BeTrue();
            return;
        }
        policy!.Public.Should().BeTrue();
        policy.NoStore.Should().BeFalse();
        policy.MaxAge.Should().Be(TimeSpan.Zero);
        policy.SharedMaxAge.Should().Be(TimeSpan.FromSeconds(seconds.Value));
    }

    private static string PersistedFile(string file) =>
        System.IO.Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "v4",
            "graphql",
            "persisted",
            file
        );

    private async Task<HttpResponseMessage> PersistedGet(
        string file,
        object? variables = null,
        string? header = null,
        string? value = null
    )
    {
        var id = System.IO.Path.GetFileNameWithoutExtension(file);
        var url = CatalogGraphRegistration.PersistedPath + "/" + id;
        if (variables is not null)
        {
            url += "?variables=" + Uri.EscapeDataString(JsonSerializer.Serialize(variables));
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Accept", "application/json");
        request.Headers.Add("Origin", "https://catalog.example.invalid");
        if (header is not null)
        {
            request.Headers.Remove(header);
            request.Headers.Add(header, value);
        }
        return await http.SendAsync(request);
    }
}
