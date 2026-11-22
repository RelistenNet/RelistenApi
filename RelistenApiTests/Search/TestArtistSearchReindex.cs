using System.Net.Http;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Relisten;
using Relisten.Identifiers;
using Relisten.Ingestion;
using Relisten.Search;
using Temporalio.Client;
using Temporalio.Testing;
using Temporalio.Worker;

namespace RelistenApiTests.Search;

[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestArtistSearchReindex
{
    private const string SharedVenue = "venue_f00ba401";
    private const string NewVenue = "venue_f00ba402";

    [Test]
    public async Task ArtistReindexRemovesAnOrphanAddsANewShowAndRepairsSharedVenues()
    {
        MigratedTestDatabase.RequireLocalDatabase();
        await using var connection = await MigratedTestDatabase.OpenAsync();
        await SeedVenues(connection);
        var first = await SeedArtist(connection);
        var second = await SeedArtist(connection);
        var old = await SeedShow(connection, first, SharedVenue);
        var otherArtist = await SeedShow(connection, second, SharedVenue);

        await using var index = new TestIndex();
        await index.Build();
        var removed = (await index.Performances(old.Show)).Should().ContainSingle().Subject;
        var venue = (await index.Document("entities", SharedVenue))!;
        venue.ArtistSlugs.Should().BeEquivalentTo(first.Slug, second.Slug);

        await using (var mutation = await connection.BeginTransactionAsync())
        {
            await connection.ExecuteAsync(
                """
                UPDATE source_tracks SET is_orphaned = true WHERE uuid = @Track;
                SELECT set_config('norm.author_type', author_type, true),
                       set_config('norm.author_ref', author_ref, true),
                       set_config('norm.run_uuid', run_uuid::text, true)
                FROM norm.show_venues WHERE show_uuid = @Show;
                UPDATE norm.show_venues SET venue_id = @newVenue WHERE show_uuid = @Show;
                """,
                new
                {
                    old.Track,
                    old.Show,
                    newVenue = NewVenue,
                },
                mutation
            );
            await mutation.CommitAsync();
        }
        var added = await SeedShow(connection, first, NewVenue, day: 2);
        await index.Reindex(first.Uuid);

        (await index.Document("performances", removed.Id)).Should().BeNull();
        (await index.Document("entities", UuidTypeId.Format("show", added.Show)))
            .Should()
            .NotBeNull();
        (await index.Performances(added.Show)).Should().ContainSingle();
        (await index.Performances(otherArtist.Show)).Should().ContainSingle();
        (await index.Document("entities", SharedVenue))!.ArtistSlugs.Should().Equal(second.Slug);
        (await index.Document("entities", NewVenue))!.ArtistSlugs.Should().Equal(first.Slug);
    }

    [Test]
    public async Task BuildCatchesUpATrackChangedDuringTheBuildWithAnOldProviderTimestamp()
    {
        MigratedTestDatabase.RequireLocalDatabase();
        await using var connection = await MigratedTestDatabase.OpenAsync();
        var artist = await SeedArtist(connection);
        var show = await SeedShow(connection, artist);

        await using var index = new TestIndex();
        await index.Build();
        const string title = "Updated provider track";
        await index.BuildDuringImport(async () =>
        {
            // This provider changes the file title without changing the source
            // or advancing the track's old provider timestamp.
            await connection.ExecuteAsync(
                "UPDATE source_tracks SET title = @title WHERE uuid = @Track",
                new { title, show.Track }
            );
            await index.Reindex(artist.Uuid);
            (await index.PerformanceLabel(show.Show)).Should().Be(title);
        });

        (await index.PerformanceLabel(show.Show)).Should().Be(title);
    }

    [Test]
    public async Task ArtistWithNoPlayableTracksLosesItsPerformancesInReindexAndFullBuild()
    {
        MigratedTestDatabase.RequireLocalDatabase();
        await using var connection = await MigratedTestDatabase.OpenAsync();
        var artist = await SeedArtist(connection);
        var show = await SeedShow(connection, artist);

        await using var index = new TestIndex();
        await index.Build();
        var old = (await index.Performances(show.Show)).Should().ContainSingle().Subject;

        await connection.ExecuteAsync(
            "UPDATE source_tracks SET mp3_url = '', flac_url = NULL WHERE uuid = @Track",
            new { show.Track }
        );
        await index.Reindex(artist.Uuid);

        (await index.Document("performances", old.Id)).Should().BeNull();
        (await index.Performances(show.Show)).Should().BeEmpty();

        await index.Build();
        (await index.Performances(show.Show)).Should().BeEmpty();
    }

    private static Task SeedVenues(Npgsql.NpgsqlConnection connection) =>
        connection.ExecuteAsync(
            """
            WITH run AS (
                INSERT INTO norm.runs (uuid, author_type, author_ref)
                VALUES (gen_random_uuid(), 'importer', 'search fixture') RETURNING uuid
            )
            INSERT INTO norm.venues (id, name, slug, author_type, author_ref, run_uuid)
            SELECT venue, venue, venue, 'importer', 'search fixture', run.uuid
            FROM unnest(@venues) AS venue CROSS JOIN run
            """,
            new { venues = new[] { SharedVenue, NewVenue } }
        );

    private static async Task<(Guid Uuid, string Slug, int Id)> SeedArtist(
        Npgsql.NpgsqlConnection connection
    )
    {
        var uuid = Guid.NewGuid();
        var slug = uuid.ToString("N");
        var id = await connection.ExecuteScalarAsync<int>(
            """
            WITH artist AS (
                INSERT INTO artists(uuid, name, slug, updated_at)
                VALUES (@uuid, 'Search reindex fixture', @slug, '2024-08-01') RETURNING id
            ), feature AS (
                INSERT INTO features(artist_id, norm_catalogue) SELECT id, true FROM artist
            )
            SELECT id FROM artist
            """,
            new { uuid, slug }
        );
        return (uuid, slug, id);
    }

    private static async Task<(Guid Show, Guid Track)> SeedShow(
        Npgsql.NpgsqlConnection connection,
        (Guid Uuid, string Slug, int Id) artist,
        string? canonicalVenue = null,
        int day = 1
    )
    {
        var show = Guid.NewGuid();
        var track = Guid.NewGuid();
        var date = FormattableString.Invariant($"2024-08-{day:00}");
        var changed =
            day == 1 ? new DateTime(2024, 8, 1, 0, 0, 0, DateTimeKind.Utc) : DateTime.UtcNow;
        await connection.ExecuteAsync(
            """
            WITH show AS (
                INSERT INTO shows(uuid, artist_id, date, display_date, updated_at)
                VALUES (@show, @artistId, @date::date, @date, @changed) RETURNING id
            ), recording AS (
                INSERT INTO sources(uuid, artist_id, show_id, upstream_identifier, display_date,
                                    is_soundboard, is_remaster, has_jamcharts, created_at, updated_at)
                SELECT gen_random_uuid(), @artistId, id, @show::text, @date::text,
                       true, false, false, @changed, @changed FROM show RETURNING id
            ), track_set AS (
                INSERT INTO source_sets(uuid, source_id, index, name, updated_at)
                SELECT gen_random_uuid(), id, 1, 'Set 1', now() FROM recording RETURNING id, source_id
            ), venue_link AS (
                INSERT INTO norm.show_venues(show_uuid, venue_id, author_type, author_ref, run_uuid)
                SELECT @show, id, author_type, author_ref, run_uuid
                FROM norm.venues WHERE id = @canonicalVenue
            )
            INSERT INTO source_tracks(uuid, artist_id, source_id, source_set_id, track_position,
                                      title, slug, mp3_url, duration, is_orphaned, updated_at)
            SELECT @track, @artistId, source_id, id, 1, 'Search fixture track', 'fixture',
                   'https://example.test/' || @track::text || '.mp3', 123, false, @changed
            FROM track_set
            """,
            new
            {
                show,
                track,
                date,
                changed,
                artistId = artist.Id,
                canonicalVenue,
            }
        );
        return (show, track);
    }

    private sealed class TestIndex : IAsyncDisposable
    {
        private readonly string prefix = "test_artist_search_" + Guid.NewGuid().ToString("N");
        private readonly string previousPrimary = DbService.ConnStr;
        private readonly string previousReplica = DbService.ReadOnlyConnStr;
        private readonly BeforePublishInterceptor interceptor = new();
        private readonly HttpClient http;
        private readonly TypesenseClient client;
        private readonly SearchBuildActivities builds;

        public TestIndex()
        {
            http = new HttpClient(interceptor);
            TypesenseClient.Configure(http, "http://127.0.0.1:18108", "local_dev_typesense");
            client = new TypesenseClient(http);
            var builder = new SearchIndexBuilder(client, NullLogger<SearchIndexBuilder>.Instance);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?> { ["Search:AliasPrefix"] = prefix }
                )
                .Build();
            var db = new DbService(
                LocalTestDatabase.DatabaseUrl,
                null,
                null,
                new HostingEnvironment { EnvironmentName = Environments.Production },
                NullLogger<DbService>.Instance
            );
            var artists = new ArtistSearchIndexer(
                db,
                client,
                builder,
                configuration,
                NullLogger<ArtistSearchIndexer>.Instance
            );
            builds = new SearchBuildActivities(
                db,
                builder,
                artists,
                NullLogger<SearchBuildActivities>.Instance
            );
        }

        public Task Build() =>
            new ActivityEnvironment().RunAsync(() =>
                builds.Build(null, Guid.NewGuid().ToString("N"))
            );

        public async Task BuildDuringImport(Func<Task> beforePublish)
        {
            interceptor.BeforePublish = beforePublish;
            await using var environment = await WorkflowEnvironment.StartLocalAsync();
            using var workflows = new TemporalWorker(
                environment.Client,
                new TemporalWorkerOptions(IngestionQueues.Workflows)
                    .AddWorkflow<SearchBuildWorkflow>()
                    .AddWorkflow<SearchRetireWorkflow>()
            );
            using var database = new TemporalWorker(
                environment.Client,
                new TemporalWorkerOptions(IngestionQueues.Database).AddAllActivities(builds)
            );
            await workflows.ExecuteAsync(() =>
                database.ExecuteAsync(() =>
                    environment.Client.ExecuteWorkflowAsync(
                        (SearchBuildWorkflow workflow) => workflow.RunAsync(null),
                        new WorkflowOptions(SearchBuildWorkflow.Id, IngestionQueues.Workflows)
                    )
                )
            );
        }

        public Task Reindex(Guid artist) =>
            new ActivityEnvironment().RunAsync(() => builds.ReindexArtist(artist));

        public Task<SearchDocument?> Document(string kind, string id) =>
            client.ReadDocument(Alias(kind), id, CancellationToken.None);

        public async Task<string> PerformanceLabel(Guid show)
        {
            var hit = (await Performances(show)).Should().ContainSingle().Subject;
            return (await Document("performances", hit.Id))!.Label;
        }

        public async Task<TypesenseClient.IndexedDocument[]> Performances(Guid show) =>
            await client
                .ExportDocuments(
                    Alias(SearchIndexSchema.Performances),
                    $"show_id:=`{UuidTypeId.Format("show", show)}`",
                    CancellationToken.None
                )
                .ToArrayAsync();

        public async ValueTask DisposeAsync()
        {
            try
            {
                foreach (var kind in SearchIndexSchema.Collections)
                {
                    await client.DeleteAlias(Alias(kind), CancellationToken.None);
                }

                foreach (var collection in await client.ReadCollectionNames(CancellationToken.None))
                {
                    if (collection.StartsWith(prefix + "_", StringComparison.Ordinal))
                    {
                        await client.DeleteCollection(collection, CancellationToken.None);
                    }
                }
            }
            finally
            {
                http.Dispose();
                DbService.ConnStr = previousPrimary;
                DbService.ReadOnlyConnStr = previousReplica;
            }
        }

        private string Alias(string kind) => SearchIndexBuilder.AliasName(prefix, kind);
    }

    private sealed class BeforePublishInterceptor() : DelegatingHandler(new HttpClientHandler())
    {
        public Func<Task>? BeforePublish { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            if (
                request.Method == HttpMethod.Put
                && request.RequestUri!.AbsolutePath.StartsWith(
                    "/aliases/",
                    StringComparison.Ordinal
                )
                && BeforePublish is { } beforePublish
            )
            {
                BeforePublish = null;
                await beforePublish();
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }
}
