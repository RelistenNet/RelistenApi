using System.Collections.Frozen;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
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
        var coordinator = new CatalogSearchCoordinator(
            new TypesenseClient(typesenseHttp),
            evidence,
            NullLogger<CatalogSearchCoordinator>.Instance,
            "test"
        );

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton(coordinator);
        builder.Services.AddScoped(_ => new PopularityRefreshRequest(null!, null!));
        builder
            .Services.AddControllers()
            .AddApplicationPart(typeof(CatalogSearchController).Assembly);
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

        using var response = await client.GetAsync("/api/v4/search?q=twist&scope=shows");
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body["availability"]!.GetValue<string>().Should().Be("unavailable");
        body["results"]!.AsArray().Should().BeEmpty();
    }
}
