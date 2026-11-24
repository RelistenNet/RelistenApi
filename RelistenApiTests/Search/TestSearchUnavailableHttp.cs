using System.Collections.Frozen;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Relisten.Controllers.V4;
using Relisten.Search;
using Relisten.Services.Popularity;

namespace RelistenApiTests.Search;

[TestFixture]
public sealed class TestSearchUnavailableHttp
{
    [Test]
    public async Task UnreachableTypesenseReturnsUnavailableWithEmptyResults()
    {
        // Reserve a loopback port without listening so no Typesense instance is needed.
        using var socket = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Stream,
            ProtocolType.Tcp
        );
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        using var typesenseHttp = new HttpClient();
        TypesenseClient.Configure(typesenseHttp, $"http://{socket.LocalEndPoint}", "unused");

        var (status, body, unauthorizedExchange) = await Search(typesenseHttp, "twist&scope=shows");

        status.Should().Be(HttpStatusCode.OK);
        body["availability"]!["catalog"]!.GetValue<string>().Should().Be("unavailable");
        body["results"]!.AsArray().Should().BeEmpty();
        unauthorizedExchange.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task ADeletedDisplayCollectionMakesSearchUnavailableAndReloadsEvidence()
    {
        var failure = new DisplayFailure(404, "Collection not found");
        using var client = new HttpClient(failure);
        TypesenseClient.Configure(client, "http://127.0.0.1:18108", "unused");

        var (status, body, _) = await Search(client, "artist&scope=artists", failure);

        status.Should().Be(HttpStatusCode.OK);
        ((string?)body["availability"]!["catalog"]).Should().Be("unavailable");
        body["results"]!.AsArray().Should().BeEmpty();
    }

    [Test]
    public async Task ADisplayServerErrorReportsPartialAvailability()
    {
        using var client = new HttpClient(new DisplayFailure(500, "simulated display failure"));
        TypesenseClient.Configure(client, "http://127.0.0.1:18108", "unused");
        var (status, body, _) = await Search(client, "artist&scope=artists");
        status.Should().Be(HttpStatusCode.OK);
        ((string?)body["availability"]!["catalog"]).Should().Be("partial");
        body["results"]!.AsArray().Should().BeEmpty();
    }

    [Test]
    public async Task AnUnknownRankingFieldReportsPartialAvailability()
    {
        using var client = new HttpClient(
            new DisplayFailure(
                404,
                "Could not find a field named `preview_track_id` in the schema.",
                whenDrawing: false
            )
        );
        TypesenseClient.Configure(client, "http://127.0.0.1:18108", "unused");
        var (status, body, _) = await Search(client, "artist&scope=shows");
        status.Should().Be(HttpStatusCode.OK);
        ((string?)body["availability"]!["catalog"]).Should().Be("partial");
    }

    private sealed class DisplayFailure(int code, string message, bool whenDrawing = true)
        : HttpMessageHandler
    {
        private int aliasReads;
        public TaskCompletionSource InitialAlias { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RecheckedAlias { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken token
        )
        {
            if (request.Method == HttpMethod.Get)
            {
                if (Interlocked.Increment(ref aliasReads) == 1)
                {
                    InitialAlias.TrySetResult();
                }
                else
                {
                    RecheckedAlias.TrySetResult();
                }
                return new(HttpStatusCode.NotFound);
            }

            var sent = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            var replies = sent["searches"]!
                .AsArray()
                .Select(search =>
                    ((string?)search!["include_fields"])!.Split(',').Contains("display")
                    == whenDrawing
                        ? new JsonObject { ["code"] = code, ["error"] = message }
                        : JsonNode.Parse(
                            """
                            {"found":1,"hits":[{"document":{"id":"artist_fixture","kind":"artist","artist_slug":"fixture","name":"artist"}}]}
                            """
                        )!
                )
                .ToArray();
            var body = new JsonObject { ["results"] = new JsonArray(replies) };
            return new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString()) };
        }
    }

    private static async Task<(
        HttpStatusCode Status,
        JsonNode Body,
        HttpStatusCode UnauthorizedExchange
    )> Search(HttpClient typesenseHttp, string query, DisplayFailure? aliasProbe = null)
    {
        var evidence = new CatalogQueryEvidenceStore();
        evidence.Publish(
            new CatalogQueryEvidence(
                "entities_fixture",
                SearchVocabulary.Empty,
                SearchPassages.Empty,
                FrozenDictionary<string, long>.Empty,
                FrozenSet<string>.Empty
            )
        );
        var builder = WebApplication.CreateSlimBuilder();
        var typesense = new TypesenseClient(typesenseHttp);
        if (aliasProbe != null)
        {
            var refresh = new CatalogQueryEvidenceRefreshService(
                null!,
                typesense,
                evidence,
                builder.Configuration,
                NullLogger<CatalogQueryEvidenceRefreshService>.Instance
            );
            builder.Services.AddSingleton<IHostedService>(refresh);
            builder.Services.AddSingleton(
                new CatalogSearchCoordinator(
                    typesense,
                    evidence,
                    refresh,
                    NullLogger<CatalogSearchCoordinator>.Instance,
                    "test"
                )
            );
        }
        else
        {
            builder.Services.AddSingleton(
                new CatalogSearchCoordinator(
                    typesense,
                    evidence,
                    null,
                    NullLogger<CatalogSearchCoordinator>.Instance,
                    "test"
                )
            );
        }
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        builder.Services.AddScoped(_ => new PopularityRefreshRequest(null!, null!));
        builder
            .Services.AddControllers()
            .AddApplicationPart(typeof(CatalogSearchController).Assembly);
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        if (aliasProbe != null)
        {
            await aliasProbe.InitialAlias.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

        using var response = await client.GetAsync("/api/v4/search?q=" + query);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        using var exchange = await client.GetAsync(CatalogSearchExchange.Path + "?q=twist");

        if (aliasProbe != null)
        {
            await aliasProbe.RecheckedAlias.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        return (response.StatusCode, body, exchange.StatusCode);
    }
}
