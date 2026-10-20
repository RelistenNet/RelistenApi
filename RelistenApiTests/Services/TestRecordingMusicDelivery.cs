using System.Net;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using Relisten.Api.Models.Normalization;
using Relisten.Controllers;
using Relisten.Data;
using Relisten.Normalization;
using CatalogSource = RelistenApiTests.Services.NormalizationDomainFixture.CatalogSource;

namespace RelistenApiTests.Services;

/// <summary>Checks durable music delivery through PostgreSQL and the actual listener HTTP routes.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestRecordingMusicDelivery
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizationRecordingLookupStore lookups = null!;
    private NormalizationRecordingLookupService lookupService = null!;
    private NormalizationRecordingLookupDispatcher dispatcher = null!;
    private NormalizationImportedCatalogService imported = null!;
    private NormalizationRepertoireSourceService sources = null!;
    private NormalizationRepertoireResolutionProcessor repertoire = null!;
    private NormalizationRecordingContentMaterializer contents = null!;
    private RepertoireResolutionPolicyService policies = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        var evidence = new NormalizationEvidenceService(fixture.Db);
        lookups = new(fixture.Db);
        var legacy = new NormalizationLegacyInputService(fixture.Db, evidence);
        var bindings = new NormalizationCatalogBindingService(fixture.Db);
        lookupService = new(lookups, legacy, evidence, bindings, new(), fixture.Work);
        dispatcher = new(
            lookups,
            new(
                legacy,
                evidence,
                bindings,
                new(),
                new(new(), new()),
                fixture.Work,
                new(fixture.Db),
                lookups
            )
        );
        imported = new(fixture.Db, evidence);
        sources = new(
            fixture.Db,
            evidence,
            new(),
            imported,
            bindings,
            new(fixture.Db),
            new(fixture.Db)
        );
        policies = new(fixture.Db, fixture.Work, allowFixturePolicies: true);
        var music = new NormalizedMusicService(fixture.Db, fixture.Work);
        repertoire = new(
            fixture.Db,
            sources,
            new(fixture.Db),
            policies,
            new(fixture.Db),
            fixture.Work,
            music
        );
        contents = new(
            fixture.Db,
            new(fixture.Db),
            new(fixture.Db),
            new(fixture.Db, fixture.Work),
            fixture.Work,
            music
        );
    }

    [TearDown]
    public async Task ClearFixturePolicy()
    {
        var state = await policies.ReadStateAsync();
        if (state.Active != null)
        {
            await policies.ApplyDecisionAsync(
                new(
                    Guid.NewGuid(),
                    state.OwnerObservation.SubjectUuid,
                    state.OwnerObservation.EditorialRevision,
                    state.Active.Policy.Artifact.Uuid,
                    null,
                    "End isolated delivery policy"
                ),
                "music-delivery-fixture"
            );
        }
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    private RecordingMusicDeliveryService Service() =>
        new(
            new(fixture.Db),
            lookups,
            dispatcher,
            imported,
            sources,
            repertoire,
            contents,
            fixture.Work
        );

    [Test]
    public async Task AcknowledgedExtractionRecoversThroughDictionaryToHttpSongShowsAndFiles()
    {
        await ActivateTitlePolicy();
        var source = await Source("Ripple");
        await Sql(
            """
            INSERT INTO setlist_songs(artist_id,name,slug,upstream_identifier,uuid,updated_at)
            VALUES(@ArtistId,'Ripple',@firstSlug,@firstSlug,@first,now()),(@ArtistId,'Ripple',@secondSlug,@secondSlug,@second,now())
            """,
            new
            {
                source.ArtistId,
                first = Guid.NewGuid(),
                second = Guid.NewGuid(),
                firstSlug = Guid.NewGuid().ToString(),
                secondSlug = Guid.NewGuid().ToString(),
            }
        );
        var handoff = await Handoff(source);
        var admitted = await Service().ProcessAsync(handoff);
        admitted.Phase.Should().Be(RecordingMusicDeliveryPhase.Extraction);
        (await fixture.Work.GetSubjectAsync(RecordingMusicDeliveryService.Owner(handoff)))
            .Should()
            .NotBeNull();

        var extracted = await Service().ProcessAsync(handoff);
        extracted.Phase.Should().Be(RecordingMusicDeliveryPhase.DictionaryCapture);
        (await lookups.GetHandoffAsync(handoff))!.CompletedResult.Should().NotBeNull();
        (await lookups.GetPendingHandoffsAsync())
            .Should()
            .NotContain(row => row.HandoffUuid == handoff);
        (await Service().GetPendingAsync())
            .Deliveries.Should()
            .Contain(row => row.HandoffUuid == handoff);

        // A new service instance models host recovery. Its child UUIDs come from PostgreSQL.
        var completed = await Drain(handoff);
        completed.Result!.Disposition.Should().Be(RecordingMusicDeliveryDisposition.Completed);
        completed.DictionaryAttemptUuid.Should().Be(admitted.DictionaryAttemptUuid);
        completed.RepertoireAttemptUuid.Should().Be(admitted.RepertoireAttemptUuid);
        completed.ContentAttemptUuid.Should().Be(admitted.ContentAttemptUuid);
        (await Service().GetPendingAsync())
            .Deliveries.Should()
            .NotContain(row => row.HandoffUuid == handoff);
        (await Service().ProcessAsync(handoff)).Should().Be(completed);

        var reads = new NormalizationReadService(
            fixture.Db,
            Options.Create(new NormalizationReadOptions { Enabled = true })
        );
        await using var app = HttpApp(reads);
        await app.StartAsync();
        using var client = Client(app);
        var songs = await Get(client, $"artists/{completed.ArtistUuid}/songs?q=Ripple");
        var song = songs["items"]!.Should().ContainSingle().Subject;
        var songUuid = Guid.Parse(song["uuid"]!.Value<string>()!);
        var shows = await Get(client, $"songs/{songUuid}/shows");
        shows["items"]!.Should().ContainSingle();
        var fileContents = await Get(client, $"tracks/{source.TrackUuid}/contents");
        fileContents["items"]!.Should().ContainSingle();
        Guid.Parse(fileContents["items"]![0]!["repertoire_song_uuid"]!.Value<string>()!)
            .Should()
            .Be(songUuid);
        Guid.Parse(fileContents["items"]![0]!["source_track_uuid"]!.Value<string>()!)
            .Should()
            .Be(source.TrackUuid);
        var detail = await Get(client, $"songs/{songUuid}");
        detail["imported_song_uuids"]!.Should().HaveCount(2);
        (await Playback(source)).Should().Be("https://example.test/file.mp3");
        await app.StopAsync();
    }

    [Test]
    public async Task CompleteEmptyDictionaryPublishesTitleDerivedMusicWithoutWithholdingRecording()
    {
        var source = await Source("Harbor Lantern");
        var result = await Drain(await Handoff(source));

        result.Result!.Disposition.Should().Be(RecordingMusicDeliveryDisposition.Completed);
        result.DictionaryFetchUuid.Should().NotBeNull();
        var dictionary = (await repertoire.GetAsync(result.DictionaryAttemptUuid))!;
        dictionary.Result!.Disposition.Should().Be(RepertoireResolutionDisposition.Completed);
        dictionary.Result.Bound.Should().Be(0);
        dictionary.Result.Provisional.Should().Be(0);
        var reads = new NormalizationReadService(
            fixture.Db,
            Options.Create(new NormalizationReadOptions { Enabled = true })
        );
        var song = (await reads.ListRepertoireAsync(result.ArtistUuid.ToString(), "Harbor Lantern"))
            .items.Should()
            .ContainSingle()
            .Subject;
        song.basis.Should().Be("title_derived");
        (await reads.ListSongShowsAsync(song.uuid))!.items.Should().ContainSingle();
        (await Playback(source)).Should().Be("https://example.test/file.mp3");
    }

    [Test]
    public async Task ChangedCatalogAfterLookupProducesStaleDeliveryAndPreservesPlayback()
    {
        var source = await Source("Before");
        var handoff = await Handoff(source);
        await Sql("UPDATE source_tracks SET title='After' WHERE uuid=@TrackUuid", source);

        var result = await Drain(handoff);

        result.Result!.Disposition.Should().Be(RecordingMusicDeliveryDisposition.Stale);
        (
            await new NormalizedMusicService(fixture.Db, fixture.Work).GetTrackContentsAsync(
                source.TrackUuid
            )
        )
            .Should()
            .BeEmpty();
        (await Playback(source)).Should().Be("https://example.test/file.mp3");
    }

    [Test]
    public async Task ReplacedInputBeforeAdmissionLeavesExtractionUnacknowledged()
    {
        var source = await Source("Before");
        var handoff = await Handoff(source);
        await Sql("UPDATE source_tracks SET title='After' WHERE uuid=@TrackUuid", source);
        await new NormalizationLegacyInputService(
            fixture.Db,
            new(fixture.Db)
        ).RefreshRecordingAsync(source.SourceUuid);

        var result = await Service().ProcessAsync(handoff);

        result.Result!.Disposition.Should().Be(RecordingMusicDeliveryDisposition.Stale);
        result.Result.Reason.Should().Be("delivery_admission_changed");
        (await lookups.GetHandoffAsync(handoff))!.CompletedResult.Should().BeNull();
        (await Playback(source)).Should().Be("https://example.test/file.mp3");
    }

    [Test]
    public async Task LaterUnchangedHandoffReusesDictionaryCaptureAndAcceptedSong()
    {
        await ActivateTitlePolicy();
        var source = await Source("Harbor Lantern");
        var first = await Drain(await Handoff(source));
        var song = (
            await new NormalizationReadService(
                fixture.Db,
                Options.Create(new NormalizationReadOptions { Enabled = true })
            ).ListRepertoireAsync(first.ArtistUuid.ToString())
        )
            .items.Single()
            .uuid;

        var second = await Drain(await Handoff(source));

        second.Result!.Disposition.Should().Be(RecordingMusicDeliveryDisposition.Completed);
        second.DictionaryFetchUuid.Should().Be(first.DictionaryFetchUuid);
        var accepted = await new NormalizedMusicService(
            fixture.Db,
            fixture.Work
        ).GetTrackContentsAsync(source.TrackUuid);
        accepted.Should().ContainSingle().Which.Value.RepertoireSongUuid.Should().Be(song);
    }

    [Test]
    public async Task ConcurrentRecordingsAndRepeatedDeliveryShareOneCurrentDictionaryCapture()
    {
        await ActivateTitlePolicy();
        var firstSource = await Source("Harbor Lantern");
        var secondSource = await Source("Harbor Lantern", firstSource.ArtistId);
        var first = await Handoff(firstSource);
        var second = await Handoff(secondSource);
        foreach (var handoff in new[] { first, second })
        {
            (await Service().ProcessAsync(handoff))
                .Phase.Should()
                .Be(RecordingMusicDeliveryPhase.Extraction);
            (await Service().ProcessAsync(handoff))
                .Phase.Should()
                .Be(RecordingMusicDeliveryPhase.DictionaryCapture);
        }

        var captures = await Task.WhenAll(
            Service().ProcessAsync(first),
            Service().ProcessAsync(first),
            Service().ProcessAsync(second)
        );

        captures.Select(row => row.DictionaryFetchUuid).Distinct().Should().ContainSingle();
        captures.Should().OnlyContain(row => row.DictionaryFetchUuid != null);
        (await Drain(first))
            .Result!.Disposition.Should()
            .Be(RecordingMusicDeliveryDisposition.Completed);
        (await Drain(second))
            .Result!.Disposition.Should()
            .Be(RecordingMusicDeliveryDisposition.Completed);
    }

    private async Task<RecordingMusicDelivery> Drain(Guid handoff)
    {
        for (var phase = 0; phase < 100; phase++)
        {
            var delivery = await Service().ProcessAsync(handoff);
            if (delivery.Result != null)
            {
                return delivery;
            }
        }
        throw new AssertionException(
            "Recording music delivery did not settle within 100 bounded phases."
        );
    }

    private async Task<Guid> Handoff(CatalogSource source)
    {
        var lookup = await lookupService.LookupLegacyAsync(Guid.NewGuid(), source.SourceUuid);
        lookup.Result!.Disposition.Should().Be(NormalizationRecordingLookupDisposition.Published);
        return lookup.Result.HandoffUuid!.Value;
    }

    private async Task<CatalogSource> Source(string title, int? artistId = null)
    {
        var source = await fixture.Source();
        if (artistId != null)
        {
            await Sql(
                """
                UPDATE sources SET artist_id=@artistId WHERE id=@SourceId;
                UPDATE source_tracks SET artist_id=@artistId,mp3_url=@url WHERE uuid=@TrackUuid;
                """,
                new
                {
                    artistId,
                    source.SourceId,
                    source.TrackUuid,
                    url = "https://example.test/" + source.TrackUuid + ".mp3",
                }
            );
            source = source with { ArtistId = artistId.Value };
        }
        await Sql(
            """
            INSERT INTO shows(uuid,artist_id,date,display_date,updated_at)
                VALUES(@show,@ArtistId,'2026-10-17','2026-10-17',now())
                ON CONFLICT(artist_id,display_date,performance_key) DO NOTHING;
            UPDATE sources SET show_id=(SELECT id FROM shows WHERE artist_id=@ArtistId AND display_date='2026-10-17')
                WHERE id=@SourceId;
            UPDATE source_tracks SET title=@title WHERE uuid=@TrackUuid;
            """,
            new
            {
                source.ArtistId,
                source.SourceId,
                source.TrackUuid,
                show = Guid.NewGuid(),
                title,
            }
        );
        return source;
    }

    private Task Sql(string sql, object? args = null) =>
        fixture.Db.WithWriteConnection(c => c.ExecuteAsync(sql, args));

    private Task<string> Playback(CatalogSource source) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<string>(
                "SELECT mp3_url FROM source_tracks WHERE uuid=@TrackUuid",
                source
            )
        );

    private static WebApplication HttpApp(NormalizationReadService reads)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(reads);
        builder
            .Services.AddControllers()
            .AddApplicationPart(typeof(NormalizationMusicController).Assembly)
            .AddNewtonsoftJson();
        var app = builder.Build();
        app.MapControllers();
        return app;
    }

    private static HttpClient Client(WebApplication app) =>
        new()
        {
            BaseAddress = new Uri(
                app.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!
                    .Addresses.Single()
            ),
        };

    private static async Task<JObject> Get(HttpClient client, string route)
    {
        using var response = await client.GetAsync("/api/v3/normalization/" + route);
        response.StatusCode.Should().Be(HttpStatusCode.OK, route);
        return JObject.Parse(await response.Content.ReadAsStringAsync());
    }

    private async Task ActivateTitlePolicy()
    {
        const string reviewer = "music-delivery-fixture";
        var artifact = new RepertoireAcceptancePolicyArtifact(
            Guid.NewGuid(),
            "delivery-fixture-v1",
            "",
            NormalizationRepertoireResolutionProcessor.Version,
            RepertoireResolutionPolicyEvaluator.SourceVersion,
            NormalizationCandidateService.FactsVersion,
            RepertoireResolutionPolicyEvaluator.MatcherVersion,
            [
                IdentityMatchRule.QualifiedEntityIdentifier,
                IdentityMatchRule.ArtistScopedNormalizedTitle,
            ],
            FixtureOnly: true
        );
        artifact = artifact with
        {
            ContentHash = RepertoireResolutionPolicyService.ComputeArtifactHash(artifact),
        };
        var evaluation = new RepertoireAcceptanceEvaluationReceipt(
            Guid.NewGuid(),
            artifact.Uuid,
            artifact.ContentHash,
            "",
            new string('a', 64),
            new string('b', 64),
            new string('c', 64),
            "fixture-only-delivery-labels",
            "independent-fixture-adjudicator",
            reviewer,
            new(100, 0),
            FixtureOnly: true
        );
        evaluation = evaluation with
        {
            ReceiptHash = RepertoireResolutionPolicyService.ComputeEvaluationHash(evaluation),
        };
        await policies.RegisterEvaluatedPolicyAsync(artifact, evaluation, reviewer);
        var state = await policies.ReadStateAsync();
        await policies.ApplyDecisionAsync(
            new(
                Guid.NewGuid(),
                state.OwnerObservation.SubjectUuid,
                state.OwnerObservation.EditorialRevision,
                state.Active?.Policy.Artifact.Uuid,
                artifact.Uuid,
                "Exercise title reuse in a disposable delivery fixture"
            ),
            reviewer
        );
    }
}
