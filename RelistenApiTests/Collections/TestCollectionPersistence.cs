using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
using Relisten.Vendor.ArchiveOrg;
using Relisten.Vendor.ArchiveOrg.Metadata;
using RelistenApiTests.Ingestion;

namespace RelistenApiTests.Collections;

/// <summary>
/// Verifies collection membership against real catalog writes and reads. The disposable
/// PostgreSQL database owns every fixture identity; Redis and provider HTTP stay local.
/// </summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public class TestCollectionPersistence
{
    private IHost host = null!;
    private IServiceScope scope = null!;
    private DbService db = null!;
    private string originalWrite = null!;
    private string originalRead = null!;
    private CollectionService Collections =>
        scope.ServiceProvider.GetRequiredService<CollectionService>();
    private ArchiveItemResolutionService Resolutions =>
        scope.ServiceProvider.GetRequiredService<ArchiveItemResolutionService>();

    [OneTimeSetUp]
    public async Task Start()
    {
        if (Environment.GetEnvironmentVariable("RELISTEN_LOCAL_INGESTION_TESTS") != "1")
        {
            Assert.Ignore("Run ./test-local.sh to include local integration tests.");
        }

        originalWrite = DbService.ConnStr;
        originalRead = DbService.ReadOnlyConnStr;
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
        builder.Services.AddSingleton<Hangfire.IBackgroundJobClient, IgnoredPopularityJobs>();
        SqlMapper.AddTypeHandler(new PersistentIdentifierHandler());
        SqlMapper.AddTypeHandler(new DateTimeHandler());
        host = builder.Build();
        db = host.Services.GetRequiredService<DbService>();
        await db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                """
                INSERT INTO upstream_sources(id,name,url) VALUES
                    (1,'archive.org','https://archive.org'),(2,'phish.in','https://phish.in')
                ON CONFLICT DO NOTHING;
                """
            )
        );
    }

    [SetUp]
    public void CreateScope() => scope = host.Services.CreateScope();

    [TearDown]
    public void DisposeScope() => scope?.Dispose();

    [OneTimeTearDown]
    public void Stop()
    {
        if (host != null)
        {
            host.Services.GetRequiredService<RedisService>().connection.Dispose();
            host.Dispose();
        }
        if (originalWrite != null)
        {
            DbService.ConnStr = originalWrite;
            DbService.ReadOnlyConnStr = originalRead;
        }
    }

    [Test]
    public async Task SelectedRecordingDoesNotIncludeTheArtistsOtherCatalogAndPublicationIsExplicit()
    {
        var artist = await CreateArtist("Selected performer");
        var selected = await PublishRecording(artist, NewItem(), "2000-01-01");
        await PublishRecording(artist, NewItem(), "2000-01-02");
        var collection = await CreateCollection(published: false);
        await Collections.SetEditorialRecordingMembershipAsync(
            collection.Uuid,
            selected.uuid,
            true,
            "Reviewed Aadam recording"
        );

        (await Collections.FindAsync(collection.Slug)).Should().BeNull();
        (await Collections.ListAsync())
            .items.Should()
            .NotContain(item => item.uuid == collection.Uuid);
        (await Collections.RecordingsAsync(collection.Uuid)).Should().BeNull();
        (await Collections.ArtistsAsync(collection.Uuid)).Should().BeNull();

        await Collections.SaveCollectionAsync(collection with { Published = true });
        var recordings = (await Collections.RecordingsAsync(collection.Uuid))!;
        recordings.items.Should().ContainSingle().Which.source_uuid.Should().Be(selected.uuid);
        var artists = (await Collections.ArtistsAsync(collection.Uuid))!;
        var facet = artists.items.Should().ContainSingle().Subject;
        facet.uuid.Should().Be(artist.uuid);
        facet.includes_all_recordings.Should().BeFalse();
        facet.source_count.Should().Be(1);
        var overview = (await Collections.FindAsync(collection.Slug))!;
        overview.source_count.Should().Be(1);
        overview.artist_count.Should().Be(1);
        overview.show_count.Should().Be(1);
    }

    [Test]
    public async Task SplitPerformanceKeepsLegacyLookupAndCollectionPlaybackIdentities()
    {
        const string displayDate = "2000-01-01";
        var artist = await CreateArtist("Two performances");
        var first = await PublishRecording(artist, NewItem(), displayDate);
        var second = await PublishRecording(artist, NewItem(), displayDate);
        var originalShow = (
            await scope
                .ServiceProvider.GetRequiredService<ShowService>()
                .ShowWithSourcesForArtistOnDate(artist, displayDate)
        )!.uuid;
        var originalPlayback = await ReadPlayback(artist, second);
        var collection = await CreateCollection();
        await Collections.SetArtistMembershipAsync(
            collection.Uuid,
            artist.uuid,
            true,
            "Reviewed performer"
        );
        await Collections.SetEditorialRecordingMembershipAsync(
            collection.Uuid,
            second.uuid,
            true,
            "Selected recording"
        );

        var assignments = scope.ServiceProvider.GetRequiredService<ShowPerformanceService>();
        var retained = await assignments.AssignSourceAsync(
            artist,
            first.id,
            "provider:first",
            displayDate,
            PerformanceType.Late,
            "Late show"
        );
        var additional = await assignments.AssignSourceAsync(
            artist,
            second.id,
            "provider:second",
            displayDate,
            PerformanceType.Early,
            "Early show"
        );
        var rebuild = scope.ServiceProvider.GetRequiredService<CatalogRebuildService>();
        await rebuild.RebuildShows(artist);
        await rebuild.RebuildYears(artist);
        retained.uuid.Should().Be(originalShow);
        additional.uuid.Should().NotBe(originalShow);

        using var controller = CreateShowsController();
        var grouped = (JsonResult)await controller.PerformancesOnDate(artist.slug, displayDate);
        var performances = ((IEnumerable<ShowWithSources>)grouped.Value!).ToList();
        performances.Select(show => show.uuid).Should().Equal(additional.uuid, retained.uuid);
        performances
            .Select(show => show.display_date)
            .Should()
            .OnlyContain(date => date == displayDate);
        performances[0].sources.Select(source => source.uuid).Should().Equal(second.uuid);
        performances[1].sources.Select(source => source.uuid).Should().Equal(first.uuid);

        var legacy = (JsonResult)await controller.ShowsOnSpecificDate(artist.slug, displayDate);
        ((ShowWithSources)legacy.Value!).uuid.Should().Be(originalShow);
        var direct = (JsonResult)await controller.ShowsOnSpecificDate(additional.uuid);
        ((ShowWithSources)direct.Value!)
            .sources.Select(source => source.uuid)
            .Should()
            .Equal(second.uuid);
        (await controller.PerformancesOnDate(artist.slug, "1900-01-01"))
            .Should()
            .BeOfType<NotFoundObjectResult>();
        (await controller.PerformancesOnDate("missing-" + Guid.NewGuid(), displayDate))
            .Should()
            .BeOfType<NotFoundObjectResult>();

        var recordings = (await Collections.RecordingsAsync(collection.Uuid))!.items;
        recordings
            .Should()
            .HaveCount(2, "overlapping membership must still deduplicate each recording");
        recordings
            .Single(item => item.source_uuid == second.uuid)
            .show_uuid.Should()
            .Be(additional.uuid);
        var overview = (await Collections.FindAsync(collection.Slug))!;
        overview.source_count.Should().Be(2);
        overview.show_count.Should().Be(2);
        (await ReadPlayback(artist, second)).Should().BeEquivalentTo(originalPlayback);

        await rebuild.RebuildShows(artist);
        var repeated = (JsonResult)await controller.PerformancesOnDate(artist.slug, displayDate);
        ((IEnumerable<ShowWithSources>)repeated.Value!)
            .Select(show => show.uuid)
            .Should()
            .Equal(additional.uuid, retained.uuid);
    }

    [Test]
    public async Task PartialDateListsSeparateUnresolvedPerformancesAndKeepsLegacyDateReadable()
    {
        var artist = await CreateArtist("Unknown day performer");
        var first = await PublishRecording(artist, NewItem(), "2000-XX-XX");
        var second = await PublishRecording(artist, NewItem(), "2000-XX-XX");
        using var controller = CreateShowsController();

        var grouped = (JsonResult)await controller.PerformancesOnDate(artist.slug, "2000-XX-XX");
        var shows = ((IEnumerable<ShowWithSources>)grouped.Value!).ToList();
        shows.Should().HaveCount(2, "matching unknown dates do not establish a shared performance");
        shows.Should().OnlyContain(show => show.display_date == "2000-XX-XX");
        shows
            .SelectMany(show => show.sources)
            .Select(source => source.uuid)
            .Should()
            .BeEquivalentTo(new[] { first.uuid, second.uuid });
        var legacy = (JsonResult)await controller.ShowsOnSpecificDate(artist.slug, "2000-XX-XX");
        ((ShowWithSources)legacy.Value!)
            .sources.Select(source => source.uuid)
            .Should()
            .Equal(first.uuid);
        (await controller.PerformancesOnDate(artist.slug, "2000-01-01"))
            .Should()
            .BeOfType<NotFoundObjectResult>();
    }

    [Test]
    public async Task RemovingLastRecordingHidesEmptyCatalogRowsAndReappearanceRetainsShowUuid()
    {
        var artist = await CreateArtist("Temporarily empty performer");
        var item = NewItem();
        var source = await PublishRecording(artist, item, "2000-01-01");
        var assignments = scope.ServiceProvider.GetRequiredService<ShowPerformanceService>();
        var show = await assignments.AssignSourceAsync(
            artist,
            source.id,
            "provider:retained",
            "2000-01-01"
        );
        var rebuild = scope.ServiceProvider.GetRequiredService<CatalogRebuildService>();
        await scope
            .ServiceProvider.GetRequiredService<SourceService>()
            .RemoveSourcesWithUpstreamIdentifiers(artist.id, [item]);
        await rebuild.RebuildShows(artist);
        await rebuild.RebuildYears(artist);

        var shows = scope.ServiceProvider.GetRequiredService<ShowService>();
        (await shows.ShowWithSourcesForUuid(artist, show.uuid)).Should().BeNull();
        (await shows.PerformancesForArtistOnDate(artist, "2000-01-01")).Should().BeEmpty();
        using var controller = CreateShowsController();
        (await controller.RandomByArtist(artist.slug)).Should().BeOfType<NotFoundObjectResult>();
        (await scope.ServiceProvider.GetRequiredService<YearService>().AllForArtist(artist))
            .Should()
            .BeEmpty();
        var artists = scope.ServiceProvider.GetRequiredService<ArtistService>();
        (await artists.AllWithCounts<ArtistWithCounts>())
            .Should()
            .NotContain(listed => listed.uuid == artist.uuid);
        var search = await scope
            .ServiceProvider.GetRequiredService<SearchService>()
            .Search("2000-01-01", artist.uuid);
        search.Shows.Should().BeEmpty();
        var references = await scope
            .ServiceProvider.GetRequiredService<CatalogReferenceResolver>()
            .Resolve([new("artist", artist.uuid), new("show", show.uuid)]);
        references.entities.shows.Should().BeEmpty();
        references.entities.artists.Single().show_count.Should().Be(0);

        var returned = await PublishRecording(artist, item, "2000-01-01");
        var restored = await assignments.AssignSourceAsync(
            artist,
            returned.id,
            "provider:retained",
            "2000-01-01"
        );
        await rebuild.RebuildShows(artist);
        await rebuild.RebuildYears(artist);
        restored.uuid.Should().Be(show.uuid);
        (await shows.ShowWithSourcesForUuid(artist, show.uuid))!
            .sources.Select(recording => recording.uuid)
            .Should()
            .Equal(returned.uuid);
        (await artists.AllWithCounts<ArtistWithCounts>())
            .Single(listed => listed.uuid == artist.uuid)
            .show_count.Should()
            .Be(1);
    }

    [Test]
    public async Task SongReadsCountOnlyAssignedPerformancesAndDeduplicateProviderSetlists()
    {
        var artist = await CreateArtist("Song assignment performer");
        var first = await PublishRecording(artist, NewItem(), "2000-01-01");
        var second = await PublishRecording(artist, NewItem(), "2000-01-01");
        var assignments = scope.ServiceProvider.GetRequiredService<ShowPerformanceService>();
        var early = await assignments.AssignSourceAsync(
            artist,
            first.id,
            "provider:early",
            "2000-01-01"
        );
        await assignments.AssignSourceAsync(artist, second.id, "provider:late", "2000-01-01");
        var songUuid = Guid.NewGuid();
        var unassignedSongUuid = Guid.NewGuid();
        var songId = await db.WithWriteConnection(connection =>
            connection.QuerySingleAsync<int>(
                """
                INSERT INTO setlist_songs(artist_id,name,slug,updated_at,upstream_identifier,uuid)
                VALUES(@artistId,'Assigned fixture song','assigned-fixture',now(),'assigned',@songUuid)
                RETURNING id
                """,
                new { artistId = artist.id, songUuid }
            )
        );
        var unassignedSongId = await db.WithWriteConnection(connection =>
            connection.QuerySingleAsync<int>(
                """
                INSERT INTO setlist_songs(artist_id,name,slug,updated_at,upstream_identifier,uuid)
                VALUES(@artistId,'Unassigned fixture song','unassigned-fixture',now(),'unassigned',@unassignedSongUuid)
                RETURNING id
                """,
                new { artistId = artist.id, unassignedSongUuid }
            )
        );
        var venueId = await db.WithWriteConnection(connection =>
            connection.QuerySingleAsync<long>(
                """
                INSERT INTO venues(artist_id,name,location,slug,upstream_identifier,uuid,updated_at)
                VALUES(@artistId,'Fixture Hall','Test City','fixture-hall','fixture-hall',@uuid,now())
                RETURNING id
                """,
                new { artistId = artist.id, uuid = Guid.NewGuid() }
            )
        );
        foreach (var provider in new[] { "provider-one", "provider-two", "unresolved" })
        {
            var setlist = await db.WithWriteConnection(connection =>
                connection.QuerySingleAsync<long>(
                    """
                    INSERT INTO setlist_shows(artist_id,venue_id,upstream_identifier,date,updated_at,uuid)
                    VALUES(@artistId,@venueId,@provider,'2000-01-01',now(),@uuid)
                    RETURNING id
                    """,
                    new
                    {
                        artistId = artist.id,
                        provider,
                        uuid = Guid.NewGuid(),
                        venueId,
                    }
                )
            );
            var playedSong = provider == "unresolved" ? unassignedSongId : songId;
            await db.WithWriteConnection(connection =>
                connection.ExecuteAsync(
                    "INSERT INTO setlist_songs_plays(played_setlist_song_id,played_setlist_show_id) VALUES(@playedSong,@setlist)",
                    new { playedSong, setlist }
                )
            );
            if (provider != "unresolved")
            {
                await assignments.AssignSetlistAsync(artist, setlist, early.uuid);
            }
        }
        await scope
            .ServiceProvider.GetRequiredService<CatalogRebuildService>()
            .RebuildShows(artist);

        var search = await scope
            .ServiceProvider.GetRequiredService<SearchService>()
            .Search("fixture song", artist.uuid);
        var result = search.Songs.Should().ContainSingle().Subject;
        result.uuid.Should().Be(songUuid);
        result.shows_played_at.Should().Be(1);
        var references = await scope
            .ServiceProvider.GetRequiredService<CatalogReferenceResolver>()
            .Resolve([new("song", songUuid), new("song", unassignedSongUuid)]);
        references
            .entities.songs.Single(song => song.uuid == songUuid)
            .shows_played_at.Should()
            .Be(1);
        references
            .entities.songs.Single(song => song.uuid == unassignedSongUuid)
            .shows_played_at.Should()
            .Be(0);
        var details = await scope
            .ServiceProvider.GetRequiredService<SetlistSongService>()
            .ForIdWithShows(artist, songId);
        details!.shows.Select(show => show.uuid).Should().Equal(early.uuid);
    }

    [Test]
    public async Task ArtistWideMembershipIncludesLaterSourcesAndDeduplicatesBeforeCountsAndPagination()
    {
        var artist = await CreateArtist("Whole project");
        var other = await CreateArtist("Another project");
        var first = await PublishRecording(artist, NewItem(), "2000-01-01");
        var second = await PublishRecording(artist, NewItem(), "2000-01-01");
        var sameDateOtherArtist = await PublishRecording(other, NewItem(), "2000-01-01");
        var collection = await CreateCollection();
        await Collections.SetArtistMembershipAsync(
            collection.Uuid,
            artist.uuid,
            true,
            "Reviewed project"
        );
        await Collections.SetEditorialRecordingMembershipAsync(
            collection.Uuid,
            first.uuid,
            true,
            "Separate recording selection"
        );
        await Collections.SetEditorialRecordingMembershipAsync(
            collection.Uuid,
            sameDateOtherArtist.uuid,
            true,
            "Same date, different performer"
        );
        var selector = await CreateSelector(collection);
        await Collections.SetProviderMembershipAsync(
            selector.Uuid,
            first.upstream_identifier,
            true,
            "Archive membership"
        );
        (await Resolutions.ResolveExistingAsync(new(first.upstream_identifier)))
            .status.Should()
            .Be("resolved");

        var later = await PublishRecording(artist, NewItem(), "2001-02-03");
        var all = (await Collections.RecordingsAsync(collection.Uuid))!;
        all.items.Select(item => item.source_uuid)
            .Should()
            .BeEquivalentTo(
                new[] { first.uuid, second.uuid, sameDateOtherArtist.uuid, later.uuid }
            );
        var overview = (await Collections.FindAsync(collection.Slug))!;
        overview.source_count.Should().Be(4);
        overview
            .show_count.Should()
            .Be(3, "the same date under two artists represents separate shows");
        overview.artist_count.Should().Be(2);
        var facets = (await Collections.ArtistsAsync(collection.Uuid))!.items;
        facets.Single(item => item.uuid == artist.uuid).includes_all_recordings.Should().BeTrue();
        facets.Single(item => item.uuid == artist.uuid).source_count.Should().Be(3);
        facets.Single(item => item.uuid == other.uuid).includes_all_recordings.Should().BeFalse();

        var paged = await ReadEveryRecording(collection.Uuid);
        paged.Should().Equal(all.items.Select(item => item.source_uuid));
        (await ReadEveryRecording(collection.Uuid)).Should().Equal(paged);
        var firstPage = (await Collections.RecordingsAsync(collection.Uuid, limit: 1))!;
        firstPage.next_cursor.Should().NotBeNull();
        var anotherCollection = await CreateCollection();
        Func<Task> malformed = () =>
            Collections.RecordingsAsync(collection.Uuid, cursor: "not a cursor");
        Func<Task> wrongCollection = () =>
            Collections.RecordingsAsync(anotherCollection.Uuid, cursor: firstPage.next_cursor);
        Func<Task> wrongKind = () =>
            Collections.ArtistsAsync(collection.Uuid, cursor: firstPage.next_cursor);
        await malformed.Should().ThrowAsync<ArgumentException>();
        await wrongCollection.Should().ThrowAsync<ArgumentException>();
        await wrongKind.Should().ThrowAsync<ArgumentException>();
    }

    [Test]
    public async Task ArtistFilterPaginatesOnlyCollectionRecordingsAndCannotReuseAnotherFiltersCursor()
    {
        var artist = await CreateArtist("Filtered performer");
        var other = await CreateArtist("Unfiltered performer");
        var first = await PublishRecording(artist, NewItem(), "2000-01-01");
        var second = await PublishRecording(artist, NewItem(), "2001-01-01");
        await PublishRecording(artist, NewItem(), "2002-01-01");
        var otherSource = await PublishRecording(other, NewItem(), "2000-01-01");
        var collection = await CreateCollection();
        foreach (var source in new[] { first, second, otherSource })
        {
            await Collections.SetEditorialRecordingMembershipAsync(
                collection.Uuid,
                source.uuid,
                true,
                "Selected recording"
            );
        }

        var filtered = (
            await Collections.RecordingsAsync(collection.Uuid, artistUuid: artist.uuid)
        )!;
        filtered.items.Select(item => item.source_uuid).Should().Equal(second.uuid, first.uuid);
        filtered.items.Should().OnlyContain(item => item.artist_uuid == artist.uuid);
        var firstPage = (
            await Collections.RecordingsAsync(collection.Uuid, limit: 1, artistUuid: artist.uuid)
        )!;
        firstPage.next_cursor.Should().NotBeNull();
        var secondPage = (
            await Collections.RecordingsAsync(
                collection.Uuid,
                limit: 1,
                cursor: firstPage.next_cursor,
                artistUuid: artist.uuid
            )
        )!;
        firstPage
            .items.Concat(secondPage.items)
            .Select(item => item.source_uuid)
            .Should()
            .Equal(filtered.items.Select(item => item.source_uuid));
        secondPage.next_cursor.Should().BeNull();
        Func<Task> anotherArtist = () =>
            Collections.RecordingsAsync(
                collection.Uuid,
                cursor: firstPage.next_cursor,
                artistUuid: other.uuid
            );
        Func<Task> noFilter = () =>
            Collections.RecordingsAsync(collection.Uuid, cursor: firstPage.next_cursor);
        await anotherArtist.Should().ThrowAsync<ArgumentException>();
        await noFilter.Should().ThrowAsync<ArgumentException>();
        (await Collections.FindAsync(collection.Slug))!.source_count.Should().Be(3);
    }

    [Test]
    public async Task RetiringOneMembershipBasisPreservesOtherInclusionsAndCanonicalPlayback()
    {
        var artist = await CreateArtist("Independent inclusion");
        var source = await PublishRecording(artist, NewItem(), "2000-01-01");
        var identity = await ReadPlayback(artist, source);
        var collection = await CreateCollection();
        var selector = await CreateSelector(collection);
        await Collections.SetArtistMembershipAsync(
            collection.Uuid,
            artist.uuid,
            true,
            "Project selection"
        );
        await Collections.SetEditorialRecordingMembershipAsync(
            collection.Uuid,
            source.uuid,
            true,
            "Recording selection"
        );
        await Collections.SetProviderMembershipAsync(
            selector.Uuid,
            source.upstream_identifier,
            true,
            "Upstream selection"
        );
        await Resolutions.ResolveExistingAsync(new(source.upstream_identifier));

        await Collections.SetProviderMembershipAsync(
            selector.Uuid,
            source.upstream_identifier,
            false,
            "Confirmed upstream removal"
        );
        await AssertContainsOnly(collection.Uuid, source.uuid);
        await Collections.SetEditorialRecordingMembershipAsync(
            collection.Uuid,
            source.uuid,
            false,
            "Retired editorial choice"
        );
        await AssertContainsOnly(collection.Uuid, source.uuid);
        await Collections.SetArtistMembershipAsync(
            collection.Uuid,
            artist.uuid,
            false,
            "Retired project choice"
        );
        await AssertContainsOnly(collection.Uuid);
        await Collections.SetProviderMembershipAsync(
            selector.Uuid,
            source.upstream_identifier,
            true,
            "Upstream membership restored"
        );
        await AssertContainsOnly(collection.Uuid, source.uuid);

        (await Collections.GetArtistMembershipAsync(collection.Uuid, artist.uuid))!
            .active.Should()
            .BeFalse();
        (await Collections.GetEditorialMembershipAsync(collection.Uuid, source.uuid))!
            .active.Should()
            .BeFalse();
        (await Collections.GetProviderMembershipAsync(selector.Uuid, source.upstream_identifier))!
            .active.Should()
            .BeTrue();
        (await ReadPlayback(artist, source)).Should().BeEquivalentTo(identity);
    }

    [Test]
    public async Task TwoSelectorsReuseOneNativeRecordingAndLeaveNativeUpdatesInCharge()
    {
        var artist = await CreateArtist("Native owner");
        var source = await PublishRecording(artist, NewItem(), "2000-01-01");
        var identity = await ReadPlayback(artist, source);
        var first = await CreateCollection();
        var second = await CreateCollection();
        var intake = scope.ServiceProvider.GetRequiredService<ArchiveIntakeService>();
        var projection = ArchiveObservationProjector.Project(
            new ArchiveItemId(source.upstream_identifier),
            RecordingJson(source.upstream_identifier, "2000-01-01", "Fixture track")
        );
        var observation = await intake.RetainObservationAsync(projection);
        (await intake.RetainObservationAsync(projection)).Should().Be(observation);
        foreach (var collection in new[] { first, second })
        {
            var selector = await CreateSelector(collection);
            await Collections.SetProviderMembershipAsync(
                selector.Uuid,
                source.upstream_identifier,
                true,
                "Discovered through selector"
            );
            var linked = await Resolutions.ResolveExistingAsync(
                new(source.upstream_identifier) { observation_id = observation }
            );
            linked.status.Should().Be("resolved");
            linked.canonical_source_uuid.Should().Be(source.uuid);
            await AssertContainsOnly(collection.Uuid, source.uuid);
        }
        (await intake.IsCohortArtistAsync(new ArtistId(artist.uuid))).Should().BeFalse();
        (await intake.PendingObservationsAsync())
            .Should()
            .NotContain(new ArchiveItemId(source.upstream_identifier));
        var transaction = scope.ServiceProvider.GetRequiredService<CatalogImportTransaction>();
        var sources = scope.ServiceProvider.GetRequiredService<SourceService>();
        (
            await transaction.RunAsync(
                DateTimeOffset.UtcNow.AddMinutes(1),
                CancellationToken.None,
                () => sources.IsArchiveItemIntakeOwnedForUpdate(source.upstream_identifier)
            )
        )
            .Should()
            .BeFalse();

        using var upstream = new FixtureHttpServer();
        upstream.Respond = uri =>
            uri.AbsolutePath.StartsWith("/metadata/")
                ? RecordingJson(source.upstream_identifier, "2000-01-01", "Native metadata update")
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
                                    identifier = source.upstream_identifier,
                                    date = "2000-01-01",
                                    addeddate = DateTime.UtcNow,
                                },
                            },
                        },
                    }
                );
        using var importScope = host.Services.CreateScope();
        var importer = importScope.ServiceProvider.GetRequiredService<ArchiveOrgImporter>();
        importer.UseIngestionHttpClient(
            new HttpClient(new FixtureRedirectHandler(upstream.Address))
        );
        var stats = await importer.ImportSpecificShowDataForArtist(
            artist,
            artist.upstream_sources.Single(),
            source.upstream_identifier,
            null
        );
        stats.Updated.Should().Be(1);
        (await ReadPlayback(artist, source)).Should().BeEquivalentTo(identity);
        var current = await sources.ForUpstreamIdentifierFromPrimary(
            artist,
            source.upstream_identifier
        );
        var full = (
            await sources.FullSourcesForShow(artist, new Show { id = current!.show_id!.Value })
        ).Single();
        full.sets.SelectMany(set => set.tracks)
            .Should()
            .ContainSingle()
            .Which.title.Should()
            .Be("Native metadata update");
        await AssertContainsOnly(first.Uuid, source.uuid);
        await AssertContainsOnly(second.Uuid, source.uuid);
    }

    [Test]
    public async Task ExactResolutionRequiresAProviderQualifiedRecordingLinkAndHoldsDuplicateMatches()
    {
        var item = NewItem();
        var expectedArtist = await CreateArtist("Archive performer");
        var expected = await PublishRecording(expectedArtist, item, "2000-01-01");
        var otherProvider = await CreateArtist("Other provider performer");
        var other = await PublishRecording(otherProvider, item, "2000-01-01", providerId: 2);
        var reviewArtist = await CreateArtist("Review link performer");
        var review = await PublishRecording(reviewArtist, item, "2000-01-01");
        var links = scope.ServiceProvider.GetRequiredService<LinkService>();
        await links.AddLinksForSource(
            review,
            [
                new Link
                {
                    source_id = review.id,
                    upstream_source_id = 1,
                    for_source = false,
                    for_reviews = true,
                    url = "https://archive.org/details/" + item,
                    label = "Review only",
                },
            ]
        );
        var collection = await CreateCollection();
        var selector = await CreateSelector(collection);
        await Collections.SetProviderMembershipAsync(
            selector.Uuid,
            item,
            true,
            "Archive source membership"
        );

        var result = await Resolutions.ResolveExistingAsync(new(item));
        result.status.Should().Be("resolved");
        result.selected_artist_uuid.Should().Be(expectedArtist.uuid);
        result.canonical_source_uuid.Should().Be(expected.uuid);
        await AssertContainsOnly(collection.Uuid, expected.uuid);

        await links.AddLinksForSource(
            other,
            [
                new Link
                {
                    source_id = other.id,
                    upstream_source_id = 1,
                    for_source = true,
                    url = "https://archive.org/details/" + item,
                    label = "Duplicate Archive claim",
                },
            ]
        );
        (await Resolutions.ResolveExistingAsync(new(item))).status.Should().Be("conflict");
        await AssertContainsOnly(collection.Uuid, expected.uuid);
        (await Resolutions.FindAsync(item))!.status.Should().Be("conflict");
        (await Collections.GetProviderMembershipAsync(selector.Uuid, item))!
            .active.Should()
            .BeTrue();
    }

    [Test]
    public async Task RemovingArchiveRecordingQualificationRetainsMembershipButStopsProviderPlayback()
    {
        var artist = await CreateArtist("Qualification changed performer");
        var source = await PublishRecording(artist, NewItem(), "2000-01-01");
        var identity = await ReadPlayback(artist, source);
        var collection = await CreateCollection();
        var selector = await CreateSelector(collection);
        await Collections.SetProviderMembershipAsync(
            selector.Uuid,
            source.upstream_identifier,
            true,
            "Exact provider inclusion"
        );
        await Resolutions.ResolveExistingAsync(new(source.upstream_identifier));
        await AssertContainsOnly(collection.Uuid, source.uuid);

        await scope
            .ServiceProvider.GetRequiredService<LinkService>()
            .AddLinksForSource(
                source,
                [
                    new Link
                    {
                        source_id = source.id,
                        upstream_source_id = 1,
                        for_source = false,
                        for_reviews = true,
                        url = "https://archive.org/details/" + source.upstream_identifier,
                        label = "Only review attribution remains",
                    },
                ]
            );
        await AssertContainsOnly(collection.Uuid);
        var retained = (
            await Collections.GetProviderMembershipAsync(selector.Uuid, source.upstream_identifier)
        )!;
        retained.active.Should().BeTrue();
        retained.source_uuid.Should().Be(source.uuid);
        retained.reference_available.Should().BeFalse();
        var missing = await Resolutions.ResolveExistingAsync(new(source.upstream_identifier));
        missing.status.Should().Be("missing");
        missing.canonical_source_uuid.Should().Be(source.uuid);
        var transaction = scope.ServiceProvider.GetRequiredService<CatalogImportTransaction>();
        (
            await transaction.RunAsync(
                DateTimeOffset.UtcNow.AddMinutes(1),
                CancellationToken.None,
                () =>
                    scope
                        .ServiceProvider.GetRequiredService<SourceService>()
                        .IsArchiveItemIntakeOwnedForUpdate(source.upstream_identifier)
            )
        )
            .Should()
            .BeFalse();
        (await ReadPlayback(artist, source)).Should().BeEquivalentTo(identity);
    }

    [Test]
    public async Task ConflictingPerformerEvidenceHoldsMembershipWithoutReassigningTheCanonicalSource()
    {
        var artist = await CreateArtist("Original performer");
        var different = await CreateArtist("Different performer");
        var source = await PublishRecording(artist, NewItem(), "2000-01-01");
        var identity = await ReadPlayback(artist, source);
        var collection = await CreateCollection();
        var selector = await CreateSelector(collection);
        await Collections.SetProviderMembershipAsync(
            selector.Uuid,
            source.upstream_identifier,
            true,
            "Reviewed membership"
        );
        var result = await Resolutions.ResolveExistingAsync(
            new(source.upstream_identifier)
            {
                expected_artist_uuid = different.uuid,
                decision_basis = "reviewed performer evidence",
            }
        );
        result.status.Should().Be("conflict");
        await AssertContainsOnly(collection.Uuid);
        (await ReadPlayback(artist, source)).Should().BeEquivalentTo(identity);
        (await Resolutions.FindAsync(source.upstream_identifier))!.status.Should().Be("conflict");
    }

    [Test]
    public async Task LaterEvidenceConflictKeepsTheAcceptedRecordingAndRetainedObservationCannotNameAnotherItem()
    {
        var artist = await CreateArtist("Pinned native performer");
        var source = await PublishRecording(artist, NewItem(), "2000-01-01");
        var collection = await CreateCollection();
        var selector = await CreateSelector(collection);
        await Collections.SetProviderMembershipAsync(
            selector.Uuid,
            source.upstream_identifier,
            true,
            "Reviewed exact recording"
        );
        var intake = scope.ServiceProvider.GetRequiredService<ArchiveIntakeService>();
        var unrelatedItem = NewItem();
        var unrelatedObservation = await intake.RetainObservationAsync(
            ArchiveObservationProjector.Project(
                new ArchiveItemId(unrelatedItem),
                RecordingJson(unrelatedItem, "2000-01-01", "Different item")
            )
        );
        Func<Task> invalidEvidence = () =>
            Resolutions.ResolveExistingAsync(
                new(source.upstream_identifier) { observation_id = unrelatedObservation }
            );
        await invalidEvidence.Should().ThrowAsync<ArgumentException>();
        (await Resolutions.FindAsync(source.upstream_identifier))!.status.Should().Be("pending");

        var accepted = await Resolutions.ResolveExistingAsync(new(source.upstream_identifier));
        accepted.canonical_source_uuid.Should().Be(source.uuid);
        var held = await Resolutions.ResolveExistingAsync(
            new(source.upstream_identifier)
            {
                conflict_reason = "New metadata names a different performer",
            }
        );
        held.status.Should().Be("conflict");
        held.canonical_source_uuid.Should().Be(source.uuid);
        held.selected_artist_uuid.Should().Be(artist.uuid);
        await AssertContainsOnly(collection.Uuid, source.uuid);
        (await Collections.GetProviderMembershipAsync(selector.Uuid, source.upstream_identifier))!
            .reference_available.Should()
            .BeTrue();
    }

    [TestCase("title", "Different performance billing")]
    [TestCase("date", "2001-02-03")]
    public async Task AcceptedMetadataNormalizesCreatorOrderButHoldsLaterAttributionChanges(
        string changedField,
        string changedValue
    )
    {
        var artist = await CreateArtist("Reviewed native collaboration");
        var source = await PublishRecording(artist, NewItem(), "2000-01-01");
        var collection = await CreateCollection();
        var selector = await CreateSelector(collection);
        await Collections.SetProviderMembershipAsync(
            selector.Uuid,
            source.upstream_identifier,
            true,
            "Exact native source"
        );
        var intake = scope.ServiceProvider.GetRequiredService<ArchiveIntakeService>();
        var original = JObject.Parse(
            RecordingJson(source.upstream_identifier, "2000-01-01", "Original file title")
        );
        original["metadata"]!["creator"] = new JArray(artist.name, "Guest");
        original["metadata"]!["year"] = "2000";
        var acceptedObservation = await intake.RetainObservationAsync(
            ArchiveObservationProjector.Project(
                new ArchiveItemId(source.upstream_identifier),
                original.ToString()
            )
        );
        var accepted = await Resolutions.ResolveExistingAsync(
            new(source.upstream_identifier) { observation_id = acceptedObservation }
        );
        accepted.status.Should().Be("resolved");

        var harmless = (JObject)original.DeepClone();
        harmless["metadata"]!["creator"] = new JArray("  guest ", artist.name.ToUpperInvariant());
        harmless["metadata"]!["title"] = " fixture   RECORDING ";
        harmless["metadata"]!["description"] = "New liner notes";
        harmless["files"]![0]!["title"] = "Changed file title";
        var harmlessObservation = await intake.RetainObservationAsync(
            ArchiveObservationProjector.Project(
                new ArchiveItemId(source.upstream_identifier),
                harmless.ToString()
            )
        );
        var unchangedAssignment = await Resolutions.ResolveExistingAsync(
            new(source.upstream_identifier) { observation_id = harmlessObservation }
        );
        unchangedAssignment.status.Should().Be("resolved");
        unchangedAssignment.accepted_observation_id.Should().Be(acceptedObservation);

        var changed = (JObject)harmless.DeepClone();
        changed["metadata"]![changedField] = changedValue;
        var latestObservation = await intake.RetainObservationAsync(
            ArchiveObservationProjector.Project(
                new ArchiveItemId(source.upstream_identifier),
                changed.ToString()
            )
        );
        var held = await Resolutions.ResolveExistingAsync(
            new(source.upstream_identifier) { observation_id = latestObservation }
        );
        held.status.Should().Be("conflict");
        held.accepted_observation_id.Should().Be(acceptedObservation);
        held.observation_id.Should().Be(latestObservation);
        held.canonical_source_uuid.Should().Be(source.uuid);
        held.selected_artist_uuid.Should().Be(artist.uuid);
        var catalogOnly = await Resolutions.ResolveExistingAsync(new(source.upstream_identifier));
        catalogOnly
            .status.Should()
            .Be("conflict", "an ID-only recheck does not replace the conflicting retained facts");
        catalogOnly.observation_id.Should().Be(latestObservation);
        catalogOnly.accepted_observation_id.Should().Be(acceptedObservation);
        await AssertContainsOnly(collection.Uuid, source.uuid);
    }

    [Test]
    public async Task DuplicateInitialMatchesStayHeldAndNeverChooseTheFirstCatalogArtist()
    {
        var item = NewItem();
        await PublishRecording(await CreateArtist("First duplicate performer"), item, "2000-01-01");
        await PublishRecording(
            await CreateArtist("Second duplicate performer"),
            item,
            "2000-01-01"
        );
        var collection = await CreateCollection();
        var selector = await CreateSelector(collection);
        await Collections.SetProviderMembershipAsync(
            selector.Uuid,
            item,
            true,
            "Unresolved duplicate"
        );
        var held = await Resolutions.ResolveExistingAsync(new(item));
        held.status.Should().Be("conflict");
        held.canonical_source_uuid.Should().BeNull();
        held.selected_artist_uuid.Should().BeNull();
        await AssertContainsOnly(collection.Uuid);
    }

    [Test]
    public async Task UnresolvedMembershipIsRetainedForOperatorsAndAppearsOnlyAfterExactResolution()
    {
        var collection = await CreateCollection();
        var selector = await CreateSelector(collection);
        var item = NewItem();
        await Collections.SetProviderMembershipAsync(
            selector.Uuid,
            item,
            true,
            "Observed before import"
        );
        var intake = scope.ServiceProvider.GetRequiredService<ArchiveIntakeService>();
        var observation = await intake.RetainObservationAsync(
            ArchiveObservationProjector.Project(
                new ArchiveItemId(item),
                RecordingJson(item, "2000-01-01", "Observed before native import")
            )
        );
        (await Resolutions.ResolveExistingAsync(new(item) { observation_id = observation }))
            .status.Should()
            .Be("pending");
        await AssertContainsOnly(collection.Uuid);
        (await Collections.ArtistsAsync(collection.Uuid))!.items.Should().BeEmpty();
        var retained = (await Collections.GetProviderMembershipAsync(selector.Uuid, item))!;
        retained.active.Should().BeTrue();
        retained.reference_available.Should().BeFalse();
        (await Resolutions.FindAsync(item))!.item_identifier.Should().Be(item);

        var artist = await CreateArtist("Later resolved performer");
        var source = await PublishRecording(artist, item, "2000-01-01");
        var accepted = await Resolutions.ResolveExistingAsync(new(item));
        accepted.canonical_source_uuid.Should().Be(source.uuid);
        accepted.accepted_observation_id.Should().Be(observation);
        await AssertContainsOnly(collection.Uuid, source.uuid);

        var changed = JObject.Parse(RecordingJson(item, "2001-02-03", "Later changed metadata"));
        var laterObservation = await intake.RetainObservationAsync(
            ArchiveObservationProjector.Project(new ArchiveItemId(item), changed.ToString())
        );
        var held = await Resolutions.ResolveExistingAsync(
            new(item) { observation_id = laterObservation }
        );
        held.status.Should().Be("conflict");
        held.accepted_observation_id.Should().Be(observation);
        await AssertContainsOnly(collection.Uuid, source.uuid);
    }

    [Test]
    public async Task SourceAndArtistDeletionRetainMembershipAndNeverSubstituteAnotherRecording()
    {
        var artist = await CreateArtist("Deleted performer");
        var source = await PublishRecording(artist, NewItem(), "2000-01-01");
        await PublishRecording(artist, NewItem(), "2000-01-01");
        var collection = await CreateCollection();
        var selector = await CreateSelector(collection);
        await Collections.SetEditorialRecordingMembershipAsync(
            collection.Uuid,
            source.uuid,
            true,
            "Pinned recording"
        );
        await Collections.SetProviderMembershipAsync(
            selector.Uuid,
            source.upstream_identifier,
            true,
            "Provider recording"
        );
        await Resolutions.ResolveExistingAsync(new(source.upstream_identifier));
        await scope
            .ServiceProvider.GetRequiredService<SourceService>()
            .RemoveSourcesWithUpstreamIdentifiers(artist.id, [source.upstream_identifier]);

        await AssertContainsOnly(collection.Uuid);
        (await Resolutions.ResolveExistingAsync(new(source.upstream_identifier)))
            .status.Should()
            .Be("missing");
        (await Collections.GetEditorialMembershipAsync(collection.Uuid, source.uuid))!
            .reference_available.Should()
            .BeFalse();
        (await Collections.GetProviderMembershipAsync(selector.Uuid, source.upstream_identifier))!
            .active.Should()
            .BeTrue();
        var replacementArtist = await CreateArtist("Replacement performer requiring repair");
        await PublishRecording(replacementArtist, source.upstream_identifier, "2000-01-01");
        var replacement = await Resolutions.ResolveExistingAsync(new(source.upstream_identifier));
        replacement.status.Should().Be("conflict");
        replacement.canonical_source_uuid.Should().Be(source.uuid);
        await AssertContainsOnly(collection.Uuid);
        await Collections.SetArtistMembershipAsync(
            collection.Uuid,
            artist.uuid,
            true,
            "Reviewed project"
        );
        (await Collections.RecordingsAsync(collection.Uuid))!.items.Should().ContainSingle();
        await scope
            .ServiceProvider.GetRequiredService<ArtistService>()
            .RemoveAllContentForArtist(artist);
        await db.WithWriteConnection(connection =>
            connection.ExecuteAsync("DELETE FROM artists WHERE id=@id", new { artist.id })
        );
        await AssertContainsOnly(collection.Uuid);
        var retained = (await Collections.GetArtistMembershipAsync(collection.Uuid, artist.uuid))!;
        retained.active.Should().BeTrue();
        retained.reference_available.Should().BeFalse();
        (await Resolutions.FindAsync(source.upstream_identifier))!
            .canonical_source_uuid.Should()
            .Be(source.uuid);
    }

    [TestCase("cohort")]
    [TestCase("needs_review")]
    public async Task ReviewedNativeAutoCreatedArtistIsVisibleButIntakeCohortWaitsForReveal(
        string identityStatus
    )
    {
        var native = await CreateArtist("Native auto-created performer");
        var cohort = await CreateArtist("Unrevealed intake performer");
        var nativeSource = await PublishRecording(native, NewItem(), "2000-01-01");
        var cohortSource = await PublishRecording(cohort, NewItem(), "2000-01-01");
        await db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                """
                UPDATE artists SET featured=2 WHERE id=ANY(@ids);
                INSERT INTO normalization.archive_artist_candidates(
                    candidate_id,creator_key,proposed_name,proposed_slug,reviewed_creator_keys,
                    identity_status,artist_uuid,artist_id,creation_slug)
                VALUES(@candidate,@key,@name,@slug,ARRAY[@key],@identityStatus,@uuid,@id,@slug);
                """,
                new
                {
                    ids = new[] { native.id, cohort.id },
                    candidate = Guid.NewGuid(),
                    key = cohort.slug,
                    name = cohort.name,
                    slug = cohort.slug,
                    uuid = cohort.uuid,
                    id = cohort.id,
                    identityStatus,
                }
            )
        );
        var collection = await CreateCollection();
        await Collections.SetEditorialRecordingMembershipAsync(
            collection.Uuid,
            nativeSource.uuid,
            true,
            "Explicit native selection"
        );
        await Collections.SetEditorialRecordingMembershipAsync(
            collection.Uuid,
            cohortSource.uuid,
            true,
            "Pending intake selection"
        );
        await AssertContainsOnly(collection.Uuid, nativeSource.uuid);
        (
            await scope
                .ServiceProvider.GetRequiredService<ArtistService>()
                .FindArtistForImport(native.uuid.ToString())
        )!
            .featured.Should()
            .Be((int)ArtistFeaturedFlags.AutoCreated);

        // Simulate the existing intake rebuild acknowledgement's visibility transition.
        await db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                "UPDATE artists SET featured=featured & ~2 WHERE id=@id",
                new { cohort.id }
            )
        );
        await AssertContainsOnly(collection.Uuid, nativeSource.uuid, cohortSource.uuid);
    }

    [TestCase("D")]
    [TestCase("N")]
    public async Task UuidShapedSlugsCannotAliasAnotherCollectionIdentity(string format)
    {
        var uuid = Guid.NewGuid();
        var definition = new CollectionDefinition(
            uuid,
            Guid.NewGuid().ToString(format),
            "Ambiguous slug",
            "Fixture",
            true
        );
        Func<Task> invalidSlug = () => Collections.SaveCollectionAsync(definition);
        await invalidSlug.Should().ThrowAsync<ArgumentException>();
        (await Collections.FindAsync(uuid.ToString())).Should().BeNull();

        var valid = definition with { Slug = "collection-" + uuid.ToString("N") };
        await Collections.SaveCollectionAsync(valid);
        (await Collections.FindAsync(uuid.ToString()))!.uuid.Should().Be(uuid);
        (await Collections.FindAsync(valid.Slug))!.uuid.Should().Be(uuid);
    }

    [Test]
    public async Task InvalidMembershipReferencesAreRejectedWithoutCreatingPublishedContents()
    {
        var collection = await CreateCollection();
        var missing = Guid.NewGuid();
        Func<Task> artist = () =>
            Collections.SetArtistMembershipAsync(collection.Uuid, missing, true, "Invalid artist");
        Func<Task> recording = () =>
            Collections.SetEditorialRecordingMembershipAsync(
                collection.Uuid,
                missing,
                true,
                "Invalid source"
            );
        Func<Task> selector = () =>
            Collections.SetProviderMembershipAsync(missing, NewItem(), true, "Invalid selector");
        await artist.Should().ThrowAsync<ArgumentException>();
        await recording.Should().ThrowAsync<ArgumentException>();
        await selector.Should().ThrowAsync<ArgumentException>();
        await AssertContainsOnly(collection.Uuid);
    }

    private async Task<CollectionDefinition> CreateCollection(bool published = true)
    {
        var uuid = Guid.NewGuid();
        var definition = new CollectionDefinition(
            uuid,
            "collection-" + uuid.ToString("N"),
            "Fixture collection " + uuid,
            "Reviewed fixture",
            published
        );
        await Collections.SaveCollectionAsync(definition);
        return definition;
    }

    private async Task<CollectionSelectorDefinition> CreateSelector(CollectionDefinition collection)
    {
        var selector = new CollectionSelectorDefinition(
            Guid.NewGuid(),
            collection.Uuid,
            1,
            "selector-" + Guid.NewGuid().ToString("N")
        );
        await Collections.SaveSelectorAsync(selector);
        return selector;
    }

    private async Task<Artist> CreateArtist(string name)
    {
        var uuid = Guid.NewGuid();
        var slug = "collection-artist-" + uuid.ToString("N");
        var id = await db.WithWriteConnection(connection =>
            connection.QuerySingleAsync<int>(
                "INSERT INTO artists(name,slug,uuid,updated_at) VALUES(@name,@slug,@uuid,now()) RETURNING id",
                new
                {
                    name,
                    slug,
                    uuid,
                }
            )
        );
        await db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                """
                INSERT INTO features(artist_id) VALUES(@id);
                INSERT INTO artists_upstream_sources(artist_id,upstream_source_id,upstream_identifier) VALUES(@id,1,@slug);
                """,
                new { id, slug }
            )
        );
        return (
            await scope
                .ServiceProvider.GetRequiredService<ArtistService>()
                .FindArtistForImport(uuid.ToString())
        )!;
    }

    private async Task<Source> PublishRecording(
        Artist artist,
        string item,
        string date,
        int providerId = 1
    )
    {
        using var writeScope = host.Services.CreateScope();
        var importer = writeScope.ServiceProvider.GetRequiredService<ArchiveOrgImporter>();
        var transaction = writeScope.ServiceProvider.GetRequiredService<CatalogImportTransaction>();
        var metadata = JsonConvert.DeserializeObject<RootObject>(
            RecordingJson(item, date, "Fixture track"),
            new TolerantStringConverter()
        )!;
        var source = await transaction.RunAsync(
            DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None,
            () =>
                importer.WriteResolvedRecordingAsync(
                    artist,
                    providerId,
                    new SearchDoc { identifier = item, addeddate = DateTime.UtcNow },
                    metadata,
                    date
                )
        );
        var rebuild = writeScope.ServiceProvider.GetRequiredService<CatalogRebuildService>();
        await rebuild.RebuildShows(artist);
        await rebuild.RebuildYears(artist);
        return source;
    }

    private static string RecordingJson(string item, string date, string title) =>
        JsonConvert.SerializeObject(
            new
            {
                metadata = new
                {
                    identifier = item,
                    date,
                    title = "Fixture recording",
                    venue = "Fixture Hall",
                    coverage = "Test City",
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

    private async Task<PlaybackIdentity> ReadPlayback(Artist artist, Source source)
    {
        var sources = scope.ServiceProvider.GetRequiredService<SourceService>();
        var current = (
            await sources.ForUpstreamIdentifierFromPrimary(artist, source.upstream_identifier)
        )!;
        var full = (
            await sources.FullSourcesForShow(artist, new Show { id = current.show_id!.Value })
        ).Single(item => item.uuid == source.uuid);
        return new(
            full.uuid,
            full.sets.Select(set => set.uuid).ToArray(),
            full.sets.SelectMany(set => set.tracks).Select(track => track.uuid).ToArray(),
            full.sets.SelectMany(set => set.tracks).Select(track => track.mp3_url!).ToArray()
        );
    }

    private async Task AssertContainsOnly(Guid collection, params Guid[] sources) =>
        (await Collections.RecordingsAsync(collection))!
            .items.Select(item => item.source_uuid)
            .Should()
            .BeEquivalentTo(sources);

    private async Task<Guid[]> ReadEveryRecording(Guid collection)
    {
        var ids = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = (await Collections.RecordingsAsync(collection, limit: 1, cursor: cursor))!;
            ids.AddRange(page.items.Select(item => item.source_uuid));
            cursor = page.next_cursor;
            ids.Count.Should()
                .BeLessThanOrEqualTo(20, "pagination must terminate without repeating results");
        } while (cursor != null);
        return ids.ToArray();
    }

    private static string NewItem() => "collection-item-" + Guid.NewGuid().ToString("N");

    private ShowsController CreateShowsController() =>
        new(
            host.Services.GetRequiredService<RedisService>(),
            db,
            scope.ServiceProvider.GetRequiredService<ArtistService>(),
            scope.ServiceProvider.GetRequiredService<ShowService>(),
            scope.ServiceProvider.GetRequiredService<SourceService>()
        )
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

    private sealed record PlaybackIdentity(
        Guid SourceUuid,
        Guid[] SetUuids,
        Guid[] TrackUuids,
        string[] Mp3Urls
    );

    // API reads may refresh popularity. This fixture has no background worker.
    private sealed class IgnoredPopularityJobs : Hangfire.IBackgroundJobClient
    {
        public string Create(Hangfire.Common.Job job, Hangfire.States.IState state) =>
            "fixture-popularity";

        public bool ChangeState(string jobId, Hangfire.States.IState state, string expectedState) =>
            true;
    }
}
