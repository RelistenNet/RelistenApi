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
    private IngestionStore store = null!;
    private string requestId = null!;
    private IngestionRun Run => new(requestId, DateTimeOffset.UtcNow.AddDays(7));
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
        var testNamespace = "ingestion-tests-" + Guid.NewGuid().ToString("N");
        var admin = await TemporalClient.ConnectAsync(
            new TemporalClientConnectOptions("127.0.0.1:17233")
        );
        await admin.WorkflowService.RegisterNamespaceAsync(
            new Temporalio.Api.WorkflowService.V1.RegisterNamespaceRequest
            {
                Namespace = testNamespace,
                WorkflowExecutionRetentionPeriod =
                    Google.Protobuf.WellKnownTypes.Duration.FromTimeSpan(TimeSpan.FromDays(1)),
            }
        );
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Temporal:Address"] = "127.0.0.1:17233",
                ["Temporal:Namespace"] = testNamespace,
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
        store = new IngestionStore(db);
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
        upstream.Redirects.Clear();
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
        var first = await store.CommitAsync(Run, "apply", Increment);
        var second = await store.CommitAsync(Run, "apply", Increment);
        second.Should().BeEquivalentTo(first);
        (await Featured()).Should().Be(1);
    }

    [Test]
    public async Task FailedCommitRollsBackCatalogAndReceiptBeforeRetry()
    {
        Func<Task> fail = async () =>
            await store.CommitAsync<Receipt>(
                Run,
                "apply",
                async () =>
                {
                    await Increment();
                    throw new InvalidDataException("Interrupted before commit");
                }
            );
        await fail.Should().ThrowAsync<InvalidDataException>();
        (await Featured()).Should().Be(0);
        (await store.ReadReceiptAsync<Receipt>(Run, "apply")).Should().BeNull();
        (await store.CommitAsync(Run, "apply", Increment)).Value.Should().Be(1);
    }

    [Test]
    public async Task CancellationBeforeCommitRollsBackCatalogChangesAndReceipt()
    {
        using var cancellation = new CancellationTokenSource();
        Func<Task> attempt = async () =>
            await store.CommitAsync(
                Run,
                "cancelled",
                async () =>
                {
                    var result = await Increment();
                    cancellation.Cancel();
                    return result;
                },
                cancellation.Token
            );

        await attempt.Should().ThrowAsync<OperationCanceledException>();
        (await Featured()).Should().Be(0);
        (await store.ReadReceiptAsync<Receipt>(Run, "cancelled")).Should().BeNull();
    }

    [Test]
    public async Task ItemRetryRefetchesMetadataInsteadOfReusingTheFailedResponse()
    {
        var attempts = 0;
        upstream.Respond = uri =>
        {
            if (!uri.AbsolutePath.StartsWith("/metadata/"))
            {
                return Listing;
            }

            return Interlocked.Increment(ref attempts) == 1 ? "{}" : Metadata("Recovered title");
        };

        var result = await Import();
        result.FailedItems.Should().Be(0);
        attempts.Should().Be(2);
        (await Catalog()).Should().ContainSingle().Which.title.Should().Be("Recovered title");
    }

    [Test]
    public async Task RepeatedItemActivityReturnsItsReceiptWithoutRefetchingOrChangingCounts()
    {
        using var scope = host.Services.CreateScope();
        var activities = ActivatorUtilities.CreateInstance<IngestionActivities>(
            scope.ServiceProvider
        );
        var environment = new Temporalio.Testing.ActivityEnvironment();
        var request = new ArtistImportRequest(artistUuid.ToString(), requestId, Run: Run);
        var page = await environment.RunAsync(() =>
            activities.DiscoverItems(request, UpstreamProvider.ArchiveOrg, null)
        );
        var first = await environment.RunAsync(() =>
            activities.ImportItem(request, UpstreamProvider.ArchiveOrg, page.Items.Single())
        );
        var requests = upstream.Requests;
        upstream.Respond = _ => "{}";
        var repeated = await environment.RunAsync(() =>
            activities.ImportItem(request, UpstreamProvider.ArchiveOrg, page.Items.Single())
        );

        repeated.Should().BeEquivalentTo(first);
        upstream.Requests.Should().Be(requests);
        await environment.RunAsync(() =>
            activities.FinishArtist(request, new ImportResult(requestId, first.Stats, 0))
        );
        (await Catalog()).Should().ContainSingle().Which.title.Should().Be("First title");
    }

    [Test]
    public async Task OverlappingAttemptsCommitOnlyOnceWhenTheConflictIsRetried()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = store.CommitAsync(
            Run,
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
            await store.CommitAsync(Run, "apply", Increment);
        }
        catch (Exception error) when (HasSerializationConflict(error)) { }
        await first;
        await store.CommitAsync(Run, "apply", Increment);
        (await Featured()).Should().Be(1);
    }

    [Test]
    public async Task SnapshotRetryKeepsTheFirstDiscoverySelection()
    {
        var first = new DiscoverySnapshot([new("a", "first")], ObservedIdentifiers: ["a"]);
        await store.SnapshotAsync(Run, "page", () => Task.FromResult(first));
        var retry = await store.SnapshotAsync(
            Run,
            "page",
            () =>
                Task.FromResult(
                    new DiscoverySnapshot([new("b", "later")], ObservedIdentifiers: ["b"])
                )
        );
        retry.Should().BeEquivalentTo(first);
        (await store.ReadItemAsync(Run, new ImportItem("a", "page", 0)))
            .Metadata.Should()
            .Be("first");
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
    public async Task ExpiredWorkCannotRecreateCleanedUpInputsOrRepeatCatalogWrites()
    {
        await store.SnapshotAsync(Run, "page", () => Task.FromResult(new Receipt(1)));
        await store.CommitAsync(Run, "apply", Increment);
        await db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                """
                UPDATE ingestion_snapshots SET expires_at=now()-interval '1 day' WHERE request_id=@requestId;
                UPDATE ingestion_receipts SET expires_at=now()-interval '1 day' WHERE request_id=@requestId;
                """,
                new { requestId }
            )
        );
        await store.DeleteExpiredAsync();
        var expired = new IngestionRun(requestId, DateTimeOffset.UtcNow.AddDays(-2));
        Func<Task> fetch = async () =>
            await store.SnapshotAsync(expired, "page", () => Task.FromResult(new Receipt(2)));
        Func<Task> apply = async () => await store.CommitAsync(expired, "apply", Increment);
        await fetch.Should().ThrowAsync<Temporalio.Exceptions.ApplicationFailureException>();
        await apply.Should().ThrowAsync<Temporalio.Exceptions.ApplicationFailureException>();
        (await Featured()).Should().Be(1);
        (await store.ReadSnapshotAsync<Receipt>(Run, "page")).Should().BeNull();
        (await store.ReadReceiptAsync<Receipt>(Run, "apply")).Should().BeNull();
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
        if (provider == "phish.net")
            upstream.Redirects["/setlists/"] = "/setlists/fixture-canonical.html";
        if (provider != "archive.org")
            upstream.Respond = uri => ProviderFixtures.Response(provider, uri);
        Dictionary<string, string[]> expected;
        using (var scope = host.Services.CreateScope())
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
        }
        expected["sources"]
            .Concat(expected["setlists"])
            .Should()
            .NotBeEmpty("the fixture must exercise catalog writes");
        // Rebuild SQL commits its own transactions, so an ambient test rollback cannot
        // provide a clean baseline. Clear catalog rows while preserving the artist identity.
        await ClearArtistCatalog();
        (await Snapshot()).Values.Should().OnlyContain(rows => rows.Length == 0);

        if (provider == "phish.net")
        {
            await ConfigureProvider("archive.org", 1);
            upstream.Respond = uri =>
                uri.AbsolutePath.StartsWith("/metadata/") ? Metadata("First title") : Listing;
            await Import();
            await ConfigureProvider(provider, providerId);
            upstream.Respond = uri => ProviderFixtures.Response(provider, uri);
        }

        var result = await Import();
        result.FailedItems.Should().Be(0);
        (await Snapshot()).Should().BeEquivalentTo(expected);
    }

    private Task ClearArtistCatalog() =>
        db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                """
                DELETE FROM setlist_songs_plays WHERE played_setlist_show_id IN
                    (SELECT id FROM setlist_shows WHERE artist_id=@artistId);
                DELETE FROM source_tracks WHERE artist_id=@artistId;
                DELETE FROM sources WHERE artist_id=@artistId;
                DELETE FROM shows WHERE artist_id=@artistId;
                DELETE FROM setlist_shows WHERE artist_id=@artistId;
                DELETE FROM setlist_songs WHERE artist_id=@artistId;
                DELETE FROM tours WHERE artist_id=@artistId;
                DELETE FROM venues WHERE artist_id=@artistId;
                DELETE FROM eras WHERE artist_id=@artistId;
                DELETE FROM years WHERE artist_id=@artistId;
                """,
                new { artistId }
            )
        );

    [Test]
    public async Task PhantasyTourRepairsExistingLocalDatesAndTourBoundsWithoutFetchingShowDetails()
    {
        await ConfigureProvider("phantasytour.com", 7);
        upstream.Respond = uri => ProviderFixtures.Response("phantasytour.com", uri);
        await Import();
        var expected = await Snapshot();
        await CorruptPhantasyDates();
        var detailRequests = 0;
        upstream.Respond = uri =>
        {
            if (uri.AbsolutePath.Contains("/shows/"))
            {
                Interlocked.Increment(ref detailRequests);
            }

            return ProviderFixtures.Response("phantasytour.com", uri);
        };

        var result = await Import();
        result.Stats.Updated.Should().Be(2);
        detailRequests.Should().Be(0);
        (await Snapshot()).Should().BeEquivalentTo(expected);
    }

    [Test]
    public async Task PhantasyTourDoesNotRepairDatesUntilEveryListingPageSucceeds()
    {
        await ConfigureProvider("phantasytour.com", 7);
        upstream.Respond = uri => ProviderFixtures.Response("phantasytour.com", uri);
        await Import();
        await CorruptPhantasyDates();
        var before = await Snapshot();
        upstream.Respond = uri =>
            uri.Query.Contains("page=2")
                ? "{}"
                : JsonConvert.SerializeObject(
                    Enumerable
                        .Range(0, 100)
                        .Select(_ => new { id = 1, dateTime = "2000-01-01T20:00:00-05:00" })
                );

        Func<Task> import = async () => await Import();
        await import.Should().ThrowAsync<Temporalio.Exceptions.WorkflowFailedException>();
        (await Snapshot()).Should().BeEquivalentTo(before);
    }

    private Task CorruptPhantasyDates() =>
        db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                """
                UPDATE setlist_shows SET date=date + 1 WHERE artist_id=@artistId;
                UPDATE tours SET start_date='1990-01-01', end_date='1990-01-02' WHERE artist_id=@artistId;
                """,
                new { artistId }
            )
        );

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
    public async Task LargeDiscoveryReturnsBoundedPagesWithoutRepeatingTheUpstreamListing()
    {
        var items = Enumerable
            .Range(0, 18386)
            .Select(index => new DiscoveredItem("item-" + index, "{}"))
            .ToArray();
        await store.SnapshotAsync(
            Run,
            "archive.org/discovery/first",
            () => Task.FromResult(new DiscoverySnapshot(items))
        );
        using var scope = host.Services.CreateScope();
        var activities = ActivatorUtilities.CreateInstance<IngestionActivities>(
            scope.ServiceProvider
        );
        var request = new ArtistImportRequest(artistUuid.ToString(), requestId, Run: Run);
        DiscoveryCursor? cursor = null;
        var identifiers = new List<string>();
        var requests = upstream.Requests;
        do
        {
            var page = await new Temporalio.Testing.ActivityEnvironment().RunAsync(() =>
                activities.DiscoverItems(request, UpstreamProvider.ArchiveOrg, cursor)
            );
            page.Items.Length.Should().BeInRange(1, 100);
            identifiers.AddRange(page.Items.Select(item => item.Identifier));
            cursor = page.NextCursor;
        } while (cursor != null);

        identifiers.Should().Equal(items.Select(item => item.Identifier));
        upstream.Requests.Should().Be(requests);
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
