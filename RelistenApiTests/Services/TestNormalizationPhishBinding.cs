using System.Text;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;

namespace RelistenApiTests.Services;

/// <summary>Phish bindings qualify stored recordings and match media without recreating playback identities.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationPhishBinding
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizationCatalogBindingService bindings = null!;
    private static int nextId = 2000000;
    private const string RawUrl = "http://audio.example.test/file name.mp3?note=http:raw";
    private const string StoredUrl = "https://audio.example.test/file name.mp3?note=https:raw";

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        bindings = new(fixture.Db);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task ExactWriterMediaTransformationBindsActualUuidsAndPreservesMemberships()
    {
        var source = await fixture.Source();
        var id = NextId();
        await Qualify(source, id);
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE source_tracks SET title='A corrected title',track_position=99,slug='unrelated-slug' WHERE uuid=@uuid",
                new { uuid = source.TrackUuid }
            )
        );
        var observation = Observation(id, Track(201, RawUrl));

        var result = await bindings.BindRecordingAsync(observation);

        result.Recording.Status.Should().Be(NormalizationCatalogBindingStatus.Bound);
        result.Recording.Value!.SourceUuid.Should().Be(source.SourceUuid);
        var file = result.Files.Single();
        file.Binding.Status.Should().Be(NormalizationCatalogBindingStatus.Bound);
        file.Binding.Value!.TrackUuid.Should().Be(source.TrackUuid);
        file.Binding.Value.MediaUrl.Should().Be(StoredUrl);
        file.Observation.SongIdentities.Select(identity => identity.Identity.ProviderKey)
            .Should()
            .Equal("4", "4", "7");
        result
            .CatalogInputs.Should()
            .ContainSingle(input => input.ArtistUuid == result.Recording.Value.ArtistUuid);
        result
            .SourceIdentities.Should()
            .ContainSingle(identity => identity.UpstreamIdentifier == id.ToString());
        var generation = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<long>(
                "SELECT generation FROM normalization.catalog_source_identity_revisions WHERE upstream_identifier=@key",
                new { key = id.ToString() }
            )
        );
        result.SourceIdentities.Single().Generation.Should().Be(generation);
    }

    [TestCase("archive.org", true)]
    [TestCase("phish.in", false)]
    public async Task NumericIdentifierRequiresAPhishRecordingLink(string provider, bool forSource)
    {
        var source = await fixture.Source();
        var id = NextId();
        await Qualify(source, id, provider, forSource);

        var result = await bindings.BindRecordingAsync(Observation(id, Track(201, RawUrl)));

        result.Recording.Status.Should().Be(NormalizationCatalogBindingStatus.Missing);
        result.Recording.Value.Should().BeNull();
        result.Files.Single().Binding.Value.Should().BeNull();
    }

    [Test]
    public async Task GlobalDuplicateIsVisibleBeforeExpectedArtistFiltering()
    {
        var first = await fixture.Source();
        var second = await fixture.Source();
        var id = NextId();
        await Qualify(first, id);
        await Qualify(second, id);
        var artist = await ArtistUuid(first);

        var result = await bindings.BindRecordingAsync(Observation(id, Track(201, RawUrl)), artist);

        result.Recording.Status.Should().Be(NormalizationCatalogBindingStatus.Ambiguous);
        result.Recording.Value.Should().BeNull();
        result
            .Files.Single()
            .Binding.Status.Should()
            .Be(NormalizationCatalogBindingStatus.Ambiguous);
    }

    [Test]
    public async Task ExpectedArtistMismatchDoesNotRetargetTheRecording()
    {
        var source = await fixture.Source();
        var id = NextId();
        await Qualify(source, id);

        var result = await bindings.BindRecordingAsync(
            Observation(id, Track(201, RawUrl)),
            Guid.NewGuid()
        );

        result.Recording.Status.Should().Be(NormalizationCatalogBindingStatus.ProviderConflict);
        result.Recording.Reason.Should().Be("artist_mismatch");
    }

    [TestCase("orphaned", NormalizationCatalogBindingStatus.Missing, "orphaned_file")]
    [TestCase(
        "artist",
        NormalizationCatalogBindingStatus.ProviderConflict,
        "file_ownership_conflict"
    )]
    [TestCase("set", NormalizationCatalogBindingStatus.ProviderConflict, "file_ownership_conflict")]
    public async Task ActiveFileOwnershipIsRequired(
        string change,
        NormalizationCatalogBindingStatus status,
        string reason
    )
    {
        var source = await fixture.Source();
        var other = await fixture.Source();
        var id = NextId();
        await Qualify(source, id);
        var sql = change switch
        {
            "orphaned" => "UPDATE source_tracks SET is_orphaned=true WHERE uuid=@uuid",
            "artist" => "UPDATE source_tracks SET artist_id=@artist WHERE uuid=@uuid",
            _ =>
                "UPDATE source_tracks SET source_set_id=(SELECT source_set_id FROM source_tracks WHERE uuid=@other) WHERE uuid=@uuid",
        };
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                sql,
                new
                {
                    uuid = source.TrackUuid,
                    artist = other.ArtistId,
                    other = other.TrackUuid,
                }
            )
        );

        var result = await bindings.BindRecordingAsync(Observation(id, Track(201, RawUrl)));

        result.Recording.Status.Should().Be(NormalizationCatalogBindingStatus.Bound);
        result.Files.Single().Binding.Status.Should().Be(status);
        result.Files.Single().Binding.Reason.Should().Be(reason);
    }

    [Test]
    public async Task ExplicitForeignTrackOwnerCannotBindThroughAnOtherwiseMatchingUrl()
    {
        var source = await fixture.Source();
        var id = NextId();
        await Qualify(source, id);
        var observation = Observation(
            id,
            new
            {
                id = 201,
                show_id = id + 1,
                title = "A song",
                mp3 = RawUrl,
                song_ids = new[] { 4 },
            }
        );

        var result = await bindings.BindRecordingAsync(observation);

        result.Recording.Status.Should().Be(NormalizationCatalogBindingStatus.Bound);
        var file = result.Files.Single();
        file.Observation.Should().BeSameAs(observation.Files.Single());
        file.Observation.RecordingIdentity!.Evidence.JsonPointer.Should().Be("/tracks/0/show_id");
        file.Binding.Status.Should().Be(NormalizationCatalogBindingStatus.ProviderConflict);
        file.Binding.Reason.Should().Be("file_recording_identity_mismatch");
        file.Binding.Value.Should().BeNull();
    }

    [Test]
    public async Task ATitleAndSlugMatchCannotReplaceMissingMedia()
    {
        var source = await fixture.Source();
        var id = NextId();
        await Qualify(source, id);
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE source_tracks SET title='A song',slug='a-song-201',mp3_url='https://audio.example.test/different.mp3' WHERE uuid=@uuid",
                new { uuid = source.TrackUuid }
            )
        );

        var result = await bindings.BindRecordingAsync(Observation(id, Track(201, RawUrl)));

        result.Files.Single().Binding.Status.Should().Be(NormalizationCatalogBindingStatus.Missing);
        result.Files.Single().Binding.Reason.Should().Be("missing_file");
    }

    [TestCase(true, "duplicate_file_identity")]
    [TestCase(false, "duplicate_media_identity")]
    public async Task ConflictingFileAccountsRemainSeparateAndUnbound(
        bool repeatTrackId,
        string reason
    )
    {
        var source = await fixture.Source();
        var id = NextId();
        await Qualify(source, id);
        var second = repeatTrackId
            ? Track(201, "https://audio.example.test/another.mp3")
            : Track(202, StoredUrl);

        var result = await bindings.BindRecordingAsync(Observation(id, Track(201, RawUrl), second));

        result.Files.Should().HaveCount(2);
        result
            .Files.Should()
            .OnlyContain(file =>
                file.Binding.Status == NormalizationCatalogBindingStatus.Ambiguous
                && file.Binding.Value == null
                && file.Binding.Reason == reason
            );
    }

    [Test]
    public async Task AnotherArtistsLaterProviderSourceInvalidatesThePublicationTicket()
    {
        var source = await fixture.Source();
        var id = NextId();
        await Qualify(source, id);
        var input = Input(id, Track(201, RawUrl));
        var retained = await new NormalizationEvidenceService(fixture.Db).CaptureInputAsync(
            new(
                Guid.NewGuid(),
                input.Resource,
                input.Payload,
                input.MediaType,
                input.CaptureVersion,
                0,
                DateTimeOffset.UtcNow
            )
        );
        var observation = new NormalizationProviderDecoder()
            .Decode(retained.Revision)
            .Recordings.Single();
        var snapshot = await bindings.BindRecordingAsync(observation);
        var ticket = await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            new("people", "recording", source.SourceUuid),
            "binding-test",
            [input.Resource],
            [],
            catalogInputs: snapshot.CatalogInputs,
            sourceIdentities: snapshot.SourceIdentities
        );
        var competitor = await fixture.Source();
        await Qualify(competitor, id);
        var originalArtist = snapshot.CatalogInputs.Single();
        var currentArtistGeneration = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<long>(
                "SELECT generation FROM normalization.catalog_artist_revisions WHERE artist_uuid=@ArtistUuid",
                originalArtist
            )
        );
        currentArtistGeneration.Should().Be(originalArtist.Generation);
        var called = false;

        var publication = await fixture.Work.TryPublishAsync(
            ticket.WorkUuid,
            (_, _, _) =>
            {
                called = true;
                return Task.CompletedTask;
            }
        );

        publication.Status.Should().Be(NormalizationPublicationStatus.Stale);
        called.Should().BeFalse();
        (await bindings.BindRecordingAsync(observation))
            .Recording.Status.Should()
            .Be(NormalizationCatalogBindingStatus.Ambiguous);

        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "DELETE FROM links WHERE source_id=@source AND upstream_source_id IN (SELECT id FROM upstream_sources WHERE name='phish.in')",
                new { source = competitor.SourceId }
            )
        );
        var recovered = await bindings.BindRecordingAsync(observation);
        recovered.Recording.Status.Should().Be(NormalizationCatalogBindingStatus.Bound);
        var retry = await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            new("people", "recording", source.SourceUuid),
            "binding-test",
            [input.Resource],
            [],
            catalogInputs: recovered.CatalogInputs,
            sourceIdentities: recovered.SourceIdentities
        );
        var committed = await fixture.Work.TryPublishAsync(
            retry.WorkUuid,
            (_, _, _) =>
            {
                called = true;
                return Task.CompletedTask;
            }
        );
        committed.Status.Should().Be(NormalizationPublicationStatus.Published);
        called.Should().BeTrue();
    }

    [Test]
    public async Task MissingIdentityFenceCannotProduceAnUnfencedBoundSnapshot()
    {
        var source = await fixture.Source();
        var id = NextId();
        await Qualify(source, id);
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "DELETE FROM normalization.catalog_source_identity_revisions WHERE upstream_identifier=@key",
                new { key = id.ToString() }
            )
        );

        var bind = () => bindings.BindRecordingAsync(Observation(id, Track(201, RawUrl)));

        await bind.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*source identity revision fence*");
    }

    private Task<Guid> ArtistUuid(NormalizationDomainFixture.CatalogSource source) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<Guid>(
                "SELECT uuid FROM artists WHERE id=@id",
                new { id = source.ArtistId }
            )
        );

    private async Task Qualify(
        NormalizationDomainFixture.CatalogSource source,
        int id,
        string provider = "phish.in",
        bool forSource = true
    )
    {
        await fixture.Db.WithWriteConnection(async c =>
        {
            var providerId =
                await c.QuerySingleOrDefaultAsync<int?>(
                    "SELECT id FROM upstream_sources WHERE name=@provider ORDER BY id LIMIT 1",
                    new { provider }
                )
                ?? await c.QuerySingleAsync<int>(
                    "INSERT INTO upstream_sources(id,name,url) VALUES((SELECT GREATEST(1000,COALESCE(max(id),0)+1) FROM upstream_sources),@provider,'https://fixture.test') RETURNING id",
                    new { provider }
                );
            await c.ExecuteAsync(
                "UPDATE sources SET upstream_identifier=@key WHERE id=@source; UPDATE source_tracks SET mp3_url=@url WHERE uuid=@track; INSERT INTO links(source_id,upstream_source_id,for_source,for_reviews,for_ratings,url,label) VALUES(@source,@providerId,@forSource,false,false,'https://phish.in/old-date','Provider fixture')",
                new
                {
                    key = id.ToString(),
                    source = source.SourceId,
                    track = source.TrackUuid,
                    url = StoredUrl,
                    providerId,
                    forSource,
                }
            );
        });
    }

    private static int NextId() => Interlocked.Increment(ref nextId);

    private static object Track(int id, string mp3) =>
        new
        {
            id,
            title = "A song",
            position = 1,
            set = "1",
            mp3,
            song_ids = new[] { 4, 4, 7 },
        };

    private static ProviderRecordingObservation Observation(int id, params object[] tracks) =>
        new NormalizationProviderDecoder().Decode(Input(id, tracks)).Recordings.Single();

    private static NormalizationInputRevision Input(int id, params object[] tracks) =>
        new()
        {
            Uuid = Guid.NewGuid(),
            Resource = new("phish.in", "show", id.ToString()),
            CaptureVersion = "phishin-show-object-v1",
            MediaType = "application/json",
            Payload = Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(
                    new
                    {
                        id,
                        date = "1990-01-01",
                        tracks,
                    }
                )
            ),
        };
}
