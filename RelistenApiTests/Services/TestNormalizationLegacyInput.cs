using System.Text.Json;
using System.Transactions;
using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;

namespace RelistenApiTests.Services;

/// <summary>Stored catalog snapshots preserve identities and incomplete context without changing current evidence.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationLegacyInput
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizationLegacyInputService service = null!;
    private NormalizationEvidenceService evidence = null!;
    private (int ProviderId, int ArtistId)? ownedProvider;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new NormalizationDomainFixture();
        evidence = new(fixture.Db);
        service = new(fixture.Db, evidence);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [TearDown]
    public async Task RemoveOwnedProviderMapping()
    {
        if (ownedProvider is not { } owned)
        {
            return;
        }

        await fixture.Db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                """
                DELETE FROM artists_upstream_sources WHERE upstream_source_id=@providerId AND artist_id=@artistId;
                DELETE FROM upstream_sources WHERE id=@providerId;
                """,
                new { providerId = owned.ProviderId, artistId = owned.ArtistId }
            )
        );
        ownedProvider = null;
    }

    [Test]
    public async Task SnapshotRetainsStoredIdentitiesCreditsAndOrderedFiles()
    {
        var source = await fixture.Source();
        var setUuid = Guid.NewGuid();
        var trackUuid = Guid.NewGuid();
        var showUuid = Guid.NewGuid();
        var venueUuid = Guid.NewGuid();
        var showVenueUuid = Guid.NewGuid();
        await fixture.Db.WithWriteConnection(async c =>
        {
            var venue = await c.QuerySingleAsync<long>(
                "INSERT INTO venues(artist_id,name,location,uuid,updated_at) VALUES(@artist,'Stored Venue','City, State',@uuid,now()) RETURNING id",
                new { artist = source.ArtistId, uuid = venueUuid }
            );
            var showVenue = await c.QuerySingleAsync<long>(
                "INSERT INTO venues(artist_id,name,location,uuid,updated_at) VALUES(@artist,'Show Venue','Other City',@uuid,now()) RETURNING id",
                new { artist = source.ArtistId, uuid = showVenueUuid }
            );
            var show = await c.QuerySingleAsync<long>(
                "INSERT INTO shows(artist_id,date,display_date,uuid,venue_id,updated_at) VALUES(@artist,'2000-01-01','2000-01-01 Late',@uuid,@venue,now()) RETURNING id",
                new
                {
                    artist = source.ArtistId,
                    uuid = showUuid,
                    venue = showVenue,
                }
            );
            var provider = await c.QuerySingleAsync<int>(
                "INSERT INTO upstream_sources(id,name,url) VALUES((SELECT GREATEST(1000,COALESCE(max(id),0)+1) FROM upstream_sources),'legacy-seed-fixture','https://user:secret-never-copy@example.test') RETURNING id"
            );
            ownedProvider = (provider, source.ArtistId);
            await c.ExecuteAsync(
                "INSERT INTO artists_upstream_sources(artist_id,upstream_source_id,upstream_identifier) VALUES(@artist,@provider,'FixtureCollection')",
                new { artist = source.ArtistId, provider }
            );
            await c.ExecuteAsync(
                "UPDATE sources SET taper='Zoë Recorder',transferrer='Pat Transfer',lineage='DAT > WAV',taper_notes='Recorded by Zoë 🎤',description='Original prose',show_id=@show,venue_id=@venue WHERE id=@id",
                new
                {
                    show,
                    venue,
                    id = source.SourceId,
                }
            );
            var set = await c.QuerySingleAsync<long>(
                "INSERT INTO source_sets(source_id,index,name,uuid,updated_at) VALUES(@source,-1,'Opening set',@uuid,now()) RETURNING id",
                new { source = source.SourceId, uuid = setUuid }
            );
            await c.ExecuteAsync(
                "INSERT INTO source_tracks(source_id,source_set_id,artist_id,track_position,title,slug,mp3_url,flac_url,uuid,updated_at) VALUES(@source,@set,@artist,2,'Opening Song',@slug,'https://example.test/open.mp3','https://example.test/open.flac',@uuid,now())",
                new
                {
                    source = source.SourceId,
                    set,
                    artist = source.ArtistId,
                    slug = trackUuid.ToString(),
                    uuid = trackUuid,
                }
            );
        });

        var result = await service.SeedRecordingAsync(source.SourceUuid);
        result.Disposition.Should().Be(NormalizationLegacySeedDisposition.Seeded);
        result.Input!.Resource.Should().Be(Resource(source.SourceUuid));
        result.Input.Generation.Should().Be(1);
        result
            .Input.Revision.CaptureVersion.Should()
            .Be(NormalizationLegacyInputService.CaptureVersion);
        using var json = JsonDocument.Parse(result.Input.Revision.Payload);
        var root = json.RootElement;
        root.GetProperty("representation").GetString().Should().Be("legacy_catalog");
        root.GetProperty("snapshot_at")
            .GetDateTimeOffset()
            .Should()
            .BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30));
        var mapping = root.GetProperty("configured_artist_mappings").EnumerateArray().Single();
        mapping.GetProperty("upstream_provider").GetString().Should().Be("legacy-seed-fixture");
        mapping.GetProperty("upstream_identifier").GetString().Should().Be("FixtureCollection");
        mapping.GetProperty("upstream_source_id").GetInt32().Should().BeGreaterThan(0);
        System
            .Text.Encoding.UTF8.GetString(result.Input.Revision.Payload)
            .Should()
            .NotContain("secret-never-copy");
        var storedSource = root.GetProperty("source");
        storedSource.GetProperty("id").GetInt64().Should().Be(source.SourceId);
        storedSource.GetProperty("uuid").GetGuid().Should().Be(source.SourceUuid);
        storedSource.GetProperty("taper").GetString().Should().Be("Zoë Recorder");
        storedSource.GetProperty("transferrer").GetString().Should().Be("Pat Transfer");
        storedSource.GetProperty("lineage").GetString().Should().Be("DAT > WAV");
        storedSource.GetProperty("taper_notes").GetString().Should().Be("Recorded by Zoë 🎤");
        root.GetProperty("artist").GetProperty("id").GetInt32().Should().Be(source.ArtistId);
        root.GetProperty("show").GetProperty("uuid").GetGuid().Should().Be(showUuid);
        root.GetProperty("source_venue").GetProperty("uuid").GetGuid().Should().Be(venueUuid);
        root.GetProperty("show_venue").GetProperty("uuid").GetGuid().Should().Be(showVenueUuid);
        root.GetProperty("sets")[0].GetProperty("uuid").GetGuid().Should().Be(setUuid);
        var files = root.GetProperty("tracks").EnumerateArray().ToArray();
        files
            .Select(file => file.GetProperty("track").GetProperty("uuid").GetGuid())
            .Should()
            .Equal(trackUuid, source.TrackUuid);
        files[0]
            .GetProperty("track")
            .GetProperty("flac_url")
            .GetString()
            .Should()
            .Be("https://example.test/open.flac");
        files[0].GetProperty("set_context").GetProperty("uuid").GetGuid().Should().Be(setUuid);
        files[0]
            .GetProperty("source_context")
            .GetProperty("uuid")
            .GetGuid()
            .Should()
            .Be(source.SourceUuid);
        var decoded = new NormalizationProviderDecoder().Decode(result.Input.Revision);
        var credits = new RetainedRecordingCreditExtractor().Extract(
            result.Input.Revision,
            decoded.Recordings.Single().Text
        );
        credits
            .Credits.Should()
            .Contain(credit =>
                credit.Name == "Zoë Recorder"
                && credit.Role == RecordingCreditRole.Taper
                && credit.Evidence.JsonPointer == "/source/taper"
            );
        credits
            .Credits.Should()
            .Contain(credit =>
                credit.Name == "Pat Transfer"
                && credit.Role == RecordingCreditRole.Transferrer
                && credit.Evidence.JsonPointer == "/source/transferrer"
            );
        decoded
            .Recordings.Single()
            .Origin.Item.ProviderKey.Should()
            .Be(source.SourceUuid.ToString("D"));
        decoded.Recordings.Single().Venue!.Names.Single().Value.Should().Be("Stored Venue");
        decoded
            .Venues.Select(venue => venue.Names.Single().Value)
            .Should()
            .Equal("Stored Venue", "Show Venue");
        decoded
            .Recordings.Single()
            .Files.Select(file => file.Identities.Single().Identity.ProviderKey)
            .Should()
            .Equal(trackUuid.ToString("D"), source.TrackUuid.ToString("D"));
        files[0]
            .GetProperty("artist_context")
            .GetProperty("id")
            .GetInt32()
            .Should()
            .Be(source.ArtistId);
    }

    [Test]
    public async Task IncompleteAndCrossLinkedTracksRemainEvidenceWithoutPlaybackClaims()
    {
        var source = await fixture.Source();
        var other = await fixture.Source();
        var reverseTrack = Guid.NewGuid();
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                """
                UPDATE source_tracks SET source_id=NULL,artist_id=NULL,mp3_url=NULL,flac_url='https://example.test/orphan.flac',is_orphaned=true WHERE uuid=@track;
                UPDATE source_tracks SET source_set_id=(SELECT id FROM source_sets WHERE source_id=@source LIMIT 1) WHERE uuid=@otherTrack;
                INSERT INTO source_tracks(source_id,source_set_id,artist_id,track_position,title,slug,mp3_url,uuid,updated_at)
                VALUES(@source,(SELECT id FROM source_sets WHERE source_id=@otherSource LIMIT 1),@artist,3,'Foreign set',@reverseTrack::text,'https://example.test/foreign.mp3',@reverseTrack,now());
                """,
                new
                {
                    track = source.TrackUuid,
                    source = source.SourceId,
                    otherTrack = other.TrackUuid,
                    otherSource = other.SourceId,
                    artist = source.ArtistId,
                    reverseTrack,
                }
            )
        );

        var result = await service.SeedRecordingAsync(source.SourceUuid);
        using var json = JsonDocument.Parse(result.Input!.Revision.Payload);
        json.RootElement.GetProperty("show").ValueKind.Should().Be(JsonValueKind.Null);
        var files = json
            .RootElement.GetProperty("tracks")
            .EnumerateArray()
            .ToDictionary(file => file.GetProperty("track").GetProperty("uuid").GetGuid());
        files.Should().HaveCount(3);
        files[source.TrackUuid]
            .GetProperty("track")
            .GetProperty("is_orphaned")
            .GetBoolean()
            .Should()
            .BeTrue();
        files[source.TrackUuid]
            .GetProperty("track")
            .GetProperty("mp3_url")
            .ValueKind.Should()
            .Be(JsonValueKind.Null);
        files[source.TrackUuid]
            .GetProperty("track")
            .GetProperty("flac_url")
            .GetString()
            .Should()
            .Be("https://example.test/orphan.flac");
        files[source.TrackUuid]
            .GetProperty("source_context")
            .ValueKind.Should()
            .Be(JsonValueKind.Null);
        files[source.TrackUuid]
            .GetProperty("artist_context")
            .ValueKind.Should()
            .Be(JsonValueKind.Null);
        files[other.TrackUuid]
            .GetProperty("source_context")
            .GetProperty("uuid")
            .GetGuid()
            .Should()
            .Be(other.SourceUuid);
        files[reverseTrack]
            .GetProperty("source_context")
            .GetProperty("uuid")
            .GetGuid()
            .Should()
            .Be(source.SourceUuid);
        files[reverseTrack]
            .GetProperty("set_context")
            .GetProperty("source_id")
            .GetInt64()
            .Should()
            .Be(other.SourceId);
        files[other.TrackUuid]
            .GetProperty("set_context")
            .GetProperty("source_id")
            .GetInt64()
            .Should()
            .Be(source.SourceId);
    }

    [Test]
    public async Task ExistingSeedIsReturnedWithoutRecapturingChangedCatalogRows()
    {
        var source = await fixture.Source();
        var first = await service.SeedRecordingAsync(source.SourceUuid);
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET taper='Later correction' WHERE id=@id",
                new { id = source.SourceId }
            )
        );
        var second = await service.SeedRecordingAsync(source.SourceUuid);
        second.Disposition.Should().Be(NormalizationLegacySeedDisposition.Existing);
        second.Input.Should().BeEquivalentTo(first.Input);
        (await evidence.GetFetchHistoryAsync(Resource(source.SourceUuid))).Should().ContainSingle();
    }

    [Test]
    public async Task AnyExistingCurrentInputWinsEvenWhenCatalogSourceIsMissing()
    {
        var uuid = Guid.NewGuid();
        var retained = await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                Resource(uuid),
                System.Text.Encoding.UTF8.GetBytes("{\"retained\":true}"),
                "application/json",
                "other-retained-version",
                0,
                DateTimeOffset.UtcNow
            )
        );
        var result = await service.SeedRecordingAsync(uuid);
        result.Disposition.Should().Be(NormalizationLegacySeedDisposition.Existing);
        result.Input!.Revision.Uuid.Should().Be(retained.Revision.Uuid);
        (await evidence.GetFetchHistoryAsync(Resource(uuid))).Should().ContainSingle();
    }

    [Test]
    public async Task MissingSourceReturnsMissingWithoutRetainingAnInput()
    {
        var uuid = Guid.NewGuid();
        var result = await service.SeedRecordingAsync(uuid);
        result.Disposition.Should().Be(NormalizationLegacySeedDisposition.Missing);
        result.Input.Should().BeNull();
        (await evidence.GetCurrentInputAsync(Resource(uuid))).Should().BeNull();
        (await evidence.GetFetchHistoryAsync(Resource(uuid))).Should().BeEmpty();
    }

    [Test]
    public async Task ConcurrentSeedsReturnOneCurrentWinnerWithoutAdvancingItsGeneration()
    {
        var source = await fixture.Source();
        var results = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(_ => service.SeedRecordingAsync(source.SourceUuid))
        );
        results
            .Count(result => result.Disposition == NormalizationLegacySeedDisposition.Seeded)
            .Should()
            .Be(1);
        results.Select(result => result.Input!.Revision.Uuid).Distinct().Should().ContainSingle();
        results
            .Select(result => result.Input!.Generation)
            .Should()
            .OnlyContain(generation => generation == 1);
        var history = await evidence.GetFetchHistoryAsync(Resource(source.SourceUuid));
        history.Count(fetch => fetch.AcceptedAsCurrent).Should().Be(1);
        history.Should().OnlyContain(fetch => fetch.ExpectedGeneration == 0);
    }

    [TestCase("tracks")]
    [TestCase("sets")]
    [TestCase("bytes")]
    public async Task OversizedSnapshotFailsWithoutTruncatingOrRetainingEvidence(string limit)
    {
        var source = await fixture.Source();
        var sql = limit switch
        {
            "tracks" => """
                INSERT INTO source_tracks(source_id,source_set_id,artist_id,track_position,title,slug,mp3_url,uuid,updated_at)
                SELECT @source,(SELECT id FROM source_sets WHERE source_id=@source LIMIT 1),@artist,n,'Song',gen_random_uuid()::text,'https://example.test/'||n||'.mp3',gen_random_uuid(),now()
                FROM generate_series(1,10000) n
                """,
            "sets" => """
                INSERT INTO source_sets(source_id,index,name,uuid,updated_at)
                SELECT @source,n,'Set',gen_random_uuid(),now() FROM generate_series(1,1000) n
                """,
            _ => "UPDATE sources SET taper_notes=repeat('x',16777216) WHERE id=@source",
        };
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(sql, new { source = source.SourceId, artist = source.ArtistId })
        );
        Func<Task> seed = () => service.SeedRecordingAsync(source.SourceUuid);
        await seed.Should()
            .ThrowAsync<InvalidDataException>()
            .WithMessage("Legacy recording snapshot exceeds*");
        (await evidence.GetCurrentInputAsync(Resource(source.SourceUuid))).Should().BeNull();
        (await evidence.GetFetchHistoryAsync(Resource(source.SourceUuid))).Should().BeEmpty();
    }

    [Test]
    public async Task UncommittedCatalogChangesDoNotEnterTheSnapshotAndRollbackDoesNotEraseTheSeed()
    {
        var source = await fixture.Source();
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET taper='Committed recorder' WHERE id=@id",
                new { id = source.SourceId }
            )
        );
        NormalizationLegacySeedResult seeded;
        using (var mutation = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteAsync(
                    "UPDATE sources SET taper='Uncommitted recorder' WHERE id=@id",
                    new { id = source.SourceId }
                )
            );
            using var independent = new TransactionScope(
                TransactionScopeOption.Suppress,
                TransactionScopeAsyncFlowOption.Enabled
            );
            seeded = await service.SeedRecordingAsync(source.SourceUuid);
        }

        var current = await evidence.GetCurrentInputAsync(Resource(source.SourceUuid));
        current.Should().BeEquivalentTo(seeded.Input);
        using var json = JsonDocument.Parse(current!.Revision.Payload);
        json.RootElement.GetProperty("source")
            .GetProperty("taper")
            .GetString()
            .Should()
            .Be("Committed recorder");
    }

    [Test]
    public async Task AmbientCatalogTransactionIsRejectedBeforeCapture()
    {
        var source = await fixture.Source();
        using (var transaction = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            Func<Task> seed = () => service.SeedRecordingAsync(source.SourceUuid);
            await seed.Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("Seed legacy evidence outside*");
        }
        (await evidence.GetCurrentInputAsync(Resource(source.SourceUuid))).Should().BeNull();
    }

    [Test]
    public async Task CancellationLeavesTheRecordingUnseeded()
    {
        var source = await fixture.Source();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Func<Task> seed = () => service.SeedRecordingAsync(source.SourceUuid, cancellation.Token);
        await seed.Should().ThrowAsync<OperationCanceledException>();
        (await evidence.GetCurrentInputAsync(Resource(source.SourceUuid))).Should().BeNull();
    }

    private static NormalizationResourceKey Resource(Guid uuid) =>
        new("legacy_catalog", "recording", uuid.ToString("D"));
}
