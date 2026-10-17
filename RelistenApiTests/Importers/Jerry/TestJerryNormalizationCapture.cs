using System.Net;
using System.Net.Http.Headers;
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
using Relisten.Ingestion.Providers.Jerry;
using RelistenApiTests.Services;

namespace RelistenApiTests.Importers.Jerry;

/// <summary>Exercises raw mirror retention before legacy setlist conversion and catalog transactions.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestJerryNormalizationCapture
{
    private NormalizationDomainFixture fixture = null!;
    private IHost host = null!;
    private NormalizationEvidenceService evidence = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new NormalizationDomainFixture();
        SqlMapper.AddTypeHandler(new DateTimeHandler());
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(fixture.Db);
        builder.Services.AddSingleton(_ => new RedisService(
            RedisService.BuildConfiguration("redis://127.0.0.1:16379")
        ));
        builder.Services.AddRelistenCatalogServices();
        builder.Services.Configure<NormalizationProviderCaptureOptions>(options =>
            options.Enabled = true
        );
        host = builder.Build();
        evidence = new(fixture.Db);
    }

    [OneTimeTearDown]
    public void Stop()
    {
        if (host != null)
        {
            host.Services.GetRequiredService<RedisService>().connection.Dispose();
            host.Dispose();
        }
        fixture?.Dispose();
    }

    [Test]
    public async Task ImportAndRepairRetainRawLinksRepeatsAndMarkersBeforeLegacyConversion()
    {
        var context = await Context();
        var item = Item();
        var handler = new MirrorHandler(Page);
        using var scope = host.Services.CreateScope();
        var importer = Importer(scope, handler);
        (await importer.ImportPageAsync(context, item))
            .Disposition.Should()
            .Be(ItemDisposition.Applied);
        var current = await Current(context, item);
        current.Revision.Payload.Should().Equal(Encoding.UTF8.GetBytes(Page));
        current.Revision.MediaType.Should().Be("text/html; charset=utf-8");
        current.Revision.CaptureVersion.Should().Be("jerry-mirror-html:1");
        (await evidence.GetFetchHistoryAsync(Resource(context, item)))
            .Single()
            .ProviderRevision.Should()
            .Be("etag:\"mirror-revision\";last-modified:Thu, 01 Jan 2026 00:00:00 GMT");

        var songs = await scope
            .ServiceProvider.GetRequiredService<SetlistSongService>()
            .AllForArtist(context.Artist);
        songs
            .Select(song => song.name)
            .Should()
            .BeEquivalentTo(new[] { "Dark Star", "Drums", "Dark Star" });
        (await importer.RepairVenueAsync(context, item))
            .Disposition.Should()
            .Be(ItemDisposition.Unchanged);
        (await Current(context, item)).Generation.Should().Be(2);
        handler.PageRequests.Should().Be(2);
    }

    [Test]
    public async Task DeclaredCharsetParsesVenueWhileRetainingOriginalResponseBytes()
    {
        var context = await Context();
        var item = Item();
        var html = Page.Replace("Fixture Field 🎵", "Café Hall");
        using var scope = host.Services.CreateScope();
        var importer = Importer(scope, new MirrorHandler(html) { TextEncoding = Encoding.Latin1 });
        await importer.ImportPageAsync(context, item);

        var current = await Current(context, item);
        current.Revision.Payload.Should().Equal(Encoding.Latin1.GetBytes(html));
        current.Revision.MediaType.Should().Be("text/html; charset=iso-8859-1");
        (
            await scope
                .ServiceProvider.GetRequiredService<VenueService>()
                .AllIncludingUnusedForArtist(context.Artist)
        )
            .Single()
            .name.Should()
            .Be("Café Hall");
    }

    [Test]
    public async Task NativeRefreshAndBackfillUseTheSameResourceWithoutExtraPageRequests()
    {
        var context = await Context();
        var item = Item();
        var handler = new MirrorHandler(Page, item.Input.FilePath);
        using var scope = host.Services.CreateScope();
        var importer = Importer(scope, handler);
        await importer.ImportDataForArtist(context.Artist, context.Source, null);
        var first = await Current(context, item);
        first.Revision.Payload.Should().Equal(Encoding.UTF8.GetBytes(Page));
        await importer.BackfillVenuesForArtist(context.Artist, context.Source, null);
        (await Current(context, item)).Revision.Uuid.Should().Be(first.Revision.Uuid);
        (await Current(context, item)).Generation.Should().Be(2);
        handler.PageRequests.Should().Be(2);
        handler.IndexRequests.Should().Be(2);
    }

    [Test]
    public async Task CapturedInputSurvivesCatalogRollbackAndCanBeImportedOnRetry()
    {
        var context = await Context();
        var item = Item();
        using var scope = host.Services.CreateScope();
        var importer = Importer(scope, new MirrorHandler(Page));
        await fixture.Db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                """
                CREATE FUNCTION normalization.fail_jerry_fixture() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'Jerry catalog fixture failure'; END $$;
                CREATE TRIGGER fail_jerry_fixture BEFORE INSERT ON setlist_shows
                FOR EACH ROW EXECUTE FUNCTION normalization.fail_jerry_fixture();
                """
            )
        );
        try
        {
            Func<Task> import = () => importer.ImportPageAsync(context, item);
            var failure = await import.Should().ThrowAsync<Exception>();
            var postgres = failure.WithInnerException<PostgresException>().Which;
            postgres.SqlState.Should().Be(PostgresErrorCodes.RaiseException);
            postgres.MessageText.Should().Be("Jerry catalog fixture failure");
            (await Current(context, item))
                .Revision.Payload.Should()
                .Equal(Encoding.UTF8.GetBytes(Page));
            (
                await scope
                    .ServiceProvider.GetRequiredService<SetlistShowService>()
                    .AllForArtist(context.Artist)
            )
                .Should()
                .BeEmpty();
            (
                await scope
                    .ServiceProvider.GetRequiredService<VenueService>()
                    .AllIncludingUnusedForArtist(context.Artist)
            )
                .Should()
                .BeEmpty();
        }
        finally
        {
            await fixture.Db.WithWriteConnection(connection =>
                connection.ExecuteAsync(
                    "DROP TRIGGER fail_jerry_fixture ON setlist_shows; DROP FUNCTION normalization.fail_jerry_fixture();"
                )
            );
        }

        (await importer.ImportPageAsync(context, item))
            .Disposition.Should()
            .Be(ItemDisposition.Applied);
        (await Current(context, item)).Generation.Should().Be(2);
    }

    [TestCase("<html>Mirror placeholder</html>", true)]
    [TestCase("<p class='venue-name'>Broken page</p>", false)]
    public async Task MissingOrMalformedDetailsDoNotReplaceCurrentEvidence(
        string replacement,
        bool skip
    )
    {
        var context = await Context();
        var item = Item();
        using var scope = host.Services.CreateScope();
        var handler = new MirrorHandler(Page);
        var importer = Importer(scope, handler);
        await importer.ImportPageAsync(context, item);
        var before = await Current(context, item);
        handler.Html = replacement;
        if (skip)
        {
            (await importer.RepairVenueAsync(context, item))
                .SkipReason.Should()
                .Be(JerrySkipReason.MissingShowDetails);
        }
        else
        {
            Func<Task> repair = () => importer.RepairVenueAsync(context, item);
            await repair.Should().ThrowAsync<InvalidDataException>();
        }
        (await Current(context, item)).Should().BeEquivalentTo(before);
        (await evidence.GetFetchHistoryAsync(Resource(context, item))).Should().ContainSingle();
    }

    [Test]
    public async Task FailedHttpResponseDoesNotReplaceCurrentEvidenceOrCatalogVenue()
    {
        var context = await Context();
        var item = Item();
        using var scope = host.Services.CreateScope();
        var handler = new MirrorHandler(Page);
        var importer = Importer(scope, handler);
        await importer.ImportPageAsync(context, item);
        var before = await Current(context, item);
        handler.StatusCode = HttpStatusCode.ServiceUnavailable;
        Func<Task> repair = () => importer.RepairVenueAsync(context, item);
        await repair.Should().ThrowAsync<HttpRequestException>();
        (await Current(context, item)).Should().BeEquivalentTo(before);
        (await evidence.GetFetchHistoryAsync(Resource(context, item))).Should().ContainSingle();
    }

    [Test]
    public async Task ResponseWithoutRevisionHeadersDoesNotInventAnUpstreamRevision()
    {
        var context = await Context();
        var item = Item();
        using var scope = host.Services.CreateScope();
        var importer = Importer(scope, new MirrorHandler(Page) { RevisionHeaders = false });
        await importer.ImportPageAsync(context, item);
        (await evidence.GetFetchHistoryAsync(Resource(context, item)))
            .Single()
            .ProviderRevision.Should()
            .BeNull();
    }

    [Test]
    public async Task DisabledCaptureKeepsCatalogImportWithoutEvidenceWrites()
    {
        var context = await Context();
        var item = Item();
        using var scope = host.Services.CreateScope();
        var services = scope.ServiceProvider;
        using var importer = new JerryGarciaComImporter(
            services.GetRequiredService<CatalogRebuildService>(),
            services.GetRequiredService<SetlistShowService>(),
            services.GetRequiredService<VenueService>(),
            services.GetRequiredService<TourService>(),
            services.GetRequiredService<SetlistSongService>(),
            services.GetRequiredService<ILogger<JerryGarciaComImporter>>(),
            new(evidence, Options.Create(new NormalizationProviderCaptureOptions()))
        );
        importer.UseIngestionHttpClient(new HttpClient(new MirrorHandler(Page)));
        (await importer.ImportPageAsync(context, item))
            .Disposition.Should()
            .Be(ItemDisposition.Applied);
        (await evidence.GetCurrentInputAsync(Resource(context, item))).Should().BeNull();
    }

    private JerryGarciaComImporter Importer(IServiceScope scope, MirrorHandler handler)
    {
        var importer = scope.ServiceProvider.GetRequiredService<JerryGarciaComImporter>();
        importer.UseIngestionHttpClient(new HttpClient(handler));
        return importer;
    }

    private async Task<ProviderContext> Context()
    {
        var uuid = Guid.NewGuid();
        var artist = await fixture.Db.WithWriteConnection(async connection =>
        {
            var id = await connection.QuerySingleAsync<int>(
                "INSERT INTO artists(name,slug,uuid,updated_at) VALUES('Jerry capture fixture',@slug,@uuid,now()) RETURNING id",
                new { slug = uuid.ToString(), uuid }
            );
            await connection.ExecuteAsync(
                "INSERT INTO features(artist_id) VALUES(@id)",
                new { id }
            );
            return new Artist
            {
                id = id,
                uuid = uuid,
                slug = uuid.ToString(),
                name = "Jerry capture fixture",
                features = new(),
            };
        });
        return new(
            artist,
            new ArtistUpstreamSource { upstream_identifier = "mirror-" + uuid },
            new IngestionExecution(Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.AddHours(1)),
            new CatalogImportTransaction(fixture.Db),
            CancellationToken.None
        );
    }

    private static DiscoveredItem<JerryShowId, JerryGarciaComImporter.FileMetaObject> Item() =>
        new(
            new("1972-08-27-veneta"),
            new()
            {
                Identifier = "1972-08-27-veneta",
                FilePath = "1972-08-27-veneta.html",
                Date = new DateTime(1972, 8, 27),
                DisplayDate = "1972-08-27",
            }
        );

    private static NormalizationResourceKey Resource(
        ProviderContext context,
        DiscoveredItem<JerryShowId, JerryGarciaComImporter.FileMetaObject> item
    ) =>
        new(
            "jerrygarcia.com",
            "mirror_show_page",
            $"{context.Source.upstream_identifier}/{item.Id.Value}"
        );

    private async Task<NormalizationCurrentInput> Current(
        ProviderContext context,
        DiscoveredItem<JerryShowId, JerryGarciaComImporter.FileMetaObject> item
    ) => (await evidence.GetCurrentInputAsync(Resource(context, item)))!;

    // Synthetic mirror document: preserves fields the legacy importer deliberately flattens.
    private const string Page = """
        <!doctype html><html><body>
        <p class="venue-name">Fixture Field 🎵</p><p class="venue-name">Grateful Dead</p>
        <p class="venue-name">Summer Tour</p><p class="venue-address">Veneta, OR</p>
        <p class="venue-country">USA</p><section data-set="2"><ol>
        <li><a href="/song/dark-star/">Dark Star</a> ></li>
        <li><a href="/song/drums/">Drums</a>*</li>
        <li><a href="/song/dark-star/">Dark Star</a></li>
        </ol></section></body></html>
        """;

    private sealed class MirrorHandler(string html, string? filename = null) : HttpMessageHandler
    {
        public string Html { get; set; } = html;
        public Encoding TextEncoding { get; init; } = Encoding.UTF8;
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
        public bool RevisionHeaders { get; init; } = true;
        public int PageRequests { get; private set; }
        public int IndexRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        )
        {
            var index = request.RequestUri!.AbsolutePath.EndsWith("show_pages.json");
            if (index)
            {
                IndexRequests++;
            }
            else
            {
                PageRequests++;
            }
            var response = new HttpResponseMessage(StatusCode)
            {
                Content = new StringContent(
                    index ? System.Text.Json.JsonSerializer.Serialize(new[] { filename! }) : Html,
                    TextEncoding,
                    index ? "application/json" : "text/html"
                ),
            };
            if (RevisionHeaders)
            {
                response.Headers.ETag = new EntityTagHeaderValue("\"mirror-revision\"");
                response.Content.Headers.LastModified = DateTimeOffset.Parse(
                    "2026-01-01T00:00:00Z"
                );
            }
            return Task.FromResult(response);
        }
    }
}
