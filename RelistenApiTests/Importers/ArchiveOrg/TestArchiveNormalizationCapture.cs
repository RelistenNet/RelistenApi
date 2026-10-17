using System.Net;
using System.Text;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Relisten;
using Relisten.Api.Models;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Import;
using Relisten.Ingestion;
using Relisten.Ingestion.Providers.Archive;
using Relisten.Ingestion.Providers.ArchiveCollections;
using Relisten.Ingestion.Providers.TapersSection;
using RelistenApiTests.Ingestion;
using Temporalio.Testing;

namespace RelistenApiTests.Importers.ArchiveOrg;

/// <summary>Verifies shared raw Archive retention through native imports, intake and collection activities.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestArchiveNormalizationCapture
{
    private IHost host = null!;
    private DbService db = null!;
    private NormalizationEvidenceService evidence = null!;
    private FixtureHttpServer upstream = null!;
    private string? previousWrite;
    private string? previousRead;

    [OneTimeSetUp]
    public void Start()
    {
        if (Environment.GetEnvironmentVariable("RELISTEN_LOCAL_INGESTION_TESTS") != "1")
        {
            Assert.Ignore("Run ./test-local.sh to include local integration tests.");
        }

        previousWrite = DbService.ConnStr;
        previousRead = DbService.ReadOnlyConnStr;
        upstream = new FixtureHttpServer();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(sp => new DbService(
            LocalTestDatabase.DatabaseUrl,
            null,
            null,
            builder.Environment,
            sp.GetRequiredService<ILogger<DbService>>()
        ));
        builder.Services.AddSingleton(_ => new RedisService(
            RedisService.BuildConfiguration("redis://127.0.0.1:16379")
        ));
        builder.Services.AddRelistenCatalogServices();
        builder.Services.Configure<NormalizationProviderCaptureOptions>(options =>
            options.Enabled = true
        );
        builder.Services.AddSingleton<IngestionHttpClientFactory>();
        builder.Services.AddScoped<TapersSectionActivities>();
        builder.Services.AddScoped<ArchiveCollectionDiscoveryActivities>();
        builder
            .Services.AddHttpClient(nameof(IngestionHttpClientFactory))
            .ConfigurePrimaryHttpMessageHandler(() => new FixtureRedirectHandler(upstream.Address));
        host = builder.Build();
        db = host.Services.GetRequiredService<DbService>();
        evidence = host.Services.GetRequiredService<NormalizationEvidenceService>();
    }

    [OneTimeTearDown]
    public void Stop()
    {
        if (host != null)
        {
            host.Services.GetRequiredService<RedisService>().connection.Dispose();
            host.Dispose();
        }
        upstream?.Dispose();
        if (previousWrite != null)
        {
            DbService.ConnStr = previousWrite;
            DbService.ReadOnlyConnStr = previousRead!;
        }
    }

    [Test]
    public async Task NativeIntakeAndCollectionFetchesRetainTheSameCompleteProviderResource()
    {
        var item = Item();
        var raw = Payload(item);
        upstream.Respond = _ => raw;
        using var scope = host.Services.CreateScope();
        var importer = scope.ServiceProvider.GetRequiredService<ArchiveOrgImporter>();
        importer.UseIngestionHttpClient(
            new HttpClient(new FixtureRedirectHandler(upstream.Address))
        );
        var context = Context();
        var result = await importer.ImportArchiveItemAsync(context, Selected(item));
        result.SkipReason.Should().Be(ArchiveSkipReason.NoVbrMp3);
        var native = await Current(item);
        native.Revision.Payload.Should().Equal(Encoding.UTF8.GetBytes(raw));
        native.Revision.CaptureVersion.Should().Be(ArchiveNormalizationInput.CaptureVersion);

        var environment = new ActivityEnvironment();
        await environment.RunAsync(() =>
            scope
                .ServiceProvider.GetRequiredService<TapersSectionActivities>()
                .ObserveItem(context.Run, item)
        );
        await environment.RunAsync(() =>
            scope
                .ServiceProvider.GetRequiredService<ArchiveCollectionDiscoveryActivities>()
                .ObserveItem(context.Run, item)
        );
        var history = await evidence.GetFetchHistoryAsync(ArchiveNormalizationInput.Resource(item));
        history.Should().HaveCount(3);
        history
            .Select(fetch => fetch.InputRevisionUuid)
            .Should()
            .OnlyContain(uuid => uuid == native.Revision.Uuid);
        (await Current(item)).Generation.Should().Be(3);
        // Both selected intake projections omit custom fields. Complete raw retention must not.
        var retained = Encoding.UTF8.GetString((await Current(item)).Revision.Payload);
        retained.Should().Be(raw);
    }

    [TestCase("{")]
    [TestCase("{\"metadata\":{\"identifier\":\"someone-else\"},\"files\":[]}")]
    [TestCase("{\"errcode\":104,\"error\":\"temporarily missing\"}")]
    [TestCase("{\"metadata\":{\"identifier\":\"ITEM\"},\"files\":[false]}")]
    [TestCase("{\"is_dark\":true,\"metadata\":{\"identifier\":\"someone-else\"}}")]
    public async Task InvalidResponsesDoNotReplaceCurrentEvidence(string invalid)
    {
        var item = Item();
        var raw = Payload(item);
        using var http = new HttpClient(new ResponseHandler(raw));
        var inputs = host.Services.GetRequiredService<NormalizationProviderInputService>();
        await ArchiveNormalizationInput.FetchAsync(http, inputs, item);
        var before = await Current(item);
        using var badHttp = new HttpClient(
            new ResponseHandler(invalid.Replace("ITEM", item.Value))
        );
        Func<Task> fetch = () => ArchiveNormalizationInput.FetchAsync(badHttp, inputs, item);
        await fetch.Should().ThrowAsync<Exception>();
        (await Current(item)).Should().BeEquivalentTo(before);
        (await evidence.GetFetchHistoryAsync(ArchiveNormalizationInput.Resource(item)))
            .Should()
            .ContainSingle();
    }

    [Test]
    public async Task IdentitylessDarkResponseIsRetainedAndNativeImportStillSkipsIt()
    {
        var item = Item();
        upstream.Respond = _ => "{\"is_dark\":true}";
        using var scope = host.Services.CreateScope();
        var importer = scope.ServiceProvider.GetRequiredService<ArchiveOrgImporter>();
        importer.UseIngestionHttpClient(
            new HttpClient(new FixtureRedirectHandler(upstream.Address))
        );
        var result = await importer.ImportArchiveItemAsync(Context(), Selected(item));
        result.SkipReason.Should().Be(ArchiveSkipReason.DarkItem);
        (await Current(item))
            .Revision.Payload.Should()
            .Equal(Encoding.UTF8.GetBytes("{\"is_dark\":true}"));
    }

    [Test]
    public async Task RetainedResponseSurvivesFailureOfTheLaterObservationTransaction()
    {
        var item = Item();
        var raw = Payload(item);
        upstream.Respond = _ => raw;
        await db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                """
                CREATE FUNCTION normalization.fail_capture_fixture() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'Observation fixture failure'; END $$;
                CREATE TRIGGER fail_capture_fixture BEFORE INSERT ON normalization.archive_item_observations
                FOR EACH ROW EXECUTE FUNCTION normalization.fail_capture_fixture();
                """
            )
        );
        try
        {
            using var scope = host.Services.CreateScope();
            var activities = scope.ServiceProvider.GetRequiredService<TapersSectionActivities>();
            Func<Task> observe = () =>
                new ActivityEnvironment().RunAsync(() =>
                    activities.ObserveItem(Context().Run, item)
                );
            var failure = await observe.Should().ThrowAsync<Exception>();
            var postgres = failure.WithInnerException<PostgresException>().Which;
            postgres.SqlState.Should().Be(PostgresErrorCodes.RaiseException);
            postgres.MessageText.Should().Be("Observation fixture failure");
            (await Current(item)).Revision.Payload.Should().Equal(Encoding.UTF8.GetBytes(raw));
            var observations = await db.WithWriteConnection(connection =>
                connection.QuerySingleAsync<int>(
                    "SELECT count(*) FROM normalization.archive_item_observations WHERE item_identifier=@item",
                    new { item = item.Value }
                )
            );
            observations.Should().Be(0);
        }
        finally
        {
            await db.WithWriteConnection(connection =>
                connection.ExecuteAsync(
                    "DROP TRIGGER fail_capture_fixture ON normalization.archive_item_observations; DROP FUNCTION normalization.fail_capture_fixture();"
                )
            );
        }

        var retained = await Current(item);
        using var retryScope = host.Services.CreateScope();
        await new ActivityEnvironment().RunAsync(() =>
            retryScope
                .ServiceProvider.GetRequiredService<TapersSectionActivities>()
                .ObserveItem(Context().Run, item)
        );
        var retried = await Current(item);
        retried.Revision.Uuid.Should().Be(retained.Revision.Uuid);
        retried.Generation.Should().Be(retained.Generation + 1);
        (
            await db.WithWriteConnection(connection =>
                connection.QuerySingleAsync<int>(
                    "SELECT count(*) FROM normalization.archive_item_observations WHERE item_identifier=@item",
                    new { item = item.Value }
                )
            )
        )
            .Should()
            .Be(1);
    }

    [Test]
    public async Task DisabledCapturePreservesLegacyAvailabilityProjectionWithoutEvidenceWrites()
    {
        var item = Item();
        var raw = "{\"errcode\":104,\"error\":\"not found\"}";
        var inputs = new NormalizationProviderInputService(
            evidence,
            Options.Create(new NormalizationProviderCaptureOptions())
        );
        using var http = new HttpClient(new ResponseHandler(raw));
        var fetched = await ArchiveNormalizationInput.FetchAsync(http, inputs, item);
        ArchiveObservationProjector.Project(item, fetched).IsAvailabilityOnly.Should().BeTrue();
        (await evidence.GetCurrentInputAsync(ArchiveNormalizationInput.Resource(item)))
            .Should()
            .BeNull();
    }

    private async Task<NormalizationCurrentInput> Current(ArchiveItemId item) =>
        (await evidence.GetCurrentInputAsync(ArchiveNormalizationInput.Resource(item)))!;

    private ProviderContext Context() =>
        new(
            new Artist(),
            new ArtistUpstreamSource(),
            new IngestionExecution(Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.AddHours(1)),
            host.Services.GetRequiredService<CatalogImportTransaction>(),
            CancellationToken.None
        );

    private static ArchiveItemId Item() => new("capture-" + Guid.NewGuid().ToString("N"));

    private static DiscoveredArchiveItem Selected(ArchiveItemId item) =>
        new(new(item, new(new() { identifier = item.Value }, "new_source")));

    private static string Payload(ArchiveItemId item) =>
        $$$"""
            {
              "metadata":{"identifier":"{{{item.Value}}}","date":"2000-01-01","creator":["Fixture Artist"],
                "taper":["Zoë Recorder","Another Taper"],"transferer":"Fixture Transfer",
                "custom_credit":{"roles":["recorded","transferred"],"person":"Fixture"}},
              "files":[{"name":"master.flac","format":"Flac","title":"Song",
                "custom_transfer":{"equipment":["Deck","Cable"],"generation":0}}],
              "custom_root":{"retained":true}
            }
            """;

    private sealed class ResponseHandler(string raw) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        ) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(raw, Encoding.UTF8, "application/json"),
                }
            );
    }
}
