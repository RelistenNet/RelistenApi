using System.Net;
using System.Text;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Npgsql;
using Relisten;
using Relisten.Api.Models;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Import;
using Relisten.Ingestion;
using Relisten.Ingestion.Providers.Local;
using RelistenApiTests.Ingestion;

namespace RelistenApiTests.Importers.Local;

/// <summary>Local directory inputs remain separate evidence when their notes become one catalog source.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestLocalNormalizationCapture
{
    private IHost host = null!;
    private DbService db = null!;
    private NormalizationEvidenceService evidence = null!;
    private string? previousWrite;
    private string? previousRead;

    [OneTimeSetUp]
    public async Task Start()
    {
        if (Environment.GetEnvironmentVariable("RELISTEN_LOCAL_INGESTION_TESTS") != "1")
        {
            Assert.Ignore("Run ./test-local.sh to include local integration tests.");
        }

        previousWrite = DbService.ConnStr;
        previousRead = DbService.ReadOnlyConnStr;
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
        SqlMapper.AddTypeHandler(new PersistentIdentifierHandler());
        SqlMapper.AddTypeHandler(new DateTimeHandler());
        host = builder.Build();
        db = host.Services.GetRequiredService<DbService>();
        evidence = host.Services.GetRequiredService<NormalizationEvidenceService>();

        // Local source links use the provider's catalog ID. Share the canonical row with other fixtures.
        var providerName = await db.WithWriteConnection(connection =>
            connection.QuerySingleAsync<string>(
                """
                INSERT INTO upstream_sources(id,name,url) VALUES(9,'local','https://audio.relisten.net')
                ON CONFLICT (id) DO NOTHING;
                SELECT name FROM upstream_sources WHERE id=9;
                """
            )
        );
        providerName.ToLowerInvariant().Should().Be("local");
    }

    [OneTimeTearDown]
    public void Stop()
    {
        if (host != null)
        {
            host.Services.GetRequiredService<RedisService>().connection.Dispose();
            host.Dispose();
        }
        if (previousWrite != null)
        {
            DbService.ConnStr = previousWrite;
            DbService.ReadOnlyConnStr = previousRead!;
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task NativeAndTemporalImportsRetainRootAndSeparateDirectoryBytes(bool temporal)
    {
        var context = await Context();
        var requests = new List<string>();
        using var scope = host.Services.CreateScope();
        var importer = Importer(
            scope,
            uri =>
            {
                requests.Add(uri.AbsolutePath);
                return Payload(context, uri);
            }
        );
        if (temporal)
        {
            var root = await importer.FetchRootAndPrepareVenuesAsync(context);
            var selected = await importer.SelectChangedDatesAsync(context, root);
            await importer.ImportDateAsync(context, selected.Items.Single());
        }
        else
        {
            await importer.ProcessVenues(context.Artist, context.Source, null);
            await importer.ProcessShows(context.Artist, context.Source, null);
        }

        var rootInput = await evidence.GetCurrentInputAsync(RootKey(context));
        rootInput!
            .Revision.Payload.Should()
            .Equal(
                Encoding.UTF8.GetBytes(
                    Payload(context, new Uri("https://fixture/Fixture/metadata.json"))
                )
            );
        rootInput.Revision.CaptureVersion.Should().Be("local-http-json-v1");
        foreach (var directory in new[] { "part-one", "part-two" })
        {
            var input = await evidence.GetCurrentInputAsync(DirectoryKey(context, directory));
            input!
                .Revision.Payload.Should()
                .Equal(
                    Encoding.UTF8.GetBytes(
                        Payload(
                            context,
                            new Uri($"https://fixture/Fixture/{directory}/metadata.json")
                        )
                    )
                );
        }
        var source = await scope
            .ServiceProvider.GetRequiredService<SourceService>()
            .ForUpstreamIdentifierFromPrimary(context.Artist, "2000-01-01");
        source!.taper_notes.Should().Be("First notes\nSecond notes");
        await importer.ProcessShows(context.Artist, context.Source, null);
        requests.Count(path => path.EndsWith("/part-one/metadata.json")).Should().Be(1);
        requests.Count(path => path.EndsWith("/part-two/metadata.json")).Should().Be(1);
    }

    [TestCase("{")]
    [TestCase("{\"tracks\":[],\"txts\":null}")]
    [TestCase("{\"tracks\":[{\"mp3\":\"track.mp3\"}],\"txts\":[]}")]
    public async Task InvalidDirectoryCannotReplaceGoodEvidenceOrCreatePartialSource(
        string malformed
    )
    {
        var context = await Context();
        using var scope = host.Services.CreateScope();
        var invalid = false;
        var importer = Importer(
            scope,
            uri =>
                invalid && uri.AbsolutePath.Contains("part-two") ? malformed : Payload(context, uri)
        );
        var root = await importer.FetchRootAndPrepareVenuesAsync(context);
        var item = (await importer.SelectChangedDatesAsync(context, root)).Items.Single();
        // Populate valid evidence without catalog writes, then make that directory's next fetch fail.
        var provider =
            scope.ServiceProvider.GetRequiredService<NormalizationProviderInputService>();
        var raw = Payload(context, new Uri("https://fixture/Fixture/part-two/metadata.json"));
        await provider.FetchAsync(
            DirectoryKey(context, "part-two"),
            "local-http-json-v1",
            _ =>
                Task.FromResult(
                    new NormalizationProviderResponse<string>(
                        raw,
                        Encoding.UTF8.GetBytes(raw),
                        "application/json"
                    )
                )
        );
        var before = await evidence.GetCurrentInputAsync(DirectoryKey(context, "part-two"));
        invalid = true;
        Func<Task> import = () => importer.ImportDateAsync(context, item);
        await import.Should().ThrowAsync<Exception>();
        (await evidence.GetCurrentInputAsync(DirectoryKey(context, "part-two")))
            .Should()
            .BeEquivalentTo(before);
        var firstDirectory = await evidence.GetCurrentInputAsync(DirectoryKey(context, "part-one"));
        firstDirectory.Should().NotBeNull();
        (
            await scope
                .ServiceProvider.GetRequiredService<SourceService>()
                .ForUpstreamIdentifierFromPrimary(context.Artist, "2000-01-01")
        )
            .Should()
            .BeNull();

        invalid = false;
        await importer.ImportDateAsync(context, item);
        var recovered = await evidence.GetCurrentInputAsync(DirectoryKey(context, "part-one"));
        recovered!.Revision.Uuid.Should().Be(firstDirectory!.Revision.Uuid);
        recovered.Generation.Should().Be(firstDirectory.Generation + 1);
        (
            await scope
                .ServiceProvider.GetRequiredService<SourceService>()
                .ForUpstreamIdentifierFromPrimary(context.Artist, "2000-01-01")
        )!
            .taper_notes.Should()
            .Be("First notes\nSecond notes");
    }

    [Test]
    public async Task RetainedDirectoriesSurviveCatalogRollbackAndRetryKeepsOneRecording()
    {
        var context = await Context();
        using var scope = host.Services.CreateScope();
        var importer = Importer(scope, uri => Payload(context, uri));
        var root = await importer.FetchRootAndPrepareVenuesAsync(context);
        var item = (await importer.SelectChangedDatesAsync(context, root)).Items.Single();
        await db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                $"""
                CREATE FUNCTION normalization.fail_local_capture_fixture() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.artist_id = {context.Artist.id} THEN RAISE EXCEPTION 'Local catalog fixture failure'; END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER fail_local_capture_fixture BEFORE INSERT ON sources
                FOR EACH ROW EXECUTE FUNCTION normalization.fail_local_capture_fixture();
                """
            )
        );
        try
        {
            Func<Task> import = () => importer.ImportDateAsync(context, item);
            var failure = await import.Should().ThrowAsync<Exception>();
            var databaseFailure = failure
                .Which.GetBaseException()
                .Should()
                .BeOfType<PostgresException>()
                .Which;
            databaseFailure.SqlState.Should().Be(PostgresErrorCodes.RaiseException);
            databaseFailure.MessageText.Should().Be("Local catalog fixture failure");
            (
                await scope
                    .ServiceProvider.GetRequiredService<SourceService>()
                    .ForUpstreamIdentifierFromPrimary(context.Artist, "2000-01-01")
            )
                .Should()
                .BeNull();
        }
        finally
        {
            await db.WithWriteConnection(connection =>
                connection.ExecuteAsync(
                    "DROP TRIGGER fail_local_capture_fixture ON sources; DROP FUNCTION normalization.fail_local_capture_fixture();"
                )
            );
        }

        var first = await evidence.GetCurrentInputAsync(DirectoryKey(context, "part-one"));
        var second = await evidence.GetCurrentInputAsync(DirectoryKey(context, "part-two"));
        first.Should().NotBeNull();
        second.Should().NotBeNull();
        await importer.ImportDateAsync(context, item);
        (await evidence.GetCurrentInputAsync(DirectoryKey(context, "part-one")))!
            .Revision.Uuid.Should()
            .Be(first!.Revision.Uuid);
        (await evidence.GetCurrentInputAsync(DirectoryKey(context, "part-two")))!
            .Revision.Uuid.Should()
            .Be(second!.Revision.Uuid);
        var counts = await db.WithWriteConnection(connection =>
            connection.QuerySingleAsync<(int Sources, int Tracks)>(
                "SELECT (SELECT count(*)::int FROM sources WHERE artist_id=@id), (SELECT count(*)::int FROM source_tracks WHERE artist_id=@id)",
                new { id = context.Artist.id }
            )
        );
        counts.Should().Be((1, 2));
    }

    [Test]
    public async Task EqualDirectoryNamesUnderDifferentConfiguredRootsRemainDifferentResources()
    {
        var first = await Context();
        var second = await Context();
        foreach (var context in new[] { first, second })
        {
            using var scope = host.Services.CreateScope();
            var importer = Importer(scope, uri => Payload(context, uri));
            var root = await importer.FetchRootAndPrepareVenuesAsync(context);
            await importer.ImportDateAsync(
                context,
                (await importer.SelectChangedDatesAsync(context, root)).Items.Single()
            );
        }

        var firstInput = await evidence.GetCurrentInputAsync(DirectoryKey(first, "part-one"));
        var secondInput = await evidence.GetCurrentInputAsync(DirectoryKey(second, "part-one"));
        firstInput!.Revision.Uuid.Should().NotBe(secondInput!.Revision.Uuid);
        firstInput.Revision.Payload.Should().Equal(secondInput.Revision.Payload);
        firstInput.Generation.Should().Be(1);
        secondInput.Generation.Should().Be(1);
    }

    [Test]
    public async Task MissingRootCannotReplacePreviouslyValidatedCurrentRoot()
    {
        var context = await Context();
        using var scope = host.Services.CreateScope();
        var raw = Payload(context, new Uri("https://fixture/Fixture/metadata.json"));
        var importer = Importer(scope, _ => raw);
        await importer.FetchRootAndPrepareVenuesAsync(context);
        var before = await evidence.GetCurrentInputAsync(RootKey(context));
        raw = "{\"success\":false}";
        Func<Task> fetch = () => importer.FetchRootAndPrepareVenuesAsync(context);
        await fetch.Should().ThrowAsync<InvalidDataException>();
        (await evidence.GetCurrentInputAsync(RootKey(context))).Should().BeEquivalentTo(before);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public async Task MissingConfiguredMetadataRootFailsBeforeHttp(string? metadataRoot)
    {
        var context = await Context();
        context.Source.upstream_identifier = metadataRoot;
        var requests = 0;
        using var scope = host.Services.CreateScope();
        var importer = Importer(
            scope,
            _ =>
            {
                requests++;
                return "{}";
            }
        );
        Func<Task> fetch = () => importer.FetchRootAndPrepareVenuesAsync(context);
        await fetch
            .Should()
            .ThrowAsync<InvalidDataException>()
            .WithMessage("Local provider mapping is missing its metadata root.");
        requests.Should().Be(0);
    }

    [Test]
    public async Task CancellationBeforeFetchLeavesNoEvidenceAndDoesNotIssueHttp()
    {
        var context = await Context();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        context = context with { CancellationToken = cancellation.Token };
        var requests = 0;
        using var scope = host.Services.CreateScope();
        var importer = Importer(
            scope,
            uri =>
            {
                requests++;
                return Payload(context, uri);
            }
        );
        Func<Task> fetch = () => importer.FetchRootAndPrepareVenuesAsync(context);
        await fetch.Should().ThrowAsync<OperationCanceledException>();
        requests.Should().Be(0);
        (await evidence.GetCurrentInputAsync(RootKey(context))).Should().BeNull();
    }

    [Test]
    public async Task DisabledCaptureKeepsCatalogImportAvailableWithoutEvidence()
    {
        var context = await Context();
        using var scope = host.Services.CreateScope();
        var disabled = new NormalizationProviderInputService(
            evidence,
            Options.Create(new NormalizationProviderCaptureOptions())
        );
        var importer = ActivatorUtilities.CreateInstance<LocalImporter>(
            scope.ServiceProvider,
            disabled
        );
        importer.UseIngestionHttpClient(new HttpClient(new Handler(uri => Payload(context, uri))));
        var root = await importer.FetchRootAndPrepareVenuesAsync(context);
        await importer.ImportDateAsync(
            context,
            (await importer.SelectChangedDatesAsync(context, root)).Items.Single()
        );
        (await evidence.GetCurrentInputAsync(RootKey(context))).Should().BeNull();
        (await evidence.GetCurrentInputAsync(DirectoryKey(context, "part-one"))).Should().BeNull();
    }

    private LocalImporter Importer(IServiceScope scope, Func<Uri, string> response)
    {
        var importer = scope.ServiceProvider.GetRequiredService<LocalImporter>();
        importer.UseIngestionHttpClient(new HttpClient(new Handler(response)));
        return importer;
    }

    private async Task<ProviderContext> Context()
    {
        var uuid = Guid.NewGuid();
        var artist = await db.WithWriteConnection(connection =>
            connection.QuerySingleAsync<Artist>(
                "INSERT INTO artists(name,slug,uuid,updated_at) VALUES('Local Capture',@slug,@uuid,now()) RETURNING *",
                new { slug = "local-capture-" + uuid.ToString("N"), uuid }
            )
        );
        return new(
            artist,
            new ArtistUpstreamSource
            {
                artist_id = artist.id,
                upstream_source_id = 9,
                upstream_identifier = "capture-" + uuid.ToString("N"),
            },
            new(Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.AddHours(1)),
            host.Services.GetRequiredService<CatalogImportTransaction>(),
            CancellationToken.None
        );
    }

    private static NormalizationResourceKey RootKey(ProviderContext context) =>
        new("local", "metadata_root", context.Source.upstream_identifier!);

    private static NormalizationResourceKey DirectoryKey(
        ProviderContext context,
        string directory
    ) =>
        new(
            "local",
            "directory_metadata",
            JsonConvert.SerializeObject(new[] { context.Source.upstream_identifier, directory })
        );

    // Reuse the ingestion suite's complete synthetic provider documents; there is no recorded Local HTTP fixture.
    private static string Payload(ProviderContext context, Uri uri) =>
        ProviderFixtures.Response(
            "local",
            new Uri(
                "https://fixture"
                    + uri.AbsolutePath.Replace(
                        context.Source.upstream_identifier!,
                        "Fixture",
                        StringComparison.Ordinal
                    )
            )
        );

    private sealed class Handler(Func<Uri, string> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        ) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        response(request.RequestUri!),
                        Encoding.UTF8,
                        "application/json"
                    ),
                }
            );
    }
}
