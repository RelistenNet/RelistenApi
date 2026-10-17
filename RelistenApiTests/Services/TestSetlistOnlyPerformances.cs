using System.Transactions;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Relisten;
using Relisten.Api.Models;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Services.Popularity;
using DomainInput = RelistenApiTests.Services.NormalizationDomainFixture.DomainInput;

namespace RelistenApiTests.Services;

/// <summary>Source-free engagements share durable catalog identities without changing recording ownership.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestSetlistOnlyPerformances
{
    private NormalizationDomainFixture fixture = null!;
    private ShowPerformanceService performances = null!;
    private CatalogRebuildService rebuild = null!;
    private NormalizationReadService normalized = null!;
    private ShowService legacy = null!;
    private RedisService redis = null!;
    private EmptyShowPopularity popularity = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        SqlMapper.AddTypeHandler(new PersistentIdentifierHandler());
        SqlMapper.AddTypeHandler(new DateTimeHandler());
        performances = new(fixture.Db);
        redis = new(RedisService.BuildConfiguration("redis://127.0.0.1:16379"));
        rebuild = new(fixture.Db, redis);
        normalized = new(
            fixture.Db,
            Options.Create(new NormalizationReadOptions { Enabled = true })
        );
        popularity = new(fixture.Db, redis);
        legacy = new(
            fixture.Db,
            new SourceService(fixture.Db, new SourceSetService(fixture.Db)),
            new Lazy<PopularityService>(() => popularity.Service)
        );
    }

    [OneTimeTearDown]
    public void Stop()
    {
        redis?.connection.Dispose();
        fixture?.Dispose();
    }

    [TestCase("1975-03-07")]
    [TestCase("1975-XX-XX")]
    public async Task SourceFreePerformanceSurvivesRebuildAndAcceptedEditionAppearsWithoutLegacyPlayback(
        string date
    )
    {
        var artist = await Artist();
        var identity = Provider();
        var show = await performances.CreateOrSelectPerformanceAsync(
            artist,
            identity,
            new PerformanceEngagementTarget.CreateNew(),
            date
        );
        await rebuild.RebuildShows(artist);
        await rebuild.RebuildYears(artist);
        var current = await ReadShow(show.uuid);
        current.uuid.Should().Be(show.uuid);
        current.display_date.Should().Be(date);
        current.performance_type.Should().Be("unknown");
        current.performance_label.Should().BeNull();
        current
            .date.Should()
            .Be(date.Contains('X') ? new DateTime(1975, 1, 1) : new DateTime(1975, 3, 7));
        var edition = await PublishEdition(artist, show.uuid, date);
        var accepted = (await normalized.ListArtistSetlistsAsync(artist.uuid.ToString()))
            .items.Should()
            .ContainSingle()
            .Which;
        accepted.uuid.Should().Be(edition);
        accepted.show_uuid.Should().Be(show.uuid);
        accepted.literal_date.Should().Be(date);
        (await legacy.PerformancesForArtistOnDate(artist, date)).Should().BeEmpty();
    }

    [Test]
    public async Task LaterRecordingUsesSourceFreeShowUuidThroughSharedEngagementKey()
    {
        var artist = await Artist();
        var identity = Provider();
        var first = await performances.CreateOrSelectPerformanceAsync(
            artist,
            identity,
            new PerformanceEngagementTarget.CreateNew(),
            "1975-03-07"
        );
        var sourceUuid = Guid.NewGuid();
        var sourceId = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<int>(
                """
                INSERT INTO sources(artist_id,upstream_identifier,display_date,uuid,updated_at,is_soundboard,is_remaster,has_jamcharts)
                VALUES(@artistId,@identifier,'1975-03-07',@sourceUuid,now(),false,false,false) RETURNING id
                """,
                new
                {
                    artistId = artist.id,
                    identifier = sourceUuid.ToString(),
                    sourceUuid,
                }
            )
        );
        var attached = await performances.AssignSourceAsync(
            artist,
            sourceId,
            identity.PerformanceKey,
            "1975-03-07"
        );
        attached.uuid.Should().Be(first.uuid);
        (await SourceShow(sourceId)).uuid.Should().Be(first.uuid);
        (await ShowCount(artist)).Should().Be(1);
    }

    [Test]
    public async Task OuterTransactionAbortRollsBackReturnedShowAndKeyThenAllowsRetry()
    {
        var artist = await Artist();
        var identity = Provider();
        using (var outer = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            var show = await performances.CreateOrSelectPerformanceAsync(
                artist,
                identity,
                new PerformanceEngagementTarget.CreateNew(),
                "1975-03-07"
            );
            show.uuid.Should().NotBeEmpty();
            // The catalog operation joins its caller's transaction; the caller has not
            // committed this identity merely because the inner operation returned.
        }
        (await ShowCount(artist)).Should().Be(0);
        var keys = await fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM show_performance_keys WHERE artist_id=@id",
                new { artist.id }
            )
        );
        keys.Should().Be(0);
        var retry = await performances.CreateOrSelectPerformanceAsync(
            artist,
            identity,
            new PerformanceEngagementTarget.CreateNew(),
            "1975-03-07"
        );
        retry.uuid.Should().NotBeEmpty();
        (await ShowCount(artist)).Should().Be(1);
    }

    [Test]
    public async Task ExplicitSelectionKeepsLegacyUuidAndEverySourceAssociation()
    {
        var source = await fixture.Source();
        var artist = await Artist(source.ArtistId);
        await rebuild.RebuildShows(artist);
        var original = await SourceShow(source.SourceId);
        var before = await Playback(source.SourceId);
        var selected = await performances.CreateOrSelectPerformanceAsync(
            artist,
            Provider(),
            new PerformanceEngagementTarget.Existing(original.uuid),
            original.display_date,
            new(PerformanceType.Early, "Early concert")
        );
        await rebuild.RebuildShows(artist);
        selected.uuid.Should().Be(original.uuid);
        (await SourceShow(source.SourceId)).uuid.Should().Be(original.uuid);
        (await Playback(source.SourceId)).Should().BeEquivalentTo(before);
    }

    [Test]
    public async Task AliasSelectionPreservesPresentationUnlessExplicitUpdateClearsIt()
    {
        var artist = await Artist();
        var first = await performances.CreateOrSelectPerformanceAsync(
            artist,
            Provider(),
            new PerformanceEngagementTarget.CreateNew(),
            "1975-03-07",
            new(PerformanceType.Early, "Early concert")
        );
        var alias = Provider();
        var selected = await performances.CreateOrSelectPerformanceAsync(
            artist,
            alias,
            new PerformanceEngagementTarget.Existing(first.uuid),
            "1975-03-07"
        );
        selected.uuid.Should().Be(first.uuid);
        selected.performance_type.Should().Be("early");
        selected.performance_label.Should().Be("Early concert");
        var replay = await performances.CreateOrSelectPerformanceAsync(
            artist,
            alias,
            new PerformanceEngagementTarget.CreateNew(),
            "1975-03-08"
        );
        replay.performance_type.Should().Be("early");
        replay.performance_label.Should().Be("Early concert");
        replay.display_date.Should().Be("1975-03-08");
        var cleared = await performances.CreateOrSelectPerformanceAsync(
            artist,
            alias,
            new PerformanceEngagementTarget.Existing(first.uuid),
            "1975-03-08",
            new(PerformanceType.Unknown, null)
        );
        cleared.performance_type.Should().Be("unknown");
        cleared.performance_label.Should().BeNull();
        (await ShowCount(artist)).Should().Be(1);
    }

    [Test]
    public async Task ExplicitNewEngagementDoesNotClaimDateDefaultOrMoveAmbiguousSources()
    {
        var source = await fixture.Source();
        var artist = await Artist(source.ArtistId);
        await rebuild.RebuildShows(artist);
        var original = await SourceShow(source.SourceId);
        var additional = await performances.CreateOrSelectPerformanceAsync(
            artist,
            Provider(),
            new PerformanceEngagementTarget.CreateNew(),
            original.display_date,
            new(PerformanceType.Late)
        );
        additional.uuid.Should().NotBe(original.uuid);
        await rebuild.RebuildShows(artist);
        var fallback = await performances.AssignDefaultSourceAsync(
            artist,
            source.SourceId,
            original.display_date
        );
        fallback.uuid.Should().Be(original.uuid);
        (await SourceShow(source.SourceId)).uuid.Should().Be(original.uuid);
        (await legacy.PerformancesForArtistOnDate(artist, original.display_date))
            .Select(show => show.uuid)
            .Should()
            .Equal(original.uuid);
    }

    [Test]
    public async Task ExistingPhishIdentityUsesOriginalImporterEncodingAndShowUuid()
    {
        var source = await fixture.Source();
        var artist = await Artist(source.ArtistId);
        var imported = await performances.AssignSourceAsync(
            artist,
            source.SourceId,
            "phish.in:12345",
            "2026-10-17"
        );
        var selected = await performances.CreateOrSelectPerformanceAsync(
            artist,
            new PerformanceEngagementIdentity.Provider("phish.in", "show", "12345"),
            new PerformanceEngagementTarget.CreateNew(),
            "2026-10-17"
        );
        selected.uuid.Should().Be(imported.uuid);
        (await SourceShow(source.SourceId)).uuid.Should().Be(imported.uuid);
        (await ShowCount(artist)).Should().Be(1);
    }

    [Test]
    public async Task RegisteredKeyKeepsUuidThroughDateCorrectionsAndRejectsConflictingSelection()
    {
        var artist = await Artist();
        var identity = Provider();
        var first = await performances.CreateOrSelectPerformanceAsync(
            artist,
            identity,
            new PerformanceEngagementTarget.CreateNew(),
            "1975-XX-XX"
        );
        var corrected = await performances.CreateOrSelectPerformanceAsync(
            artist,
            identity,
            new PerformanceEngagementTarget.CreateNew(),
            "1975-03-08",
            new(PerformanceType.Late, "Late concert")
        );
        corrected.uuid.Should().Be(first.uuid);
        corrected.display_date.Should().Be("1975-03-08");
        corrected.performance_label.Should().Be("Late concert");
        var other = await performances.CreateOrSelectPerformanceAsync(
            artist,
            Provider(),
            new PerformanceEngagementTarget.CreateNew(),
            "1975-03-08"
        );
        await FluentActions
            .Invoking(() =>
                performances.CreateOrSelectPerformanceAsync(
                    artist,
                    identity,
                    new PerformanceEngagementTarget.Existing(other.uuid),
                    "1975-03-08"
                )
            )
            .Should()
            .ThrowAsync<InvalidOperationException>();
        (await ReadShow(first.uuid)).performance_label.Should().Be("Late concert");
    }

    [Test]
    public async Task ReviewedKeysAndProviderNamespacesRemainDistinctAcrossArtistsAndRetries()
    {
        var artist = await Artist();
        var identity = new PerformanceEngagementIdentity.Reviewed(
            "review:" + Guid.NewGuid().ToString("N")
        );
        var tasks = Enumerable
            .Range(0, 3)
            .Select(_ =>
                performances.CreateOrSelectPerformanceAsync(
                    artist,
                    identity,
                    new PerformanceEngagementTarget.CreateNew(),
                    "1975-03-07"
                )
            )
            .ToArray();
        var results = await Task.WhenAll(tasks);
        results.Select(show => show.uuid).Distinct().Should().ContainSingle();
        (await ShowCount(artist)).Should().Be(1);
        var otherArtist = await Artist();
        var other = await performances.CreateOrSelectPerformanceAsync(
            otherArtist,
            identity,
            new PerformanceEngagementTarget.CreateNew(),
            "1975-03-07"
        );
        other.uuid.Should().NotBe(results[0].uuid);
        await FluentActions
            .Invoking(() =>
                performances.CreateOrSelectPerformanceAsync(
                    artist,
                    Provider(),
                    new PerformanceEngagementTarget.Existing(other.uuid),
                    "1975-03-07"
                )
            )
            .Should()
            .ThrowAsync<ArgumentException>();
        (await ShowCount(artist)).Should().Be(1);
    }

    [Test]
    public async Task QualifiedProviderTupleKeepsNamespacesDistinctWithoutDelimiterCollisions()
    {
        var artist = await Artist();
        var first = await performances.CreateOrSelectPerformanceAsync(
            artist,
            new PerformanceEngagementIdentity.Provider("provider:room", "engagement", "é/one"),
            new PerformanceEngagementTarget.CreateNew(),
            "1975-03-07"
        );
        var second = await performances.CreateOrSelectPerformanceAsync(
            artist,
            new PerformanceEngagementIdentity.Provider("provider", "room:engagement", "é/one"),
            new PerformanceEngagementTarget.CreateNew(),
            "1975-03-07"
        );
        second.uuid.Should().NotBe(first.uuid);
        (await ShowCount(artist)).Should().Be(2);
    }

    [TestCase("XXXX-XX-XX")]
    [TestCase("unknown")]
    public async Task UnknownYearDoesNotInventShowOrKeyAndEditionEvidenceRemainsAvailable(
        string date
    )
    {
        var artist = await Artist();
        var input = await fixture.Capture(date, "songs");
        await FluentActions
            .Invoking(() =>
                performances.CreateOrSelectPerformanceAsync(
                    artist,
                    Provider(),
                    new PerformanceEngagementTarget.CreateNew(),
                    date
                )
            )
            .Should()
            .ThrowAsync<FormatException>();
        (await ShowCount(artist)).Should().Be(0);
        var keys = await fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM show_performance_keys WHERE artist_id=@id",
                new { artist.id }
            )
        );
        keys.Should().Be(0);
        var evidence = new NormalizationEvidenceService(fixture.Db);
        (await evidence.GetInputAsync(input.Reference.InputRevisionUuid)).Should().NotBeNull();
        await PublishEdition(artist, null, date, input);
        var edition = (await normalized.ListArtistSetlistsAsync(artist.uuid.ToString()))
            .items.Should()
            .ContainSingle()
            .Which;
        edition.show_uuid.Should().BeNull();
        edition.literal_date.Should().Be(date);
    }

    [Test]
    public async Task DateOnlyOrUnqualifiedKeyIsRejectedBeforeCatalogChanges()
    {
        var artist = await Artist();
        await FluentActions
            .Invoking(() =>
                performances.CreateOrSelectPerformanceAsync(
                    artist,
                    new PerformanceEngagementIdentity.Reviewed("1975-03-07"),
                    new PerformanceEngagementTarget.CreateNew(),
                    "1975-03-07"
                )
            )
            .Should()
            .ThrowAsync<ArgumentException>();
        await FluentActions
            .Invoking(() =>
                performances.CreateOrSelectPerformanceAsync(
                    artist,
                    new PerformanceEngagementIdentity.Provider("fixture", "", "123"),
                    new PerformanceEngagementTarget.CreateNew(),
                    "1975-03-07"
                )
            )
            .Should()
            .ThrowAsync<ArgumentException>();
        (await ShowCount(artist)).Should().Be(0);
    }

    [Test]
    public async Task CancelledArtistLockWaitRollsBackAndSameEngagementCanRetry()
    {
        var artist = await Artist();
        var identity = Provider();
        await using var gateConnection = fixture.Db.CreateConnection(false, false);
        await gateConnection.OpenAsync();
        await using var gate = await gateConnection.BeginTransactionAsync();
        var gatePid = await gateConnection.ExecuteScalarAsync<int>(
            "SELECT pg_backend_pid()",
            transaction: gate
        );
        await gateConnection.ExecuteAsync(
            "SELECT pg_advisory_xact_lock(@ns,@id)",
            new { ns = ShowPerformanceService.LockNamespace, artist.id },
            gate
        );
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pending = performances.CreateOrSelectPerformanceAsync(
            artist,
            identity,
            new PerformanceEngagementTarget.CreateNew(),
            "1975-03-07",
            cancellationToken: deadline.Token
        );
        var released = false;
        try
        {
            await WaitForArtistLock(gatePid, deadline.Token);
            deadline.Cancel();
            await FluentActions
                .Invoking(() => pending)
                .Should()
                .ThrowAsync<OperationCanceledException>();
            await gate.RollbackAsync();
            released = true;
            (await ShowCount(artist)).Should().Be(0);
            var retry = await performances.CreateOrSelectPerformanceAsync(
                artist,
                identity,
                new PerformanceEngagementTarget.CreateNew(),
                "1975-03-07"
            );
            retry.uuid.Should().NotBeEmpty();
        }
        finally
        {
            try
            {
                if (!released)
                {
                    await gate.RollbackAsync();
                }
            }
            finally
            {
                deadline.Cancel();
                try
                {
                    await pending.WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (OperationCanceledException) { }
            }
        }
    }

    [Test]
    public async Task CallerArtistMutationDuringLockWaitCannotChangeLockedArtistIdentity()
    {
        var original = await Artist();
        var destination = await Artist();
        var request = new Artist
        {
            id = original.id,
            uuid = original.uuid,
            features = new Features(),
        };
        await using var gateConnection = fixture.Db.CreateConnection(false, false);
        await gateConnection.OpenAsync();
        await using var gate = await gateConnection.BeginTransactionAsync();
        var gatePid = await gateConnection.ExecuteScalarAsync<int>(
            "SELECT pg_backend_pid()",
            transaction: gate
        );
        await gateConnection.ExecuteAsync(
            "SELECT pg_advisory_xact_lock(@ns,@id)",
            new { ns = ShowPerformanceService.LockNamespace, original.id },
            gate
        );
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pending = performances.CreateOrSelectPerformanceAsync(
            request,
            Provider(),
            new PerformanceEngagementTarget.CreateNew(),
            "1975-03-07",
            cancellationToken: deadline.Token
        );
        var released = false;
        try
        {
            await WaitForArtistLock(gatePid, deadline.Token);
            request.id = destination.id;
            request.uuid = destination.uuid;
            await gate.RollbackAsync();
            released = true;
            var show = await pending;
            show.artist_id.Should().Be(original.id);
            (await ShowCount(original)).Should().Be(1);
            (await ShowCount(destination)).Should().Be(0);
        }
        finally
        {
            try
            {
                if (!released)
                {
                    await gate.RollbackAsync();
                }
            }
            finally
            {
                deadline.Cancel();
                try
                {
                    await pending.WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (OperationCanceledException) { }
            }
        }
    }

    private async Task WaitForArtistLock(int gatePid, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var blocked = await fixture.Db.WithWriteConnection(c =>
                c.ExecuteScalarAsync<bool>(
                    new CommandDefinition(
                        """
                        SELECT EXISTS(SELECT 1 FROM pg_stat_activity activity WHERE activity.datname=current_database()
                            AND @gatePid=ANY(pg_blocking_pids(activity.pid)) AND activity.query LIKE '%pg_advisory_xact_lock%')
                        """,
                        new { gatePid },
                        cancellationToken: ct
                    )
                )
            );
            if (blocked)
            {
                return;
            }
            await Task.Delay(20, ct);
        }
    }

    private async Task<Guid> PublishEdition(
        Artist artist,
        Guid? showUuid,
        string date,
        DomainInput? input = null
    )
    {
        input ??= await fixture.Capture(date, "songs");
        var edition = new SetlistEditionChange
        {
            Uuid = Guid.NewGuid(),
            ArtistUuid = artist.uuid,
            ShowUuid = showUuid,
            LiteralDate = date,
            Provider = "performance-fixture",
            ResourceType = "setlist",
            ProviderKey = Guid.NewGuid().ToString(),
            Evidence = [input.Reference],
        };
        var owner = await fixture.Own(input, "setlist", edition.Uuid);
        var music = new NormalizedMusicService(fixture.Db, fixture.Work);
        var result = await music.PublishAsync(
            owner.Ticket.WorkUuid,
            new() { Editions = [edition] }
        );
        result.Status.Should().Be(NormalizationPublicationStatus.Published);
        return edition.Uuid;
    }

    private async Task<Artist> Artist(int? id = null)
    {
        if (id != null)
        {
            var existing = await fixture.Db.WithWriteConnection(c =>
                c.QuerySingleAsync<Artist>("SELECT * FROM artists WHERE id=@id", new { id })
            );
            existing.features = new Features();
            await popularity.PrepareArtistAsync(existing.uuid);
            return existing;
        }
        var uuid = Guid.NewGuid();
        var artistId = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<int>(
                "INSERT INTO artists(name,slug,uuid,updated_at) VALUES('Setlist performance fixture',@slug,@uuid,now()) RETURNING id",
                new { slug = "setlist-performance-" + uuid, uuid }
            )
        );
        await popularity.PrepareArtistAsync(uuid);
        return new Artist
        {
            id = artistId,
            uuid = uuid,
            features = new Features(),
        };
    }

    private Task<Show> ReadShow(Guid uuid) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<Show>("SELECT * FROM shows WHERE uuid=@uuid", new { uuid })
        );

    private Task<Show> SourceShow(int sourceId) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<Show>(
                "SELECT sh.* FROM sources src JOIN shows sh ON sh.id=src.show_id WHERE src.id=@sourceId",
                new { sourceId }
            )
        );

    private Task<long> ShowCount(Artist artist) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM shows WHERE artist_id=@id",
                new { artist.id }
            )
        );

    private Task<(Guid Uuid, int? Show, string? Url)> Playback(int sourceId) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<(Guid, int?, string?)>(
                "SELECT src.uuid,src.show_id,st.mp3_url FROM sources src JOIN source_tracks st ON st.source_id=src.id WHERE src.id=@sourceId",
                new { sourceId }
            )
        );

    private static PerformanceEngagementIdentity.Provider Provider() =>
        new("performance-fixture", "engagement", Guid.NewGuid().ToString("N"));
}
