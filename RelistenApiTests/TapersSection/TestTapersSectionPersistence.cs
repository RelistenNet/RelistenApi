using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Relisten;
using Relisten.Api.Models;
using Relisten.Api.Models.Api;
using Relisten.Controllers;
using Relisten.Data;
using Relisten.Import;
using Relisten.Ingestion;
using Relisten.Ingestion.Providers.TapersSection;
using RelistenApiTests.Ingestion;
using Temporalio.Testing;

namespace RelistenApiTests.TapersSection;

/// <summary>
/// Exercises retained intake state, Temporal orchestration and listener contracts against a
/// disposable catalog. Provider traffic stays on loopback; no real catalog is imported.
/// </summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public class TestTapersSectionPersistence
{
    private IHost host = null!;
    private WorkflowEnvironment? temporal;
    private FixtureHttpServer upstream = null!;
    private DbService db = null!;
    private string originalWrite = null!;
    private string originalRead = null!;
    private string performer = null!;
    private readonly IngestionLogCapture logs = new();
    private readonly Dictionary<string, JObject> recordings = new();

    [OneTimeSetUp]
    public async Task Start()
    {
        if (Environment.GetEnvironmentVariable("RELISTEN_LOCAL_INGESTION_TESTS") != "1")
        {
            Assert.Ignore("Run ./test-local.sh to include local integration tests.");
        }

        originalWrite = DbService.ConnStr;
        originalRead = DbService.ReadOnlyConnStr;
        upstream = new FixtureHttpServer();
        temporal = await WorkflowEnvironment.StartLocalAsync();
        var testNamespace = "taperssection-tests-" + Guid.NewGuid().ToString("N");
        await temporal.Client.WorkflowService.RegisterNamespaceAsync(
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
                ["Temporal:Address"] = temporal.Client.Connection.Options.TargetHost,
                ["Temporal:Namespace"] = testNamespace,
                ["Ingestion:Engine"] = "Temporal",
            }
        );
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
        builder.Services.AddSingleton<Hangfire.IBackgroundJobClient, IgnoredPopularityJobs>();
        builder
            .Services.AddRelistenCatalogServices()
            .AddTemporalIngestionWorkers(builder.Configuration);
        builder
            .Services.AddHttpClient(nameof(IngestionHttpClientFactory))
            .ConfigurePrimaryHttpMessageHandler(() => new FixtureRedirectHandler(upstream.Address));
        SqlMapper.AddTypeHandler(new PersistentIdentifierHandler());
        SqlMapper.AddTypeHandler(new DateTimeHandler());
        host = builder.Build();
        db = host.Services.GetRequiredService<DbService>();
        await host.StartAsync();
        await db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                "INSERT INTO upstream_sources(id,name,url) VALUES(1,'archive.org','https://archive.org') ON CONFLICT DO NOTHING"
            )
        );
    }

    [SetUp]
    public async Task ResetIntake()
    {
        // All integration fixtures are nonparallel. Clear only this feature's durable work;
        // unique performer names keep catalog rows from previous cases independent.
        await db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                "TRUNCATE normalization.archive_item_intake, normalization.archive_item_observations, normalization.archive_artist_candidates CASCADE"
            )
        );
        performer = "Intake Fixture " + Guid.NewGuid().ToString("N");
        recordings.Clear();
        logs.Entries.Clear();
        upstream.BeforeResponse = null;
        upstream.Respond = uri =>
            uri.AbsolutePath.StartsWith("/metadata/")
                ? recordings[Uri.UnescapeDataString(uri.Segments.Last())].ToString()
                : JsonConvert.SerializeObject(
                    new
                    {
                        total = recordings.Count,
                        items = recordings.Keys.Select(identifier => new { identifier }),
                    }
                );
    }

    [OneTimeTearDown]
    public async Task Stop()
    {
        if (host != null)
        {
            await host.StopAsync();
            host.Services.GetRequiredService<RedisService>().connection.Dispose();
            host.Dispose();
        }

        if (temporal != null)
        {
            await temporal.DisposeAsync();
        }

        upstream?.Dispose();
        if (originalWrite != null)
        {
            DbService.ConnStr = originalWrite;
            DbService.ReadOnlyConnStr = originalRead;
        }
    }

    [Test]
    public async Task ApprovedNewArtistPublishesTwoRecordingsAndLaterItemsWithoutAnotherReview()
    {
        var firstItem = AddRecording("first", "2000-01-01");
        AddRecording("second", "2001-01-02");
        var discovery = await Discover();
        discovery.Observed.Should().Be(2);
        (await ArtistsNamedPerformer()).Should().BeEmpty();

        var candidate = await Approve();
        var first = await Refresh();
        first.Published.Should().Be(2);
        first.Failed.Should().Be(0);
        var artist = (await ArtistsNamedPerformer()).Single();
        artist
            .upstream_sources.Should()
            .BeEmpty("mixed collection membership is not an artist mapping");
        var initial = await ListenerCatalog(artist, ["2000-01-01", "2001-01-02"]);
        await AssertDefaultListContains(artist.uuid);
        (await NormalizedArtist(artist.uuid))["shows"]!.Count().Should().Be(2);
        (await host.Services.GetRequiredService<YearService>().AllForArtist(artist))
            .Select(year => year.year)
            .Should()
            .BeEquivalentTo("2000", "2001");

        AddRecording("third", "2002-01-03");
        await Discover();
        var later = await Refresh();
        later.Published.Should().Be(1);
        later.Failed.Should().Be(0);
        (await ArtistsNamedPerformer())
            .Should()
            .ContainSingle()
            .Which.uuid.Should()
            .Be(artist.uuid);
        var expanded = await ListenerCatalog(artist, ["2000-01-01", "2001-01-02", "2002-01-03"]);
        expanded.Take(2).Should().BeEquivalentTo(initial);
        (await NormalizedArtist(artist.uuid))["shows"]!
            .Count()
            .Should()
            .Be(
                3,
                "rebuild must invalidate the cached normalized artist after a later recording arrives"
            );

        var repeated = await Refresh();
        repeated.Unchanged.Should().Be(3);
        (await ListenerCatalog(artist, ["2000-01-01", "2001-01-02", "2002-01-03"]))
            .Should()
            .BeEquivalentTo(expanded);
        (
            await db.WithWriteConnection(connection =>
                connection.QuerySingleAsync<int>(
                    "SELECT count(*) FROM normalization.archive_item_observations"
                )
            )
        )
            .Should()
            .Be(3, "identical evidence is retained once even after activity retries");
        (
            await db.WithWriteConnection(connection =>
                connection.QuerySingleAsync<bool>(
                    "SELECT approved FROM normalization.archive_artist_candidates WHERE candidate_id=@candidate",
                    new { candidate }
                )
            )
        )
            .Should()
            .BeTrue();

        var status = JObject.FromObject(
            await host.Services.GetRequiredService<ArchiveIntakeService>().StatusAsync(firstItem)
        );
        var itemStatus = status["items"]!.Single();
        itemStatus["item_identifier"]!.Value<string>().Should().Be(firstItem);
        itemStatus["approved"]!.Value<bool>().Should().BeTrue();
        itemStatus["published_source_uuid"]!.Value<Guid>().Should().Be(initial[0].Source);
        itemStatus["rebuilt_revision"]!
            .Value<long>()
            .Should()
            .Be(itemStatus["catalog_revision"]!.Value<long>());
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ConflictingAssignmentRetainsPublishedPlaybackAndCanResumeTheSameAssignment(
        bool changeCreator
    )
    {
        var item = AddRecording("conflict", "2000-01-01");
        await Discover();
        await Approve();
        await Refresh();
        var artist = (await ArtistsNamedPerformer()).Single();
        var before = await ListenerCatalog(artist, ["2000-01-01"]);

        if (changeCreator)
        {
            var other = performer + " Other";
            await host
                .Services.GetRequiredService<ArtistService>()
                .Save(
                    new SlimArtistWithFeatures
                    {
                        name = other,
                        slug = SlugUtils.Slugify(other),
                        features = new Features(),
                    }
                );
            recordings[item]["metadata"]!["creator"] = other;
        }
        else
        {
            recordings[item]["metadata"]!["date"] = "2000-01-02";
        }
        var held = await Refresh();
        held.NeedsReview.Should().Be(1);
        held.Deferred.Should()
            .Be(
                0,
                "a conflicting published assignment requires review rather than a new-artist deferral"
            );
        var status = JObject.FromObject(
            await host.Services.GetRequiredService<ArchiveIntakeService>().StatusAsync(item)
        );
        status["items"]!.Single()["reason"]!
            .Value<string>()
            .Should()
            .Be("published_assignment_conflict");
        (await ListenerCatalog(artist, ["2000-01-01"])).Should().BeEquivalentTo(before);
        (
            await host
                .Services.GetRequiredService<ShowService>()
                .ShowWithSourcesForArtistOnDate(artist, "2000-01-02")
        )
            .Should()
            .BeNull();
        var state = await Publication(item);
        state.LatestObservationId.Should().NotBe(state.PublishedObservationId);
        state.PublishedDisplayDate.Should().Be("2000-01-01");

        recordings[item]["metadata"]!["date"] = "2000-01-01";
        recordings[item]["metadata"]!["creator"] = performer;
        recordings[item]["metadata"]!["description"] = "Corrected recording notes";
        var resumed = await Refresh();
        resumed.Published.Should().Be(1);
        var after = await ListenerCatalog(artist, ["2000-01-01"]);
        after.Select(row => row.Source).Should().Equal(before.Select(row => row.Source));
        after.SelectMany(row => row.Tracks).Should().Equal(before.SelectMany(row => row.Tracks));
    }

    [Test]
    public async Task UnavailableMetadataRetainsEvidenceWithoutParsingInvalidReviewsOrRemovingPlayback()
    {
        var item = AddRecording("dark-item", "2000-01-01");
        await Discover();
        await Approve();
        await Refresh();
        var artist = (await ArtistsNamedPerformer()).Single();
        var before = await ListenerCatalog(artist, ["2000-01-01"]);
        var published = await Publication(item);

        recordings[item]["is_dark"] = true;
        recordings[item]["reviews"] = new JArray(new JObject { ["reviewdate"] = "not a date" });
        var result = await Refresh();

        result.Unavailable.Should().Be(1);
        result.Failed.Should().Be(0);
        var retained = await Publication(item);
        retained.PublishedObservationId.Should().Be(published.PublishedObservationId);
        retained.LatestObservationId.Should().Be(published.LatestObservationId);
        (await ListenerCatalog(artist, ["2000-01-01"])).Should().BeEquivalentTo(before);
        (
            await db.WithWriteConnection(connection =>
                connection.QuerySingleAsync<int>(
                    "SELECT count(*) FROM normalization.archive_item_observations WHERE item_identifier=@item",
                    new { item }
                )
            )
        )
            .Should()
            .Be(2, "availability evidence is retained separately from the last valid content");
    }

    [Test]
    public async Task ReviewOnlyUpdatesReachListenersWithoutDuplicatingAttributionEvidence()
    {
        var item = AddRecording("review-update", "2000-01-01");
        JObject Review(string body) =>
            new()
            {
                ["stars"] = 5,
                ["reviewtitle"] = "Fixture listener review",
                ["reviewer"] = "Fixture listener",
                ["reviewbody"] = body,
                ["reviewdate"] = "2026-10-01T12:00:00Z",
            };
        recordings[item]["reviews"] = new JArray(Review("Earlier review"));
        await Discover();
        await Approve();
        await Refresh();
        var artist = (await ArtistsNamedPerformer()).Single();
        var source = (
            await host.Services.GetRequiredService<SourceService>().AllForArtistFromPrimary(artist)
        ).Single();
        var reviews = host.Services.GetRequiredService<SourceReviewService>();
        var original = (await reviews.AllForSources([source.id])).Single();

        recordings[item]["reviews"] = new JArray(Review("Corrected review"));
        (await Refresh()).Failed.Should().Be(0);

        var revised = (await reviews.AllForSources([source.id])).Should().ContainSingle().Which;
        revised.review.Should().Be("Corrected review");
        revised.uuid.Should().Be(original.uuid);
        (
            await db.WithWriteConnection(connection =>
                connection.QuerySingleAsync<int>(
                    "SELECT count(*) FROM normalization.archive_item_observations WHERE item_identifier=@item",
                    new { item }
                )
            )
        )
            .Should()
            .Be(1, "reviews do not supply performer or date evidence");
        await ListenerCatalog(artist, ["2000-01-01"]);
    }

    [Test]
    public async Task ExistingArtistCannotBecomeANewCohortArtistEvenAfterBulkApproval()
    {
        var existingUuid = Guid.NewGuid();
        await db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                "INSERT INTO artists(name,slug,uuid,updated_at) VALUES(@performer,@slug,@existingUuid,now())",
                new
                {
                    performer,
                    slug = "existing-" + existingUuid,
                    existingUuid,
                }
            )
        );
        AddRecording("existing", "2000-01-01");
        await Discover();
        await Approve();

        await Refresh();

        (await ArtistsNamedPerformer())
            .Should()
            .ContainSingle()
            .Which.uuid.Should()
            .Be(existingUuid);
        (
            await db.WithWriteConnection(connection =>
                connection.QuerySingleAsync<int>(
                    "SELECT count(*) FROM sources s JOIN artists a ON a.id=s.artist_id WHERE a.uuid=@existingUuid",
                    new { existingUuid }
                )
            )
        )
            .Should()
            .Be(0);
        (
            await db.WithWriteConnection(connection =>
                connection.QuerySingleAsync<string>(
                    "SELECT status FROM normalization.archive_item_intake"
                )
            )
        )
            .Should()
            .Be("deferred_existing_artist");
    }

    [Test]
    public async Task PausedArtistRecoversCommittedCatalogWithoutImportingOrBecomingVisible()
    {
        var item = AddRecording("recovery", "2000-01-01");
        await Discover();
        var candidate = await Approve();
        using var scope = host.Services.CreateScope();
        var intake = scope.ServiceProvider.GetRequiredService<ArchiveIntakeService>();
        await intake.PrepareCohortAsync(CancellationToken.None);
        var artist = (await ArtistsNamedPerformer()).Single();
        await db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                """
                CREATE FUNCTION normalization.reject_fixture_rebuild() RETURNS trigger
                LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'fixture rebuild failure'; END $$;
                CREATE TRIGGER reject_fixture_rebuild BEFORE INSERT OR UPDATE ON shows
                FOR EACH ROW EXECUTE FUNCTION normalization.reject_fixture_rebuild();
                """
            )
        );
        try
        {
            // Source publication commits first. Rebuild failure must leave durable repair
            // work for a new workflow execution after the original execution has failed.
            (await Refresh())
                .Failed.Should()
                .Be(1);
            var committed = await Publication(item);
            committed.CatalogRevision.Should().BeGreaterThan(committed.RebuiltRevision);
        }
        finally
        {
            await db.WithWriteConnection(connection =>
                connection.ExecuteAsync(
                    "DROP TRIGGER reject_fixture_rebuild ON shows; DROP FUNCTION normalization.reject_fixture_rebuild()"
                )
            );
        }

        await db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                "UPDATE normalization.archive_artist_candidates SET approved=false WHERE candidate_id=@candidate",
                new { candidate }
            )
        );
        var requests = upstream.Requests;

        var recovered = await Refresh();

        recovered.Published.Should().Be(0);
        upstream
            .Requests.Should()
            .Be(requests, "paused recovery must not fetch or import another observation");
        var state = await Publication(item);
        state.RebuiltRevision.Should().Be(state.CatalogRevision);
        await ListenerCatalog(artist, ["2000-01-01"]);
        var artists = await DefaultArtistList();
        artists.Should().NotContain(row => row.uuid == artist.uuid);
    }

    [Test]
    public async Task NativeArchiveImportsCannotUpdateOrDeletePausedIntakeRecordings()
    {
        var item = AddRecording("owned", "2000-01-01");
        await Discover();
        var candidate = await Approve();
        await Refresh();
        var artist = (await ArtistsNamedPerformer()).Single();
        var before = await ListenerCatalog(artist, ["2000-01-01"]);
        const string collection = "DedicatedFixtureCollection";
        await db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                """
                INSERT INTO artists_upstream_sources(artist_id,upstream_source_id,upstream_identifier)
                    VALUES(@id,1,@collection);
                UPDATE normalization.archive_artist_candidates SET approved=false WHERE candidate_id=@candidate;
                """,
                new
                {
                    artist.id,
                    collection,
                    candidate,
                }
            )
        );
        recordings[item]["metadata"]!["collection"] = new JArray(collection);
        recordings[item]["metadata"]!["date"] = "2000-01-02";
        upstream.Respond = uri =>
            uri.AbsolutePath.StartsWith("/metadata/")
                ? recordings[item].ToString()
                : JsonConvert.SerializeObject(
                    new
                    {
                        responseHeader = new { status = 0 },
                        response = new
                        {
                            numFound = 1,
                            start = 0,
                            docs = new[]
                            {
                                new
                                {
                                    identifier = item,
                                    date = "2000-01-02T00:00:00Z",
                                    addeddate = "2026-10-16T00:00:00Z",
                                    max_updated_at = "2026-10-16T00:00:00Z",
                                },
                            },
                        },
                    }
                );
        var client = host.Services.GetRequiredService<TemporalIngestionClient>();
        var artistId = new ArtistId(artist.uuid);

        foreach (
            var start in new Func<Task<ImportHandle<ArtistImportResult>>>[]
            {
                () => client.RefreshAsync(new RefreshArtist(artistId)),
                () => client.RefreshAsync(new RefreshArchiveArtist(artistId, 2000)),
                () =>
                    client.ImportArchiveItemAsync(
                        new ImportArchiveItem(artistId, new ArchiveItemId(item))
                    ),
            }
        )
        {
            var result = await (await start()).GetResultAsync();
            result.FailedItems.Should().Be(0);
            result.SkippedItems.Should().Be(1);
        }

        (await ListenerCatalog(artist, ["2000-01-01"])).Should().BeEquivalentTo(before);

        upstream.Respond = _ =>
            """{"responseHeader":{"status":0},"response":{"numFound":0,"start":0,"docs":[]}}""";
        (await (await client.RefreshAsync(new RefreshArtist(artistId))).GetResultAsync())
            .FailedItems.Should()
            .Be(0);
        (await ListenerCatalog(artist, ["2000-01-01"])).Should().BeEquivalentTo(before);
    }

    [Test]
    public async Task FailedPublicationRollsBackTheRecordingAndCanRetryRetainedEvidence()
    {
        var item = AddRecording("rollback", "2000-01-01");
        await Discover();
        await Approve();
        using var scope = host.Services.CreateScope();
        var intake = scope.ServiceProvider.GetRequiredService<ArchiveIntakeService>();
        var artist = (await intake.PrepareCohortAsync(CancellationToken.None)).Single();
        var input = (await intake.ItemsForArtistAsync(artist, CancellationToken.None)).Single();
        var raw = recordings[item].ToString();

        // Fail after the shared recording writer has finished. This proves the publication
        // references and source children share one commit, rather than two successful calls.
        await db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                """
                CREATE FUNCTION normalization.reject_fixture_publication() RETURNS trigger
                LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'fixture publication failure'; END $$;
                CREATE TRIGGER reject_fixture_publication BEFORE UPDATE ON normalization.archive_item_intake
                FOR EACH ROW WHEN (NEW.published_source_uuid IS NOT NULL)
                EXECUTE FUNCTION normalization.reject_fixture_publication();
                """
            )
        );
        try
        {
            var apply = () =>
                intake.ApplyObservationAsync(
                    input,
                    ArchiveObservationProjector.Project(new ArchiveItemId(item), raw),
                    raw,
                    CancellationToken.None
                );
            var failure = await apply.Should().ThrowAsync<Exception>();
            failure
                .Which.InnerException.Should()
                .BeOfType<Npgsql.PostgresException>()
                .Which.SqlState.Should()
                .Be("P0001");
            var exhausted = await Refresh();
            exhausted.Failed.Should().Be(1);
            exhausted
                .Observed.Should()
                .Be(
                    0,
                    "Observed counts successful processing results; failed writes retain their evidence in PostgreSQL"
                );
            (
                await db.WithWriteConnection(connection =>
                    connection.QuerySingleAsync<int>(
                        "SELECT count(*) FROM sources WHERE upstream_identifier=@item",
                        new { item }
                    )
                )
            )
                .Should()
                .Be(0);
            (
                await db.WithWriteConnection(connection =>
                    connection.QuerySingleAsync<int>(
                        "SELECT count(*) FROM normalization.archive_item_observations WHERE item_identifier=@item",
                        new { item }
                    )
                )
            )
                .Should()
                .Be(1, "evidence is retained independently of a failed catalog transaction");
            (
                await db.WithWriteConnection(connection =>
                    connection.QuerySingleAsync<bool>(
                        "SELECT published_source_uuid IS NULL AND catalog_revision=0 FROM normalization.archive_item_intake WHERE item_identifier=@item",
                        new { item }
                    )
                )
            )
                .Should()
                .BeTrue();
        }
        finally
        {
            await db.WithWriteConnection(connection =>
                connection.ExecuteAsync(
                    "DROP TRIGGER reject_fixture_publication ON normalization.archive_item_intake; DROP FUNCTION normalization.reject_fixture_publication()"
                )
            );
        }

        var retry = await Refresh();
        retry.Published.Should().Be(1);
        retry.Failed.Should().Be(0);
        await ListenerCatalog((await ArtistsNamedPerformer()).Single(), ["2000-01-01"]);
    }

    [Test]
    public async Task ApprovalClearedAfterSelectionPreventsThePendingRecordingWrite()
    {
        var item = AddRecording("paused-before-write", "2000-01-01");
        await Discover();
        var candidate = await Approve();
        using var scope = host.Services.CreateScope();
        var intake = scope.ServiceProvider.GetRequiredService<ArchiveIntakeService>();
        var artist = (await intake.PrepareCohortAsync(CancellationToken.None)).Single();
        var selected = (await intake.ItemsForArtistAsync(artist, CancellationToken.None)).Single();
        await db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                "UPDATE normalization.archive_artist_candidates SET approved=false WHERE candidate_id=@candidate",
                new { candidate }
            )
        );
        var raw = recordings[item].ToString();

        var result = await intake.ApplyObservationAsync(
            selected,
            ArchiveObservationProjector.Project(new ArchiveItemId(item), raw),
            raw,
            CancellationToken.None
        );

        result.Outcome.Should().Be(TapersSectionItemOutcome.Paused);
        (
            await db.WithWriteConnection(connection =>
                connection.QuerySingleAsync<int>(
                    "SELECT count(*) FROM sources WHERE upstream_identifier=@item",
                    new { item }
                )
            )
        )
            .Should()
            .Be(0);
        (
            await db.WithWriteConnection(connection =>
                connection.QuerySingleAsync<bool>(
                    "SELECT published_source_uuid IS NULL AND catalog_revision=0 FROM normalization.archive_item_intake WHERE item_identifier=@item",
                    new { item }
                )
            )
        )
            .Should()
            .BeTrue();
    }

    [Test]
    public async Task MissingMappedArtistRetainsMembershipAndRequiresRepairInsteadOfRecreation()
    {
        var item = AddRecording("missing-artist", "2000-01-01");
        await Discover();
        var candidate = await Approve();
        await Refresh();
        var artist = (await ArtistsNamedPerformer()).Single();
        await db.WithWriteConnection(connection =>
            connection.ExecuteAsync("DELETE FROM artists WHERE uuid=@uuid", new { artist.uuid })
        );

        var result = await Refresh();

        result.Failed.Should().Be(1, "rebuilding a missing artist requires an operator repair");
        (await ArtistsNamedPerformer()).Should().BeEmpty();
        (
            await db.WithWriteConnection(connection =>
                connection.QuerySingleAsync<Guid>(
                    "SELECT artist_uuid FROM normalization.archive_artist_candidates WHERE candidate_id=@candidate",
                    new { candidate }
                )
            )
        )
            .Should()
            .Be(artist.uuid);
        (
            await db.WithWriteConnection(connection =>
                connection.QuerySingleAsync<string>(
                    "SELECT reason FROM normalization.archive_item_intake WHERE item_identifier=@item",
                    new { item }
                )
            )
        )
            .Should()
            .Be("catalog_identity_missing");
    }

    [Test]
    public async Task PartialMetadataFailureRebuildsSuccessfulWorkAndTheNextRefreshRecovers()
    {
        AddRecording("successful", "2000-01-01");
        var failedItem = AddRecording("failed", "2000-01-02");
        await Discover();
        await Approve();
        upstream.Respond = uri =>
            Uri.UnescapeDataString(uri.Segments.Last()) == failedItem
                ? "{ malformed metadata"
                : recordings[Uri.UnescapeDataString(uri.Segments.Last())].ToString();

        var partial = await Refresh();

        partial.Failed.Should().Be(1);
        partial
            .Observed.Should()
            .Be(1, "malformed metadata was not retained as a successful observation");
        partial.Published.Should().Be(1);
        partial.Failures.Should().ContainSingle();
        var artist = (await ArtistsNamedPerformer()).Single();
        var published = await ListenerCatalog(artist, ["2000-01-01"]);
        await AssertDefaultListContains(artist.uuid);
        (
            await db.WithWriteConnection(connection =>
                connection.QuerySingleAsync<bool>(
                    "SELECT last_error IS NOT NULL AND published_source_uuid IS NULL FROM normalization.archive_item_intake WHERE item_identifier=@item",
                    new { item = failedItem }
                )
            )
        )
            .Should()
            .BeTrue();
        (
            await db.WithWriteConnection(connection =>
                connection.QuerySingleAsync<int>(
                    "SELECT count(*) FROM normalization.archive_item_intake WHERE catalog_revision>rebuilt_revision"
                )
            )
        )
            .Should()
            .Be(0, "successful item writes must rebuild even when another item exhausts retries");

        upstream.Respond = uri =>
            recordings[Uri.UnescapeDataString(uri.Segments.Last())].ToString();
        var recovered = await Refresh();

        recovered.Failed.Should().Be(0);
        recovered.Published.Should().Be(1);
        recovered.Unchanged.Should().Be(1);
        var catalog = await ListenerCatalog(artist, ["2000-01-01", "2000-01-02"]);
        catalog.Take(1).Should().BeEquivalentTo(published);
        (
            await db.WithWriteConnection(connection =>
                connection.QuerySingleAsync<bool>(
                    "SELECT last_error IS NULL AND catalog_revision=rebuilt_revision FROM normalization.archive_item_intake WHERE item_identifier=@item",
                    new { item = failedItem }
                )
            )
        )
            .Should()
            .BeTrue();
    }

    private string AddRecording(string suffix, string date)
    {
        var item = "intake-" + Guid.NewGuid().ToString("N") + "-" + suffix;
        recordings[item] = JObject.FromObject(
            new
            {
                metadata = new
                {
                    identifier = item,
                    mediatype = "audio",
                    creator = performer,
                    title = performer + " live",
                    date,
                    collection = new[] { "taperssection" },
                },
                files = new[]
                {
                    new
                    {
                        name = "02.mp3",
                        title = "Second song",
                        track = "2",
                        format = "VBR MP3",
                        length = "2:03",
                        md5 = "second",
                    },
                    new
                    {
                        name = "01.mp3",
                        title = "First song",
                        track = "1",
                        format = "VBR MP3",
                        length = "1:02",
                        md5 = "first",
                    },
                },
            }
        );
        return item;
    }

    private async Task<TapersSectionSummary> Discover() =>
        await (
            await host
                .Services.GetRequiredService<TemporalIngestionClient>()
                .DiscoverTapersSectionAsync(new DiscoverTapersSection())
        ).GetResultAsync();

    private async Task<TapersSectionSummary> Refresh() =>
        await (
            await host
                .Services.GetRequiredService<TemporalIngestionClient>()
                .RefreshTapersSectionAsync(new RefreshTapersSection())
        ).GetResultAsync();

    private Task<Guid> Approve() =>
        db.WithWriteConnection(connection =>
            connection.QuerySingleAsync<Guid>(
                "UPDATE normalization.archive_artist_candidates SET approved=true WHERE proposed_name=@performer RETURNING candidate_id",
                new { performer }
            )
        );

    private async Task<Artist[]> ArtistsNamedPerformer() =>
        (await host.Services.GetRequiredService<ArtistService>().All())
            .Where(artist => artist.name == performer)
            .ToArray();

    private async Task<ArtistWithCounts[]> DefaultArtistList()
    {
        using var scope = host.Services.CreateScope();
        var controller = ActivatorUtilities.CreateInstance<ArtistsController>(
            scope.ServiceProvider
        );
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext(),
        };
        controller.HttpContext.Request.Path = "/api/v3/artists";
        return ((JsonResult)await controller.Get())
            .Value.Should()
            .BeAssignableTo<IEnumerable<ArtistWithCounts>>()
            .Which.ToArray();
    }

    private async Task AssertDefaultListContains(Guid artistUuid) =>
        (await DefaultArtistList()).Should().Contain(row => row.uuid == artistUuid);

    private async Task<JObject> NormalizedArtist(Guid artistUuid)
    {
        using var scope = host.Services.CreateScope();
        var controller = ActivatorUtilities.CreateInstance<ArtistsController>(
            scope.ServiceProvider
        );
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext(),
        };
        controller.HttpContext.Request.Path = "/api/v3/artists/" + artistUuid + "/normalized";
        return JObject.Parse(
            ((ContentResult)await controller.GetFullArtist(artistUuid.ToString())).Content!
        );
    }

    private async Task<RecordingIdentity[]> ListenerCatalog(Artist artist, string[] dates)
    {
        var result = new List<RecordingIdentity>();
        foreach (var date in dates)
        {
            var show = await host
                .Services.GetRequiredService<ShowService>()
                .ShowWithSourcesForArtistOnDate(artist, date);
            show.Should().NotBeNull();
            var source = show!.sources.Should().ContainSingle().Which;
            source.updated_at.Should().BeAfter(new DateTime(2020, 1, 1));
            var tracks = source.sets.SelectMany(set => set.tracks).ToArray();
            tracks.Select(track => track.title).Should().Equal("First song", "Second song");
            tracks
                .Select(track => track.mp3_url)
                .Should()
                .OnlyContain(url => url.StartsWith("https://archive.org/download/"));
            var resolver = new CatalogController(
                host.Services.GetRequiredService<CatalogReferenceResolver>()
            );
            var resolved = (CatalogResolveResponse)
                (
                    (JsonResult)
                        await resolver.Resolve(
                            new CatalogResolveRequest
                            {
                                contract_version = 1,
                                references = tracks
                                    .Select(track => new CatalogReferenceRequest
                                    {
                                        catalog_type = "source_track",
                                        catalog_uuid = track.uuid.ToString(),
                                    })
                                    .ToArray(),
                            }
                        )
                ).Value!;
            resolved
                .references.Should()
                .OnlyContain(reference => reference.availability == "available");
            resolved
                .entities.source_tracks.Select(track => track.uuid)
                .Should()
                .BeEquivalentTo(tracks.Select(track => track.uuid));
            result.Add(
                new RecordingIdentity(
                    source.uuid,
                    show.uuid,
                    source.sets.Select(set => set.uuid).ToArray(),
                    tracks.Select(track => track.uuid).ToArray()
                )
            );
        }

        return result.ToArray();
    }

    private Task<PublicationState> Publication(string item) =>
        db.WithWriteConnection(connection =>
            connection.QuerySingleAsync<PublicationState>(
                """
                SELECT latest_observation_id LatestObservationId, published_observation_id PublishedObservationId,
                    published_display_date PublishedDisplayDate, catalog_revision CatalogRevision, rebuilt_revision RebuiltRevision
                FROM normalization.archive_item_intake WHERE item_identifier=@item
                """,
                new { item }
            )
        );

    private sealed record RecordingIdentity(Guid Source, Guid Show, Guid[] Sets, Guid[] Tracks);

    private sealed class PublicationState
    {
        public Guid LatestObservationId { get; init; }
        public Guid PublishedObservationId { get; init; }
        public string PublishedDisplayDate { get; init; } = "";
        public long CatalogRevision { get; init; }
        public long RebuiltRevision { get; init; }
    }

    // Listener services may request an asynchronous refresh for stale popularity caches.
    // This fixture verifies intake and playback without starting the separate Hangfire host.
    private sealed class IgnoredPopularityJobs : Hangfire.IBackgroundJobClient
    {
        public string Create(Hangfire.Common.Job job, Hangfire.States.IState state) =>
            "fixture-popularity";

        public bool ChangeState(string jobId, Hangfire.States.IState state, string expectedState) =>
            true;
    }
}
