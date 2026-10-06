using System.Transactions;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Relisten;
using Relisten.Api.Models;
using Relisten.Data;
using Relisten.Ingestion;
using Relisten.Ingestion.Workflows;
using Temporalio.Client;

namespace RelistenApiTests.Ingestion;

/// <summary>Opt-in tests against the dedicated local-dev/temporal stack, never the application's configured database.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public class TestIngestionPersistence
{
    private IHost host = null!;
    private FixtureHttpServer upstream = null!;
    private DbService db = null!;
    private ImportRunRepository store = null!;
    private string requestId = null!;
    private Guid artistUuid;
    private int artistId;
    private string originalWrite = null!;
    private string originalRead = null!;

    [OneTimeSetUp]
    public async Task Start()
    {
        if (Environment.GetEnvironmentVariable("RELISTEN_LOCAL_INGESTION_TESTS") != "1")
            Assert.Ignore("Start local-dev/temporal and set RELISTEN_LOCAL_INGESTION_TESTS=1.");
        originalWrite = DbService.ConnStr;
        originalRead = DbService.ReadOnlyConnStr;
        upstream = new FixtureHttpServer();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Temporal:Address"] = "127.0.0.1:17233",
                ["Ingestion:Engine"] = "Temporal",
                ["Ingestion:UpstreamBaseUrl"] = upstream.Address.ToString(),
            }
        );
        builder.Services.AddSingleton(sp => new DbService(
            "postgresql://relisten:local_ingestion@127.0.0.1:15433/relisten_ingestion",
            null,
            null,
            builder.Environment,
            sp.GetRequiredService<ILogger<DbService>>()
        ));
        builder.Services.AddSingleton(_ => new RedisService(
            RedisService.BuildConfiguration("redis://127.0.0.1:16380")
        ));
        builder
            .Services.AddRelistenCatalogServices()
            .AddTemporalIngestionWorkers(builder.Configuration);
        SqlMapper.AddTypeHandler(new PersistentIdentifierHandler());
        SqlMapper.AddTypeHandler(new DateTimeHandler());
        host = builder.Build();
        db = host.Services.GetRequiredService<DbService>();
        store = new ImportRunRepository(db);
        await host.StartAsync();
        await db.WithWriteConnection(c =>
            c.ExecuteAsync(
                """
                INSERT INTO upstream_sources(id,name,url) VALUES(1,'archive.org','https://archive.org') ON CONFLICT DO NOTHING;
                """
            )
        );
    }

    [SetUp]
    public async Task SeedArtist()
    {
        requestId = Guid.NewGuid().ToString();
        artistUuid = Guid.NewGuid();
        artistId = await db.WithWriteConnection(c =>
            c.QuerySingleAsync<int>(
                """
                INSERT INTO artists(name,slug,uuid,updated_at) VALUES('Fixture',@slug,@artistUuid,now()) RETURNING id
                """,
                new { slug = "fixture-" + artistUuid, artistUuid }
            )
        );
        await db.WithWriteConnection(c =>
            c.ExecuteAsync(
                """
                INSERT INTO features(artist_id) VALUES(@artistId);
                INSERT INTO artists_upstream_sources(artist_id,upstream_source_id,upstream_identifier) VALUES(@artistId,1,'Fixture');
                """,
                new { artistId }
            )
        );
        upstream.Respond = uri =>
            uri.AbsolutePath.StartsWith("/metadata/") ? Metadata("First title") : Listing;
    }

    [OneTimeTearDown]
    public async Task Stop()
    {
        if (host == null)
            return;
        await host.StopAsync();
        host.Services.GetRequiredService<RedisService>().connection.Dispose();
        host.Dispose();
        upstream.Dispose();
        DbService.ConnStr = originalWrite;
        DbService.ReadOnlyConnStr = originalRead;
    }

    [Test]
    public async Task LostAcknowledgementReturnsCommittedReceiptWithoutRepeatingWrites()
    {
        var first = await store.ApplyOnceAsync(requestId, "apply", Increment);
        var second = await store.ApplyOnceAsync(requestId, "apply", Increment);
        second.Should().BeEquivalentTo(first);
        (await Featured()).Should().Be(1);
    }

    [Test]
    public async Task FailedApplyRollsBackDomainWritesAndReceiptThenCanResume()
    {
        Func<Task> fail = async () =>
            await store.ApplyOnceAsync<Receipt>(
                requestId,
                "apply",
                async () =>
                {
                    await Increment();
                    throw new InvalidDataException("Interrupted before commit");
                }
            );
        await fail.Should().ThrowAsync<InvalidDataException>();
        (await Featured()).Should().Be(0);
        (await store.ReadAsync<Receipt>(requestId, "apply")).Should().BeNull();
        (await store.ApplyOnceAsync(requestId, "apply", Increment)).Value.Should().Be(1);
    }

    [Test]
    public async Task OverlappingAttemptsCommitOnlyOnceWhenTheConflictIsRetried()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = store.ApplyOnceAsync(
            requestId,
            "apply",
            async () =>
            {
                entered.SetResult();
                await Task.Delay(100);
                return await Increment();
            }
        );
        await entered.Task;
        try
        {
            await store.ApplyOnceAsync(requestId, "apply", Increment);
        }
        catch (Exception error) when (HasSerializationConflict(error)) { }
        await first;
        await store.ApplyOnceAsync(requestId, "apply", Increment);
        (await Featured()).Should().Be(1);
    }

    [Test]
    public async Task PageRetryKeepsTheFirstSelectionAndItemBodiesTogether()
    {
        await store.SavePageAsync(
            requestId,
            "archive.org",
            "page",
            new ProviderPage([new("a", "first")], ObservedKeys: ["a"])
        );
        await store.SavePageAsync(
            requestId,
            "archive.org",
            "page",
            new ProviderPage([new("b", "later"), new("c", "extra")], ObservedKeys: ["b", "c"])
        );
        (await store.ReadAsync<ImportPage>(requestId, "page"))!.ItemCount.Should().Be(1);
        (await store.ReadAsync<ProviderItem>(requestId, "page/item/0"))!.Key.Should().Be("a");
        (await store.ReadAsync<ProviderItem>(requestId, "page/item/1")).Should().BeNull();
        (await store.ReadAsync<string[]>(requestId, "archive.org/observed")).Should().Equal("a");
    }

    [Test]
    public async Task ArchiveWorkflowReimportPreservesSourceAndTrackIdentityAndUpdatesMetadata()
    {
        var first = await Import();
        first.FailedItems.Should().Be(0);
        first.Stats.Created.Should().BeGreaterThan(0);
        var before = await Catalog();
        before.Should().ContainSingle();
        before[0].title.Should().Be("First title");
        upstream.Respond = uri =>
            uri.AbsolutePath.StartsWith("/metadata/") ? Metadata("Updated title") : Listing;
        await Import(item: "fixture-2000-01-01");
        var after = await Catalog();
        after.Should().ContainSingle();
        after[0].source_uuid.Should().Be(before[0].source_uuid);
        after[0].track_uuid.Should().Be(before[0].track_uuid);
        after[0].show_uuid.Should().Be(before[0].show_uuid);
        after[0].title.Should().Be("Updated title");
    }

    [Test]
    public async Task MissingArchiveListingKeepsPresentOrUnknownItemsAndDeletesOnlyConfirmedDeletedItems()
    {
        await Import();
        upstream.Respond = uri =>
            uri.AbsolutePath.StartsWith("/metadata/") ? Metadata("Present") : EmptyListing;
        (await Import()).Stats.Removed.Should().Be(0);
        (await Catalog()).Should().ContainSingle();
        upstream.Respond = uri => uri.AbsolutePath.StartsWith("/metadata/") ? "{}" : EmptyListing;
        (await Import()).Stats.Removed.Should().Be(0);
        (await Catalog()).Should().ContainSingle();
        upstream.Respond = uri =>
            uri.AbsolutePath.StartsWith("/metadata/") ? "{\"errcode\":104}" : EmptyListing;
        (await Import()).Stats.Removed.Should().BeGreaterThan(0);
        (await Catalog()).Should().BeEmpty();
    }

    [Test]
    public async Task ReusingARequestForDifferentInputsFailsWithoutChangingTheCatalog()
    {
        await Import();
        var client = host.Services.GetRequiredService<ITemporalClient>();
        var wrongRequest = new ArtistImportRequest(artistUuid.ToString(), requestId, Year: 2026);
        Func<Task> wrong = async () =>
            await client.ExecuteWorkflowAsync(
                (ArtistRefreshWorkflow w) => w.RunAsync(wrongRequest, null),
                new WorkflowOptions("test/wrong/" + requestId, IngestionQueues.Workflows)
            );
        await wrong.Should().ThrowAsync<Temporalio.Exceptions.WorkflowFailedException>();
        (await Catalog()).Should().ContainSingle();
    }

    [TestCase("archive.org", 1)]
    [TestCase("phish.in", 2)]
    [TestCase("phish.net", 3)]
    [TestCase("setlist.fm", 6)]
    [TestCase("phantasytour.com", 7)]
    [TestCase("jerrygarcia.com", 8)]
    [TestCase("local", 9)]
    public async Task ProviderWorkflowMatchesLegacyCatalogOutput(string provider, int providerId)
    {
        if (provider == "phish.net")
            await Import(); // Enrichment needs an existing recording.
        await ConfigureProvider(provider, providerId);
        if (provider != "archive.org")
            upstream.Respond = uri => ProviderFixtures.Response(provider, uri);
        Dictionary<string, string[]> expected;
        using (var scope = host.Services.CreateScope())
        using (
            var transaction = new TransactionScope(
                TransactionScopeOption.Required,
                new TransactionOptions
                {
                    IsolationLevel = IsolationLevel.RepeatableRead,
                    Timeout = TimeSpan.FromMinutes(5),
                },
                TransactionScopeAsyncFlowOption.Enabled
            )
        )
        {
            var artist = (
                await scope
                    .ServiceProvider.GetRequiredService<ArtistService>()
                    .FindArtistWithIdOrSlug(artistUuid.ToString())
            )!;
            var source = artist.upstream_sources.Single();
            var importer = source.upstream_source.importer!;
            importer.UseIngestionHttpClient(
                new HttpClient(new FixtureRedirectHandler(upstream.Address))
            );
            await importer.ImportDataForArtist(artist, source, null);
            expected = await Snapshot();
            // Roll back the legacy write, then run Temporal against the same artist identity.
        }
        expected["sources"]
            .Concat(expected["setlists"])
            .Should()
            .NotBeEmpty("the fixture must exercise catalog writes");
        var result = await Import();
        result.FailedItems.Should().Be(0);
        (await Snapshot()).Should().BeEquivalentTo(expected);
    }

    private Task ConfigureProvider(string provider, int providerId) =>
        db.WithWriteConnection(c =>
            c.ExecuteAsync(
                """
                INSERT INTO upstream_sources(id,name) VALUES(@providerId,@provider) ON CONFLICT DO NOTHING;
                DELETE FROM artists_upstream_sources WHERE artist_id=@artistId;
                INSERT INTO artists_upstream_sources(artist_id,upstream_source_id,upstream_identifier)
                    VALUES(@artistId,@providerId,@upstreamIdentifier);
                UPDATE artists SET musicbrainz_id='fixture' WHERE id=@artistId;
                UPDATE features SET tours=true,per_show_venues=true,eras=@eras WHERE artist_id=@artistId;
                """,
                new
                {
                    artistId,
                    providerId,
                    provider,
                    upstreamIdentifier = provider == "phantasytour.com" ? "1" : "Fixture",
                    eras = provider == "phish.in",
                }
            )
        );

    [Test]
    public async Task JerryVenueBackfillChangesExistingSetlistsAndRemovesTheOrphanVenue()
    {
        await ConfigureProvider("jerrygarcia.com", 8);
        upstream.Respond = uri => ProviderFixtures.Response("jerrygarcia.com", uri);
        await Import();
        var before = await db.WithWriteConnection(c =>
            c.QueryAsync<long>(
                "SELECT id FROM setlist_shows WHERE artist_id=@artistId ORDER BY id",
                new { artistId }
            )
        );
        upstream.Respond = uri =>
            ProviderFixtures
                .Response("jerrygarcia.com", uri)
                .Replace("Fixture Venue", "Corrected Venue");
        var result = await Import(backfill: true);
        result.FailedItems.Should().Be(0);
        result.Stats.Updated.Should().Be(2);
        result.Stats.Removed.Should().Be(1);
        var after = await db.WithWriteConnection(c =>
            c.QueryAsync<long>(
                "SELECT id FROM setlist_shows WHERE artist_id=@artistId ORDER BY id",
                new { artistId }
            )
        );
        after.Should().Equal(before);
        var venues = await db.WithWriteConnection(c =>
            c.QueryAsync<string>(
                "SELECT name FROM venues WHERE artist_id=@artistId",
                new { artistId }
            )
        );
        venues.Should().Equal("Corrected Venue");
    }

    [Test]
    public async Task DiscoveryCreatesOneArtistMappingAndRepeatingItDoesNotDuplicateTheArtist()
    {
        var identifier = "FixtureCollection-" + artistUuid;
        upstream.Respond = _ =>
            JsonConvert.SerializeObject(
                new
                {
                    total = 1,
                    items = new[]
                    {
                        new
                        {
                            identifier,
                            title = identifier,
                            item_count = 12,
                            month = 1,
                        },
                    },
                }
            );
        var client = host.Services.GetRequiredService<ITemporalClient>();
        for (var i = 0; i < 2; i++)
        {
            var id = await host
                .Services.GetRequiredService<TemporalIngestionClient>()
                .StartBatchAsync("discover");
            (
                await client
                    .GetWorkflowHandle<ArchiveArtistDiscoveryWorkflow, int>(id)
                    .GetResultAsync()
            )
                .Should()
                .Be(1);
        }
        (
            await db.WithWriteConnection(c =>
                c.QuerySingleAsync<int>(
                    "SELECT count(*) FROM artists_upstream_sources WHERE upstream_source_id=1 AND upstream_identifier=@identifier",
                    new { identifier }
                )
            )
        )
            .Should()
            .Be(1);
    }

    private async Task<Dictionary<string, string[]>> Snapshot()
    {
        var queries = new Dictionary<string, string>
        {
            ["sources"] =
                "SELECT jsonb_build_array(uuid,upstream_identifier,display_date,description,taper_notes,avg_rating,num_reviews)::text FROM sources WHERE artist_id=@artistId ORDER BY upstream_identifier",
            ["tracks"] =
                "SELECT jsonb_build_array(uuid,title,track_position,mp3_url,duration)::text FROM source_tracks WHERE artist_id=@artistId AND NOT is_orphaned ORDER BY mp3_url",
            ["shows"] =
                "SELECT jsonb_build_array(uuid,display_date)::text FROM shows WHERE artist_id=@artistId ORDER BY display_date",
            ["setlists"] =
                "SELECT jsonb_build_array(ss.upstream_identifier,ss.date,v.name,t.name)::text FROM setlist_shows ss LEFT JOIN venues v ON v.id=ss.venue_id LEFT JOIN tours t ON t.id=ss.tour_id WHERE ss.artist_id=@artistId ORDER BY ss.upstream_identifier",
            ["songs"] =
                "SELECT jsonb_build_array(name,slug,upstream_identifier)::text FROM setlist_songs WHERE artist_id=@artistId ORDER BY slug",
            ["plays"] =
                "SELECT jsonb_build_array(ss.upstream_identifier,s.slug)::text FROM setlist_songs_plays p JOIN setlist_shows ss ON ss.id=p.played_setlist_show_id JOIN setlist_songs s ON s.id=p.played_setlist_song_id WHERE ss.artist_id=@artistId ORDER BY ss.upstream_identifier,s.slug",
            ["tours"] =
                "SELECT jsonb_build_array(name,start_date,end_date)::text FROM tours WHERE artist_id=@artistId ORDER BY name",
            ["reviews"] =
                "SELECT jsonb_build_array(r.author,r.review,r.rating)::text FROM source_reviews r JOIN sources s ON s.id=r.source_id WHERE s.artist_id=@artistId ORDER BY r.author,r.review",
        };
        var result = new Dictionary<string, string[]>();
        foreach (var (name, sql) in queries)
            result[name] = await db.WithWriteConnection(async c =>
                (await c.QueryAsync<string>(sql, new { artistId })).ToArray()
            );
        return result;
    }

    private sealed class FixtureRedirectHandler(Uri endpoint)
        : DelegatingHandler(new HttpClientHandler())
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            request.RequestUri = new Uri(endpoint, request.RequestUri!.PathAndQuery);
            return base.SendAsync(request, cancellationToken);
        }
    }

    [Test]
    public async Task TemporalApiAdmissionKeepsTheExistingResponseAndHangfireEntryDoesNoWork()
    {
        using var scope = host.Services.CreateScope();
        var controller = ActivatorUtilities.CreateInstance<Relisten.Controllers.ImportController>(
            scope.ServiceProvider
        );
        var response = (Microsoft.AspNetCore.Mvc.ObjectResult)
            await controller.Post(artistUuid.ToString());
        response.StatusCode.Should().Be(201);
        response.Value.Should().Be($"Queued as job ingest/artist/{artistUuid}!");
        await host
            .Services.GetRequiredService<ITemporalClient>()
            .GetWorkflowHandle<ArtistRefreshWorkflow, ImportResult>("ingest/artist/" + artistUuid)
            .GetResultAsync();
        var requests = upstream.Requests;
        await scope
            .ServiceProvider.GetRequiredService<ScheduledService>()
            .RefreshArtist(artistUuid.ToString(), false, null);
        upstream.Requests.Should().Be(requests);
        (await Catalog()).Should().ContainSingle();
        (await controller.Post(artistUuid.ToString(), deleteOldContent: true))
            .Should()
            .BeOfType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>();
    }

    [TestCase(2000, null)]
    [TestCase(null, "missing-item")]
    public async Task ThinAndTargetedArchiveImportsDoNotReconcileUnselectedSources(
        int? year,
        string? item
    )
    {
        await Import();
        upstream.Respond = uri =>
            uri.AbsolutePath.StartsWith("/metadata/") ? "{\"errcode\":104}" : EmptyListing;
        (await Import(item, year)).Stats.Removed.Should().Be(0);
        (await Catalog()).Should().ContainSingle();
    }

    [Test]
    public async Task LargeDiscoveryStagesEveryItemAndReturnsOnlyAPageReference()
    {
        var items = Enumerable
            .Range(0, 18386)
            .Select(i => new ProviderItem("item-" + i, Metadata("Fixture track")))
            .ToArray();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var page = await store.SavePageAsync(
            requestId,
            "archive.org",
            "large-page",
            new ProviderPage(items)
        );
        page.ItemCount.Should().Be(items.Length);
        (await store.ReadAsync<ProviderItem>(requestId, "large-page/item/18385"))!
            .Should()
            .Be(items[^1]);
        var bytes = await db.WithWriteConnection(c =>
            c.QuerySingleAsync<long>(
                "SELECT sum(pg_column_size(payload)) FROM ingestion_records WHERE request_id=@requestId",
                new { requestId }
            )
        );
        TestContext.Progress.WriteLine(
            $"Synthetic 18,386-item staging: {clock.Elapsed.TotalSeconds:F2}s, {bytes:N0} payload bytes; returned reference {JsonConvert.SerializeObject(page).Length} characters."
        );
    }

    private Task<int> Featured() =>
        db.WithWriteConnection(c =>
            c.QuerySingleAsync<int>(
                "SELECT featured FROM artists WHERE id=@artistId",
                new { artistId }
            )
        );

    private async Task<Receipt> Increment() =>
        new(
            await db.WithWriteConnection(c =>
                c.QuerySingleAsync<int>(
                    "UPDATE artists SET featured=featured+1 WHERE id=@artistId RETURNING featured",
                    new { artistId }
                )
            )
        );

    private static bool HasSerializationConflict(Exception? error) =>
        error != null
        && (
            error is Npgsql.PostgresException { SqlState: "40001" }
            || HasSerializationConflict(error.InnerException)
        );

    private async Task<ImportResult> Import(
        string? item = null,
        int? year = null,
        bool backfill = false
    )
    {
        // Each fresh import gets a new logical request, just like the API.
        requestId = Guid.NewGuid().ToString();
        var client = host.Services.GetRequiredService<ITemporalClient>();
        var request = new ArtistImportRequest(
            artistUuid.ToString(),
            requestId,
            Item: item,
            Year: year,
            BackfillJerryVenues: backfill
        );
        var id = await host
            .Services.GetRequiredService<TemporalIngestionClient>()
            .StartArtistAsync(request);
        return await client
            .GetWorkflowHandle<ArtistRefreshWorkflow, ImportResult>(id)
            .GetResultAsync();
    }

    private Task<CatalogRow[]> Catalog() =>
        db.WithWriteConnection(async c =>
            (
                await c.QueryAsync<CatalogRow>(
                    """
                    SELECT s.uuid source_uuid,t.uuid track_uuid,sh.uuid show_uuid,t.title
                    FROM sources s JOIN source_tracks t ON t.source_id=s.id JOIN shows sh ON sh.id=s.show_id
                    WHERE s.artist_id=@artistId AND NOT t.is_orphaned
                    """,
                    new { artistId }
                )
            ).ToArray()
        );

    public record Receipt(int Value);

    public class CatalogRow
    {
        public Guid source_uuid { get; set; }
        public Guid track_uuid { get; set; }
        public Guid show_uuid { get; set; }
        public string title { get; set; } = "";
    }

    private const string EmptyListing =
        """{"responseHeader":{"status":0},"response":{"numFound":0,"start":0,"docs":[]}}""";
    private const string Listing =
        """{"responseHeader":{"status":0},"response":{"numFound":1,"start":0,"docs":[{"identifier":"fixture-2000-01-01","date":"2000-01-01T00:00:00Z","addeddate":"2026-10-06T00:00:00Z","max_updated_at":"2026-10-06T00:00:00Z"}]}}""";

    private static string Metadata(string title) =>
        JsonConvert.SerializeObject(
            new
            {
                metadata = new
                {
                    identifier = "fixture-2000-01-01",
                    date = "2000-01-01",
                    title = "Fixture recording",
                },
                files = new[]
                {
                    new
                    {
                        name = "track.mp3",
                        title,
                        format = "VBR MP3",
                        length = "2:03",
                        md5 = "fixture",
                    },
                },
            }
        );
}
