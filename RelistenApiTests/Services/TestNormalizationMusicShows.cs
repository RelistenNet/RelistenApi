using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Relisten;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

/// <summary>Search follows accepted musical claims and current playback relationships, not title equality.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationMusicShows
{
    private DbService db = null!;
    private NormalizationReadService reads = null!;
    private string? previousWrite;
    private string? previousRead;

    [OneTimeSetUp]
    public void Start()
    {
        if (Environment.GetEnvironmentVariable("RELISTEN_LOCAL_INGESTION_TESTS") != "1")
        {
            Assert.Ignore("Run ./test-local.sh to include local integration tests.");
        }
        previousWrite = DbService.ConnStr;
        previousRead = DbService.ReadOnlyConnStr;
        db = new DbService(
            LocalTestDatabase.DatabaseUrl,
            null,
            null,
            Host.CreateApplicationBuilder().Environment,
            NullLogger<DbService>.Instance
        );
        reads = new NormalizationReadService(
            db,
            Options.Create(new NormalizationReadOptions { Enabled = true })
        );
    }

    [OneTimeTearDown]
    public void Stop()
    {
        if (previousWrite != null)
        {
            DbService.ConnStr = previousWrite;
            DbService.ReadOnlyConnStr = previousRead!;
        }
    }

    [Test]
    public async Task RepeatedPassagesAndConflictingAccountsDeduplicateSupportedEvidence()
    {
        var recording = await Recording("1977-xx-xx");
        var song = await Song(recording.Artist);
        var selected = await Edition(recording, true, true);
        await Occurrence(selected, song, 1);
        await Occurrence(selected, song, 2);
        var otherAccount = await Edition(recording, false);
        await Occurrence(otherAccount, song);
        await Content(recording, song);

        var result = (await reads.ListSongShowsAsync(song))!.items.Should().ContainSingle().Subject;
        result.show_uuid.Should().Be(recording.Show);
        result.display_date.Should().Be("1977-xx-xx");
        result
            .match_evidence.Should()
            .BeEquivalentTo(["ordered_setlist", "unordered_membership", "file_title"]);
        result.association_kind.Should().Be("performance_occurrence");
        result.decision_basis.Should().BeEquivalentTo(["provider_assertion", "title_derived"]);
    }

    [Test]
    public async Task UnselectedAccountAndTitleOnlyFilesRemainSearchableWithoutInventingCoverage()
    {
        var recording = await Recording("1980-02-xx");
        var song = await Song(recording.Artist);
        var other = await Song(recording.Artist);
        var edition = await Edition(recording, true, true);
        await Occurrence(edition, other, 1);
        var unselected = await Edition(recording, true);
        var occurrence = await Occurrence(unselected, song, 1);
        (await reads.ListSongShowsAsync(song))!
            .items.Should()
            .ContainSingle()
            .Which.association_kind.Should()
            .Be("performance_occurrence");
        await Sql(
            "UPDATE normalization.song_occurrences SET retracted=true WHERE uuid=@occurrence",
            new { occurrence }
        );
        await Content(recording, song);
        var titleOnly = (await reads.ListSongShowsAsync(song))!.items.Single();
        titleOnly.match_evidence.Should().Equal("file_title");
        titleOnly.association_kind.Should().Be("recording_primary_show");
    }

    [Test]
    public async Task UnassignedEditionCanSupportSongIdentityWithoutClaimingEngagementAlignment()
    {
        var recording = await Recording("1980-xx-xx");
        var song = await Song(recording.Artist);
        var edition = await Edition(recording, true);
        await Sql(
            "UPDATE normalization.setlist_editions SET show_uuid=null WHERE uuid=@edition",
            new { edition }
        );
        var occurrence = await Occurrence(edition, song, 1);
        await Sql(
            "UPDATE normalization.song_occurrences SET revision_uuid=@revision WHERE uuid=@occurrence",
            new { occurrence, revision = await Revision("reviewed") }
        );
        await Content(recording, song, occurrence);
        await Sql(
            "UPDATE normalization.track_contents SET repertoire_song_uuid=null WHERE source_uuid=@Source",
            recording
        );

        var result = (await reads.ListSongShowsAsync(song))!.items.Single();
        result.show_uuid.Should().Be(recording.Show);
        result.association_kind.Should().Be("recording_primary_show");
        result.match_evidence.Should().Equal("file_title");
        result
            .decision_basis.Should()
            .BeEquivalentTo(["title_derived", "provider_assertion", "reviewed"]);
    }

    [Test]
    public async Task GlobalCompositionUsesAcceptedCrossArtistLinksAndBoundArtistPaging()
    {
        var first = await Recording("1997-xx-xx");
        var second = await Recording("2001-01-01");
        var third = await Recording("2002-01-01");
        var firstSong = await Song(first.Artist);
        var secondSong = await Song(second.Artist, "A different cover title");
        var unlinkedSameTitle = await Song(third.Artist);
        var composition = await Composition();
        await Link(firstSong, composition);
        await Link(secondSong, composition);
        await Content(first, firstSong);
        await Content(second, secondSong);
        await Content(third, unlinkedSameTitle);

        var page = (await reads.ListCompositionShowsAsync(composition, limit: 1))!;
        page.items.Single().show_uuid.Should().Be(second.Show);
        var next = (
            await reads.ListCompositionShowsAsync(composition, limit: 1, cursor: page.next_cursor)
        )!;
        next.items.Single().show_uuid.Should().Be(first.Show);
        next.next_cursor.Should().BeNull();
        (await reads.ListCompositionShowsAsync(composition, first.Artist))!
            .items.Single()
            .show_uuid.Should()
            .Be(first.Show);
        var changedFilter = () =>
            reads.ListCompositionShowsAsync(composition, first.Artist, 1, page.next_cursor);
        await changedFilter.Should().ThrowAsync<ArgumentException>();
        var wrongRoute = () => reads.ListSongShowsAsync(firstSong, 1, page.next_cursor);
        await wrongRoute.Should().ThrowAsync<ArgumentException>();
        var absentArtist = () => reads.ListCompositionShowsAsync(composition, Guid.NewGuid());
        await absentArtist.Should().ThrowAsync<NormalizationEntityNotFoundException>();
        (await reads.ListCompositionShowsAsync(Guid.NewGuid())).Should().BeNull();
        (await reads.ListSongShowsAsync(Guid.NewGuid())).Should().BeNull();
    }

    [Test]
    public async Task OneFileCanDescribeSeveralSetsWithinItsPerformance()
    {
        var recording = await Recording("1975-04-11");
        var song = await Song(recording.Artist);
        var edition = await Edition(recording, true);
        var first = await Occurrence(edition, song, 1);
        var reprise = await Occurrence(edition, song, 2);
        await Sql(
            "UPDATE normalization.song_occurrences SET set_label='Set 1' WHERE uuid=@first; UPDATE normalization.song_occurrences SET set_label='Set 2' WHERE uuid=@reprise",
            new { first, reprise }
        );
        await Content(recording, song, first);
        await Content(recording, song, reprise, 2);
        await Sql(
            "UPDATE normalization.track_contents SET repertoire_song_uuid=null WHERE source_uuid=@Source",
            recording
        );

        var result = (await reads.ListSongShowsAsync(song))!.items.Single();
        result.show_uuid.Should().Be(recording.Show);
        result.association_kind.Should().Be("performance_occurrence");
        result.match_evidence.Should().BeEquivalentTo(["ordered_setlist", "file_title"]);
        await Sql("UPDATE source_tracks SET is_orphaned=true WHERE uuid=@Track", recording);
        (await reads.ListSongShowsAsync(song))!.items.Should().BeEmpty();
    }

    [Test]
    public async Task InconsistentFileAlignmentCannotMakeAnotherPerformancePlayable()
    {
        var early = await Recording("1975-04-11 Early");
        var late = await Recording("1975-04-11 Late", early.Artist);
        await Sql("UPDATE source_tracks SET is_orphaned=true WHERE uuid=@Track", late);
        var song = await Song(early.Artist);
        var lateOccurrence = await Occurrence(await Edition(late, true), song, 1);
        await Content(early, song, lateOccurrence);

        var titleMatch = (await reads.ListSongShowsAsync(song))!.items.Single();
        titleMatch.show_uuid.Should().Be(early.Show);
        titleMatch.association_kind.Should().Be("recording_primary_show");
        titleMatch.match_evidence.Should().Equal("file_title");
        await Sql(
            "UPDATE normalization.track_contents SET repertoire_song_uuid=null WHERE source_uuid=@Source",
            early
        );
        (await reads.ListSongShowsAsync(song))!.items.Should().BeEmpty();
        await Sql("UPDATE source_tracks SET is_orphaned=false WHERE uuid=@Track", late);
        var independentlyPlayable = (await reads.ListSongShowsAsync(song))!.items.Single();
        independentlyPlayable.show_uuid.Should().Be(late.Show);
        independentlyPlayable.association_kind.Should().Be("performance_occurrence");
        independentlyPlayable.match_evidence.Should().Equal("ordered_setlist");
    }

    [Test]
    public async Task OccurrenceSongCorrectionDoesNotReuseOldFileAlignment()
    {
        var recording = await Recording("1995-xx-xx");
        var originalSong = await Song(recording.Artist, "Original title");
        var correctedSong = await Song(recording.Artist, "Corrected title");
        var occurrence = await Occurrence(await Edition(recording, true), originalSong, 1);
        await Content(recording, originalSong, occurrence);
        (await reads.ListSongShowsAsync(originalSong))!
            .items.Single()
            .association_kind.Should()
            .Be("performance_occurrence");
        await Sql(
            "UPDATE normalization.song_occurrences SET repertoire_song_uuid=@correctedSong,revision_uuid=@revision WHERE uuid=@occurrence",
            new
            {
                occurrence,
                correctedSong,
                revision = await Revision("reviewed"),
            }
        );

        var oldTitle = (await reads.ListSongShowsAsync(originalSong))!.items.Single();
        oldTitle.show_uuid.Should().Be(recording.Show);
        oldTitle.association_kind.Should().Be("recording_primary_show");
        oldTitle.match_evidence.Should().Equal("file_title");
        oldTitle.decision_basis.Should().Equal("title_derived");
        var corrected = (await reads.ListSongShowsAsync(correctedSong))!.items.Single();
        corrected.show_uuid.Should().Be(recording.Show);
        corrected.association_kind.Should().Be("performance_occurrence");
        corrected.match_evidence.Should().Equal("ordered_setlist");
        corrected.decision_basis.Should().Contain("reviewed");
    }

    [Test]
    public async Task CurrentClaimedFileMustBePlayableEvenWhenAnotherFileInItsSourceIsPlayable()
    {
        var recording = await Recording("2000-xx-xx");
        var song = await Song(recording.Artist);
        await Content(recording, song);
        await Sql(
            """
            INSERT INTO source_tracks(uuid,source_id,source_set_id,artist_id,track_position,title,slug,mp3_url,duration,updated_at)
            SELECT @extra,t.source_id,t.source_set_id,t.artist_id,2,'Other','other','https://example.test/other.mp3',120,now()
            FROM source_tracks t WHERE t.uuid=@Track;
            UPDATE source_tracks SET is_orphaned=true WHERE uuid=@Track;
            """,
            new { recording.Track, extra = Guid.NewGuid() }
        );
        (await reads.ListSongShowsAsync(song))!.items.Should().BeEmpty();
        await Sql(
            "UPDATE source_tracks SET is_orphaned=false,mp3_url=' ' WHERE uuid=@Track",
            recording
        );
        (await reads.ListSongShowsAsync(song))!.items.Should().BeEmpty();
        await Sql(
            "UPDATE source_tracks SET mp3_url='https://example.test/restored.mp3' WHERE uuid=@Track",
            recording
        );
        (await reads.ListSongShowsAsync(song))!.items.Should().ContainSingle();
        var other = await Recording("2000-01-01");
        await Sql(
            "UPDATE source_tracks SET source_set_id=(SELECT source_set_id FROM source_tracks WHERE uuid=@other) WHERE uuid=@Track",
            new { recording.Track, other = other.Track }
        );
        (await reads.ListSongShowsAsync(song))!.items.Should().BeEmpty();
    }

    [Test]
    public async Task FileSearchFollowsRecordingMovesAndRejectsArtistMismatch()
    {
        var original = await Recording("2000-01-01");
        var reassigned = await Recording("2000-01-01 Late", original.Artist);
        var song = await Song(original.Artist);
        var occurrence = await Occurrence(await Edition(original, true), song, 1);
        await Content(original, song, occurrence);
        await Sql(
            "UPDATE sources SET show_id=(SELECT id FROM shows WHERE uuid=@show) WHERE uuid=@Source",
            new { original.Source, show = reassigned.Show }
        );
        var result = (await reads.ListSongShowsAsync(song))!.items.Single();
        result.show_uuid.Should().Be(reassigned.Show);
        result.association_kind.Should().Be("recording_primary_show");
        var foreign = await Recording("2000-01-02");
        await Sql(
            "UPDATE source_tracks SET artist_id=(SELECT id FROM artists WHERE uuid=@artist) WHERE uuid=@Track",
            new { original.Track, artist = foreign.Artist }
        );
        (await reads.ListSongShowsAsync(song))!.items.Should().BeEmpty();
    }

    [Test]
    public async Task RedirectsAndRelationshipRetractionsChangeSearchWithoutChangingPlaybackIds()
    {
        var recording = await Recording("1990-xx-xx");
        var oldSong = await Song(recording.Artist);
        var survivor = await Song(recording.Artist, "Survivor");
        var oldWork = await Composition();
        var currentWork = await Composition();
        var link = await Link(oldSong, oldWork);
        await Content(recording, oldSong);
        await Sql(
            """
            UPDATE normalization.repertoire_songs SET redirect_uuid=@survivor WHERE uuid=@oldSong;
            UPDATE normalization.compositions SET redirect_uuid=@currentWork WHERE uuid=@oldWork;
            """,
            new
            {
                survivor,
                oldSong,
                currentWork,
                oldWork,
            }
        );
        (await reads.ListSongShowsAsync(survivor))!
            .items.Single()
            .show_uuid.Should()
            .Be(recording.Show);
        (await reads.ListSongShowsAsync(oldSong))!
            .items.Single()
            .show_uuid.Should()
            .Be(recording.Show);
        (await reads.ListCompositionShowsAsync(currentWork))!
            .items.Single()
            .show_uuid.Should()
            .Be(recording.Show);
        (await reads.ListCompositionShowsAsync(oldWork))!
            .items.Single()
            .show_uuid.Should()
            .Be(recording.Show);
        await Sql(
            "UPDATE normalization.repertoire_compositions SET retracted=true WHERE uuid=@link",
            new { link }
        );
        (await reads.ListCompositionShowsAsync(currentWork))!.items.Should().BeEmpty();
        (await reads.ListSongShowsAsync(survivor))!.items.Should().ContainSingle();
    }

    [Test]
    public async Task SeveralRecordingsKeepOnePerformanceAvailableUntilLastAudioDisappears()
    {
        var first = await Recording("1977-05-08");
        var second = await Recording("1977-05-08 Other", first.Artist);
        await Sql(
            "UPDATE sources SET show_id=(SELECT id FROM shows WHERE uuid=@show) WHERE uuid=@Source",
            new { second.Source, show = first.Show }
        );
        var song = await Song(first.Artist);
        await Occurrence(await Edition(first, true), song, 1);

        (await reads.ListSongShowsAsync(song))!
            .items.Should()
            .ContainSingle()
            .Which.show_uuid.Should()
            .Be(first.Show);
        await Sql("UPDATE source_tracks SET is_orphaned=true WHERE uuid=@Track", second);
        (await reads.ListSongShowsAsync(song))!
            .items.Should()
            .ContainSingle()
            .Which.show_uuid.Should()
            .Be(first.Show);
        await Sql("UPDATE source_tracks SET is_orphaned=true WHERE uuid=@Track", first);
        (await reads.ListSongShowsAsync(song))!.items.Should().BeEmpty();
    }

    [Test]
    public async Task SetlistOnlyAccountIsHistoricalUntilCatalogAudioBecomesAvailable()
    {
        var recording = await Recording("1970-xx-xx");
        var song = await Song(recording.Artist);
        await Occurrence(await Edition(recording, false), song);
        await Sql("UPDATE source_tracks SET is_orphaned=true WHERE uuid=@Track", recording);
        (await reads.ListSongShowsAsync(song))!.items.Should().BeEmpty();
        await Sql("UPDATE source_tracks SET is_orphaned=false WHERE uuid=@Track", recording);
        (await reads.ListSongShowsAsync(song))!
            .items.Single()
            .match_evidence.Should()
            .Equal("unordered_membership");
    }

    private Task<int> Sql(string sql, object? args = null) =>
        db.WithWriteConnection(c => c.ExecuteAsync(sql, args));

    // These fixtures seed accepted projections directly. Domain mutation and evidence admission
    // have separate service tests; this suite exercises the public search contract over that state.
    private async Task<Guid> Revision(string basis = "provider_assertion")
    {
        var uuid = Guid.NewGuid();
        await Sql(
            """
            INSERT INTO normalization.music_revisions(uuid,entity_uuid,entity_kind,revision,decision_uuid,basis,payload,evidence)
            VALUES(@uuid,@entity,'fixture',1,@decision,@basis,'{}','[]')
            """,
            new
            {
                uuid,
                entity = Guid.NewGuid(),
                decision = Guid.NewGuid(),
                basis,
            }
        );
        return uuid;
    }

    private async Task<Guid> Song(Guid artist, string title = "Same title")
    {
        var uuid = Guid.NewGuid();
        await Sql(
            "INSERT INTO normalization.repertoire_songs(uuid,artist_uuid,display_title,revision_uuid) VALUES(@uuid,@artist,@title,@revision)",
            new
            {
                uuid,
                artist,
                title,
                revision = await Revision("title_derived"),
            }
        );
        return uuid;
    }

    private async Task<Guid> Composition()
    {
        var uuid = Guid.NewGuid();
        await Sql(
            "INSERT INTO normalization.compositions(uuid,display_title,revision_uuid) VALUES(@uuid,'Work',@revision)",
            new { uuid, revision = await Revision() }
        );
        return uuid;
    }

    private async Task<Guid> Link(Guid song, Guid work)
    {
        var uuid = Guid.NewGuid();
        await Sql(
            "INSERT INTO normalization.repertoire_compositions(uuid,repertoire_song_uuid,composition_uuid,revision_uuid) VALUES(@uuid,@song,@work,@revision)",
            new
            {
                uuid,
                song,
                work,
                revision = await Revision("reviewed"),
            }
        );
        return uuid;
    }

    private async Task<Guid> Edition(
        RecordingFixture recording,
        bool ordered,
        bool selected = false
    )
    {
        var uuid = Guid.NewGuid();
        await Sql(
            """
            INSERT INTO normalization.setlist_editions(uuid,artist_uuid,show_uuid,literal_date,ordered,provider,resource_type,provider_key,selected,revision_uuid)
            VALUES(@uuid,@Artist,@Show,'fixture',@ordered,'test','account',@key,@selected,@revision)
            """,
            new
            {
                uuid,
                recording.Artist,
                recording.Show,
                ordered,
                selected,
                key = uuid.ToString(),
                revision = await Revision(),
            }
        );
        return uuid;
    }

    private async Task<Guid> Occurrence(Guid edition, Guid song, int? position = null)
    {
        var uuid = Guid.NewGuid();
        await Sql(
            """
            INSERT INTO normalization.song_occurrences(uuid,edition_uuid,position,kind,repertoire_song_uuid,original_text,transition_after,revision_uuid)
            VALUES(@uuid,@edition,@position,'song',@song,'Song','unknown',@revision)
            """,
            new
            {
                uuid,
                edition,
                position,
                song,
                revision = await Revision(),
            }
        );
        return uuid;
    }

    private async Task Content(
        RecordingFixture recording,
        Guid song,
        Guid? occurrence = null,
        int position = 1
    )
    {
        await Sql(
            """
            INSERT INTO normalization.track_contents(uuid,source_track_uuid,artist_uuid,source_uuid,position,kind,original_text,repertoire_song_uuid,occurrence_uuid,match_evidence,revision_uuid)
            VALUES(@uuid,@Track,@Artist,@Source,@position,'song','Song',@song,@occurrence,'file_title',@revision)
            """,
            new
            {
                uuid = Guid.NewGuid(),
                recording.Track,
                recording.Artist,
                recording.Source,
                position,
                song,
                occurrence,
                revision = await Revision("title_derived"),
            }
        );
    }

    private sealed record RecordingFixture(Guid Artist, Guid Source, Guid Show, Guid Track);

    private async Task<RecordingFixture> Recording(string date, Guid? existingArtist = null)
    {
        var fixture = new RecordingFixture(
            existingArtist ?? Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid()
        );
        await Sql(
            """
            INSERT INTO artists(uuid,name,slug,updated_at) VALUES(@Artist,'Search fixture',@artistSlug,now()) ON CONFLICT(uuid) DO NOTHING;
            INSERT INTO shows(uuid,artist_id,date,display_date,updated_at) SELECT @Show,id,'2000-01-01',@date,now() FROM artists WHERE uuid=@Artist;
            INSERT INTO sources(uuid,show_id,artist_id,is_soundboard,is_remaster,has_jamcharts,upstream_identifier,display_date,updated_at)
                SELECT @Source,id,artist_id,false,false,false,@sourceKey,@date,now() FROM shows WHERE uuid=@Show;
            INSERT INTO source_sets(uuid,source_id,name,updated_at) SELECT @set,id,'Set 1',now() FROM sources WHERE uuid=@Source;
            INSERT INTO source_tracks(uuid,source_id,source_set_id,artist_id,track_position,title,slug,mp3_url,duration,updated_at)
                SELECT @Track,s.id,sets.id,s.artist_id,1,'Song','song',@url,120,now()
                FROM sources s JOIN source_sets sets ON sets.source_id=s.id WHERE s.uuid=@Source;
            """,
            new
            {
                fixture.Artist,
                fixture.Show,
                fixture.Source,
                fixture.Track,
                date,
                artistSlug = fixture.Artist.ToString(),
                sourceKey = fixture.Source.ToString(),
                url = "https://example.test/" + fixture.Track + ".mp3",
                set = Guid.NewGuid(),
            }
        );
        return fixture;
    }
}
