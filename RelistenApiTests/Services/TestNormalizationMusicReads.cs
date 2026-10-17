using System.Net;
using System.Text;
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

namespace RelistenApiTests.Services;

/// <summary>Exercises the public read contract against retained accepted rows and real catalog relationships.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationMusicReads
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizationReadService reads = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new NormalizationDomainFixture();
        reads = new NormalizationReadService(
            fixture.Db,
            Options.Create(new NormalizationReadOptions { Enabled = true })
        );
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task RepertoireNamesakesRemainDistinctAndArtistSlugAndBasisConstrainPages()
    {
        var artist = await Artist();
        var title = "Search " + Guid.NewGuid().ToString("N");
        var a = await Song(artist.Uuid, title, "title_derived");
        var b = await Song(artist.Uuid, title, "provider_assertion");
        var other = await Artist();
        await Song(other.Uuid, title, "reviewed");
        var first = await reads.ListRepertoireAsync(artist.Slug, title, limit: 1);
        first.items.Should().ContainSingle();
        var second = await reads.ListRepertoireAsync(
            artist.Slug,
            title,
            limit: 1,
            cursor: first.next_cursor
        );
        first.items.Concat(second.items).Select(x => x.uuid).Should().BeEquivalentTo([a, b]);
        second.next_cursor.Should().BeNull();
        (await reads.ListRepertoireAsync(artist.Uuid.ToString(), title, "title_derived"))
            .items.Should()
            .ContainSingle()
            .Which.uuid.Should()
            .Be(a);
        await FluentActions
            .Awaiting(() =>
                reads.ListRepertoireAsync(other.Slug, title, limit: 1, cursor: first.next_cursor)
            )
            .Should()
            .ThrowAsync<ArgumentException>();
        await FluentActions
            .Awaiting(() =>
                reads.ListRepertoireAsync(artist.Slug, title, "reviewed", 1, first.next_cursor)
            )
            .Should()
            .ThrowAsync<ArgumentException>();
        await FluentActions
            .Awaiting(() => reads.ListRepertoireAsync("missing-artist"))
            .Should()
            .ThrowAsync<NormalizationEntityNotFoundException>();
    }

    [TestCase("Ｆｕｌｌｗｉｄｔｈ　Ｔｉｔｌｅ", "Fullwidth Title")]
    [TestCase("Cafe\u0301 Night", "Café Night")]
    [TestCase("Many \t  Spaces", "Many Spaces")]
    public async Task MusicSearchNormalizesTitlesAliasesAndCopiedQueries(
        string original,
        string normalized
    )
    {
        var artist = await Artist();
        var suffix = " " + Guid.NewGuid().ToString("N");
        var song = await Song(artist.Uuid, original + suffix);
        var aliasSong = await Song(artist.Uuid, "Alias song" + suffix);
        var work = await Composition(original + suffix);
        var aliasWork = await Composition("Alias work" + suffix);
        await Sql(
            "UPDATE normalization.repertoire_songs SET aliases=ARRAY[@alias] WHERE uuid=@aliasSong; UPDATE normalization.compositions SET aliases=ARRAY[@alias] WHERE uuid=@aliasWork",
            new
            {
                alias = original + suffix,
                aliasSong,
                aliasWork,
            }
        );
        foreach (var query in new[] { normalized + suffix, original + suffix })
        {
            (await reads.ListRepertoireAsync(artist.Slug, query))
                .items.Select(x => x.uuid)
                .Should()
                .BeEquivalentTo([song, aliasSong]);
            (await reads.ListCompositionsAsync(query))
                .items.Select(x => x.uuid)
                .Should()
                .BeEquivalentTo([work, aliasWork]);
        }
    }

    [Test]
    public async Task WithdrawnMusicEndpointsDisappearFromPublicLinksButRetainTheirHistory()
    {
        var artist = await Artist();
        var song = await Song(artist.Uuid, "Song");
        var work = await Composition("Work");
        var link = Guid.NewGuid();
        var linkRevision = await Revision(link, "repertoire_compositions", "provider_assertion");
        await Sql(
            "INSERT INTO normalization.repertoire_compositions(uuid,repertoire_song_uuid,composition_uuid,revision_uuid) VALUES(@link,@song,@work,@linkRevision)",
            new
            {
                link,
                song,
                work,
                linkRevision,
            }
        );
        (await reads.GetSongAsync(song))!.composition_uuid.Should().Be(work);
        var music = new NormalizedMusicService(fixture.Db, fixture.Work);
        var workInput = await fixture.Capture("Work", "compositions");
        var workOwner = await fixture.Own(workInput, "composition", work);
        await music.PublishAsync(
            workOwner.Ticket.WorkUuid,
            new MusicPublication
            {
                Compositions =
                [
                    new CompositionChange
                    {
                        Uuid = work,
                        ExpectedRevision = 1,
                        DisplayTitle = "Work",
                        Retracted = true,
                        Evidence = [workInput.Reference],
                    },
                ],
            }
        );
        (await reads.GetSongAsync(song))!.composition_uuid.Should().BeNull();
        (await reads.GetSongAsync(song))!.composition.Should().BeNull();
        (await reads.GetCompositionAsync(work)).Should().BeNull();
        // Restore the work through the same typed owner, then withdraw only the repertoire endpoint.
        workOwner = await fixture.Own(workInput, "composition", work);
        await music.PublishAsync(
            workOwner.Ticket.WorkUuid,
            new MusicPublication
            {
                Compositions =
                [
                    new CompositionChange
                    {
                        Uuid = work,
                        ExpectedRevision = 2,
                        DisplayTitle = "Work",
                        Evidence = [workInput.Reference],
                    },
                ],
            }
        );
        var songInput = await fixture.Capture("Song", "songs");
        var songOwner = await fixture.Own(songInput, "artist_repertoire", artist.Uuid);
        await music.PublishAsync(
            songOwner.Ticket.WorkUuid,
            new MusicPublication
            {
                RepertoireSongs =
                [
                    new RepertoireSongChange
                    {
                        Uuid = song,
                        ArtistUuid = artist.Uuid,
                        ExpectedRevision = 1,
                        DisplayTitle = "Song",
                        Retracted = true,
                        Evidence = [songInput.Reference],
                    },
                ],
            }
        );
        (await reads.GetCompositionAsync(work))!.repertoire_links.Should().BeEmpty();
        (
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteScalarAsync<int>(
                    "SELECT count(*) FROM normalization.repertoire_compositions WHERE uuid=@link AND NOT retracted",
                    new { link }
                )
            )
        )
            .Should()
            .Be(1);
    }

    [Test]
    public async Task CompositionDetailKeepsRoleCreditsSeparateAndRetiredTitlesFindSurvivor()
    {
        var artist = await Artist();
        var song = await Song(artist.Uuid, "Interpretation");
        var oldTitle = "Old work " + Guid.NewGuid().ToString("N");
        var old = await Composition(oldTitle);
        var work = await Composition("Current work " + Guid.NewGuid().ToString("N"));
        await Sql(
            "UPDATE normalization.compositions SET redirect_uuid=@work WHERE uuid=@old",
            new { work, old }
        );
        var link = Guid.NewGuid();
        var linkRevision = await Revision(
            link,
            "repertoire_compositions",
            "reviewed",
            "{\"MatchReason\":\"reviewed\"}"
        );
        await Sql(
            "INSERT INTO normalization.repertoire_compositions(uuid,repertoire_song_uuid,composition_uuid,revision_uuid) VALUES(@link,@song,@work,@linkRevision)",
            new
            {
                link,
                song,
                work,
                linkRevision,
            }
        );
        foreach (var role in new[] { "writer", "cover_artist" })
        {
            var id = Guid.NewGuid();
            var rev = await Revision(id, "work_credits", "provider_assertion");
            await Sql(
                "INSERT INTO normalization.work_credits(uuid,composition_uuid,role,original_text,revision_uuid) VALUES(@id,@work,@role,'Credited name',@rev)",
                new
                {
                    id,
                    work,
                    role,
                    rev,
                }
            );
        }
        var result = (await reads.GetCompositionAsync(old))!;
        result.uuid.Should().Be(work);
        result.requested_uuid.Should().Be(old);
        result.redirect_uuid.Should().Be(work);
        result.work_credits.Select(x => x.role).Should().BeEquivalentTo(["writer", "cover_artist"]);
        result
            .repertoire_links.Should()
            .ContainSingle()
            .Which.repertoire_song_uuid.Should()
            .Be(song);
        (await reads.GetSongAsync(song))!.composition!.composition_uuid.Should().Be(work);
        (await reads.ListCompositionsAsync(oldTitle))
            .items.Should()
            .ContainSingle()
            .Which.uuid.Should()
            .Be(work);
        (await reads.GetCompositionAsync(Guid.NewGuid())).Should().BeNull();
    }

    [Test]
    public async Task OrderedPassagesTraverseComponentsAcrossPagesAndUnorderedAccountsKeepUnknownPositions()
    {
        var artist = await Artist();
        var edition = await Edition(artist.Uuid, true);
        var root = await Occurrence(edition, 1);
        var child = await Occurrence(edition, null, root, 1);
        var next = await Occurrence(edition, 2);
        var first = (await reads.ListOccurrencesAsync(edition, 1))!;
        first.items.Single().uuid.Should().Be(root);
        var second = (await reads.ListOccurrencesAsync(edition, 1, first.next_cursor))!;
        second.items.Single().uuid.Should().Be(child);
        second.items.Single().edition.ordered.Should().BeTrue();
        var third = (await reads.ListOccurrencesAsync(edition, 1, second.next_cursor))!;
        third.items.Single().uuid.Should().Be(next);
        third.next_cursor.Should().BeNull();
        var unordered = await Edition(artist.Uuid, false);
        var a = await Occurrence(unordered, null);
        var b = await Occurrence(unordered, null);
        var list = (await reads.ListOccurrencesAsync(unordered))!.items;
        list.Select(x => x.uuid).Should().Equal(new[] { a, b }.Order());
        list.Should().OnlyContain(x => x.position == null && !x.edition.ordered);
        var accounts = await reads.ListArtistSetlistsAsync(artist.Slug);
        accounts.items.Select(x => x.uuid).Should().Contain([edition, unordered]);
        accounts.items.Should().OnlyContain(x => x.show_uuid == null);
        await FluentActions
            .Awaiting(() => reads.ListOccurrencesAsync(unordered, 1, first.next_cursor))
            .Should()
            .ThrowAsync<ArgumentException>();
        (await reads.ListOccurrencesAsync(Guid.NewGuid())).Should().BeNull();
    }

    [Test]
    public async Task FileContentsRetainUnknownTimesAndHistoricalEvidenceAfterFileDeletion()
    {
        var source = await fixture.Source();
        var artist = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<Guid>(
                "SELECT uuid FROM artists WHERE id=@id",
                new { id = source.ArtistId }
            )
        );
        var song = await Song(artist, "Song");
        var id = Guid.NewGuid();
        var revision = await Revision(id, "track_contents", "title_derived");
        var evidence = await fixture.Capture("Song", "songs");
        await Sql(
            """
            INSERT INTO normalization.track_contents(uuid,source_track_uuid,artist_uuid,source_uuid,position,kind,original_text,repertoire_song_uuid,match_evidence,revision_uuid)
            VALUES(@id,@track,@artist,@source,1,'song','Song',@song,'file_title',@revision);
            INSERT INTO normalization.music_evidence_references(revision_uuid,ordinal,input_revision_uuid,json_pointer,start_code_point,end_code_point,quote)
            VALUES(@revision,0,@input,'/text',0,4,'Song')
            """,
            new
            {
                id,
                track = source.TrackUuid,
                artist,
                source = source.SourceUuid,
                song,
                revision,
                input = evidence.Reference.InputRevisionUuid,
            }
        );
        var content = (await reads.ListTrackContentsAsync(source.TrackUuid))!.items.Single();
        content.start_ms.Should().BeNull();
        content.end_ms.Should().BeNull();
        content.evidence_status.Should().Be("current");
        content.evidence.Single().quote.Should().Be("Song");
        await new NormalizationEvidenceService(fixture.Db).CaptureInputAsync(
            new(
                Guid.NewGuid(),
                evidence.Resource,
                Encoding.UTF8.GetBytes("{\"text\":\"Changed\"}"),
                "application/json",
                "test",
                1,
                DateTimeOffset.UtcNow
            )
        );
        await Sql("DELETE FROM source_tracks WHERE uuid=@track", new { track = source.TrackUuid });
        content = (await reads.ListTrackContentsAsync(source.TrackUuid))!.items.Single();
        content.reference_available.Should().BeFalse();
        content.evidence_status.Should().Be("changed");
        content.repertoire_song_uuid.Should().Be(song);
        (await reads.ListTrackContentsAsync(Guid.NewGuid())).Should().BeNull();
    }

    [Test]
    public async Task RecordingMoveHidesStaleOccurrenceAlignmentWithoutChangingContentsOrAudio()
    {
        var source = await fixture.Source();
        var artist = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<Guid>(
                "SELECT uuid FROM artists WHERE id=@id",
                new { id = source.ArtistId }
            )
        );
        var early = Guid.NewGuid();
        var late = Guid.NewGuid();
        await Sql(
            """
            INSERT INTO shows(artist_id,date,display_date,uuid,updated_at,performance_key,is_legacy_survivor)
            VALUES(@artist,'1970-01-01','1970-01-01 (early)',@early,now(),'default',true),
                (@artist,'1970-01-01','1970-01-01 (late)',@late,now(),'late',false);
            UPDATE sources SET show_id=(SELECT id FROM shows WHERE uuid=@early) WHERE uuid=@source
            """,
            new
            {
                artist = source.ArtistId,
                early,
                late,
                source = source.SourceUuid,
            }
        );
        var song = await Song(artist, "Song");
        var edition = await Edition(artist, true);
        await Sql(
            "UPDATE normalization.setlist_editions SET show_uuid=@early WHERE uuid=@edition",
            new { early, edition }
        );
        var occurrence = await Occurrence(edition, 1);
        var content = Guid.NewGuid();
        var revision = await Revision(content, "track_contents", "provider_assertion");
        var evidence = await fixture.Capture("Song", "songs");
        await Sql(
            """
            INSERT INTO normalization.track_contents(uuid,source_track_uuid,artist_uuid,source_uuid,position,kind,original_text,repertoire_song_uuid,occurrence_uuid,match_evidence,revision_uuid)
            VALUES(@content,@track,@artist,@source,1,'song','Song',@song,@occurrence,'recording_metadata',@revision);
            INSERT INTO normalization.music_evidence_references(revision_uuid,ordinal,input_revision_uuid,json_pointer,start_code_point,end_code_point,quote)
            VALUES(@revision,0,@input,'/text',0,4,'Song')
            """,
            new
            {
                content,
                track = source.TrackUuid,
                artist,
                source = source.SourceUuid,
                song,
                occurrence,
                revision,
                input = evidence.Reference.InputRevisionUuid,
            }
        );
        var before = (await reads.ListTrackContentsAsync(source.TrackUuid))!.items.Single();
        before.occurrence_uuid.Should().Be(occurrence);
        before.reference_available.Should().BeTrue();
        await Sql(
            "UPDATE sources SET show_id=(SELECT id FROM shows WHERE uuid=@late) WHERE uuid=@source",
            new { late, source = source.SourceUuid }
        );
        var moved = (await reads.ListTrackContentsAsync(source.TrackUuid))!.items.Single();
        moved.uuid.Should().Be(content);
        moved.repertoire_song_uuid.Should().Be(song);
        moved.occurrence_uuid.Should().BeNull();
        moved.reference_available.Should().BeTrue();
        moved.evidence_status.Should().Be("current");
        moved.evidence.Single().quote.Should().Be("Song");
        (
            await fixture.Db.WithWriteConnection(c =>
                c.QuerySingleAsync<Guid>(
                    "SELECT occurrence_uuid FROM normalization.track_contents WHERE uuid=@content",
                    new { content }
                )
            )
        )
            .Should()
            .Be(occurrence);
        // An account without an assigned performance can still identify its own occurrence.
        await Sql(
            "UPDATE normalization.setlist_editions SET show_uuid=NULL WHERE uuid=@edition",
            new { edition }
        );
        (await reads.ListTrackContentsAsync(source.TrackUuid))!
            .items.Single()
            .occurrence_uuid.Should()
            .Be(occurrence);
        await Sql(
            "UPDATE normalization.song_occurrences SET retracted=true WHERE uuid=@occurrence",
            new { occurrence }
        );
        (await reads.ListTrackContentsAsync(source.TrackUuid))!
            .items.Single()
            .occurrence_uuid.Should()
            .BeNull();
        await Sql(
            "UPDATE normalization.song_occurrences SET retracted=false WHERE uuid=@occurrence; UPDATE normalization.setlist_editions SET retracted=true WHERE uuid=@edition",
            new { occurrence, edition }
        );
        (await reads.ListTrackContentsAsync(source.TrackUuid))!
            .items.Single()
            .occurrence_uuid.Should()
            .BeNull();
        await Sql(
            "UPDATE normalization.setlist_editions SET retracted=false WHERE uuid=@edition; DELETE FROM source_tracks WHERE uuid=@track",
            new { edition, track = source.TrackUuid }
        );
        var historical = (await reads.ListTrackContentsAsync(source.TrackUuid))!.items.Single();
        historical.occurrence_uuid.Should().Be(occurrence);
        historical.reference_available.Should().BeFalse();
    }

    [Test]
    public async Task OccurrenceSongCorrectionRemovesConflictingFileAlignmentButKeepsItsOwnSongAndAudio()
    {
        var source = await fixture.Source();
        var artist = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<Guid>(
                "SELECT uuid FROM artists WHERE id=@id",
                new { id = source.ArtistId }
            )
        );
        var show = Guid.NewGuid();
        await Sql(
            """
            INSERT INTO shows(artist_id,date,display_date,uuid,updated_at) VALUES(@artist,'1970-01-01','1970-01-01',@show,now());
            UPDATE sources SET show_id=(SELECT id FROM shows WHERE uuid=@show) WHERE uuid=@source
            """,
            new
            {
                artist = source.ArtistId,
                show,
                source = source.SourceUuid,
            }
        );
        var input = await fixture.Capture("Song A; Song B", "songs");
        var music = new NormalizedMusicService(fixture.Db, fixture.Work);
        var songA = new RepertoireSongChange
        {
            Uuid = Guid.NewGuid(),
            ArtistUuid = artist,
            DisplayTitle = "Song A",
            Evidence = [input.Reference],
        };
        var songB = songA with { Uuid = Guid.NewGuid(), DisplayTitle = "Song B" };
        var repertoireOwner = await fixture.Own(input, "artist_repertoire", artist);
        await music.PublishAsync(
            repertoireOwner.Ticket.WorkUuid,
            new MusicPublication { RepertoireSongs = [songA, songB] }
        );
        var edition = new SetlistEditionChange
        {
            Uuid = Guid.NewGuid(),
            ArtistUuid = artist,
            ShowUuid = show,
            LiteralDate = "1970-01-01",
            Ordered = true,
            Provider = "fixture",
            ResourceType = "setlist",
            ProviderKey = Guid.NewGuid().ToString(),
            Evidence = [input.Reference],
        };
        var occurrence = new SongOccurrenceChange
        {
            Uuid = Guid.NewGuid(),
            EditionUuid = edition.Uuid,
            Position = 1,
            RepertoireSongUuid = songA.Uuid,
            OriginalText = "Song A",
            Evidence = [input.Reference],
        };
        var editionOwner = await fixture.Own(
            input,
            "setlist",
            edition.Uuid,
            [repertoireOwner.Ticket.SubjectUuid]
        );
        await music.PublishAsync(
            editionOwner.Ticket.WorkUuid,
            new MusicPublication { Editions = [edition], Occurrences = [occurrence] }
        );
        var content = new TrackContentChange
        {
            Uuid = Guid.NewGuid(),
            SourceTrackUuid = source.TrackUuid,
            Position = 1,
            OriginalText = "Song A",
            RepertoireSongUuid = songA.Uuid,
            OccurrenceUuid = occurrence.Uuid,
            MatchEvidence = "recording_metadata",
            Evidence = [input.Reference],
        };
        var fileOwner = await fixture.Own(
            input,
            "recording",
            source.SourceUuid,
            [repertoireOwner.Ticket.SubjectUuid, editionOwner.Ticket.SubjectUuid]
        );
        await music.PublishAsync(
            fileOwner.Ticket.WorkUuid,
            new MusicPublication { TrackContents = [content] }
        );
        (await reads.ListTrackContentsAsync(source.TrackUuid))!
            .items.Single()
            .occurrence_uuid.Should()
            .Be(occurrence.Uuid);
        editionOwner = await fixture.Own(
            input,
            "setlist",
            edition.Uuid,
            [repertoireOwner.Ticket.SubjectUuid]
        );
        await music.PublishAsync(
            editionOwner.Ticket.WorkUuid,
            new MusicPublication
            {
                Occurrences =
                [
                    occurrence with
                    {
                        ExpectedRevision = 1,
                        RepertoireSongUuid = songB.Uuid,
                        OriginalText = "Song B",
                    },
                ],
            }
        );
        var result = (await reads.ListTrackContentsAsync(source.TrackUuid))!.items.Single();
        result.uuid.Should().Be(content.Uuid);
        result.repertoire_song_uuid.Should().Be(songA.Uuid);
        result.original_text.Should().Be("Song A");
        result.occurrence_uuid.Should().BeNull();
        result.reference_available.Should().BeTrue();
        result.evidence_status.Should().Be("current");
        (await reads.ListOccurrencesAsync(edition.Uuid))!
            .items.Single()
            .repertoire_song_uuid.Should()
            .Be(songB.Uuid);
        (
            await fixture.Db.WithWriteConnection(c =>
                c.QuerySingleAsync<Guid>(
                    "SELECT occurrence_uuid FROM normalization.track_contents WHERE uuid=@uuid",
                    new { uuid = content.Uuid }
                )
            )
        )
            .Should()
            .Be(occurrence.Uuid);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task PassageSongReferencesFollowLookupAvailabilityAfterWithdrawal(bool redirected)
    {
        var source = await fixture.Source();
        var artist = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<Guid>(
                "SELECT uuid FROM artists WHERE id=@id",
                new { id = source.ArtistId }
            )
        );
        var survivor = await Song(artist, "Song");
        var original = redirected ? await Song(artist, "Old title") : survivor;
        if (redirected)
        {
            await Sql(
                "UPDATE normalization.repertoire_songs SET redirect_uuid=@survivor WHERE uuid=@original",
                new { survivor, original }
            );
        }
        var edition = await Edition(artist, true);
        var occurrence = await Occurrence(edition, 1);
        await Sql(
            "UPDATE normalization.song_occurrences SET repertoire_song_uuid=@original WHERE uuid=@occurrence",
            new { original, occurrence }
        );
        var content = Guid.NewGuid();
        var revision = await Revision(content, "track_contents", "provider_assertion");
        var input = await fixture.Capture("Song", "songs");
        await Sql(
            """
            INSERT INTO normalization.track_contents(uuid,source_track_uuid,artist_uuid,source_uuid,position,kind,original_text,repertoire_song_uuid,occurrence_uuid,match_evidence,revision_uuid)
            VALUES(@content,@track,@artist,@source,1,'song','Song',@original,@occurrence,'recording_metadata',@revision);
            INSERT INTO normalization.music_evidence_references(revision_uuid,ordinal,input_revision_uuid,json_pointer,start_code_point,end_code_point,quote)
            VALUES(@revision,0,@inputRevision,'/text',0,4,'Song')
            """,
            new
            {
                content,
                track = source.TrackUuid,
                artist,
                source = source.SourceUuid,
                original,
                occurrence,
                revision,
                inputRevision = input.Reference.InputRevisionUuid,
            }
        );
        (await reads.GetSongAsync(original)).Should().NotBeNull();
        (await reads.ListOccurrencesAsync(edition))!
            .items.Single()
            .repertoire_song_uuid.Should()
            .Be(original);
        (await reads.ListTrackContentsAsync(source.TrackUuid))!
            .items.Single()
            .repertoire_song_uuid.Should()
            .Be(original);
        var owner = await fixture.Own(input, "artist_repertoire", artist);
        await new NormalizedMusicService(fixture.Db, fixture.Work).PublishAsync(
            owner.Ticket.WorkUuid,
            new MusicPublication
            {
                RepertoireSongs =
                [
                    new RepertoireSongChange
                    {
                        Uuid = survivor,
                        ArtistUuid = artist,
                        DisplayTitle = "Song",
                        ExpectedRevision = 1,
                        Retracted = true,
                        Evidence = [input.Reference],
                    },
                ],
            }
        );
        (await reads.GetSongAsync(original)).Should().BeNull();
        var account = (await reads.ListOccurrencesAsync(edition))!.items.Single();
        account.uuid.Should().Be(occurrence);
        account.repertoire_song_uuid.Should().BeNull();
        account.original_text.Should().Be("Song");
        var segment = (await reads.ListTrackContentsAsync(source.TrackUuid))!.items.Single();
        segment.uuid.Should().Be(content);
        segment.repertoire_song_uuid.Should().BeNull();
        segment.original_text.Should().Be("Song");
        segment.evidence.Single().quote.Should().Be("Song");
        (
            await fixture.Db.WithWriteConnection(c =>
                c.QuerySingleAsync<Guid>(
                    "SELECT repertoire_song_uuid FROM normalization.track_contents WHERE uuid=@content",
                    new { content }
                )
            )
        )
            .Should()
            .Be(original);
        (
            await fixture.Db.WithWriteConnection(c =>
                c.QuerySingleAsync<Guid>(
                    "SELECT repertoire_song_uuid FROM normalization.song_occurrences WHERE uuid=@occurrence",
                    new { occurrence }
                )
            )
        )
            .Should()
            .Be(original);
    }

    [Test]
    public async Task HttpReadsExposeNullableDomainFieldsAndDistinguishKnownEmptyFromMissing()
    {
        var artist = await Artist();
        var song = await Song(artist.Uuid, "HTTP song");
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(reads);
        builder
            .Services.AddControllers()
            .AddApplicationPart(typeof(NormalizationMusicController).Assembly)
            .AddNewtonsoftJson();
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        using var client = new HttpClient
        {
            BaseAddress = new Uri(
                app.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!
                    .Addresses.Single()
            ),
        };
        using var response = await client.GetAsync($"/api/v3/normalization/songs/{song}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var json = JObject.Parse(await response.Content.ReadAsStringAsync());
        json["display_title"]!.Value<string>().Should().Be("HTTP song");
        json["composition_uuid"]!.Type.Should().Be(JTokenType.Null);
        json["requested_uuid"]!.Type.Should().Be(JTokenType.Null);
        using var empty = await client.GetAsync(
            $"/api/v3/normalization/artists/{artist.Slug}/setlists"
        );
        empty.StatusCode.Should().Be(HttpStatusCode.OK);
        JObject.Parse(await empty.Content.ReadAsStringAsync())["items"]!.Should().BeEmpty();
        using var missing = await client.GetAsync($"/api/v3/normalization/songs/{Guid.NewGuid()}");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await app.StopAsync();
    }

    private Task<int> Sql(string sql, object? args = null) =>
        fixture.Db.WithWriteConnection(c => c.ExecuteAsync(sql, args));

    private async Task<(Guid Uuid, string Slug)> Artist()
    {
        var uuid = Guid.NewGuid();
        var slug = uuid.ToString("N");
        await Sql(
            "INSERT INTO artists(uuid,name,slug,updated_at) VALUES(@uuid,'Music reads',@slug,now())",
            new { uuid, slug }
        );
        return (uuid, slug);
    }

    // Read fixtures specify accepted rows directly. Publication ownership and revision validation have separate contract tests.
    private async Task<Guid> Revision(Guid entity, string kind, string basis, string payload = "{}")
    {
        var uuid = Guid.NewGuid();
        await Sql(
            "INSERT INTO normalization.music_revisions(uuid,entity_uuid,entity_kind,revision,decision_uuid,basis,payload,evidence) VALUES(@uuid,@entity,@kind,1,@decision,@basis,@payload::jsonb,'[]')",
            new
            {
                uuid,
                entity,
                kind,
                basis,
                payload,
                decision = Guid.NewGuid(),
            }
        );
        return uuid;
    }

    private async Task<Guid> Song(Guid artist, string title, string basis = "provider_assertion")
    {
        var uuid = Guid.NewGuid();
        var revision = await Revision(uuid, "repertoire_songs", basis);
        await Sql(
            "INSERT INTO normalization.repertoire_songs(uuid,artist_uuid,display_title,revision_uuid) VALUES(@uuid,@artist,@title,@revision)",
            new
            {
                uuid,
                artist,
                title,
                revision,
            }
        );
        return uuid;
    }

    private async Task<Guid> Composition(string title)
    {
        var uuid = Guid.NewGuid();
        var revision = await Revision(uuid, "compositions", "provider_assertion");
        await Sql(
            "INSERT INTO normalization.compositions(uuid,display_title,revision_uuid) VALUES(@uuid,@title,@revision)",
            new
            {
                uuid,
                title,
                revision,
            }
        );
        return uuid;
    }

    private async Task<Guid> Edition(Guid artist, bool ordered)
    {
        var uuid = Guid.NewGuid();
        var revision = await Revision(uuid, "setlist_editions", "provider_assertion");
        await Sql(
            "INSERT INTO normalization.setlist_editions(uuid,artist_uuid,literal_date,ordered,provider,resource_type,provider_key,revision_uuid) VALUES(@uuid,@artist,'1970-xx-xx',@ordered,'fixture','setlist',@key,@revision)",
            new
            {
                uuid,
                artist,
                ordered,
                key = uuid.ToString(),
                revision,
            }
        );
        return uuid;
    }

    private async Task<Guid> Occurrence(
        Guid edition,
        int? position,
        Guid? parent = null,
        int? component = null
    )
    {
        var uuid = Guid.NewGuid();
        var revision = await Revision(uuid, "song_occurrences", "provider_assertion");
        await Sql(
            "INSERT INTO normalization.song_occurrences(uuid,edition_uuid,position,kind,original_text,transition_after,parent_occurrence_uuid,component_position,revision_uuid) VALUES(@uuid,@edition,@position,'song','Song','unknown',@parent,@component,@revision)",
            new
            {
                uuid,
                edition,
                position,
                parent,
                component,
                revision,
            }
        );
        return uuid;
    }
}
