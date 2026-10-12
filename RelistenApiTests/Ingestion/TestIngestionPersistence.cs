using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Relisten;
using Relisten.Api.Models;
using Relisten.Data;
using Relisten.Import;
using Relisten.Ingestion;
using Relisten.Ingestion.Providers.Archive;
using Relisten.Ingestion.Providers.Jerry;
using Relisten.Ingestion.Providers.Local;
using Relisten.Ingestion.Providers.PhantasyTour;
using Relisten.Ingestion.Providers.PhishIn;
using Relisten.Ingestion.Providers.PhishNet;
using Relisten.Ingestion.Providers.SetlistFm;
using Relisten.Ingestion.Workflows;
using Relisten.Services.Notifications;
using Temporalio.Client;

namespace RelistenApiTests.Ingestion;

/// <summary>Opt-in tests against the dedicated local-dev/temporal stack, never the application's configured database.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public class TestIngestionPersistence
{
    private readonly IngestionLogCapture logs = new();
    private readonly RecordingNotifier notifications = new();
    private IHost host = null!;
    private FixtureHttpServer upstream = null!;
    private DbService db = null!;
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
        builder.Logging.AddProvider(logs);
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Temporal:Address"] = "127.0.0.1:17233",
                ["Temporal:Namespace"] = testNamespace,
                ["Ingestion:Engine"] = "Temporal",
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
        builder
            .Services.AddHttpClient(nameof(IngestionHttpClientFactory))
            .ConfigurePrimaryHttpMessageHandler(() => new FixtureRedirectHandler(upstream.Address));
        SqlMapper.AddTypeHandler(new PersistentIdentifierHandler());
        SqlMapper.AddTypeHandler(new DateTimeHandler());
        builder.Services.AddSingleton<IDiscordWebhookNotifier>(notifications);
        host = builder.Build();
        db = host.Services.GetRequiredService<DbService>();
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
        upstream.BeforeResponse = null;
        logs.Entries.Clear();
        notifications.Messages.Clear();
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

    [TestCase(false)]
    [TestCase(true)]
    public async Task ArchiveMissingReviewTimestampsDoNotBlockOtherRecordings(bool temporal)
    {
        await Import();
        await db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                """
                INSERT INTO source_review_counts(source_id, source_review_max_updated_at, source_review_count)
                SELECT id, now(), 1 FROM sources WHERE artist_id = @artistId
                ON CONFLICT(source_id) DO UPDATE SET source_review_count = 1,
                    source_review_max_updated_at = now()
                """,
                new { artistId }
            )
        );

        var listing = JObject.Parse(Listing);
        var documents = (JArray)listing["response"]!["docs"]!;
        var nextRecording = (JObject)documents[0].DeepClone();
        nextRecording["identifier"] = "fixture-2000-01-02";
        nextRecording["date"] = "2000-01-02T00:00:00Z";
        documents.Add(nextRecording);
        listing["response"]!["numFound"] = 2;

        // This source is already in the catalog, but Archive supplies no usable
        // review freshness evidence. The second recording remains a new import.
        documents[0]["addeddate"] = null;
        documents[0]["publicdate"] = null;
        documents[0]["reviewdate"] = null;
        logs.Entries.Clear();
        upstream.Respond = uri =>
            uri.AbsolutePath.StartsWith("/metadata/")
                ? uri.Segments.Last() == "fixture-2000-01-02"
                    ? Metadata("New recording").Replace("2000-01-01", "2000-01-02")
                    : Metadata("Refreshed recording")
                : listing.ToString();

        var result = await RunArchive(temporal);

        (await Catalog())
            .Select(row => row.title)
            .Should()
            .BeEquivalentTo("Refreshed recording", "New recording");
        if (temporal)
        {
            result!.CompletedItems.Should().Be(2);
            result.FailedItems.Should().Be(0);
        }

        logs.Entries.Should()
            .Contain(entry =>
                entry.Event == ImportEvents.ArchiveItemSelected
                && Equals(entry.Fields["ItemIdentifier"], "fixture-2000-01-01")
                && Equals(entry.Fields["Reason"], "review_timestamp_missing")
            );
    }

    [TestCase("uuid")]
    [TestCase("slug")]
    [TestCase("numeric")]
    public async Task ImportUsesPrimaryArtistAndMappingsWhenReplicaHasNotCaughtUp(
        string identifierKind
    )
    {
        // A shadow read schema simulates replication lag without changing the
        // primary artist or its mapping. Every connection remains local to this fixture.
        var replicaSchema = "ingestion_replica_" + Guid.NewGuid().ToString("N");
        var readConnection = DbService.ReadOnlyConnStr;
        await db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                $"""
                CREATE SCHEMA {replicaSchema};
                CREATE VIEW {replicaSchema}.artists AS SELECT * FROM public.artists WHERE id <> {artistId};
                CREATE VIEW {replicaSchema}.artists_upstream_sources AS
                    SELECT * FROM public.artists_upstream_sources WHERE artist_id <> {artistId};
                """
            )
        );

        try
        {
            DbService.ReadOnlyConnStr = new Npgsql.NpgsqlConnectionStringBuilder(readConnection)
            {
                SearchPath = replicaSchema + ",public",
            }.ConnectionString;
            var identifier = identifierKind switch
            {
                "slug" => "fixture-" + artistUuid,
                "numeric" => artistId.ToString(),
                _ => artistUuid.ToString(),
            };
            using var scope = host.Services.CreateScope();
            var artists = scope.ServiceProvider.GetRequiredService<ArtistService>();

            (await artists.FindArtistWithIdOrSlug(identifier)).Should().BeNull();
            var importArtist = await artists.FindArtistForImport(identifier);
            importArtist.Should().NotBeNull();
            importArtist!.uuid.Should().Be(artistUuid);
            importArtist
                .upstream_sources.Should()
                .ContainSingle()
                .Which.upstream_source.Provider.Should()
                .Be(UpstreamProvider.ArchiveOrg);

            // Exercise the HTTP resolver, admission, artist preparation, and
            // provider activity context while the listener lookup still sees no artist.
            var controller =
                ActivatorUtilities.CreateInstance<Relisten.Controllers.ImportController>(
                    scope.ServiceProvider
                );
            var response = (Microsoft.AspNetCore.Mvc.ObjectResult)await controller.Post(identifier);
            response.StatusCode.Should().Be(201);
            var result = await host
                .Services.GetRequiredService<ITemporalClient>()
                .GetWorkflowHandle<ArtistRefreshWorkflow, ArtistImportResult>(
                    "ingest/artist/" + artistUuid
                )
                .GetResultAsync();

            result.CompletedItems.Should().Be(1);
            result.FailedItems.Should().Be(0);
            (await Catalog()).Should().ContainSingle();
        }
        finally
        {
            DbService.ReadOnlyConnStr = readConnection;
            await db.WithWriteConnection(connection =>
                connection.ExecuteAsync($"DROP SCHEMA {replicaSchema} CASCADE")
            );
        }
    }

    [TestCase(false, "missing_date")]
    [TestCase(true, "missing_date")]
    [TestCase(false, "dark")]
    [TestCase(true, "dark")]
    public async Task ArchiveMetadataSkipsHaveTheSameBehaviorAndDiagnostics(
        bool temporal,
        string reason
    )
    {
        var metadata = JObject.Parse(Metadata("Skipped"));
        if (reason == "dark")
            metadata["is_dark"] = true;
        else
            metadata["metadata"]!["date"] = null;
        upstream.Respond = uri =>
            uri.AbsolutePath.StartsWith("/metadata/") ? metadata.ToString() : Listing;

        var result = await RunArchive(temporal);

        (await Catalog()).Should().BeEmpty();
        if (temporal)
        {
            result!.CompletedItems.Should().Be(0);
            result.SkippedItems.Should().Be(1);
            result.Status.Should().Be(ImportStatus.Completed);
        }
        var skipped = logs.Entries.Single(entry => entry.Event == ImportEvents.ArchiveItemSkipped);
        skipped.Fields["Reason"].Should().Be(reason);
        skipped.Fields["ItemIdentifier"].Should().Be("fixture-2000-01-01");
        if (temporal)
        {
            skipped
                .Fields["ExecutionId"]
                .Should()
                .BeOfType<string>()
                .Which.Should()
                .NotBeNullOrEmpty();
            skipped.Fields["Provider"].Should().Be("archive.org");
            skipped
                .Fields["WorkflowId"]
                .Should()
                .BeOfType<string>()
                .Which.Should()
                .StartWith("ingest/artist/" + artistUuid);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ArchiveDateRepairAndSelectionReasonRemainVisible(bool temporal)
    {
        upstream.Respond = uri =>
            uri.AbsolutePath.StartsWith("/metadata/")
                ? Metadata("Remapped")
                    .Replace("2000-01-01", "2000-13-01")
                    .Replace("fixture-2000-13-01", "fixture-2000-01-01")
                : Listing;
        await RunArchive(temporal);

        var selected = logs.Entries.Single(entry =>
            entry.Event == ImportEvents.ArchiveItemSelected
        );
        selected.Fields["Reason"].Should().Be("new_source");
        var remapped = logs.Entries.Single(entry =>
            entry.Event == ImportEvents.ArchiveDateRemapped
        );
        remapped.Fields["DisplayDate"].Should().Be("2000-01-13");
        var source = await host
            .Services.GetRequiredService<SourceService>()
            .ForArtistFromPrimary(
                (await host.Services.GetRequiredService<ArtistService>().FindArtistById(artistId))!,
                await db.WithWriteConnection(c =>
                    c.QuerySingleAsync<long>(
                        "SELECT id FROM sources WHERE artist_id=@artistId",
                        new { artistId }
                    )
                )
            );
        source.display_date.Should().Be("2000-01-13");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ArchiveGuardPreservesSourcesAndAlertsOperators(bool temporal)
    {
        var listing = JObject.Parse(Listing);
        var template = listing["response"]!["docs"]![0]!;
        listing["response"]!["docs"] = new JArray(
            Enumerable
                .Range(0, 6)
                .Select(index =>
                {
                    var item = template.DeepClone();
                    item["identifier"] = "fixture-" + index;
                    return item;
                })
        );
        listing["response"]!["numFound"] = 6;
        upstream.Respond = uri =>
            uri.AbsolutePath.StartsWith("/metadata/")
                ? Metadata("Guarded").Replace("fixture-2000-01-01", uri.Segments.Last())
                : listing.ToString();
        await RunArchive(temporal);
        logs.Entries.Clear();
        upstream.Respond = _ => EmptyListing;

        await RunArchive(temporal);

        (await Catalog()).Should().HaveCount(6);
        var guard = logs.Entries.Single(entry => entry.Event == ImportEvents.ArchiveDeletionGuard);
        guard.Fields["SourceDeletionCount"].Should().Be(6);
        notifications.Messages.Should().ContainSingle();
    }

    private async Task<ArtistImportResult?> RunArchive(bool temporal)
    {
        if (temporal)
        {
            var result = await Import();
            result.FailedItems.Should().Be(0);
            return result;
        }

        using var scope = host.Services.CreateScope();
        var artist = (
            await scope.ServiceProvider.GetRequiredService<ArtistService>().FindArtistById(artistId)
        )!;
        var source = artist.upstream_sources.Single();
        var importer = source.upstream_source.importer!;
        importer.UseIngestionHttpClient(
            new HttpClient(new FixtureRedirectHandler(upstream.Address))
        );
        await importer.ImportDataForArtist(artist, source, null);
        return null;
    }

    private sealed class RecordingNotifier : IDiscordWebhookNotifier
    {
        internal List<string> Messages { get; } = new();

        public Task SendAsync(string message, CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
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
    public async Task LostItemAcknowledgementRefetchesAndConvergesOnTheExistingCatalogIdentity()
    {
        var metadataRequests = 0;
        var title = "First title";
        upstream.Respond = uri =>
        {
            if (!uri.AbsolutePath.StartsWith("/metadata/"))
                return Listing;
            Interlocked.Increment(ref metadataRequests);
            return Metadata(title);
        };
        var activities = host.Services.GetRequiredService<ArchiveActivities>();
        var environment = new Temporalio.Testing.ActivityEnvironment();
        var context = new ProviderWorkflowContext(
            new ArtistId(artistUuid),
            new IngestionExecution(Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.AddDays(7))
        );
        var item = new RequestedArchiveItem(new ArchiveItemId("fixture-2000-01-01"));

        // The first commit succeeds but its acknowledgement is treated as lost.
        // Retrying the same input must resolve catalog state and fetch metadata again.
        var first = await environment.RunAsync(() => activities.ImportItem(context, item));
        var before = await SourceBeforeRebuild();
        title = "Current upstream title";
        var repeated = await environment.RunAsync(() => activities.ImportItem(context, item));
        var after = await SourceBeforeRebuild();

        metadataRequests.Should().Be(2);
        first.Disposition.Should().Be(ItemDisposition.Applied);
        repeated.Source.Should().Be(first.Source);
        after.SourceId.Should().Be(before.SourceId);
        after.SourceUuid.Should().Be(before.SourceUuid);
        after.TrackUuid.Should().Be(before.TrackUuid);
        after.Title.Should().Be(title);
        var rebuild = await host
            .Services.GetRequiredService<TemporalIngestionClient>()
            .RebuildArtistCatalogAsync(new RebuildArtistCatalog(new ArtistId(artistUuid)));
        (await rebuild.GetResultAsync()).Status.Should().Be(ImportStatus.Completed);
        (await Catalog()).Should().ContainSingle().Which.title.Should().Be(title);
    }

    private Task<SourceIdentity> SourceBeforeRebuild() =>
        db.WithWriteConnection(connection =>
            connection.QuerySingleAsync<SourceIdentity>(
                """
                SELECT s.id "SourceId", s.uuid "SourceUuid", t.uuid "TrackUuid", t.title "Title"
                FROM sources s JOIN source_tracks t ON t.source_id=s.id
                WHERE s.artist_id=@artistId AND NOT t.is_orphaned
                """,
                new { artistId }
            )
        );

    private sealed record SourceIdentity(
        long SourceId,
        Guid SourceUuid,
        Guid TrackUuid,
        string Title
    );

    [Test]
    public async Task RepeatingVerifiedArchiveDeletionsNeverExpandsTheApprovedItemSet()
    {
        await Import();
        var activities = host.Services.GetRequiredService<ArchiveActivities>();
        var environment = new Temporalio.Testing.ActivityEnvironment();
        var context = new ProviderWorkflowContext(
            new ArtistId(artistUuid),
            new IngestionExecution(Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.AddDays(7))
        );
        upstream.Respond = _ => "{\"errcode\":104}";
        var plan = await environment.RunAsync(() =>
            activities.VerifyDeletionCandidates(context, new ArchiveSelection([], []), [])
        );
        plan.Disposition.Should().Be(ArchiveDeletionDisposition.Allowed);
        plan.Decisions.Should()
            .ContainSingle()
            .Which.Action.Should()
            .Be(ArchiveDeletionAction.Delete);

        const string laterItem = "fixture-added-after-verification";
        upstream.Respond = uri =>
            (
                uri.AbsolutePath.StartsWith("/metadata/") ? Metadata("Later recording") : Listing
            ).Replace("fixture-2000-01-01", laterItem);
        await environment.RunAsync(() =>
            activities.ImportItem(context, new RequestedArchiveItem(new ArchiveItemId(laterItem)))
        );
        var first = await environment.RunAsync(() =>
            activities.ApplyVerifiedDeletions(context, plan)
        );
        var repeated = await environment.RunAsync(() =>
            activities.ApplyVerifiedDeletions(context, plan)
        );

        first.AbsentIdentifiers.Should().Equal(new ArchiveItemId("fixture-2000-01-01"));
        repeated.Should().BeEquivalentTo(first);
        var retained = await db.WithWriteConnection(connection =>
            connection.QueryAsync<string>(
                "SELECT upstream_identifier FROM sources WHERE artist_id=@artistId",
                new { artistId }
            )
        );
        retained.Should().Equal(laterItem);
    }

    [Test]
    public async Task ArchiveWorkflowReimportPreservesSourceAndTrackIdentityAndUpdatesMetadata()
    {
        var first = await Import();
        first.FailedItems.Should().Be(0);
        first.CompletedItems.Should().Be(1);
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
        (await Import()).FailedItems.Should().Be(0);
        (await Catalog()).Should().ContainSingle();
        upstream.Respond = uri => uri.AbsolutePath.StartsWith("/metadata/") ? "{}" : EmptyListing;
        (await Import()).FailedItems.Should().Be(0);
        (await Catalog()).Should().ContainSingle();
        upstream.Respond = uri =>
            uri.AbsolutePath.StartsWith("/metadata/") ? "{\"errcode\":104}" : EmptyListing;
        (await Import()).FailedItems.Should().Be(0);
        (await Catalog()).Should().BeEmpty();
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

    [TestCase("phish.in", 2)]
    [TestCase("phish.net", 3)]
    [TestCase("setlist.fm", 6)]
    [TestCase("phantasytour.com", 7)]
    [TestCase("jerrygarcia.com", 8)]
    [TestCase("local", 9)]
    public async Task LostItemAcknowledgementPreservesProviderCatalogIdentities(
        string provider,
        int providerId
    )
    {
        if (provider == "phish.net")
            await Import();
        await ConfigureProvider(provider, providerId);
        upstream.Respond = uri => ProviderFixtures.Response(provider, uri);
        var environment = new Temporalio.Testing.ActivityEnvironment();
        var context = new ProviderWorkflowContext(
            new ArtistId(artistUuid),
            new IngestionExecution(Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.AddDays(7))
        );
        switch (provider)
        {
            case "phish.in":
            {
                var activities = host.Services.GetRequiredService<PhishInActivities>();
                var catalogs = await environment.RunAsync(() =>
                    activities.PrepareCatalogsAsync(context)
                );
                var page = await environment.RunAsync(() =>
                    activities.ReadShowPageAsync(
                        new ProviderRequest<RefreshPhishInArtist>(
                            new(context.Artist),
                            context.Execution
                        ),
                        null
                    )
                );
                await AssertRepeatedItem(
                    () =>
                        environment.RunAsync(() =>
                            activities.ImportShowAsync(context, catalogs, page.Items[0])
                        ),
                    result => (result.Source, result.Setlist)
                );
                break;
            }
            case "phish.net":
            {
                var activities = host.Services.GetRequiredService<PhishNetActivities>();
                var items = await environment.RunAsync(() =>
                    activities.SelectSourcesAsync(
                        new ProviderRequest<RefreshPhishNetArtist>(
                            new(context.Artist),
                            context.Execution
                        )
                    )
                );
                await AssertRepeatedItem(
                    () =>
                        environment.RunAsync(() =>
                            activities.EnrichSourceAsync(context, items.Single())
                        ),
                    result => result.Source
                );
                break;
            }
            case "setlist.fm":
            {
                var activities = host.Services.GetRequiredService<SetlistFmActivities>();
                var page = await environment.RunAsync(() =>
                    activities.ReadSetlistPageAsync(context, null)
                );
                await AssertRepeatedItem(
                    () =>
                        environment.RunAsync(() =>
                            activities.ImportSetlistAsync(context, page.Items[0])
                        ),
                    result => result.Setlist
                );
                break;
            }
            case "phantasytour.com":
            {
                var activities = host.Services.GetRequiredService<PhantasyTourActivities>();
                var page = await environment.RunAsync(() =>
                    activities.ReadShowPageAsync(context, null)
                );
                await AssertRepeatedItem(
                    () =>
                        environment.RunAsync(() =>
                            activities.ImportShowAsync(context, page.Items[0])
                        ),
                    result => result.Setlist
                );
                break;
            }
            case "jerrygarcia.com":
            {
                var activities = host.Services.GetRequiredService<JerryActivities>();
                var selection = await environment.RunAsync(() =>
                    activities.SelectMissingPagesAsync(context)
                );
                await AssertRepeatedItem(
                    () =>
                        environment.RunAsync(() =>
                            activities.ImportPageAsync(context, selection.Items[0])
                        ),
                    result => result.Setlist
                );
                break;
            }
            case "local":
            {
                var activities = host.Services.GetRequiredService<LocalActivities>();
                var root = await environment.RunAsync(() =>
                    activities.FetchRootAndPrepareVenues(context)
                );
                var selection = await environment.RunAsync(() =>
                    activities.SelectChangedDates(context, root)
                );
                await AssertRepeatedItem(
                    () =>
                        environment.RunAsync(() =>
                            activities.ImportDate(context, selection.Items.Single())
                        ),
                    result => result.Source
                );
                break;
            }
        }
    }

    private async Task AssertRepeatedItem<TResult, TIdentity>(
        Func<Task<TResult>> apply,
        Func<TResult, TIdentity> identity
    )
    {
        // Run the same selected item again after a successful commit whose result
        // could have been lost. Do not repeat discovery, which may now omit it.
        var first = await apply();
        var before = await Snapshot();
        var identities = await CatalogIdentities();
        identities.Should().NotBeEmpty();
        var repeated = await apply();

        identity(repeated).Should().Be(identity(first));
        (await CatalogIdentities()).Should().Equal(identities);
        (await Snapshot()).Should().BeEquivalentTo(before);
    }

    private Task<string[]> CatalogIdentities() =>
        db.WithWriteConnection(async connection =>
            (
                await connection.QueryAsync<string>(
                    """
                    SELECT 'source:' || id || ':' || uuid FROM sources WHERE artist_id=@artistId
                    UNION ALL SELECT 'track:' || id || ':' || uuid FROM source_tracks WHERE artist_id=@artistId
                    UNION ALL SELECT 'setlist:' || id FROM setlist_shows WHERE artist_id=@artistId
                    UNION ALL SELECT 'play:' || played_setlist_show_id || ':' || played_setlist_song_id FROM setlist_songs_plays WHERE played_setlist_show_id IN
                        (SELECT id FROM setlist_shows WHERE artist_id=@artistId)
                    ORDER BY 1
                    """,
                    new { artistId }
                )
            ).ToArray()
        );

    [TestCase(false)]
    [TestCase(true)]
    public async Task PhishNetRetriesWhenConcurrentCatalogChangesInvalidateFetchedInputs(
        bool changeDate
    )
    {
        await Import();
        await ConfigureProvider("phish.net", 3);
        upstream.Respond = uri => ProviderFixtures.Response("phish.net", uri);
        var activities = host.Services.GetRequiredService<PhishNetActivities>();
        var environment = new Temporalio.Testing.ActivityEnvironment();
        var context = new ProviderWorkflowContext(
            new ArtistId(artistUuid),
            new IngestionExecution(Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.AddDays(7))
        );
        var items = await environment.RunAsync(() =>
            activities.SelectSourcesAsync(
                new ProviderRequest<RefreshPhishNetArtist>(new(context.Artist), context.Execution)
            )
        );
        await db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                "UPDATE sources SET num_reviews=@reviews WHERE artist_id=@artistId",
                new { artistId, reviews = changeDate ? 0 : 1 }
            )
        );
        var ratingsStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseRatings = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        upstream.BeforeResponse = async uri =>
        {
            if (!uri.AbsolutePath.StartsWith("/setlists/"))
                return;
            ratingsStarted.TrySetResult();
            await releaseRatings.Task;
        };
        var attempt = environment.RunAsync(() =>
            activities.EnrichSourceAsync(context, items.Single())
        );
        try
        {
            await ratingsStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await db.WithWriteConnection(connection =>
                connection.ExecuteAsync(
                    """
                    UPDATE sources SET taper_notes='Concurrent recording edit',
                        display_date=@date, num_reviews=@reviews WHERE artist_id=@artistId
                    """,
                    new
                    {
                        artistId,
                        date = changeDate ? "2000-01-02" : "2000-01-01",
                        reviews = changeDate ? 0 : 2,
                    }
                )
            );
        }
        finally
        {
            releaseRatings.TrySetResult();
        }
        Func<Task> failedAttempt = async () => await attempt;
        var failure = await failedAttempt
            .Should()
            .ThrowAsync<Temporalio.Exceptions.ApplicationFailureException>();
        failure.Which.ErrorType.Should().Be("EnrichmentInputsChanged");
        failure.Which.NonRetryable.Should().BeFalse();
        var beforeRetry = await Snapshot();
        beforeRetry["reviews"].Should().BeEmpty("the stale enrichment must roll back");
        upstream.BeforeResponse = null;

        var result = await environment.RunAsync(() =>
            activities.EnrichSourceAsync(context, items.Single())
        );
        result.Source.Should().Be(items.Single().Id);
        var source = await db.WithWriteConnection(connection =>
            connection.QuerySingleAsync<(string Notes, string Date, int Reviews)>(
                "SELECT taper_notes, display_date, num_reviews FROM sources WHERE artist_id=@artistId",
                new { artistId }
            )
        );
        source.Notes.Should().Be("Concurrent recording edit");
        source.Date.Should().Be(changeDate ? "2000-01-02" : "2000-01-01");
        source.Reviews.Should().Be(1);
        (await Snapshot())["reviews"].Should().ContainSingle();
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
        result.CompletedItems.Should().Be(0);
        result.Status.Should().Be(ImportStatus.Completed);
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
        result.CompletedItems.Should().Be(2);
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
        for (var i = 0; i < 2; i++)
        {
            var handle = await host
                .Services.GetRequiredService<TemporalIngestionClient>()
                .DiscoverArchiveArtistsAsync(new DiscoverArchiveArtists());
            (await handle.GetResultAsync()).Should().Be(1);
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
            .GetWorkflowHandle<ArtistRefreshWorkflow, ArtistImportResult>(
                "ingest/artist/" + artistUuid
            )
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
        var result = await Import(item, year);
        result.FailedItems.Should().Be(item == null ? 0 : 1);
        (await Catalog()).Should().ContainSingle();
    }

    private async Task<ArtistImportResult> Import(
        string? item = null,
        int? year = null,
        bool backfill = false
    )
    {
        var client = host.Services.GetRequiredService<TemporalIngestionClient>();
        var artist = new ArtistId(artistUuid);
        var handle =
            backfill ? await client.Jerry.RepairVenuesAsync(new RepairJerryVenues(artist))
            : item != null
                ? await client.Archive.ImportItemAsync(
                    new ImportArchiveItem(artist, new ArchiveItemId(item))
                )
            : year.HasValue
                ? await client.Archive.RefreshAsync(new RefreshArchiveArtist(artist, year))
            : await client.RefreshArtistAsync(new RefreshArtist(artist));
        return await handle.GetResultAsync();
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
