using System.Security.Cryptography;
using System.Text;
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
    public async Task UncertainOrCombinedNewRecordingsPublishOnTheDefaultShow()
    {
        Guid? defaultShow = null;
        foreach (var title in new[] { "Fixture Early Show & Late Show", "Fixture Early Show?" })
        {
            var recording = Recording(title);
            var published = await Publish(recording);
            defaultShow ??= published.Show;
            published.Show.Should().Be(defaultShow.Value);
            published.Tracks.Should().ContainSingle();
            (await Publish(recording)).Should().BeEquivalentTo(published);
            (await ReadAssignment(published.Source))
                .Kind.Should()
                .Be(title.Contains('&') ? "combined" : "unresolved");
        }
    }

    [Test]
    public async Task DescriptionOnlyCombinedCoveragePublishesWithoutAuthorizingEnrichment()
    {
        var recording = Recording("Fixture live at Fixture Hall");
        recording.metadata.description = "Early Show:\nTrack A\nLate Show:\nTrack B";
        var published = await Publish(recording);
        var source = (
            await host
                .Services.GetRequiredService<SourceService>()
                .ForUpstreamIdentifierFromPrimary(artist, recording.metadata.identifier)
        )!;
        var assignment = await ReadAssignment(source.uuid);
        assignment.Kind.Should().Be("combined");
        var evidence = Newtonsoft.Json.Linq.JObject.Parse(assignment.Evidence);
        ((string?)evidence["field"]).Should().Be("description");
        ((string?)evidence["value"]).Should().Be(recording.metadata.description);
        (
            await host
                .Services.GetRequiredService<ShowPerformanceService>()
                .CanEnrichSourceAsync(artist, source.id)
        )
            .Should()
            .BeFalse();
        (await Publish(recording)).Should().BeEquivalentTo(published);
    }

    [Test]
    public async Task ClearBillingMovesAnUnresolvedDefaultRecordingToTheSupportedPerformance()
    {
        var early = await Publish(Recording("Fixture Early Show"));
        var late = await Publish(Recording("Fixture Late Show"));
        var recording = Recording("Fixture live");
        var before = await Publish(recording);
        before.Show.Should().Be(early.Show);
        (await ReadAssignment(before.Source)).Kind.Should().Be("unresolved");

        recording.metadata.title = "Fixture Late Show";
        var reassigned = await Publish(recording);
        reassigned.Show.Should().Be(late.Show);
        reassigned
            .Should()
            .BeEquivalentTo(before, options => options.Excluding(value => value.Show));
        (await ReadAssignment(before.Source)).Kind.Should().Be("accepted");
        (await Publish(recording)).Should().BeEquivalentTo(reassigned);
    }

    [Test]
    public async Task ContradictoryPublishedBillingUpdatesAudioOnTheExistingPrimaryWithUnresolvedEvidence()
    {
        var recording = Recording("Fixture Early Show");
        var before = await Publish(recording);
        recording.metadata.title = "Fixture Late Show";
        recording.files[0].title = "Updated track title";
        (await Publish(recording)).Should().BeEquivalentTo(before);
        await host.Services.GetRequiredService<CatalogRebuildService>().RebuildShows(artist);
        (await Identity(recording.metadata.identifier)).Should().BeEquivalentTo(before);
        (await ReadSource(recording.metadata.identifier))
            .sets.Single()
            .tracks.Single()
            .title.Should()
            .Be("Updated track title");
        var retained = await db.WithWriteConnection(connection =>
            connection.QuerySingleAsync<string>(
                "SELECT a.assignment_kind FROM source_show_assignments a JOIN sources s ON s.id=a.source_id WHERE s.uuid=@Source",
                new { before.Source }
            )
        );
        retained.Should().Be("unresolved");
    }

    [Test]
    public async Task NewUnlabelledRecordingOnSplitDateUsesTheDefaultAndExistingUncertainPlaybackSurvives()
    {
        var uncertain = Recording("Fixture live");
        var before = await Publish(uncertain);
        await Publish(Recording("Fixture Early Show"));
        await Publish(Recording("Fixture Late Show"));

        var newRecording = Recording("Another recording of Fixture live");
        var publishedDefault = await Publish(newRecording);
        publishedDefault.Show.Should().Be(before.Show);
        (await ReadAssignment(publishedDefault.Source)).Kind.Should().Be("unresolved");

        await Publish(uncertain);
        await host.Services.GetRequiredService<CatalogRebuildService>().RebuildShows(artist);
        (await Identity(uncertain.metadata.identifier)).Should().BeEquivalentTo(before);
    }

    [Test]
    public async Task UnlabelledTitlePreservesReviewedDescriptionAndContradictionRetainsThePrimary()
    {
        var recording = Recording("Fixture live at Fixture Hall");
        recording.metadata.description = "Early Show: Fixture Hall";
        await Publish(recording);
        var source = (
            await host
                .Services.GetRequiredService<SourceService>()
                .ForUpstreamIdentifierFromPrimary(artist, recording.metadata.identifier)
        )!;
        var evidence = Newtonsoft.Json.JsonConvert.SerializeObject(
            new
            {
                basis = "reviewed description heading",
                provider = "archive.org",
                field = "description",
                observation_id = Guid.NewGuid(),
                content_hash = "retained-observation-hash",
                value = recording.metadata.description,
                reviewed_fields = new Dictionary<string, string>
                {
                    ["metadata.description"] = Convert.ToHexString(
                        SHA256.HashData(Encoding.UTF8.GetBytes(recording.metadata.description))
                    ),
                },
            }
        );
        var assigned = await host
            .Services.GetRequiredService<ShowPerformanceService>()
            .AssignSourceAsync(
                artist,
                source.id,
                "reviewed:" + Guid.NewGuid(),
                recording.metadata.date,
                PerformanceType.Early,
                "Early show",
                evidence
            );
        await host.Services.GetRequiredService<CatalogRebuildService>().RebuildShows(artist);
        var before = await Identity(recording.metadata.identifier);
        before.Show.Should().Be(assigned.uuid);
        var beforeAssignment = await ReadAssignment(source.uuid);

        (await Publish(recording)).Should().BeEquivalentTo(before);
        (await ReadAssignment(source.uuid)).Should().BeEquivalentTo(beforeAssignment);
        var show = await host
            .Services.GetRequiredService<ShowService>()
            .ShowWithSourcesForUuid(artist, assigned.uuid);
        show!.performance_type.Should().Be("early");
        show.performance_label.Should().Be("Early show");

        recording.metadata.date = "1975-03-08";
        (await Publish(recording)).Should().BeEquivalentTo(before);
        (await ReadAssignment(source.uuid)).Should().BeEquivalentTo(beforeAssignment);
        (
            await host
                .Services.GetRequiredService<ShowService>()
                .ShowWithSourcesForUuid(artist, assigned.uuid)
        )!
            .display_date.Should()
            .Be("1975-03-08");

        foreach (
            var (title, kind) in new[]
            {
                ("Fixture Late Show", "unresolved"),
                ("Fixture Early Show & Late Show", "combined"),
                ("Fixture Late Show", "unresolved"),
            }
        )
        {
            recording.metadata.title = title;
            (await Publish(recording)).Should().BeEquivalentTo(before);
            var current = await ReadAssignment(source.uuid);
            current.Kind.Should().Be(kind);
            current.Key.Should().Be(beforeAssignment.Key);
            current.Type.Should().Be("early");
            var retained = Newtonsoft.Json.Linq.JObject.Parse(current.Evidence);
            retained["current_evidence_conflict"].Should().NotBeNull();
            retained.Remove("current_evidence_conflict");
            Newtonsoft
                .Json.Linq.JToken.DeepEquals(
                    retained,
                    Newtonsoft.Json.Linq.JObject.Parse(beforeAssignment.Evidence)
                )
                .Should()
                .BeTrue();
        }

        recording.metadata.title = "Fixture Early Show";
        (await Publish(recording)).Should().BeEquivalentTo(before);
        (await ReadAssignment(source.uuid)).Should().BeEquivalentTo(beforeAssignment);
    }

    [Test]
    public async Task ChangedReviewedDescriptionUpdatesAudioWithUnresolvedPerformanceAndRetainedReviewBasis()
    {
        var recording = Recording("Fixture live at Fixture Hall");
        recording.metadata.description = "Early Show: Fixture Hall";
        await Publish(recording);
        var source = (
            await host
                .Services.GetRequiredService<SourceService>()
                .ForUpstreamIdentifierFromPrimary(artist, recording.metadata.identifier)
        )!;
        var key = "reviewed:" + Guid.NewGuid();

        Task<Show> Review(PerformanceType type, string label) =>
            host
                .Services.GetRequiredService<ShowPerformanceService>()
                .AssignSourceAsync(
                    artist,
                    source.id,
                    key,
                    recording.metadata.date,
                    type,
                    label,
                    Newtonsoft.Json.JsonConvert.SerializeObject(
                        new
                        {
                            basis = "reviewed description heading",
                            field = "description",
                            value = recording.metadata.description,
                            reviewed_fields = new Dictionary<string, string>
                            {
                                ["metadata.description"] = Convert.ToHexString(
                                    SHA256.HashData(
                                        Encoding.UTF8.GetBytes(recording.metadata.description)
                                    )
                                ),
                            },
                        }
                    )
                );

        await Review(PerformanceType.Early, "Early show");
        await host.Services.GetRequiredService<CatalogRebuildService>().RebuildShows(artist);
        var before = await Identity(recording.metadata.identifier);
        var accepted = await ReadAssignment(source.uuid);

        recording.metadata.description = "Late Show: Fixture Hall";
        (await Publish(recording)).Should().BeEquivalentTo(before);
        (await Publish(recording)).Should().BeEquivalentTo(before);
        (await Identity(recording.metadata.identifier)).Should().BeEquivalentTo(before);
        var held = await ReadAssignment(source.uuid);
        held.Kind.Should().Be("unresolved");
        held.Key.Should().Be(accepted.Key);
        var heldEvidence = Newtonsoft.Json.Linq.JObject.Parse(held.Evidence);
        ((string?)heldEvidence["current_evidence_conflict"]?["diagnostic"])
            .Should()
            .Be("reviewed_performance_evidence_changed");
        heldEvidence.Remove("current_evidence_conflict");
        Newtonsoft
            .Json.Linq.JToken.DeepEquals(
                heldEvidence,
                Newtonsoft.Json.Linq.JObject.Parse(accepted.Evidence)
            )
            .Should()
            .BeTrue();
        (await ReadSource(recording.metadata.identifier))
            .description.Should()
            .Be("Late Show: Fixture Hall");

        await Review(PerformanceType.Late, "Late show");
        var reviewedAgain = await ReadAssignment(source.uuid);
        (await Publish(recording)).Should().BeEquivalentTo(before);
        (await ReadAssignment(source.uuid)).Should().BeEquivalentTo(reviewedAgain);
        (await ReadSource(recording.metadata.identifier))
            .description.Should()
            .Be(recording.metadata.description);
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

    private Task<AssignmentSnapshot> ReadAssignment(Guid sourceUuid) =>
        db.WithWriteConnection(connection =>
            connection.QuerySingleAsync<AssignmentSnapshot>(
                """
                SELECT a.assignment_kind AS Kind,sh.performance_key AS Key,
                    sh.performance_type AS Type,sh.performance_label AS Label,a.evidence::text AS Evidence
                FROM source_show_assignments a JOIN shows sh ON sh.id=a.show_id
                JOIN sources s ON s.id=a.source_id WHERE s.uuid=@sourceUuid
                """,
                new { sourceUuid }
            )
        );

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

    private sealed record AssignmentSnapshot(
        string Kind,
        string Key,
        string Type,
        string? Label,
        string Evidence
    );

    private sealed class IgnoredPopularityJobs : Hangfire.IBackgroundJobClient
    {
        public string Create(Hangfire.Common.Job job, Hangfire.States.IState state) =>
            "fixture-popularity";

        public bool ChangeState(string jobId, Hangfire.States.IState state, string expectedState) =>
            true;
    }
}
