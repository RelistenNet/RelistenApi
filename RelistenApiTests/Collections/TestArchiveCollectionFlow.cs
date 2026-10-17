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
using Relisten.Ingestion.Providers.ArchiveCollections;
using Relisten.Vendor.ArchiveOrg;
using Relisten.Vendor.ArchiveOrg.Metadata;
using RelistenApiTests.Ingestion;
using Temporalio.Testing;

namespace RelistenApiTests.Collections;

/// <summary>
/// Runs registered collection workers against loopback Archive responses and a disposable catalog.
/// Collection discovery must reuse native playback without taking over native ingestion.
/// </summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestArchiveCollectionFlow
{
    private IHost host = null!;
    private WorkflowEnvironment? temporal;
    private FixtureHttpServer upstream = null!;
    private DbService db = null!;
    private string originalWrite = null!;
    private string originalRead = null!;
    private readonly Dictionary<string, JObject> recordings = new();
    private string archiveCollection = null!;

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
        var testNamespace = "collection-flow-" + Guid.NewGuid().ToString("N");
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
    public void ConfigureProvider()
    {
        archiveCollection = "fixture-" + Guid.NewGuid().ToString("N");
        recordings.Clear();
        upstream.Respond = uri =>
            uri.AbsolutePath.StartsWith("/metadata/")
                ? recordings[Uri.UnescapeDataString(uri.Segments.Last())].ToString()
                : JsonConvert.SerializeObject(
                    new
                    {
                        total = recordings.Count,
                        items = recordings.Keys.Select(identifier => new
                        {
                            identifier,
                            mediatype = "audio",
                            collection = new[] { archiveCollection },
                        }),
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

    [TestCase("creator")]
    [TestCase("title")]
    public async Task DiscoveryReusesNativePlaybackAndRetainsUnresolvedAndConflictingEvidence(
        string changedField
    )
    {
        using var scope = host.Services.CreateScope();
        var collections = scope.ServiceProvider.GetRequiredService<CollectionService>();
        var resolutions = scope.ServiceProvider.GetRequiredService<ArchiveItemResolutionService>();
        var intake = scope.ServiceProvider.GetRequiredService<ArchiveIntakeService>();
        var sources = scope.ServiceProvider.GetRequiredService<SourceService>();
        var artist = await CreateNativeArtist(scope.ServiceProvider);
        var selectedItem = AddRecording(artist.name, "2000-01-01");
        var unresolvedItem = AddRecording("Uncatalogued fixture performer", "2000-01-02");
        var selected = await PublishNativeRecording(artist, selectedItem, recordings[selectedItem]);
        var otherItem = "not-selected-" + Guid.NewGuid().ToString("N");
        var unrelated = await PublishNativeRecording(
            artist,
            otherItem,
            Recording(otherItem, artist.name, "2000-01-03")
        );
        var selectedBefore = await Playback(scope.ServiceProvider, artist, selected);
        var unrelatedBefore = await Playback(scope.ServiceProvider, artist, unrelated);
        var collectionId = Guid.NewGuid();
        var collection = new CollectionDefinition(
            collectionId,
            "flow-" + collectionId.ToString("N"),
            "Selected fixture recordings",
            "Real worker contract",
            true
        );
        await collections.SaveCollectionAsync(collection);
        var selector = new CollectionSelectorDefinition(
            Guid.NewGuid(),
            collectionId,
            1,
            archiveCollection
        );
        await collections.SaveSelectorAsync(selector);

        var initial = await Discover(selector.Uuid);
        initial.Completed.Should().BeTrue();
        initial.Discovered.Should().Be(2);
        initial.Observed.Should().Be(2);
        initial.Linked.Should().Be(1);
        initial.Unresolved.Should().Be(1);
        initial.Failed.Should().Be(0);
        var accepted = (await resolutions.FindAsync(selectedItem))!;
        accepted.status.Should().Be("resolved");
        accepted.canonical_source_uuid.Should().Be(selected.uuid);
        accepted.selected_artist_uuid.Should().Be(artist.uuid);
        accepted.accepted_observation_id.Should().NotBeNull();
        var pending = (await resolutions.FindAsync(unresolvedItem))!;
        pending.status.Should().Be("pending");
        pending.observation_id.Should().NotBeNull();
        pending.canonical_source_uuid.Should().BeNull();
        pending.selected_artist_uuid.Should().BeNull();
        var unresolvedMembership = (
            await collections.GetProviderMembershipAsync(selector.Uuid, unresolvedItem)
        )!;
        unresolvedMembership.active.Should().BeTrue();
        unresolvedMembership.reference_available.Should().BeFalse();
        await AssertCollectionContainsSelectedOnly(
            collectionId,
            selected.uuid,
            artist.uuid,
            collections
        );

        var repeated = await Discover(selector.Uuid);
        repeated.Completed.Should().BeTrue();
        repeated.Linked.Should().Be(1);
        repeated.Unresolved.Should().Be(1);
        repeated.Failed.Should().Be(0);
        var unchanged = (await resolutions.FindAsync(selectedItem))!;
        unchanged.observation_id.Should().Be(accepted.observation_id);
        unchanged.accepted_observation_id.Should().Be(accepted.accepted_observation_id);
        await AssertCollectionContainsSelectedOnly(
            collectionId,
            selected.uuid,
            artist.uuid,
            collections
        );

        recordings[selectedItem]["metadata"]![changedField] = "Changed performer billing";
        var conflicted = await Discover(selector.Uuid);
        conflicted.Completed.Should().BeTrue();
        conflicted.Linked.Should().Be(0);
        conflicted.Unresolved.Should().Be(2);
        conflicted.Failed.Should().Be(0);
        var held = (await resolutions.FindAsync(selectedItem))!;
        held.status.Should().Be("conflict");
        held.reason.Should().NotBeNullOrWhiteSpace();
        held.observation_id.Should().NotBeNull();
        held.observation_id!.Value.Should().NotBe(accepted.observation_id!.Value);
        held.accepted_observation_id.Should().Be(accepted.accepted_observation_id);
        held.canonical_source_uuid.Should().Be(selected.uuid);
        held.selected_artist_uuid.Should().Be(artist.uuid);
        await AssertCollectionContainsSelectedOnly(
            collectionId,
            selected.uuid,
            artist.uuid,
            collections
        );

        (await Playback(scope.ServiceProvider, artist, selected))
            .Should()
            .BeEquivalentTo(selectedBefore);
        (await Playback(scope.ServiceProvider, artist, unrelated))
            .Should()
            .BeEquivalentTo(unrelatedBefore);
        (await intake.IsCohortArtistAsync(new ArtistId(artist.uuid))).Should().BeFalse();
        (await intake.PendingObservationsAsync())
            .Should()
            .NotContain(new ArchiveItemId(selectedItem))
            .And.NotContain(new ArchiveItemId(unresolvedItem));
        var transaction = scope.ServiceProvider.GetRequiredService<CatalogImportTransaction>();
        foreach (var item in new[] { selectedItem, unresolvedItem })
        {
            (
                await transaction.RunAsync(
                    DateTimeOffset.UtcNow.AddMinutes(1),
                    CancellationToken.None,
                    () => sources.IsArchiveItemIntakeOwnedForUpdate(item)
                )
            )
                .Should()
                .BeFalse("collection evidence must leave native ingestion in charge");
        }
        (await sources.ForUpstreamIdentifierFromPrimary(artist, unresolvedItem)).Should().BeNull();
    }

    private async Task<ArchiveCollectionDiscoverySummary> Discover(Guid selector) =>
        await (
            await host
                .Services.GetRequiredService<TemporalIngestionClient>()
                .DiscoverArchiveCollectionAsync(new(selector))
        ).GetResultAsync();

    private static async Task AssertCollectionContainsSelectedOnly(
        Guid collection,
        Guid source,
        Guid artist,
        CollectionService collections
    )
    {
        var recordings = (await collections.RecordingsAsync(collection))!;
        recordings.items.Should().ContainSingle().Which.source_uuid.Should().Be(source);
        var artists = (await collections.ArtistsAsync(collection))!;
        var performer = artists.items.Should().ContainSingle().Subject;
        performer.uuid.Should().Be(artist);
        performer.includes_all_recordings.Should().BeFalse();
        var overview = (await collections.FindAsync(collection.ToString()))!;
        overview.source_count.Should().Be(1);
        overview.show_count.Should().Be(1);
    }

    private async Task<Artist> CreateNativeArtist(IServiceProvider services)
    {
        var uuid = Guid.NewGuid();
        var name = "Collection native fixture " + uuid.ToString("N");
        var slug = "native-flow-" + uuid.ToString("N");
        await db.WithWriteConnection(async connection =>
        {
            var id = await connection.QuerySingleAsync<int>(
                "INSERT INTO artists(name,slug,uuid,updated_at) VALUES(@name,@slug,@uuid,now()) RETURNING id",
                new
                {
                    name,
                    slug,
                    uuid,
                }
            );
            await connection.ExecuteAsync(
                "INSERT INTO features(artist_id) VALUES(@id); INSERT INTO artists_upstream_sources(artist_id,upstream_source_id,upstream_identifier) VALUES(@id,1,@slug)",
                new { id, slug }
            );
        });
        return (
            await services.GetRequiredService<ArtistService>().FindArtistForImport(uuid.ToString())
        )!;
    }

    private string AddRecording(string creator, string date)
    {
        var item = "collection-flow-" + Guid.NewGuid().ToString("N");
        recordings[item] = Recording(item, creator, date);
        return item;
    }

    private JObject Recording(string item, string creator, string date) =>
        JObject.FromObject(
            new
            {
                metadata = new
                {
                    identifier = item,
                    mediatype = "audio",
                    creator,
                    title = creator + " Live at Fixture Hall",
                    date,
                    venue = "Fixture Hall",
                    coverage = "Test City",
                    collection = new[] { archiveCollection },
                },
                files = new[]
                {
                    new
                    {
                        name = "track.mp3",
                        title = "Fixture song",
                        format = "VBR MP3",
                        length = "2:03",
                        md5 = "fixture",
                    },
                },
            }
        );

    private async Task<Source> PublishNativeRecording(Artist artist, string item, JObject recording)
    {
        using var scope = host.Services.CreateScope();
        var importer = scope.ServiceProvider.GetRequiredService<ArchiveOrgImporter>();
        var metadata = JsonConvert.DeserializeObject<RootObject>(
            recording.ToString(),
            new TolerantStringConverter()
        )!;
        var source = await scope
            .ServiceProvider.GetRequiredService<CatalogImportTransaction>()
            .RunAsync(
                DateTimeOffset.UtcNow.AddMinutes(1),
                CancellationToken.None,
                () =>
                    importer.WriteResolvedRecordingAsync(
                        artist,
                        1,
                        new SearchDoc { identifier = item, addeddate = DateTime.UtcNow },
                        metadata,
                        (string)recording["metadata"]!["date"]!
                    )
            );
        var rebuild = scope.ServiceProvider.GetRequiredService<CatalogRebuildService>();
        await rebuild.RebuildShows(artist);
        await rebuild.RebuildYears(artist);
        return source;
    }

    private static async Task<PlaybackIdentity> Playback(
        IServiceProvider services,
        Artist artist,
        Source source
    )
    {
        var sources = services.GetRequiredService<SourceService>();
        var current = (
            await sources.ForUpstreamIdentifierFromPrimary(artist, source.upstream_identifier)
        )!;
        var full = (
            await sources.FullSourcesForShow(artist, new Show { id = current.show_id!.Value })
        ).Single(item => item.uuid == source.uuid);
        return new(
            full.uuid,
            current.show_id.Value,
            full.sets.Select(set => set.uuid).ToArray(),
            full.sets.SelectMany(set => set.tracks).Select(track => track.uuid).ToArray(),
            full.sets.SelectMany(set => set.tracks).Select(track => track.mp3_url!).ToArray()
        );
    }

    private sealed record PlaybackIdentity(
        Guid Source,
        int ShowId,
        Guid[] Sets,
        Guid[] Tracks,
        string[] Mp3Urls
    );

    // Listener reads may request popularity refreshes; this fixture starts only Temporal workers.
    private sealed class IgnoredPopularityJobs : Hangfire.IBackgroundJobClient
    {
        public string Create(Hangfire.Common.Job job, Hangfire.States.IState state) =>
            "fixture-popularity";

        public bool ChangeState(string jobId, Hangfire.States.IState state, string expectedState) =>
            true;
    }
}
