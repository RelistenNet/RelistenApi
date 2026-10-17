using System.Text.Json;
using System.Transactions;
using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;
using CatalogSource = RelistenApiTests.Services.NormalizationDomainFixture.CatalogSource;

namespace RelistenApiTests.Services;

/// <summary>Catalog refreshes retain stable facts together with the revisions needed for checked binding.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationLegacyRefresh
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizationEvidenceService evidence = null!;
    private NormalizationLegacyInputService service = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        evidence = new(fixture.Db);
        service = new(fixture.Db, evidence);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task EqualFactsReuseTheirRevisionWhileEachRefreshFencesOlderObservations()
    {
        var source = await fixture.Source();
        var first = await Refresh(source);
        var repeated = await Refresh(source);

        repeated.Capture.Revision.Should().BeEquivalentTo(first.Capture.Revision);
        repeated.Capture.Fetch.AdvancedCurrent.Should().BeFalse();
        repeated.Capture.Fetch.AcceptedAsCurrent.Should().BeTrue();
        repeated
            .Capture.Fetch.ResourceGeneration.Should()
            .Be(first.Capture.Fetch.ResourceGeneration + 1);
        repeated.CatalogInputs.Should().Equal(first.CatalogInputs);
        repeated.Capture.Fetch.ObservedAt.Should().BeOnOrAfter(first.Capture.Fetch.ObservedAt);
        using var json = JsonDocument.Parse(repeated.Capture.Revision.Payload);
        json.RootElement.TryGetProperty("snapshot_at", out _).Should().BeFalse();
        (await evidence.GetFetchHistoryAsync(Resource(source))).Should().HaveCount(2);
    }

    [Test]
    public async Task RefreshAfterHistoricalSeedRetainsCorrectedCreditsAndFileTitles()
    {
        var source = await fixture.Source();
        var seed = await service.SeedRecordingAsync(source.SourceUuid);
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                """
                UPDATE sources SET taper='Zoë Recorder',transferrer='Pat Transfer',taper_notes='Corrected recording notes' WHERE id=@SourceId;
                UPDATE source_tracks SET title='Corrected Song 🎤' WHERE uuid=@TrackUuid;
                """,
                source
            )
        );

        var refreshed = await Refresh(source);
        refreshed.Capture.Revision.Uuid.Should().NotBe(seed.Input!.Revision.Uuid);
        refreshed
            .Capture.Revision.CaptureVersion.Should()
            .Be(NormalizationLegacyInputService.RefreshCaptureVersion);
        refreshed.Capture.Fetch.ExpectedGeneration.Should().Be(seed.Input.Generation);
        var recording = new NormalizationProviderDecoder()
            .Decode(refreshed.Capture.Revision)
            .Recordings.Single();
        var credits = new RetainedRecordingCreditExtractor().Extract(
            refreshed.Capture.Revision,
            recording.Text
        );
        credits
            .Credits.Should()
            .Contain(credit =>
                credit.Name == "Zoë Recorder"
                && credit.Role == RecordingCreditRole.Taper
                && credit.Evidence.InputRevisionUuid == refreshed.Capture.Revision.Uuid
                && credit.Evidence.JsonPointer == "/source/taper"
            );
        credits
            .Credits.Should()
            .Contain(credit =>
                credit.Name == "Pat Transfer"
                && credit.Role == RecordingCreditRole.Transferrer
                && credit.Evidence.JsonPointer == "/source/transferrer"
            );
        recording.Files.Single().Title!.Value.Should().Be("Corrected Song 🎤");
        recording
            .Files.Single()
            .Title!.Evidence.InputRevisionUuid.Should()
            .Be(refreshed.Capture.Revision.Uuid);
        using var historical = JsonDocument.Parse(
            (await evidence.GetInputAsync(seed.Input.Revision.Uuid))!.Payload
        );
        historical
            .RootElement.GetProperty("tracks")[0]
            .GetProperty("track")
            .GetProperty("title")
            .GetString()
            .Should()
            .Be("Song");
        var current = await evidence.GetCurrentInputAsync(Resource(source));
        current!.Revision.Uuid.Should().Be(refreshed.Capture.Revision.Uuid);
        current.Generation.Should().Be(refreshed.Capture.Fetch.ResourceGeneration);
    }

    [Test]
    public async Task OperationalTimestampAndRatingUpdatesReuseContentAndCatalogReadSet()
    {
        var source = await fixture.Source();
        var context = await AddShowAndVenues(source, source, source, source);
        var first = await Refresh(source);
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                """
                UPDATE sources SET updated_at=clock_timestamp(),created_at=created_at-interval '1 second',
                    avg_rating=9,avg_rating_weighted=8,num_reviews=4,num_ratings=7 WHERE id=@sourceId;
                UPDATE source_sets SET updated_at=clock_timestamp(),created_at=created_at-interval '1 second' WHERE source_id=@sourceId;
                UPDATE source_tracks SET updated_at=clock_timestamp(),created_at=created_at-interval '1 second' WHERE source_id=@sourceId;
                UPDATE shows SET updated_at=clock_timestamp(),created_at=created_at-interval '1 second',avg_rating=9 WHERE id=@showId;
                UPDATE venues SET updated_at=clock_timestamp(),created_at=created_at-interval '1 second' WHERE id=ANY(@venueIds);
                """,
                new
                {
                    sourceId = source.SourceId,
                    context.ShowId,
                    venueIds = new[] { context.SourceVenueId, context.ShowVenueId },
                }
            )
        );

        var repeated = await Refresh(source);
        repeated.Capture.Revision.Uuid.Should().Be(first.Capture.Revision.Uuid);
        repeated.Capture.Fetch.AdvancedCurrent.Should().BeFalse();
        repeated.CatalogInputs.Should().Equal(first.CatalogInputs);
    }

    [Test]
    public async Task SnapshotIncludesForeignShowVenueFileAndSetOwners()
    {
        var source = await fixture.Source();
        var fileSource = await fixture.Source();
        var fileArtist = await fixture.Source();
        var setSource = await fixture.Source();
        var showOwner = await fixture.Source();
        var sourceVenueOwner = await fixture.Source();
        var showVenueOwner = await fixture.Source();
        await AddShowAndVenues(source, showOwner, sourceVenueOwner, showVenueOwner);
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                """
                UPDATE source_tracks SET source_set_id=(SELECT id FROM source_sets WHERE source_id=@requestedSource LIMIT 1),artist_id=@foreignArtist,
                    mp3_url='https://example.test/foreign/'||@foreignTrack::text||'.mp3'
                    WHERE uuid=@foreignTrack;
                UPDATE source_tracks SET source_set_id=(SELECT id FROM source_sets WHERE source_id=@foreignSetSource LIMIT 1) WHERE uuid=@requestedTrack;
                """,
                new
                {
                    requestedSource = source.SourceId,
                    foreignArtist = fileArtist.ArtistId,
                    foreignTrack = fileSource.TrackUuid,
                    foreignSetSource = setSource.SourceId,
                    requestedTrack = source.TrackUuid,
                }
            )
        );

        var snapshot = await Refresh(source);
        var expectedArtists = await fixture.Db.WithWriteConnection(c =>
            c.QueryAsync<NormalizationCatalogDependency>(
                """
                SELECT a.uuid AS "ArtistUuid",r.generation AS "Generation"
                FROM artists a JOIN normalization.catalog_artist_revisions r ON r.artist_uuid=a.uuid
                WHERE a.id=ANY(@ids) ORDER BY a.uuid
                """,
                new
                {
                    ids = new[]
                    {
                        source.ArtistId,
                        fileSource.ArtistId,
                        fileArtist.ArtistId,
                        setSource.ArtistId,
                        showOwner.ArtistId,
                        sourceVenueOwner.ArtistId,
                        showVenueOwner.ArtistId,
                    },
                }
            )
        );
        snapshot.CatalogInputs.Should().Equal(expectedArtists);
        using var json = JsonDocument.Parse(snapshot.Capture.Revision.Payload);
        var tracks = json
            .RootElement.GetProperty("tracks")
            .EnumerateArray()
            .ToDictionary(file => file.GetProperty("track").GetProperty("uuid").GetGuid());
        tracks[fileSource.TrackUuid]
            .GetProperty("source_context")
            .GetProperty("uuid")
            .GetGuid()
            .Should()
            .Be(fileSource.SourceUuid);
        tracks[fileSource.TrackUuid]
            .GetProperty("artist_context")
            .GetProperty("id")
            .GetInt32()
            .Should()
            .Be(fileArtist.ArtistId);
        tracks[source.TrackUuid]
            .GetProperty("set_context")
            .GetProperty("source_id")
            .GetInt64()
            .Should()
            .Be(setSource.SourceId);

        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE artists SET name='Corrected foreign artist' WHERE id=@ArtistId",
                fileArtist
            )
        );
        var corrected = await Refresh(source);
        corrected.Capture.Revision.Uuid.Should().NotBe(snapshot.Capture.Revision.Uuid);
        corrected
            .CatalogInputs.Where(input => snapshot.CatalogInputs.Contains(input))
            .Should()
            .HaveCount(6);
    }

    [Test]
    public async Task SharedVenueWithoutArtistOwnershipRemainsRefreshable()
    {
        var source = await fixture.Source();
        var context = await AddShowAndVenues(source, source, source, source);
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE venues SET artist_id=NULL WHERE id=ANY(@ids)",
                new { ids = new[] { context.SourceVenueId, context.ShowVenueId } }
            )
        );
        var first = await Refresh(source);
        first.CatalogInputs.Should().ContainSingle();

        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE venues SET name='Corrected shared venue' WHERE id=@id",
                new { id = context.SourceVenueId }
            )
        );
        var corrected = await Refresh(source);
        corrected.Capture.Revision.Uuid.Should().NotBe(first.Capture.Revision.Uuid);
        corrected
            .CatalogInputs.Single()
            .Generation.Should()
            .BeGreaterThan(first.CatalogInputs.Single().Generation);
    }

    [Test]
    public async Task MissingContextClockFailsWithoutCaptureAndDoesNotPreventHistoricalSeeding()
    {
        var source = await fixture.Source();
        var foreign = await fixture.Source();
        await AddShowAndVenues(source, foreign, source, source);
        var clock = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<NormalizationCatalogDependency>(
                """
                SELECT a.uuid AS "ArtistUuid",r.generation AS "Generation" FROM artists a
                JOIN normalization.catalog_artist_revisions r ON r.artist_uuid=a.uuid WHERE a.id=@ArtistId
                """,
                foreign
            )
        );
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "DELETE FROM normalization.catalog_artist_revisions WHERE artist_uuid=@ArtistUuid",
                clock
            )
        );
        try
        {
            Func<Task> refresh = () => service.RefreshRecordingAsync(source.SourceUuid);
            await refresh.Should().ThrowAsync<NormalizationConflictException>();
            (await evidence.GetFetchHistoryAsync(Resource(source))).Should().BeEmpty();
            var seeded = await service.SeedRecordingAsync(source.SourceUuid);
            seeded.Disposition.Should().Be(NormalizationLegacySeedDisposition.Seeded);
        }
        finally
        {
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteAsync(
                    "INSERT INTO normalization.catalog_artist_revisions(artist_uuid,generation) VALUES(@ArtistUuid,@Generation)",
                    clock
                )
            );
        }
    }

    [TestCase("tracks")]
    [TestCase("sets")]
    [TestCase("bytes")]
    public async Task OversizedRefreshFailsWithoutTruncationOrCapture(string limit)
    {
        var source = await fixture.Source();
        var sql = limit switch
        {
            "tracks" => """
                INSERT INTO source_tracks(source_id,source_set_id,artist_id,track_position,title,slug,mp3_url,uuid,updated_at)
                SELECT @SourceId,(SELECT id FROM source_sets WHERE source_id=@SourceId LIMIT 1),@ArtistId,n,'Song',gen_random_uuid()::text,'https://example.test/'||n||'.mp3',gen_random_uuid(),now()
                FROM generate_series(1,10000) n
                """,
            "sets" => """
                INSERT INTO source_sets(source_id,index,name,uuid,updated_at)
                SELECT @SourceId,n,'Set',gen_random_uuid(),now() FROM generate_series(1,1000) n
                """,
            _ => "UPDATE sources SET taper_notes=repeat('x',16777216) WHERE id=@SourceId",
        };
        await fixture.Db.WithWriteConnection(c => c.ExecuteAsync(sql, source));

        Func<Task> refresh = () => service.RefreshRecordingAsync(source.SourceUuid);
        await refresh.Should().ThrowAsync<InvalidDataException>();
        (await evidence.GetCurrentInputAsync(Resource(source))).Should().BeNull();
        (await evidence.GetFetchHistoryAsync(Resource(source))).Should().BeEmpty();
    }

    [Test]
    public async Task MissingSourceDoesNotReturnEarlierEvidenceAsAFreshSnapshot()
    {
        var source = await fixture.Source();
        var earlier = await Refresh(source);
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync("DELETE FROM sources WHERE id=@SourceId", source)
        );

        (await service.RefreshRecordingAsync(source.SourceUuid)).Should().BeNull();
        var current = await evidence.GetCurrentInputAsync(Resource(source));
        current!.Revision.Uuid.Should().Be(earlier.Capture.Revision.Uuid);
        (await evidence.GetFetchHistoryAsync(Resource(source))).Should().ContainSingle();
    }

    [Test]
    public async Task AmbientCatalogTransactionIsRejectedBeforeRetention()
    {
        var source = await fixture.Source();
        using (var transaction = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            Func<Task> refresh = () => service.RefreshRecordingAsync(source.SourceUuid);
            await refresh
                .Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("Refresh legacy evidence outside*");
        }
        (await evidence.GetFetchHistoryAsync(Resource(source))).Should().BeEmpty();
    }

    [Test]
    public async Task CancellationLeavesNoSnapshotAndAFreshAttemptCanSucceed()
    {
        var source = await fixture.Source();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Func<Task> refresh = () =>
            service.RefreshRecordingAsync(source.SourceUuid, cancellation.Token);
        await refresh.Should().ThrowAsync<OperationCanceledException>();
        (await evidence.GetFetchHistoryAsync(Resource(source))).Should().BeEmpty();

        (await Refresh(source)).Capture.Fetch.AcceptedAsCurrent.Should().BeTrue();
    }

    [Test]
    public async Task CancellationDuringCaptureRollsBackReceiptAndAllowsCorrectedRetry()
    {
        var source = await fixture.Source();
        var initial = await Refresh(source);
        await using var blocker = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync(
            "SELECT uuid FROM normalization.input_resources WHERE provider_key=@key FOR UPDATE",
            new { key = source.SourceUuid.ToString("D") },
            transaction
        );

        using var cancellation = new CancellationTokenSource();
        var pending = service.RefreshRecordingAsync(source.SourceUuid, cancellation.Token);
        try
        {
            await RequireCaptureWait(blocker.ProcessID);
            // Capture holds no catalog transaction. Corrections can commit while retention waits.
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteAsync(
                    "UPDATE sources SET taper='Corrected while capture waited' WHERE id=@SourceId",
                    source
                )
            );
            cancellation.Cancel();
            Func<Task> refresh = () => pending;
            await refresh.Should().ThrowAsync<OperationCanceledException>();
        }
        finally
        {
            cancellation.Cancel();
            await transaction.RollbackAsync();
            try
            {
                await pending;
            }
            catch (OperationCanceledException) { }
        }

        (await evidence.GetFetchHistoryAsync(Resource(source))).Should().ContainSingle();
        var retried = await Refresh(source);
        retried
            .Capture.Fetch.ResourceGeneration.Should()
            .Be(initial.Capture.Fetch.ResourceGeneration + 1);
        var decoded = new NormalizationProviderDecoder().Decode(retried.Capture.Revision);
        decoded
            .Recordings.Single()
            .Text.Should()
            .Contain(text =>
                text.Field == "taper" && text.Text.Value == "Corrected while capture waited"
            );
    }

    [Test]
    public async Task CompetingRefreshesKeepTheirOwnFactsAndOnlyOneCanAdvanceTheInput()
    {
        var source = await fixture.Source();
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync("UPDATE sources SET taper='Initial recorder' WHERE id=@SourceId", source)
        );
        var initial = await Refresh(source);
        await using var blocker = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync(
            "SELECT uuid FROM normalization.input_resources WHERE provider_key=@key FOR UPDATE",
            new { key = source.SourceUuid.ToString("D") },
            transaction
        );

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var first = ObserveRefresh(service.RefreshRecordingAsync(source.SourceUuid, timeout.Token));
        Task<RefreshAttempt>? second = null;
        try
        {
            await RequireCaptureWait(blocker.ProcessID);
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteAsync(
                    "UPDATE sources SET taper='Corrected recorder' WHERE id=@SourceId",
                    source
                )
            );
            second = ObserveRefresh(
                service.RefreshRecordingAsync(source.SourceUuid, timeout.Token)
            );
            // Both attempts must have consumed the same input generation before either can commit.
            await RequireCaptureWait(blocker.ProcessID, minimumWaiting: 2);
        }
        finally
        {
            await transaction.RollbackAsync();
            await Task.WhenAll(second == null ? [first] : new[] { first, second });
        }

        var attempts = new[] { await first, await second! };
        attempts.Count(attempt => attempt.Snapshot != null).Should().Be(1);
        attempts.Count(attempt => attempt.Stale != null).Should().Be(1);
        var captures = attempts.Select(attempt => attempt.Capture).ToArray();
        captures
            .Should()
            .OnlyContain(capture =>
                capture.Fetch.ExpectedGeneration == initial.Capture.Fetch.ResourceGeneration
            );
        captures[0].Revision.Uuid.Should().Be(initial.Capture.Revision.Uuid);
        captures[1].Revision.Uuid.Should().NotBe(initial.Capture.Revision.Uuid);
        using var older = JsonDocument.Parse(captures[0].Revision.Payload);
        using var corrected = JsonDocument.Parse(captures[1].Revision.Payload);
        older
            .RootElement.GetProperty("source")
            .GetProperty("taper")
            .GetString()
            .Should()
            .Be("Initial recorder");
        corrected
            .RootElement.GetProperty("source")
            .GetProperty("taper")
            .GetString()
            .Should()
            .Be("Corrected recorder");

        var winner = attempts.Single(attempt => attempt.Snapshot != null).Snapshot!;
        var loser = attempts.Single(attempt => attempt.Stale != null).Stale!;
        loser.Resource.Should().Be(Resource(source));
        loser.Capture.Fetch.AcceptedAsCurrent.Should().BeFalse();
        loser.Capture.Fetch.AdvancedCurrent.Should().BeFalse();
        loser.Capture.Revision.Uuid.Should().NotBe(winner.Capture.Revision.Uuid);
        if (attempts[0].Snapshot is { } olderWinner)
        {
            olderWinner.CatalogInputs.Should().Equal(initial.CatalogInputs);
        }
        else
        {
            winner
                .CatalogInputs.Single()
                .Generation.Should()
                .BeGreaterThan(initial.CatalogInputs.Single().Generation);
        }

        var current = await evidence.GetCurrentInputAsync(Resource(source));
        current!.Revision.Uuid.Should().Be(winner.Capture.Revision.Uuid);
        current.Generation.Should().Be(initial.Capture.Fetch.ResourceGeneration + 1);
        var history = await evidence.GetFetchHistoryAsync(Resource(source));
        history.Should().HaveCount(3);
        history.Count(fetch => fetch.AcceptedAsCurrent).Should().Be(2);
    }

    private static async Task<RefreshAttempt> ObserveRefresh(
        Task<NormalizationLegacyRecordingSnapshot?> pending
    )
    {
        try
        {
            return new(await pending, null);
        }
        catch (NormalizationStaleInputException stale)
        {
            return new(null, stale);
        }
    }

    private sealed record RefreshAttempt(
        NormalizationLegacyRecordingSnapshot? Snapshot,
        NormalizationStaleInputException? Stale
    )
    {
        public NormalizationCaptureResult Capture => Snapshot?.Capture ?? Stale!.Capture;
    }

    private async Task<NormalizationLegacyRecordingSnapshot> Refresh(CatalogSource source) =>
        await service.RefreshRecordingAsync(source.SourceUuid)
        ?? throw new AssertionException("The fixture recording must exist.");

    private async Task<(long ShowId, long SourceVenueId, long ShowVenueId)> AddShowAndVenues(
        CatalogSource source,
        CatalogSource showOwner,
        CatalogSource sourceVenueOwner,
        CatalogSource showVenueOwner
    ) =>
        await fixture.Db.WithWriteConnection(async c =>
        {
            var sourceVenue = await c.QuerySingleAsync<long>(
                "INSERT INTO venues(artist_id,name,location,uuid,updated_at) VALUES(@ArtistId,'Source venue','Source city',@uuid,now()) RETURNING id",
                new { sourceVenueOwner.ArtistId, uuid = Guid.NewGuid() }
            );
            var showVenue = await c.QuerySingleAsync<long>(
                "INSERT INTO venues(artist_id,name,location,uuid,updated_at) VALUES(@ArtistId,'Show venue','Show city',@uuid,now()) RETURNING id",
                new { showVenueOwner.ArtistId, uuid = Guid.NewGuid() }
            );
            var show = await c.QuerySingleAsync<long>(
                "INSERT INTO shows(artist_id,date,display_date,uuid,venue_id,updated_at) VALUES(@ArtistId,'2000-01-01','2000-01-01',@uuid,@showVenue,now()) RETURNING id",
                new
                {
                    showOwner.ArtistId,
                    uuid = Guid.NewGuid(),
                    showVenue,
                }
            );
            await c.ExecuteAsync(
                "UPDATE sources SET show_id=@show,venue_id=@sourceVenue WHERE id=@SourceId",
                new
                {
                    show,
                    sourceVenue,
                    source.SourceId,
                }
            );
            return (show, sourceVenue, showVenue);
        });

    private static NormalizationResourceKey Resource(CatalogSource source) =>
        new("legacy_catalog", "recording", source.SourceUuid.ToString("D"));

    private static async Task RequireCaptureWait(int blockerPid, int minimumWaiting = 1)
    {
        await using var observer = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await observer.OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var waiting = await observer.QuerySingleAsync<int>(
                """
                WITH RECURSIVE blocked(pid) AS (
                    SELECT @blockerPid::integer
                    UNION
                    SELECT activity.pid FROM pg_stat_activity activity
                    JOIN blocked parent ON parent.pid=ANY(pg_blocking_pids(activity.pid))
                    WHERE activity.datname=current_database()
                )
                SELECT count(*)::integer FROM pg_stat_activity activity JOIN blocked USING(pid)
                WHERE activity.datname=current_database() AND wait_event_type='Lock'
                    AND query LIKE '%normalization.input_resources%'
                """,
                new { blockerPid }
            );
            if (waiting >= minimumWaiting)
            {
                return;
            }
            await Task.Delay(10);
        }
        throw new AssertionException("Refresh capture did not reach the held resource lock.");
    }
}
