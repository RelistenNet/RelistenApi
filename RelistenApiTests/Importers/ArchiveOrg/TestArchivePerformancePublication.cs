using Dapper;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Relisten;
using Relisten.Api.Models;
using Relisten.Data;
using Relisten.Import;
using Relisten.Ingestion;
using Relisten.Vendor.ArchiveOrg;
using Relisten.Vendor.ArchiveOrg.Metadata;

namespace RelistenApiTests.Importers.ArchiveOrg;

/// <summary>Exercises the shared Archive writer used by native and reviewed intake imports.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestArchivePerformancePublication
{
    private IHost host = null!;
    private DbService db = null!;
    private Artist artist = null!;
    private string originalWrite = null!;
    private string originalRead = null!;

    [OneTimeSetUp]
    public void Start()
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
    }

    [SetUp]
    public async Task SeedArtist()
    {
        var uuid = Guid.NewGuid();
        var id = await db.WithWriteConnection(connection =>
            connection.QuerySingleAsync<int>(
                """
                INSERT INTO upstream_sources(id,name,url) VALUES(1,'archive.org','https://archive.org') ON CONFLICT DO NOTHING;
                INSERT INTO artists(name,slug,uuid,updated_at) VALUES('Performance fixture',@slug,@uuid,now()) RETURNING id;
                """,
                new { uuid, slug = "performance-" + uuid.ToString("N") }
            )
        );
        await db.WithWriteConnection(connection =>
            connection.ExecuteAsync("INSERT INTO features(artist_id) VALUES(@id)", new { id })
        );
        artist = (
            await host
                .Services.GetRequiredService<ArtistService>()
                .FindArtistForImport(uuid.ToString())
        )!;
    }

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
    public async Task ExplicitEarlyAndLateSplitTheOldShowAndPreservePlaybackAcrossCorrectionAndRetry()
    {
        var early = Recording("Fixture live, Set 1 and Set 2");
        var late = Recording("Fixture live");
        var earlyBefore = await Publish(early);
        var lateBefore = await Publish(late);
        lateBefore.Show.Should().Be(earlyBefore.Show);

        early.metadata.title = "Fixture live (Early Show)";
        var acceptedEarly = await Publish(early);
        acceptedEarly.Should().BeEquivalentTo(earlyBefore);
        late.metadata.title = "Fixture live (Late Show)";
        var acceptedLate = await Publish(late);
        acceptedLate.Show.Should().NotBe(earlyBefore.Show);
        acceptedLate
            .Should()
            .BeEquivalentTo(lateBefore, options => options.Excluding(value => value.Show));
        (await Publish(late)).Should().BeEquivalentTo(acceptedLate);
        var anotherEarlyRecording = await Publish(Recording("Fixture live (Early Show)"));
        anotherEarlyRecording.Show.Should().Be(acceptedEarly.Show);

        // Metadata is authoritative. Moving the same known performance to its corrected
        // date changes neither its UUID nor any recording/track playback identity.
        late.metadata.date = "1975-03-08";
        var corrected = await Publish(late);
        corrected.Should().BeEquivalentTo(acceptedLate);
        var full = await ReadSource(late.metadata.identifier);
        full.display_date.Should().Be("1975-03-08");
        var correctedShow = await host
            .Services.GetRequiredService<ShowService>()
            .ShowWithSourcesForUuid(artist, corrected.Show);
        correctedShow.Should().NotBeNull();
        correctedShow!.display_date.Should().Be("1975-03-08");
        (await Publish(Recording("Fixture Late Show", "1975-03-08")))
            .Show.Should()
            .Be(corrected.Show);
    }

    [Test]
    public async Task OrdinaryDateCorrectionPreservesTheExistingShowUuid()
    {
        var recording = Recording("Fixture live, Set 1 and Set 2");
        var before = await Publish(recording);
        recording.metadata.date = "1975-03-08";
        (await Publish(recording)).Should().BeEquivalentTo(before);
        (await ReadSource(recording.metadata.identifier)).display_date.Should().Be("1975-03-08");
    }

    [Test]
    public async Task UncertainOrCombinedNewRecordingsDoNotCreatePlaybackRows()
    {
        foreach (var title in new[] { "Fixture Early Show & Late Show", "Fixture Early Show?" })
        {
            var recording = Recording(title);
            Func<Task> publish = () => Publish(recording);
            await publish.Should().ThrowAsync<ArchivePerformanceHeldException>();
            (
                await host
                    .Services.GetRequiredService<SourceService>()
                    .ForUpstreamIdentifierFromPrimary(artist, recording.metadata.identifier)
            )
                .Should()
                .BeNull();
        }
    }

    [Test]
    public async Task ContradictoryPublishedBillingRetainsPlaybackAndRecordsUnresolvedEvidence()
    {
        var recording = Recording("Fixture Early Show");
        var before = await Publish(recording);
        recording.metadata.title = "Fixture Late Show";
        recording.files[0].title = "Must remain unchanged";
        (await HoldAndCommit(recording)).Should().Be("performance_billing_changed");
        await host.Services.GetRequiredService<CatalogRebuildService>().RebuildShows(artist);
        (await Identity(recording.metadata.identifier)).Should().BeEquivalentTo(before);
        (await ReadSource(recording.metadata.identifier))
            .sets.Single()
            .tracks.Single()
            .title.Should()
            .Be("Fixture song");
        var retained = await db.WithWriteConnection(connection =>
            connection.QuerySingleAsync<string>(
                "SELECT a.assignment_kind FROM source_show_assignments a JOIN sources s ON s.id=a.source_id WHERE s.uuid=@Source",
                new { before.Source }
            )
        );
        retained.Should().Be("unresolved");
    }

    [Test]
    public async Task NewUnlabelledRecordingOnSplitDateIsHeldWhileExistingUncertainPlaybackSurvives()
    {
        var uncertain = Recording("Fixture live");
        var before = await Publish(uncertain);
        await Publish(Recording("Fixture Early Show"));
        await Publish(Recording("Fixture Late Show"));

        var newRecording = Recording("Another recording of Fixture live");
        (await HoldAndCommit(newRecording)).Should().Be("performance_required_on_split_date");
        (
            await host
                .Services.GetRequiredService<SourceService>()
                .ForUpstreamIdentifierFromPrimary(artist, newRecording.metadata.identifier)
        )
            .Should()
            .BeNull();

        await HoldAndCommit(uncertain);
        await host.Services.GetRequiredService<CatalogRebuildService>().RebuildShows(artist);
        (await Identity(uncertain.metadata.identifier)).Should().BeEquivalentTo(before);
    }

    [Test]
    public async Task DateCorrectionCannotMoveAPerformanceAwayFromAnotherAcceptedRecording()
    {
        var first = Recording("Fixture Early Show");
        var second = Recording("Fixture Early Show");
        var firstBefore = await Publish(first);
        var secondBefore = await Publish(second);
        secondBefore.Show.Should().Be(firstBefore.Show);

        first.metadata.date = "1975-03-08";
        (await HoldAndCommit(first)).Should().Be("performance_date_conflicts_with_recordings");
        await host.Services.GetRequiredService<CatalogRebuildService>().RebuildShows(artist);
        (await Identity(first.metadata.identifier)).Should().BeEquivalentTo(firstBefore);
        (await Identity(second.metadata.identifier)).Should().BeEquivalentTo(secondBefore);
        (await ReadSource(first.metadata.identifier)).display_date.Should().Be("1975-03-07");

        // Repeating either observation cannot flip the shared show's date. A group date
        // correction now requires a supported review of the conflicting recordings.
        (await Publish(second))
            .Should()
            .BeEquivalentTo(secondBefore);
        (await HoldAndCommit(first)).Should().Be("performance_date_conflicts_with_recordings");
        second.metadata.date = "1975-03-09";
        (await HoldAndCommit(second)).Should().Be("performance_date_conflicts_with_recordings");
        var show = await host
            .Services.GetRequiredService<ShowService>()
            .ShowWithSourcesForUuid(artist, firstBefore.Show);
        show!.display_date.Should().Be("1975-03-07");
    }

    [Test]
    public async Task EqualPartialDatesDoNotGroupIndependentRecordings()
    {
        var first = Recording("Fixture live", "1975-XX-XX");
        var second = Recording("Fixture live", "1975-XX-XX");
        var firstIdentity = await Publish(first);
        var secondIdentity = await Publish(second);
        firstIdentity.Show.Should().NotBe(secondIdentity.Show);
        (await Publish(first)).Should().BeEquivalentTo(firstIdentity);
    }

    private async Task<PlaybackIdentity> Publish(RootObject metadata)
    {
        using var scope = host.Services.CreateScope();
        var importer = scope.ServiceProvider.GetRequiredService<ArchiveOrgImporter>();
        await scope
            .ServiceProvider.GetRequiredService<CatalogImportTransaction>()
            .RunAsync(
                DateTimeOffset.UtcNow.AddMinutes(1),
                CancellationToken.None,
                () =>
                    importer.WriteResolvedRecordingAsync(
                        artist,
                        1,
                        Document(metadata),
                        metadata,
                        metadata.metadata.date
                    )
            );
        await scope
            .ServiceProvider.GetRequiredService<CatalogRebuildService>()
            .RebuildShows(artist);
        return await Identity(metadata.metadata.identifier);
    }

    private async Task<string> HoldAndCommit(RootObject metadata)
    {
        // Entry points catch within their transaction so held evidence commits while
        // source, set and track metadata retain the last accepted publication.
        using var scope = host.Services.CreateScope();
        var importer = scope.ServiceProvider.GetRequiredService<ArchiveOrgImporter>();
        return await scope
            .ServiceProvider.GetRequiredService<CatalogImportTransaction>()
            .RunAsync(
                DateTimeOffset.UtcNow.AddMinutes(1),
                CancellationToken.None,
                async () =>
                {
                    try
                    {
                        await importer.WriteResolvedRecordingAsync(
                            artist,
                            1,
                            Document(metadata),
                            metadata,
                            metadata.metadata.date
                        );
                    }
                    catch (ArchivePerformanceHeldException held)
                    {
                        return held.Message;
                    }
                    throw new AssertionException(
                        "The recording should require performance review."
                    );
                }
            );
    }

    private async Task<SourceFull> ReadSource(string identifier)
    {
        var service = host.Services.GetRequiredService<SourceService>();
        var current = (await service.ForUpstreamIdentifierFromPrimary(artist, identifier))!;
        return (
            await service.FullSourcesForShow(artist, new Show { id = current.show_id!.Value })
        ).Single(source => source.uuid == current.uuid);
    }

    private async Task<PlaybackIdentity> Identity(string identifier)
    {
        var source = await ReadSource(identifier);
        return new(
            source.uuid,
            source.show_uuid,
            source.sets.Select(set => set.uuid).ToArray(),
            source.sets.SelectMany(set => set.tracks).Select(track => track.uuid).ToArray(),
            source.sets.SelectMany(set => set.tracks).Select(track => track.mp3_url!).ToArray()
        );
    }

    private static SearchDoc Document(RootObject metadata) =>
        new() { identifier = metadata.metadata.identifier, addeddate = DateTime.UtcNow };

    private static RootObject Recording(string title, string date = "1975-03-07") =>
        new()
        {
            metadata = new Metadata
            {
                identifier = "performance-" + Guid.NewGuid().ToString("N"),
                title = title,
                date = date,
                venue = "Fixture Hall",
                coverage = "Test City",
            },
            files =
            [
                new()
                {
                    name = "track.mp3",
                    format = "VBR MP3",
                    title = "Fixture song",
                    length = "2:03",
                },
            ],
        };

    private sealed record PlaybackIdentity(
        Guid Source,
        Guid Show,
        Guid[] Sets,
        Guid[] Tracks,
        string[] Mp3Urls
    );

    private sealed class IgnoredPopularityJobs : Hangfire.IBackgroundJobClient
    {
        public string Create(Hangfire.Common.Job job, Hangfire.States.IState state) =>
            "fixture-popularity";

        public bool ChangeState(string jobId, Hangfire.States.IState state, string expectedState) =>
            true;
    }
}
