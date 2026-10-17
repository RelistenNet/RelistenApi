using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Relisten;
using Relisten.Api.Models;
using Relisten.Data;

namespace RelistenApiTests.Services;

/// <summary>Exercises durable identities and ambiguity through assignment and rebuild contracts.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestShowPerformances
{
    private DbService db = null!;
    private RedisService redis = null!;
    private CatalogRebuildService rebuild = null!;
    private ShowPerformanceService performances = null!;
    private string? previousWrite;
    private string? previousRead;

    [OneTimeSetUp]
    public void Start()
    {
        if (Environment.GetEnvironmentVariable("RELISTEN_LOCAL_INGESTION_TESTS") != "1")
        {
            Assert.Ignore("Run ./test-local.sh to include local integration tests.");
        }

        SqlMapper.AddTypeHandler(new PersistentIdentifierHandler());
        SqlMapper.AddTypeHandler(new DateTimeHandler());
        previousWrite = DbService.ConnStr;
        previousRead = DbService.ReadOnlyConnStr;
        db = new DbService(
            LocalTestDatabase.DatabaseUrl,
            null,
            null,
            Host.CreateApplicationBuilder().Environment,
            NullLogger<DbService>.Instance
        );
        redis = new RedisService(RedisService.BuildConfiguration("redis://127.0.0.1:16379"));
        rebuild = new CatalogRebuildService(db, redis);
        performances = new ShowPerformanceService(db);
    }

    [OneTimeTearDown]
    public void Stop()
    {
        redis?.connection.Dispose();
        if (previousWrite != null)
        {
            DbService.ConnStr = previousWrite;
            DbService.ReadOnlyConnStr = previousRead!;
        }
    }

    [Test]
    public async Task SplitKeepsOriginalShowAndMovesOnlySupportedRecordingsAcrossRebuildAndCorrections()
    {
        var artist = await CreateArtist();
        var early = await CreateSource(artist, "1975-03-07");
        var late = await CreateSource(artist, "1975-03-07");
        var uncertain = await CreateSource(artist, "1975-03-07");
        await rebuild.RebuildShows(artist);
        var original = await ShowForSource(early);
        var playback = await Playback(late);

        var survivor = await performances.AssignSourceAsync(
            artist,
            early,
            "review:early",
            "1975-03-07",
            PerformanceType.Early,
            "Early show"
        );
        var added = await performances.AssignSourceAsync(
            artist,
            late,
            "review:late",
            "1975-03-07",
            PerformanceType.Late,
            "Late show"
        );
        await performances.AssignSourceAsync(
            artist,
            uncertain,
            "retained:combined",
            "1975-03-07",
            assignmentKind: "combined",
            evidence: "{\"basis\":\"recording contains early and late shows; boundaries unknown\"}"
        );
        await rebuild.RebuildShows(artist);
        survivor.uuid.Should().Be(original.uuid);
        added.uuid.Should().NotBe(original.uuid);
        (await ShowForSource(uncertain)).uuid.Should().Be(original.uuid);
        (await Playback(late)).Should().BeEquivalentTo(playback);
        (await ShowForSource(late)).uuid.Should().Be(added.uuid);

        // Correct metadata after the explicit move committed, simulating recovery before rebuild.
        await db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET display_date='1975-03-08' WHERE id=@late",
                new { late }
            )
        );
        var corrected = await performances.AssignSourceAsync(
            artist,
            late,
            "review:late",
            "1975-03-08",
            PerformanceType.Other,
            "Midnight concert"
        );
        await rebuild.RebuildShows(artist);
        await rebuild.RebuildShows(artist);
        corrected.uuid.Should().Be(added.uuid);
        var after = await ShowForSource(late);
        after.uuid.Should().Be(added.uuid);
        after.display_date.Should().Be("1975-03-08");
        after.performance_type.Should().Be("other");
        (await Playback(late)).Should().BeEquivalentTo(playback);
    }

    [TestCase("unresolved")]
    [TestCase("combined")]
    public async Task NewUncertainCoverageUsesStableDateDefaultAndCanLaterMoveWithPlaybackUnchanged(
        string kind
    )
    {
        var artist = await CreateArtist();
        var early = await CreateSource(artist, "1975-03-07");
        var late = await CreateSource(artist, "1975-03-07");
        var first = await performances.AssignSourceAsync(
            artist,
            early,
            "provider:early",
            "1975-03-07",
            PerformanceType.Early,
            "Early show"
        );
        var second = await performances.AssignSourceAsync(
            artist,
            late,
            "provider:late",
            "1975-03-07",
            PerformanceType.Late,
            "Late show"
        );
        var uncertain = await CreateSource(artist, "1975-03-07");
        var playback = await Playback(uncertain);
        var assigned = await performances.AssignDefaultSourceAsync(
            artist,
            uncertain,
            "1975-03-07",
            "{\"basis\":\"artist and date known; performance coverage uncertain\"}",
            kind
        );
        await rebuild.RebuildShows(artist);
        assigned.uuid.Should().Be(first.uuid);
        (await ShowForSource(uncertain)).uuid.Should().Be(first.uuid);
        (await performances.CanEnrichSourceAsync(artist, uncertain)).Should().BeFalse();
        var classification = await db.WithWriteConnection(c =>
            c.QuerySingleAsync<string>(
                "SELECT assignment_kind FROM source_show_assignments WHERE source_id=@uncertain",
                new { uncertain }
            )
        );
        classification.Should().Be(kind);
        await performances.AssignSourceAsync(
            artist,
            early,
            "provider:early",
            "1975-03-07",
            PerformanceType.Other,
            "Z-last label"
        );
        var repeated = await performances.AssignDefaultSourceAsync(
            artist,
            uncertain,
            "1975-03-07",
            assignmentKind: kind
        );
        repeated.uuid.Should().Be(first.uuid, "presentation changes cannot move the date default");
        await performances.AssignSourceAsync(
            artist,
            uncertain,
            "provider:late",
            "1975-03-07",
            PerformanceType.Late,
            "Late show"
        );
        await rebuild.RebuildShows(artist);
        (await ShowForSource(uncertain)).uuid.Should().Be(second.uuid);
        (await Playback(uncertain)).Should().BeEquivalentTo(playback);
        var retained = await performances.AssignDefaultSourceAsync(
            artist,
            uncertain,
            "1975-03-07",
            assignmentKind: kind
        );
        retained
            .uuid.Should()
            .Be(second.uuid, "uncertain updates retain an existing accepted primary");
    }

    [TestCase("1975-03-07")]
    [TestCase("1975-XX-XX")]
    public async Task DefaultCoverageMetadataCorrectionKeepsUnclaimedIdentity(string originalDate)
    {
        var artist = await CreateArtist();
        var source = await CreateSource(artist, originalDate);
        var original = await performances.AssignDefaultSourceAsync(
            artist,
            source,
            originalDate,
            assignmentKind: "combined"
        );
        await rebuild.RebuildShows(artist);
        await db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET display_date='1975-03-08' WHERE id=@source",
                new { source }
            )
        );
        await performances.AssignDefaultSourceAsync(
            artist,
            source,
            "1975-03-08",
            assignmentKind: "combined"
        );
        await rebuild.RebuildShows(artist);
        var corrected = await ShowForSource(source);
        corrected.uuid.Should().Be(original.uuid);
        corrected.display_date.Should().Be("1975-03-08");
    }

    [Test]
    public async Task DefaultCombinedCoverageStaysPlayableWithoutAuthorizingDateOnlySetlistEnrichment()
    {
        var artist = await CreateArtist();
        var source = await CreateSource(artist, "1975-03-07");
        var venue = await db.WithWriteConnection(c =>
            c.QuerySingleAsync<int>(
                "INSERT INTO venues(artist_id,name,slug,uuid,updated_at) VALUES(@id,'Unassigned venue',@slug,@uuid,now()) RETURNING id",
                new
                {
                    artist.id,
                    slug = "unassigned-" + Guid.NewGuid(),
                    uuid = Guid.NewGuid(),
                }
            )
        );
        var providerSetlists = new SetlistShowService(db);
        var setlist = await providerSetlists.Save(
            new SetlistShow
            {
                artist_id = artist.id,
                venue_id = venue,
                upstream_identifier = "date-only",
                date = new DateTime(1975, 3, 7),
                updated_at = DateTime.UtcNow,
            }
        );
        var songService = new SetlistSongService(db);
        var song = (
            await songService.InsertAll(
                artist,
                new[]
                {
                    new SetlistSong
                    {
                        artist_id = artist.id,
                        name = "Unassigned song",
                        slug = "unassigned-song",
                        upstream_identifier = "unassigned-song",
                        updated_at = DateTime.UtcNow,
                    },
                }
            )
        ).Single();
        await providerSetlists.UpdateSongPlays(setlist, new[] { song });
        await rebuild.RebuildShows(artist);
        var original = await ShowForSource(source);
        original.venue_id.Should().Be(venue);
        await performances.AssignDefaultSourceAsync(
            artist,
            source,
            "1975-03-07",
            assignmentKind: "combined"
        );
        await rebuild.RebuildShows(artist);
        var uncertain = await ShowForSource(source);
        uncertain.uuid.Should().Be(original.uuid);
        uncertain.venue_id.Should().BeNull();
        (await performances.CanEnrichSourceAsync(artist, source)).Should().BeFalse();
        (await songService.AllForArtistWithPlayCount(artist))
            .Single()
            .shows_played_at.Should()
            .Be(0);
        await performances.AssignSourceAsync(
            artist,
            source,
            "review:one-performance",
            "1975-03-07"
        );
        await rebuild.RebuildShows(artist);
        (await ShowForSource(source)).uuid.Should().Be(original.uuid);
        (await ShowForSource(source)).venue_id.Should().Be(venue);
        (await songService.AllForArtistWithPlayCount(artist))
            .Single()
            .shows_played_at.Should()
            .Be(1);
    }

    [Test]
    public async Task NewCombinedUnknownDatesStayPlayableWithSeparateSourceIdentities()
    {
        var artist = await CreateArtist();
        var first = await CreateSource(artist, "1975-XX-XX");
        var second = await CreateSource(artist, "1975-XX-XX");
        var firstShow = await performances.AssignDefaultSourceAsync(
            artist,
            first,
            "1975-XX-XX",
            assignmentKind: "combined"
        );
        var secondShow = await performances.AssignDefaultSourceAsync(
            artist,
            second,
            "1975-XX-XX",
            assignmentKind: "combined"
        );
        await rebuild.RebuildShows(artist);
        firstShow.uuid.Should().NotBe(secondShow.uuid);
        (await ShowForSource(first)).uuid.Should().Be(firstShow.uuid);
        (await ShowForSource(second)).uuid.Should().Be(secondShow.uuid);
    }

    [Test]
    public async Task RebuildDoesNotMergeEqualPartialDatesAndOrdinaryDateCorrectionKeepsIdentity()
    {
        var artist = await CreateArtist();
        var first = await CreateSource(artist, "1974-XX-XX");
        var second = await CreateSource(artist, "1974-XX-XX");
        var exact = await CreateSource(artist, "1974-03-07");
        await rebuild.RebuildShows(artist);
        (await ShowForSource(first)).uuid.Should().NotBe((await ShowForSource(second)).uuid);
        var original = await ShowForSource(exact);
        await db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET display_date='1974-03-08' WHERE id=@exact",
                new { exact }
            )
        );
        await rebuild.RebuildShows(artist);
        var corrected = await ShowForSource(exact);
        corrected.uuid.Should().Be(original.uuid);
        corrected.display_date.Should().Be("1974-03-08");
    }

    [Test]
    public async Task AmbiguousDateSetlistCannotContaminateSplitButExplicitAssignmentCanEnrichIt()
    {
        var artist = await CreateArtist();
        var early = await CreateSource(artist, "1975-03-07");
        var late = await CreateSource(artist, "1975-03-07");
        var venue = await db.WithWriteConnection(c =>
            c.QuerySingleAsync<int>(
                "INSERT INTO venues(artist_id,name,slug,uuid,updated_at) VALUES(@id,'Later venue',@slug,@uuid,now()) RETURNING id",
                new
                {
                    artist.id,
                    slug = "venue-" + Guid.NewGuid(),
                    uuid = Guid.NewGuid(),
                }
            )
        );
        var setlist = await new SetlistShowService(db).Save(
            new SetlistShow
            {
                artist_id = artist.id,
                upstream_identifier = "provider-show",
                date = new DateTime(1975, 3, 7),
                venue_id = venue,
                updated_at = DateTime.UtcNow,
            }
        );
        await rebuild.RebuildShows(artist);
        (await ShowForSource(early)).venue_id.Should().Be(venue);
        var earlyShow = await performances.AssignSourceAsync(
            artist,
            early,
            "review:early",
            "1975-03-07",
            PerformanceType.Early
        );
        var lateShow = await performances.AssignSourceAsync(
            artist,
            late,
            "review:late",
            "1975-03-07",
            PerformanceType.Late
        );
        await rebuild.RebuildShows(artist);
        (await ShowForSource(early)).venue_id.Should().BeNull();
        (await ShowForSource(late)).venue_id.Should().BeNull();
        await performances.AssignSetlistAsync(
            artist,
            setlist.id,
            lateShow.uuid,
            "{\"provider\":\"reviewed show identity\"}"
        );
        await rebuild.RebuildShows(artist);
        (await ShowForSource(early)).venue_id.Should().BeNull();
        (await ShowForSource(late)).venue_id.Should().Be(venue);
        earlyShow.uuid.Should().NotBe(lateShow.uuid);
    }

    [TestCase("1975-03-07")]
    [TestCase("1975-03-XX")]
    [TestCase("1975-03-08")]
    public async Task SourceSpecificUnknownDateCorrectionKeepsIdentity(string correctedDate)
    {
        var artist = await CreateArtist();
        var uncertain = await CreateSource(artist, "1975-XX-XX");
        var neighbor = await CreateSource(artist, "1975-03-08");
        await rebuild.RebuildShows(artist);
        var original = await ShowForSource(uncertain);
        var other = await ShowForSource(neighbor);
        await db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET display_date=@correctedDate WHERE id=@uncertain",
                new { correctedDate, uncertain }
            )
        );
        await rebuild.RebuildShows(artist);
        await rebuild.RebuildShows(artist);
        var corrected = await ShowForSource(uncertain);
        corrected.uuid.Should().Be(original.uuid);
        corrected.display_date.Should().Be(correctedDate);
        (await ShowForSource(neighbor)).uuid.Should().Be(other.uuid);
    }

    [Test]
    public async Task CorrectingDateOntoAnotherPerformanceRetainsBothIdentities()
    {
        var artist = await CreateArtist();
        var first = await CreateSource(artist, "1975-03-07");
        var second = await CreateSource(artist, "1975-03-08");
        await rebuild.RebuildShows(artist);
        var originalFirst = await ShowForSource(first);
        var originalSecond = await ShowForSource(second);
        await db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET display_date='1975-03-08' WHERE id=@first",
                new { first }
            )
        );
        await rebuild.RebuildShows(artist);
        await rebuild.RebuildShows(artist);
        (await ShowForSource(first)).uuid.Should().Be(originalFirst.uuid);
        (await ShowForSource(second)).uuid.Should().Be(originalSecond.uuid);
        (await ShowForSource(first)).display_date.Should().Be("1975-03-08");
    }

    [Test]
    public async Task ConflictingCorrectionInRefinedUnknownGroupWaitsForAgreementThenKeepsIdentity()
    {
        var artist = await CreateArtist();
        var first = await CreateSource(artist, "1975-XX-XX");
        await rebuild.RebuildShows(artist);
        await db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET display_date='1975-03-07' WHERE id=@first",
                new { first }
            )
        );
        await rebuild.RebuildShows(artist);
        var second = await CreateSource(artist, "1975-03-07");
        await rebuild.RebuildShows(artist);
        var original = await ShowForSource(first);
        await db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET display_date='1975-03-08' WHERE id=@first",
                new { first }
            )
        );
        await rebuild.RebuildShows(artist);
        var held = await ShowForSource(first);
        held.uuid.Should().Be(original.uuid);
        held.display_date.Should().Be("1975-03-07");
        held.date.Should().Be(new DateTime(1975, 3, 7));
        await db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET display_date='1975-03-08' WHERE id=@second",
                new { second }
            )
        );
        await rebuild.RebuildShows(artist);
        var recovered = await ShowForSource(first);
        recovered.uuid.Should().Be(original.uuid);
        recovered.display_date.Should().Be("1975-03-08");
        (await ShowForSource(second)).uuid.Should().Be(original.uuid);
    }

    [Test]
    public async Task FirstProviderIdentityCanAdoptCorrectedLegacyShowWithoutReplacingUuid()
    {
        var artist = await CreateArtist();
        var source = await CreateSource(artist, "1975-03-07");
        await rebuild.RebuildShows(artist);
        var original = await ShowForSource(source);
        await db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET display_date='1975-03-08' WHERE id=@source",
                new { source }
            )
        );
        var adopted = await performances.AssignSourceAsync(
            artist,
            source,
            "provider:first-known-key",
            "1975-03-08"
        );
        await rebuild.RebuildShows(artist);
        adopted.uuid.Should().Be(original.uuid);
        (await ShowForSource(source)).display_date.Should().Be("1975-03-08");
    }

    [Test]
    public async Task InvalidDateCorrectionKeepsLastPublishedDateAndRecoversWithoutNewIdentity()
    {
        var artist = await CreateArtist();
        var source = await CreateSource(artist, "1975-03-07");
        await rebuild.RebuildShows(artist);
        var original = await ShowForSource(source);
        await db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET display_date='unknown' WHERE id=@source",
                new { source }
            )
        );
        await rebuild.RebuildShows(artist);
        var retained = await ShowForSource(source);
        retained.uuid.Should().Be(original.uuid);
        retained.display_date.Should().Be("1975-03-07");
        await db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET display_date='1975-03-08' WHERE id=@source",
                new { source }
            )
        );
        await rebuild.RebuildShows(artist);
        var recovered = await ShowForSource(source);
        recovered.uuid.Should().Be(original.uuid);
        recovered.display_date.Should().Be("1975-03-08");
    }

    [Test]
    public async Task RefinedSourceSpecificDateCanAcceptAnOrdinaryRecordingWithoutLosingIdentity()
    {
        var artist = await CreateArtist();
        var first = await CreateSource(artist, "1975-XX-XX");
        await rebuild.RebuildShows(artist);
        var original = await ShowForSource(first);
        await db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET display_date='1975-03-07' WHERE id=@first",
                new { first }
            )
        );
        await rebuild.RebuildShows(artist);
        var second = await CreateSource(artist, "1975-03-07");
        await rebuild.RebuildShows(artist);
        (await ShowForSource(first)).uuid.Should().Be(original.uuid);
        (await ShowForSource(second)).uuid.Should().Be(original.uuid);
    }

    [Test]
    public async Task TwoDateCorrectionsIntoEmptyDateRetainIdentitiesAndCanBeReviewedAfterward()
    {
        var artist = await CreateArtist();
        var first = await CreateSource(artist, "1975-03-07");
        var second = await CreateSource(artist, "1975-03-08");
        await rebuild.RebuildShows(artist);
        var oldFirst = await ShowForSource(first);
        var oldSecond = await ShowForSource(second);
        await db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET display_date='1975-03-09' WHERE id IN (@first,@second)",
                new { first, second }
            )
        );
        await rebuild.RebuildShows(artist);
        var acceptedFirst = await performances.AssignSourceAsync(
            artist,
            first,
            "review:first",
            "1975-03-09",
            PerformanceType.Early
        );
        var acceptedSecond = await performances.AssignSourceAsync(
            artist,
            second,
            "review:second",
            "1975-03-09",
            PerformanceType.Late
        );
        await rebuild.RebuildShows(artist);
        acceptedFirst.uuid.Should().Be(oldFirst.uuid);
        acceptedSecond.uuid.Should().Be(oldSecond.uuid);
    }

    [Test]
    public async Task RemovingLastRecordingClearsCountsButReappearanceRestoresAcceptedShow()
    {
        var artist = await CreateArtist();
        var source = await CreateSource(artist, "1975-03-07");
        var accepted = await performances.AssignSourceAsync(
            artist,
            source,
            "provider:stable",
            "1975-03-07"
        );
        await rebuild.RebuildShows(artist);
        await rebuild.RebuildYears(artist);
        await db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "DELETE FROM source_tracks WHERE source_id=@source; DELETE FROM source_sets WHERE source_id=@source; DELETE FROM sources WHERE id=@source",
                new { source }
            )
        );
        await rebuild.RebuildShows(artist);
        await rebuild.RebuildYears(artist);
        var yearCount = await db.WithWriteConnection(c =>
            c.QuerySingleAsync<int>(
                "SELECT count(*) FROM years WHERE artist_id=@id",
                new { artist.id }
            )
        );
        yearCount.Should().Be(0);
        var later = await CreateSource(artist, "1975-03-07");
        var restored = await performances.AssignSourceAsync(
            artist,
            later,
            "provider:stable",
            "1975-03-07"
        );
        await rebuild.RebuildShows(artist);
        await rebuild.RebuildYears(artist);
        restored.uuid.Should().Be(accepted.uuid);
        (await ShowForSource(later)).uuid.Should().Be(accepted.uuid);
    }

    [Test]
    public async Task SongBrowseCountsAndShowsFollowExplicitPerformancesInsteadOfDate()
    {
        var artist = await CreateArtist();
        var early = await CreateSource(artist, "1975-03-07");
        var late = await CreateSource(artist, "1975-03-07");
        var earlyShow = await performances.AssignSourceAsync(
            artist,
            early,
            "provider:early",
            "1975-03-07",
            PerformanceType.Early
        );
        var lateShow = await performances.AssignSourceAsync(
            artist,
            late,
            "provider:late",
            "1975-03-07",
            PerformanceType.Late
        );
        var songService = new SetlistSongService(db);
        var songs = (
            await songService.InsertAll(
                artist,
                new[]
                {
                    new SetlistSong
                    {
                        artist_id = artist.id,
                        name = "Early song",
                        slug = "early-song",
                        upstream_identifier = "early-song",
                        updated_at = DateTime.UtcNow,
                    },
                    new SetlistSong
                    {
                        artist_id = artist.id,
                        name = "Shared song",
                        slug = "shared-song",
                        upstream_identifier = "shared-song",
                        updated_at = DateTime.UtcNow,
                    },
                }
            )
        ).ToArray();
        var venue = await db.WithWriteConnection(c =>
            c.QuerySingleAsync<int>(
                "INSERT INTO venues(artist_id,name,slug,uuid,updated_at) VALUES(@id,'Song fixture venue',@slug,@uuid,now()) RETURNING id",
                new
                {
                    artist.id,
                    slug = "song-venue-" + Guid.NewGuid(),
                    uuid = Guid.NewGuid(),
                }
            )
        );
        var setlists = new SetlistShowService(db);
        var earlySetlist = await setlists.Save(
            new SetlistShow
            {
                artist_id = artist.id,
                venue_id = venue,
                upstream_identifier = "early",
                date = new DateTime(1975, 3, 7),
                updated_at = DateTime.UtcNow,
            }
        );
        var lateSetlist = await setlists.Save(
            new SetlistShow
            {
                artist_id = artist.id,
                venue_id = venue,
                upstream_identifier = "late",
                date = new DateTime(1975, 3, 7),
                updated_at = DateTime.UtcNow,
            }
        );
        await setlists.UpdateSongPlays(earlySetlist, songs);
        await setlists.UpdateSongPlays(lateSetlist, songs.Where(s => s.slug == "shared-song"));
        await performances.AssignSetlistAsync(artist, earlySetlist.id, earlyShow.uuid);
        await performances.AssignSetlistAsync(artist, lateSetlist.id, lateShow.uuid);
        await rebuild.RebuildShows(artist);
        await rebuild.RebuildYears(artist);
        var counts = (await songService.AllForArtistWithPlayCount(artist)).ToArray();
        counts.Single(s => s.slug == "early-song").shows_played_at.Should().Be(1);
        counts.Single(s => s.slug == "shared-song").shows_played_at.Should().Be(2);
        var earlySong = await songService.ForIdWithShows(
            artist,
            songs.Single(s => s.slug == "early-song").id
        );
        earlySong!.shows.Select(sh => sh.uuid).Should().Equal(earlyShow.uuid);
        var sharedSong = await songService.ForIdWithShows(
            artist,
            songs.Single(s => s.slug == "shared-song").id
        );
        sharedSong!
            .shows.Select(sh => sh.uuid)
            .Should()
            .BeEquivalentTo(new[] { earlyShow.uuid, lateShow.uuid });
    }

    private async Task<Artist> CreateArtist()
    {
        var uuid = Guid.NewGuid();
        var id = await db.WithWriteConnection(c =>
            c.QuerySingleAsync<int>(
                "INSERT INTO artists(name,slug,uuid,updated_at) VALUES('Performance fixture',@slug,@uuid,now()) RETURNING id",
                new { slug = "performance-" + uuid, uuid }
            )
        );
        return new Artist
        {
            id = id,
            uuid = uuid,
            features = new Features(),
        };
    }

    private async Task<int> CreateSource(Artist artist, string displayDate)
    {
        var uuid = Guid.NewGuid();
        return await db.WithWriteConnection(async c =>
        {
            var id = await c.QuerySingleAsync<int>(
                """
                INSERT INTO sources(artist_id,upstream_identifier,display_date,uuid,updated_at,is_soundboard,is_remaster,has_jamcharts)
                VALUES(@artistId,@identifier,@displayDate,@uuid,now(),false,false,false) RETURNING id
                """,
                new
                {
                    artistId = artist.id,
                    identifier = uuid.ToString(),
                    displayDate,
                    uuid,
                }
            );
            var setId = await c.QuerySingleAsync<long>(
                "INSERT INTO source_sets(source_id,index,name,uuid,updated_at) VALUES(@id,0,'Set',@uuid,now()) RETURNING id",
                new { id, uuid = Guid.NewGuid() }
            );
            await c.ExecuteAsync(
                """
                INSERT INTO source_tracks(source_id,source_set_id,artist_id,track_position,title,slug,mp3_url,uuid,updated_at)
                VALUES(@id,@setId,@artistId,1,'Song','song',@url,@uuid,now())
                """,
                new
                {
                    id,
                    setId,
                    artistId = artist.id,
                    url = "https://example.test/" + uuid + ".mp3",
                    uuid = Guid.NewGuid(),
                }
            );
            return id;
        });
    }

    private Task<Show> ShowForSource(int sourceId) =>
        db.WithWriteConnection(c =>
            c.QuerySingleAsync<Show>(
                "SELECT sh.* FROM sources src JOIN shows sh ON sh.id=src.show_id WHERE src.id=@sourceId",
                new { sourceId }
            )
        );

    private Task<PlaybackIdentity> Playback(int sourceId) =>
        db.WithWriteConnection(c =>
            c.QuerySingleAsync<PlaybackIdentity>(
                """
                SELECT src.uuid AS Source,ss.uuid AS Set,st.uuid AS Track,st.mp3_url AS Url
                FROM sources src JOIN source_sets ss ON ss.source_id=src.id JOIN source_tracks st ON st.source_set_id=ss.id WHERE src.id=@sourceId
                """,
                new { sourceId }
            )
        );

    private sealed record PlaybackIdentity(Guid Source, Guid Set, Guid Track, string Url);
}
