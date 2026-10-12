using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Relisten;
using Relisten.Api.Models;
using Relisten.Controllers;
using Relisten.Controllers.Admin;
using Relisten.Data;
using Relisten.Ingestion;
using Temporalio.Testing;

namespace RelistenApiTests.Ingestion;

/// <summary>
/// Exercises routing, authorization, MVC serialization, and admission filters over HTTP.
/// Artist lookup uses an isolated PostgreSQL schema; admission uses real Temporal starts.
/// </summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public class TestImportHttpAdapters
{
    private readonly string schema = "import_http_" + Guid.NewGuid().ToString("N");
    private readonly Guid artistUuid = Guid.NewGuid();
    private readonly Guid unsupportedUuid = Guid.NewGuid();
    private readonly HashSet<string> workflows = [];
    private WorkflowEnvironment? environment;
    private WebApplication? app;
    private HttpClient http = null!;
    private NpgsqlConnection? connection;
    private string previousWrite = null!;
    private string previousRead = null!;
    private string ArtistWorkflowId => "ingest/artist/" + artistUuid;

    [OneTimeSetUp]
    public async Task Start()
    {
        if (Environment.GetEnvironmentVariable("RELISTEN_LOCAL_INGESTION_TESTS") != "1")
            Assert.Ignore("Start local-dev/temporal and set RELISTEN_LOCAL_INGESTION_TESTS=1.");
        previousWrite = DbService.ConnStr;
        previousRead = DbService.ReadOnlyConnStr;
        var db = new DbService(
            "postgres://relisten:local_ingestion@127.0.0.1:15433/relisten_ingestion",
            null,
            null,
            new HostingEnvironment { EnvironmentName = Environments.Production },
            NullLogger<DbService>.Instance
        );
        connection = new NpgsqlConnection(DbService.ConnStr);
        await connection.OpenAsync();
        await connection.ExecuteAsync($"CREATE SCHEMA {schema}; SET search_path TO {schema};");
        DbService.ConnStr += $";Search Path={schema};Pooling=false";
        DbService.ReadOnlyConnStr = DbService.ConnStr;
        await connection.ExecuteAsync(
            """
            CREATE TABLE artists (id int PRIMARY KEY, uuid uuid, slug text);
            CREATE TABLE features (id int, artist_id int);
            CREATE TABLE artists_upstream_sources (id int, artist_id int, upstream_source_id int);
            CREATE TABLE upstream_sources (id int);
            INSERT INTO artists VALUES (1, @artistUuid, 'phish'), (2, @unsupportedUuid, 'unsupported');
            INSERT INTO features VALUES (1, 1), (2, 2);
            """,
            new { artistUuid, unsupportedUuid }
        );
        environment = await WorkflowEnvironment.StartLocalAsync();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Ingestion:Engine"] = "Temporal",
                ["PANIC_KEY"] = "test-hook-key",
            }
        );
        var admission = new TemporalIngestionClient(
            environment.Client,
            builder.Configuration,
            new Catalog(artistUuid)
        );
        var artists = new ArtistService(db, null!);
        builder
            .Services.AddControllers()
            .AddApplicationPart(typeof(ImportController).Assembly)
            .AddNewtonsoftJson()
            .AddControllersAsServices();
        builder.Services.AddTransient(_ => new ImportController(
            null!,
            db,
            artists,
            null!,
            null!,
            builder.Configuration,
            admission
        ));
        builder.Services.AddTransient(service => new AdminController(
            null!,
            null!,
            service.GetRequiredService<ILoggerFactory>(),
            artists,
            null!,
            null!,
            null!,
            null!,
            admission
        ));
        builder
            .Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, HeaderAuthentication>("test", _ => { });
        builder.Services.AddAuthorization();
        app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        var address = app
            .Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.Single();
        http = new HttpClient { BaseAddress = new Uri(address) };
    }

    [TearDown]
    public async Task StopImports()
    {
        foreach (var id in workflows)
            await environment!.Client.GetWorkflowHandle(id).TerminateAsync("HTTP test cleanup");
        workflows.Clear();
    }

    [OneTimeTearDown]
    public async Task Stop()
    {
        http?.Dispose();
        if (app != null)
            await app.DisposeAsync();
        if (environment != null)
            await environment.DisposeAsync();
        if (connection != null)
        {
            try
            {
                await connection.ExecuteAsync($"DROP SCHEMA IF EXISTS {schema} CASCADE;");
            }
            finally
            {
                await connection.DisposeAsync();
                DbService.ConnStr = previousWrite;
                DbService.ReadOnlyConnStr = previousRead;
            }
        }
    }

    [TestCase("/api/v2/import/phish")]
    [TestCase("/api/v2/import/phish/show-one")]
    [TestCase("/api/v2/import/all-years-and-shows")]
    [TestCase("/relisten-admin/refresh-all-artists")]
    [TestCase("/relisten-admin/backfill-jerrygarcia-venues")]
    public async Task ProtectedRoutesRejectUnauthenticatedRequests(string route)
    {
        using var response = await Post(route, authenticated: false);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [TestCase("/api/v2/import/phish")]
    [TestCase("/api/v2/import/phish/show-one")]
    public async Task SuccessfulArtistRequestsKeepTheCreatedTextResponse(string route)
    {
        using var response = await Post(route);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        workflows.Add(ArtistWorkflowId);
        (await response.Content.ReadAsStringAsync())
            .Should()
            .Be($"Queued as job {ArtistWorkflowId}!");
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/plain");
    }

    [TestCase("/api/v2/import/phish/show-one")]
    [TestCase("/relisten-admin/backfill-jerrygarcia-venues?artistSlug=phish")]
    public async Task ActiveArtistOperationReturnsConflictWithItsWorkflowId(string route)
    {
        using var first = await Post("/api/v2/import/phish");
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        workflows.Add(ArtistWorkflowId);
        using var conflict = await Post(route);
        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var body = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("code").GetString().Should().Be("import_already_running");
        body.RootElement.GetProperty("workflowId").GetString().Should().Be(ArtistWorkflowId);
    }

    [Test]
    public async Task TargetedImportForAnUnsupportedArtistReturnsBadRequest()
    {
        using var response = await Post("/api/v2/import/unsupported/show-one");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("code")
            .GetString()
            .Should()
            .Be("unsupported_import_operation");
    }

    [TestCase("/api/v2/import/phish?deleteOldContent=true")]
    [TestCase("/api/v2/import/phish/show-one?deleteOldContent=true")]
    [TestCase("/api/v2/import/phish/debug")]
    [TestCase("/api/v2/import/phish/show-one/debug")]
    public async Task TemporalModeRejectsDestructiveAndSynchronousImports(string route)
    {
        using var response = await Post(route);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        // Rejection must not reserve the artist or silently enqueue another kind of operation.
        using var accepted = await Post("/api/v2/import/phish");
        accepted.StatusCode.Should().Be(HttpStatusCode.Created);
        workflows.Add(ArtistWorkflowId);
    }

    [Test]
    public async Task PhishHookRequiresItsApiKeyAndKeepsTheBareWorkflowIdResponse()
    {
        using var rejected = await Post("/api/v2/import/phishin", authenticated: false);
        rejected.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v2/import/phishin");
        request.Headers.Add("X-Relisten-Api-Key", "test-hook-key");
        using var response = await http.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        workflows.Add(ArtistWorkflowId);
        (await response.Content.ReadAsStringAsync()).Should().Be(ArtistWorkflowId);
    }

    [TestCase("/relisten-admin/refresh-all-artists", "ingest/full/")]
    [TestCase("/relisten-admin/index-archiveorg", "ingest/discover/")]
    public async Task AdminSuccessKeepsItsJsonMessage(string route, string prefix)
    {
        using var response = await Post(route);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var message = body.RootElement.GetProperty("message").GetString()!;
        message.Should().StartWith("Queued as job " + prefix).And.EndWith("!");
        workflows.Add(message["Queued as job ".Length..^1]);
    }

    private async Task<HttpResponseMessage> Post(string route, bool authenticated = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route);
        if (authenticated)
            request.Headers.Add("X-Test-User", "operator");
        return await http.SendAsync(request);
    }

    private sealed class Catalog(Guid supportedArtist) : IImportAdmissionCatalog
    {
        public Task<UpstreamProvider[]?> ProvidersForArtistAsync(ArtistId artist) =>
            Task.FromResult<UpstreamProvider[]?>(
                artist.Value == supportedArtist
                    ?
                    [
                        UpstreamProvider.ArchiveOrg,
                        UpstreamProvider.Phishin,
                        UpstreamProvider.JerryGarcia,
                    ]
                    : [UpstreamProvider.Local]
            );
    }

    public sealed class HeaderAuthentication(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder
    ) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(
                Request.Headers.ContainsKey("X-Test-User")
                    ? AuthenticateResult.Success(
                        new AuthenticationTicket(
                            new ClaimsPrincipal(
                                new ClaimsIdentity(
                                    [new Claim(ClaimTypes.Name, "operator")],
                                    Scheme.Name
                                )
                            ),
                            Scheme.Name
                        )
                    )
                    : AuthenticateResult.NoResult()
            );
    }
}
