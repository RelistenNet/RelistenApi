using System.Net;
using System.Security.Claims;
using System.Text;
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
using Relisten.Data;
using Relisten.Import;
using Relisten.Ingestion;
using Relisten.Services.Indexing;
using Relisten.Vendor.ArchiveOrg;

namespace RelistenApiTests.Services;

/// <summary>Checks registry races and the authenticated artist-write HTTP contract on disposable PostgreSQL.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public class TestTapersSectionArtistRegistry
{
    private DbService db = null!;
    private ArtistService artists = null!;
    private CatalogImportTransaction transactions = null!;
    private ServiceProvider services = null!;
    private AsyncServiceScope catalogScope;
    private RedisService redis = null!;
    private string previousWrite = "";
    private string previousRead = "";
    private int? ownedArchiveSourceId;
    private readonly HashSet<int> artistIds = [];
    private readonly HashSet<Guid> candidateIds = [];
    private readonly string prefix = "registry-test-" + Guid.NewGuid().ToString("N");
    private int sequence;

    [OneTimeSetUp]
    public async Task ConfigureDatabase()
    {
        if (Environment.GetEnvironmentVariable("RELISTEN_LOCAL_INGESTION_TESTS") != "1")
        {
            Assert.Ignore("Run ./test-local.sh to include local integration tests.");
        }

        previousWrite = DbService.ConnStr;
        previousRead = DbService.ReadOnlyConnStr;
        SqlMapper.AddTypeHandler(new PersistentIdentifierHandler());
        db = new DbService(
            LocalTestDatabase.DatabaseUrl,
            null,
            null,
            new HostingEnvironment { EnvironmentName = Environments.Production },
            NullLogger<DbService>.Instance
        );
        redis = new RedisService(RedisService.BuildConfiguration("redis://127.0.0.1:16379"));
        // Collection discovery loads the whole disposable artist registry, including
        // mapped artists from other fixtures. Their enrichment needs real importers.
        var registrations = new ServiceCollection();
        registrations.AddLogging();
        registrations.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        registrations.AddSingleton(db);
        registrations.AddSingleton(redis);
        registrations.AddRelistenCatalogServices();
        services = registrations.BuildServiceProvider();
        catalogScope = services.CreateAsyncScope();
        artists = catalogScope.ServiceProvider.GetRequiredService<ArtistService>();
        transactions = catalogScope.ServiceProvider.GetRequiredService<CatalogImportTransaction>();
        ownedArchiveSourceId = await db.WithWriteConnection(connection =>
            connection.QuerySingleOrDefaultAsync<int?>(
                """
                INSERT INTO upstream_sources(name) SELECT 'archive.org'
                WHERE NOT EXISTS (SELECT 1 FROM upstream_sources WHERE name = 'archive.org')
                RETURNING id
                """
            )
        );
    }

    [TearDown]
    public async Task CleanUp()
    {
        if (db == null)
        {
            return;
        }

        await db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                """
                DELETE FROM normalization.archive_artist_candidates WHERE candidate_id = ANY(@candidateIds);
                DELETE FROM artists_upstream_sources WHERE artist_id = ANY(@artistIds);
                DELETE FROM features WHERE artist_id = ANY(@artistIds);
                DELETE FROM artists WHERE id = ANY(@artistIds);
                """,
                new { candidateIds = candidateIds.ToArray(), artistIds = artistIds.ToArray() }
            )
        );
        candidateIds.Clear();
        artistIds.Clear();
    }

    [OneTimeTearDown]
    public async Task RestoreDatabaseConfiguration()
    {
        if (db == null)
        {
            return;
        }

        if (ownedArchiveSourceId != null)
        {
            await db.WithWriteConnection(connection =>
                connection.ExecuteAsync(
                    "DELETE FROM upstream_sources WHERE id = @id",
                    new { id = ownedArchiveSourceId }
                )
            );
        }
        await catalogScope.DisposeAsync();
        await services.DisposeAsync();
        await redis.connection.DisposeAsync();
        DbService.ConnStr = previousWrite;
        DbService.ReadOnlyConnStr = previousRead;
    }

    [Test]
    public async Task RetainedIdentityBlocksRenameAndAnotherArtistsReviewedLabel()
    {
        var cohort = await SaveArtist();
        await RetainMembership(cohort, "  Reviewed\t" + cohort.name + "  ");
        var originalName = cohort.name;
        cohort.name += " renamed";
        Func<Task> rename = () => artists.Save(cohort);
        await rename.Should().ThrowAsync<ArtistIntakeIdentityConflictException>();
        cohort.name = originalName;
        cohort.slug += "-renamed";
        Func<Task> changeSlug = () => artists.Save(cohort);
        await changeSlug.Should().ThrowAsync<ArtistIntakeIdentityConflictException>();

        var other = NewArtist();
        other.name = "REVIEWED  " + originalName.ToUpperInvariant();
        Func<Task> createDuplicate = () => artists.Save(other);
        await createDuplicate.Should().ThrowAsync<ArtistIntakeIdentityConflictException>();
        (
            await db.WithWriteConnection(connection =>
                connection.QuerySingleAsync<string>(
                    "SELECT name FROM artists WHERE id = @id",
                    new { cohort.id }
                )
            )
        )
            .Should()
            .Be(originalName);
    }

    [Test]
    public async Task CohortArtistCanChangeFeaturesAndAttachItsOwnDedicatedCollection()
    {
        var cohort = await SaveArtist();
        await RetainMembership(cohort, "reviewed " + cohort.name);
        var originalUuid = cohort.uuid;
        cohort.features.songs = true;
        cohort.featured = (int)ArtistFeaturedFlags.Featured;

        var updated = await artists.Save(cohort);
        var archiveId = await db.WithWriteConnection(connection =>
            connection.QuerySingleAsync<int>(
                "SELECT id FROM upstream_sources WHERE name = 'archive.org'"
            )
        );
        await new UpstreamSourceService(db).EnsureUpstreamSourceForArtist(
            cohort.id,
            archiveId,
            "reviewed " + cohort.name
        );

        updated.uuid.Should().Be(originalUuid);
        updated.features.songs.Should().BeTrue();
        (
            await db.WithWriteConnection(connection =>
                connection.QuerySingleAsync<string>(
                    "SELECT upstream_identifier FROM artists_upstream_sources WHERE artist_id = @id",
                    new { cohort.id }
                )
            )
        )
            .Should()
            .Be("reviewed " + cohort.name);
    }

    [Test]
    public async Task ArtistAndCollectionWritersRecheckCohortAfterWaitingForMaterialization()
    {
        var identity = NewArtist();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var materialize = transactions.RunArtistRegistryAsync(
            Deadline,
            CancellationToken.None,
            async () =>
            {
                var cohort = await artists.Save(identity);
                artistIds.Add(cohort.id);
                await RetainMembership(cohort, "reviewed " + cohort.name);
                entered.SetResult();
                await release.Task;
                return cohort;
            }
        );
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var duplicate = NewArtist();
        duplicate.name = "reviewed " + identity.name;
        var duplicateWrite = artists.Save(duplicate);
        var indexer = new ArchiveOrgArtistIndexer(
            new UnusedCollectionClient(),
            new ArchiveOrgArtistIndexRepository(artists, new UpstreamSourceService(db), db),
            new UpstreamSourceService(db)
        );
        var collectionWrite = indexer.ApplyCollectionsAsync([
            new()
            {
                identifier = "reviewed " + identity.name,
                title = identity.name,
                item_count = 8,
            },
        ]);
        try
        {
            await WaitForRegistryWaiters(2);
            duplicateWrite.IsCompleted.Should().BeFalse();
            collectionWrite.IsCompleted.Should().BeFalse();
        }
        finally
        {
            release.TrySetResult();
        }

        var cohortArtist = await materialize.WaitAsync(TimeSpan.FromSeconds(5));
        Func<Task> duplicateResult = async () =>
            await duplicateWrite.WaitAsync(TimeSpan.FromSeconds(5));
        await duplicateResult.Should().ThrowAsync<ArtistIntakeIdentityConflictException>();
        var discovered = await collectionWrite.WaitAsync(TimeSpan.FromSeconds(5));
        discovered.Created.Should().Be(0);
        discovered.SkippedIntakeIdentity.Should().Be(1);
        (
            await db.WithWriteConnection(connection =>
                connection.QuerySingleAsync<int>(
                    "SELECT count(*) FROM artists WHERE name = @name",
                    new { identity.name }
                )
            )
        )
            .Should()
            .Be(1);
        cohortArtist.slug.Should().Be(identity.slug);
    }

    [Test]
    public async Task AuthenticatedCreateRejectsSharedCollectionWithoutCreatingArtist()
    {
        await using var app = await StartArtistApi();
        var address = app
            .Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.Single();
        using var http = new HttpClient { BaseAddress = new Uri(address) };
        var artist = NewArtist();
        var archiveId = await db.WithWriteConnection(connection =>
            connection.QuerySingleAsync<int>(
                "SELECT id FROM upstream_sources WHERE name = 'archive.org'"
            )
        );
        var request = JsonSerializer.Serialize(
            new CreateUpdateArtistDto
            {
                SlimArtist = artist,
                SlimUpstreamSources =
                [
                    new() { upstream_source_id = archiveId, upstream_identifier = "taperssection" },
                ],
            }
        );

        using var response = await http.PostAsync(
            "/api/v2/artists",
            new StringContent(request, Encoding.UTF8, "application/json")
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("code").GetString().Should().Be("intake_identity_conflict");
        body.RootElement.GetProperty("artist_uuid").ValueKind.Should().Be(JsonValueKind.Null);
        (
            await db.WithWriteConnection(connection =>
                connection.QuerySingleAsync<int>(
                    "SELECT count(*) FROM artists WHERE slug = @slug",
                    new { artist.slug }
                )
            )
        )
            .Should()
            .Be(0);
        await app.StopAsync();
    }

    [Test]
    public async Task LiteralReviewedLabelBlocksAuthenticatedCreationAndCollectionDiscovery()
    {
        var cohort = await SaveArtist();
        await RetainMembership(cohort, "  Reviewed\t" + cohort.name + "  ");
        var duplicate = NewArtist();
        duplicate.name = "REVIEWED  " + cohort.name.ToUpperInvariant();
        await using var app = await StartArtistApi();
        var address = app
            .Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.Single();
        using var http = new HttpClient { BaseAddress = new Uri(address) };
        var request = JsonSerializer.Serialize(
            new CreateUpdateArtistDto { SlimArtist = duplicate, SlimUpstreamSources = [] }
        );

        using var response = await http.PostAsync(
            "/api/v2/artists",
            new StringContent(request, Encoding.UTF8, "application/json")
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("code").GetString().Should().Be("intake_identity_conflict");
        body.RootElement.GetProperty("artist_uuid").GetGuid().Should().Be(cohort.uuid);
        var indexer = new ArchiveOrgArtistIndexer(
            new UnusedCollectionClient(),
            new ArchiveOrgArtistIndexRepository(artists, new UpstreamSourceService(db), db),
            new UpstreamSourceService(db)
        );

        var discovered = await indexer.ApplyCollectionsAsync([
            new()
            {
                identifier = "unique-collection-" + Guid.NewGuid().ToString("N"),
                title = duplicate.name,
                item_count = 8,
            },
        ]);

        discovered.Created.Should().Be(0);
        discovered.SkippedIntakeIdentity.Should().Be(1);
        (
            await db.WithWriteConnection(connection =>
                connection.QuerySingleAsync<int>(
                    "SELECT count(*) FROM artists WHERE name = @name OR slug = @slug",
                    new { duplicate.name, duplicate.slug }
                )
            )
        )
            .Should()
            .Be(0);
        await app.StopAsync();
    }

    private async Task<WebApplication> StartArtistApi()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        builder
            .Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, AuthenticatedOperator>("test", _ => { });
        builder.Services.AddAuthorization();
        builder
            .Services.AddControllers()
            .AddApplicationPart(typeof(ArtistsController).Assembly)
            .AddNewtonsoftJson()
            .AddControllersAsServices();
        builder.Services.AddTransient(_ => new ArtistsController(
            null!,
            db,
            artists,
            null!,
            null!,
            null!,
            null!,
            new UpstreamSourceService(db),
            null!,
            null!,
            null!,
            null!
        ));
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        return app;
    }

    private SlimArtistWithFeatures NewArtist()
    {
        var name = prefix + "-" + ++sequence;
        return new()
        {
            name = name,
            slug = name,
            sort_name = name,
            musicbrainz_id = "",
            features = new(),
        };
    }

    private async Task<SlimArtistWithFeatures> SaveArtist()
    {
        var artist = await artists.Save(NewArtist());
        artistIds.Add(artist.id);
        return artist;
    }

    private async Task RetainMembership(SlimArtist artist, string reviewedLabel)
    {
        var candidateId = Guid.NewGuid();
        candidateIds.Add(candidateId);
        await db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                """
                INSERT INTO normalization.archive_artist_candidates
                    (candidate_id, creator_key, proposed_name, proposed_slug, reviewed_creator_keys,
                     approved, identity_status, artist_id, artist_uuid, creation_slug)
                VALUES (@candidateId, @key, @name, @slug, @keys, true, 'cohort', @id, @uuid, @slug)
                """,
                new
                {
                    candidateId,
                    key = ArtistRegistryService.NormalizeIdentityKey(artist.name),
                    artist.name,
                    artist.slug,
                    artist.id,
                    artist.uuid,
                    keys = new[] { reviewedLabel },
                }
            )
        );
    }

    private async Task WaitForRegistryWaiters(int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (
            await db.WithWriteConnection(connection =>
                connection.QuerySingleAsync<int>(
                    "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event = 'advisory'"
                )
            ) < count
        )
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private static DateTimeOffset Deadline => DateTimeOffset.UtcNow.AddMinutes(1);

    private sealed class UnusedCollectionClient : IArchiveOrgCollectionIndexClient
    {
        public Task<IReadOnlyList<ArchiveOrgCollectionIndexItem>> FetchCollectionsAsync(
            int count,
            CancellationToken token
        ) =>
            throw new InvalidOperationException(
                "This test applies an already fetched collection page."
            );
    }

    private sealed class AuthenticatedOperator(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder
    ) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(
                AuthenticateResult.Success(
                    new AuthenticationTicket(
                        new ClaimsPrincipal(
                            new ClaimsIdentity(
                                [new Claim(ClaimTypes.NameIdentifier, "test-operator")],
                                Scheme.Name
                            )
                        ),
                        Scheme.Name
                    )
                )
            );
    }
}
