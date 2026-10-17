using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using CatalogSource = RelistenApiTests.Services.NormalizationDomainFixture.CatalogSource;
using DomainInput = RelistenApiTests.Services.NormalizationDomainFixture.DomainInput;

namespace RelistenApiTests.Services;

[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationMusicIdempotence
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizedMusicService music = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        music = new(fixture.Db, fixture.Work);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [TestCase("repertoire_songs")]
    [TestCase("compositions")]
    [TestCase("work_credits")]
    [TestCase("imported_song_assignments")]
    [TestCase("repertoire_compositions")]
    [TestCase("music_external_identities")]
    public async Task NewAttemptsWithTheSameAcceptedFactsPreserveCurrentRevisionAndCandidatePools(
        string table
    )
    {
        var input = await fixture.Capture("Song written by Pat", "songs");
        var artist = await NewArtist();
        var composition = new CompositionChange
        {
            Uuid = Guid.NewGuid(),
            DisplayTitle = "Work",
            Evidence = [input.Reference],
        };
        var author = new WorkCreditChange
        {
            Uuid = Guid.NewGuid(),
            CompositionUuid = composition.Uuid,
            OriginalText = "Pat",
            Evidence = [input.Reference],
        };
        var external = new MusicExternalIdentityChange
        {
            Uuid = Guid.NewGuid(),
            CompositionUuid = composition.Uuid,
            Provider = "fixture",
            ResourceType = "work",
            ProviderKey = Guid.NewGuid().ToString(),
            Evidence = [input.Reference],
        };
        var global = await Ticket(input, "compositions", "composition", composition.Uuid);
        await music.PublishAsync(
            global.WorkUuid,
            new MusicPublication
            {
                Compositions = [composition],
                WorkCredits = [author],
                ExternalIdentities = [external],
            }
        );
        var song = Song(artist, input.Reference);
        var imported = new ImportedSongAssignmentChange
        {
            Uuid = Guid.NewGuid(),
            ImportedSongUuid = await ImportedSong(artist),
            RepertoireSongUuid = song.Uuid,
            Evidence = [input.Reference],
        };
        var link = new RepertoireCompositionChange
        {
            Uuid = Guid.NewGuid(),
            RepertoireSongUuid = song.Uuid,
            CompositionUuid = composition.Uuid,
            MatchedWorkIdentity = new(
                external.Provider,
                external.ResourceType,
                external.ProviderKey
            ),
            Evidence = [input.Reference],
        };
        var dictionary = await Ticket(
            input,
            "songs",
            "artist_repertoire",
            artist,
            [global.SubjectUuid]
        );
        await music.PublishAsync(
            dictionary.WorkUuid,
            new MusicPublication
            {
                RepertoireSongs = [song],
                ImportedSongs = [imported],
                CompositionLinks = [link],
            }
        );

        var (uuid, publication, isGlobal) = table switch
        {
            "repertoire_songs" => (
                song.Uuid,
                new MusicPublication { RepertoireSongs = [song with { ExpectedRevision = 1 }] },
                false
            ),
            "compositions" => (
                composition.Uuid,
                new MusicPublication { Compositions = [composition with { ExpectedRevision = 1 }] },
                true
            ),
            "work_credits" => (
                author.Uuid,
                new MusicPublication { WorkCredits = [author with { ExpectedRevision = 1 }] },
                true
            ),
            "imported_song_assignments" => (
                imported.Uuid,
                new MusicPublication { ImportedSongs = [imported with { ExpectedRevision = 1 }] },
                false
            ),
            "repertoire_compositions" => (
                link.Uuid,
                new MusicPublication { CompositionLinks = [link with { ExpectedRevision = 1 }] },
                false
            ),
            "music_external_identities" => (
                external.Uuid,
                new MusicPublication
                {
                    ExternalIdentities = [external with { ExpectedRevision = 1 }],
                },
                true
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(table)),
        };
        var before = await State(table, uuid, artist);
        var retry = isGlobal
            ? await Ticket(input, "compositions", "composition", composition.Uuid)
            : await Ticket(input, "songs", "artist_repertoire", artist, [global.SubjectUuid]);
        var result = await music.PublishAsync(retry.WorkUuid, publication);

        result.Status.Should().Be(NormalizationPublicationStatus.Published);
        result.Replayed.Should().BeFalse();
        (await State(table, uuid, artist)).Should().Be(before);
        (await fixture.Work.GetSubjectAsync(retry.SubjectUuid))!
            .PublishedWorkUuid.Should()
            .Be(retry.WorkUuid);
    }

    [TestCase("title")]
    [TestCase("aliases")]
    [TestCase("basis")]
    [TestCase("retracted")]
    [TestCase("citation")]
    [TestCase("evidence_order")]
    public async Task EveryAcceptedFieldAndFullCitationRemainSignificant(string changedField)
    {
        var input = await fixture.CaptureJson(
            "{\"title\":\"Song\",\"alternate\":\"Song\"}",
            "songs",
            "/title"
        );
        var artist = await NewArtist();
        var alternate = input.Reference with { JsonPointer = "/alternate" };
        var song = Song(artist, input.Reference) with { Evidence = [input.Reference, alternate] };
        var owner = await Ticket(input, "songs", "artist_repertoire", artist);
        await music.PublishAsync(owner.WorkUuid, new MusicPublication { RepertoireSongs = [song] });
        var changed = song with { ExpectedRevision = 1 };
        changed = changedField switch
        {
            "title" => changed with { DisplayTitle = "Other song" },
            "aliases" => changed with { Aliases = ["Other wording"] },
            "basis" => changed with { Basis = "provider_assertion" },
            "retracted" => changed with { Retracted = true },
            "citation" => changed with
            {
                Evidence =
                [
                    input.Reference with
                    {
                        StartCodePoint = 0,
                        EndCodePoint = 4,
                        Quote = "Song",
                    },
                    alternate,
                ],
            },
            "evidence_order" => changed with { Evidence = [alternate, input.Reference] },
            _ => throw new ArgumentOutOfRangeException(nameof(changedField)),
        };
        var before = await State("repertoire_songs", song.Uuid, artist);
        var next = await Ticket(input, "songs", "artist_repertoire", artist);
        await music.PublishAsync(
            next.WorkUuid,
            new MusicPublication { RepertoireSongs = [changed] }
        );
        var after = await State("repertoire_songs", song.Uuid, artist);

        after.Revision.Should().Be(2);
        after.RevisionUuid.Should().NotBe(before.RevisionUuid);
        after.Revisions.Should().Be(before.Revisions + 1);
        after.Compositions.Should().BeGreaterThan(before.Compositions);
        after.Repertoire.Should().BeGreaterThan(before.Repertoire);
    }

    [Test]
    public async Task IdenticalPayloadCannotBypassExpectedRevisionOrReviewedAuthority()
    {
        var input = await fixture.Capture("Song", "songs");
        var artist = await NewArtist();
        var song = Song(artist, input.Reference);
        var owner = await Ticket(input, "songs", "artist_repertoire", artist);
        await music.PublishAsync(owner.WorkUuid, new MusicPublication { RepertoireSongs = [song] });
        var wrongRevision = await Ticket(input, "songs", "artist_repertoire", artist);
        var stale = () =>
            music.PublishAsync(
                wrongRevision.WorkUuid,
                new MusicPublication { RepertoireSongs = [song] }
            );
        await stale.Should().ThrowAsync<NormalizationConflictException>();
        await music.ApplyDecisionAsync(
            Guid.NewGuid(),
            owner.SubjectUuid,
            0,
            "accept",
            new MusicPublication { RepertoireSongs = [song with { ExpectedRevision = 1 }] },
            "Reviewed title",
            "test-operator"
        );
        var reviewed = await State("repertoire_songs", song.Uuid, artist);
        var automatic = await Ticket(input, "songs", "artist_repertoire", artist);
        var replaceReview = () =>
            music.PublishAsync(
                automatic.WorkUuid,
                new MusicPublication { RepertoireSongs = [song with { ExpectedRevision = 2 }] }
            );
        await replaceReview.Should().ThrowAsync<NormalizationConflictException>();
        (await State("repertoire_songs", song.Uuid, artist)).Should().Be(reviewed);
    }

    [Test]
    public async Task RepeatedReviewedAcceptKeepsDomainRevisionButRecordsNewEditorialDecision()
    {
        var input = await fixture.Capture("Song", "songs");
        var artist = await NewArtist();
        var song = Song(artist, input.Reference);
        var owner = await Ticket(input, "songs", "artist_repertoire", artist);
        await music.ApplyDecisionAsync(
            Guid.NewGuid(),
            owner.SubjectUuid,
            0,
            "accept",
            new MusicPublication { RepertoireSongs = [song] },
            "Accepted title",
            "test-operator"
        );
        var before = await State("repertoire_songs", song.Uuid, artist);
        var operation = Guid.NewGuid();
        var result = await music.ApplyDecisionAsync(
            operation,
            owner.SubjectUuid,
            1,
            "accept",
            new MusicPublication { RepertoireSongs = [song with { ExpectedRevision = 1 }] },
            "Confirmed title",
            "test-operator"
        );

        result.Replayed.Should().BeFalse();
        (await State("repertoire_songs", song.Uuid, artist)).Should().Be(before);
        (await fixture.Work.GetSubjectAsync(owner.SubjectUuid))!.EditorialRevision.Should().Be(2);
        (
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS(SELECT 1 FROM normalization.review_operations WHERE uuid=@operation)",
                    new { operation }
                )
            )
        )
            .Should()
            .BeTrue();
    }

    [Test]
    public async Task FailureAfterNoOpAndChangedAssertionRollsBackAndTheSameWorkCanRetry()
    {
        var input = await fixture.Capture("Song", "songs");
        var artist = await NewArtist();
        var unchanged = Song(artist, input.Reference);
        var changed = Song(artist, input.Reference);
        var stale = Song(artist, input.Reference);
        var owner = await Ticket(input, "songs", "artist_repertoire", artist);
        await music.PublishAsync(
            owner.WorkUuid,
            new MusicPublication { RepertoireSongs = [unchanged, changed, stale] }
        );
        var before = await State("repertoire_songs", changed.Uuid, artist);
        var next = await Ticket(input, "songs", "artist_repertoire", artist);
        var fail = () =>
            music.PublishAsync(
                next.WorkUuid,
                new MusicPublication
                {
                    RepertoireSongs =
                    [
                        unchanged with
                        {
                            ExpectedRevision = 1,
                        },
                        changed with
                        {
                            ExpectedRevision = 1,
                            DisplayTitle = "Changed",
                        },
                        stale,
                    ],
                }
            );
        await fail.Should().ThrowAsync<NormalizationConflictException>();
        (await State("repertoire_songs", changed.Uuid, artist)).Should().Be(before);
        var result = await music.PublishAsync(
            next.WorkUuid,
            new MusicPublication
            {
                RepertoireSongs =
                [
                    unchanged with
                    {
                        ExpectedRevision = 1,
                    },
                    changed with
                    {
                        ExpectedRevision = 1,
                    },
                    stale with
                    {
                        ExpectedRevision = 1,
                    },
                ],
            }
        );
        result.Status.Should().Be(NormalizationPublicationStatus.Published);
        result.Replayed.Should().BeFalse();
        (await State("repertoire_songs", changed.Uuid, artist)).Should().Be(before);
    }

    [Test]
    public async Task TwoCandidateConsumersConvergeWhenTheOlderConsumerRepublishesUnchangedFacts()
    {
        var input = await fixture.Capture("Song", "songs");
        var artistA = await NewArtist();
        var artistB = await NewArtist();
        var songA = Song(artistA, input.Reference);
        var songB = Song(artistB, input.Reference);
        var a = await Ticket(
            input,
            "songs",
            "artist_repertoire",
            artistA,
            consumeCompositions: true
        );
        await music.PublishAsync(a.WorkUuid, new MusicPublication { RepertoireSongs = [songA] });
        var b = await Ticket(
            input,
            "songs",
            "artist_repertoire",
            artistB,
            consumeCompositions: true
        );
        await music.PublishAsync(b.WorkUuid, new MusicPublication { RepertoireSongs = [songB] });
        (await IsPending(a.SubjectUuid)).Should().BeTrue();
        (await IsPending(b.SubjectUuid)).Should().BeFalse();
        var before = await State("repertoire_songs", songA.Uuid, artistA);
        var refreshA = await Ticket(
            input,
            "songs",
            "artist_repertoire",
            artistA,
            consumeCompositions: true
        );
        await music.PublishAsync(
            refreshA.WorkUuid,
            new MusicPublication { RepertoireSongs = [songA with { ExpectedRevision = 1 }] }
        );

        (await State("repertoire_songs", songA.Uuid, artistA)).Should().Be(before);
        (await IsPending(a.SubjectUuid)).Should().BeFalse();
        (await IsPending(b.SubjectUuid)).Should().BeFalse();
    }

    [Test]
    public async Task UnchangedFileContentRefreshesDerivedScopeAfterTheFileMovesBetweenArtists()
    {
        var input = await fixture.Capture("Stage announcement", "songs");
        var original = await fixture.Source();
        var destination = await fixture.Source();
        await MakePlayable(original);
        await MakePlayable(destination);
        var originalScope = await CatalogScope(original);
        var destinationScope = await CatalogScope(destination);
        var playbackUrl = await PlaybackUrl(original.TrackUuid);
        // Speech has no repertoire or occurrence assignment tied to the original artist.
        // Correcting its catalog recording can retain the file and content UUIDs.
        var content = new TrackContentChange
        {
            Uuid = Guid.NewGuid(),
            SourceTrackUuid = original.TrackUuid,
            Position = 1,
            Kind = "speech",
            OriginalText = "Stage announcement",
            Evidence = [input.Reference],
        };
        var owner = await Ticket(input, "songs", "recording", original.SourceUuid);
        await music.PublishAsync(
            owner.WorkUuid,
            new MusicPublication { TrackContents = [content] }
        );
        var reads = new NormalizationReadService(
            fixture.Db,
            Options.Create(new NormalizationReadOptions { Enabled = true })
        );
        var initial = (await reads.ListTrackContentsAsync(original.TrackUuid))!.items.Single();
        initial.reference_available.Should().BeTrue();
        (await ContentScope(content.Uuid)).Should().Be(originalScope);

        // Catalog correction alone cannot rewrite the prior normalization decision.
        var moved = await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                """
                UPDATE source_tracks track SET source_id=target.source_id,source_set_id=target.id,
                    artist_id=@ArtistId,track_position=2
                FROM source_sets target WHERE target.source_id=@SourceId AND target.index=0 AND track.uuid=@TrackUuid
                """,
                new
                {
                    destination.SourceId,
                    destination.ArtistId,
                    original.TrackUuid,
                }
            )
        );
        moved.Should().Be(1);
        (await reads.ListTrackContentsAsync(original.TrackUuid))!
            .items.Single()
            .reference_available.Should()
            .BeFalse();
        var before = await State("track_contents", content.Uuid, originalScope.ArtistUuid);
        var unchanged = content with { ExpectedRevision = before.Revision };
        var oldOwner = await Ticket(input, "songs", "recording", original.SourceUuid);

        Func<Task> oldPublication = () =>
            music.PublishAsync(
                oldOwner.WorkUuid,
                new MusicPublication { TrackContents = [unchanged] }
            );
        await oldPublication
            .Should()
            .ThrowAsync<ArgumentException>()
            .WithMessage("*decision owner*");
        (await State("track_contents", content.Uuid, originalScope.ArtistUuid)).Should().Be(before);
        (await ContentScope(content.Uuid)).Should().Be(originalScope);

        var currentOwner = await Ticket(input, "songs", "recording", destination.SourceUuid);
        var result = await music.PublishAsync(
            currentOwner.WorkUuid,
            new MusicPublication { TrackContents = [unchanged] }
        );

        result.Status.Should().Be(NormalizationPublicationStatus.Published);
        result.Replayed.Should().BeFalse();
        (await ContentScope(content.Uuid)).Should().Be(destinationScope);
        (await PlaybackUrl(original.TrackUuid)).Should().Be(playbackUrl);
        var accepted = (await music.GetTrackContentsAsync(original.TrackUuid)).Single();
        accepted.Revision.Should().Be(before.Revision + 1);
        accepted.Value.Should().BeEquivalentTo(unchanged);

        var current = (await reads.ListTrackContentsAsync(original.TrackUuid))!.items.Single();
        current.uuid.Should().Be(initial.uuid);
        current.source_track_uuid.Should().Be(initial.source_track_uuid);
        current.revision.Should().Be(before.Revision + 1);
        current.kind.Should().Be("speech");
        current.reference_available.Should().BeTrue();
        current.evidence_status.Should().Be("current");
        current.evidence.Should().BeEquivalentTo(initial.evidence);
        current.repertoire_song_uuid.Should().BeNull();
        current.occurrence_uuid.Should().BeNull();
    }

    [TestCase("songs")]
    [TestCase("compositions")]
    public async Task MergeAndSplitVersionAlreadyReviewedIdentitiesWithIdenticalMetadata(
        string domain
    )
    {
        var input = await fixture.Capture("Song", "songs");
        var artist = await NewArtist();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var a =
            domain == "songs"
                ? await Ticket(input, "songs", "artist_repertoire", artist)
                : await Ticket(input, "compositions", "composition", first);
        var b = domain == "songs" ? a : await Ticket(input, "compositions", "composition", second);
        var songA = Song(artist, input.Reference) with { Uuid = first };
        var songB = songA with { Uuid = second };
        var workA = new CompositionChange
        {
            Uuid = first,
            DisplayTitle = "Work",
            Evidence = [input.Reference],
        };
        var workB = workA with { Uuid = second };
        await music.ApplyDecisionAsync(
            Guid.NewGuid(),
            a.SubjectUuid,
            0,
            "accept",
            domain == "songs"
                ? new MusicPublication { RepertoireSongs = [songA, songB] }
                : new MusicPublication { Compositions = [workA] },
            "Accepted identities",
            "test-operator"
        );
        if (domain == "compositions")
        {
            await music.ApplyDecisionAsync(
                Guid.NewGuid(),
                b.SubjectUuid,
                0,
                "accept",
                new MusicPublication { Compositions = [workB] },
                "Accepted survivor",
                "test-operator"
            );
        }
        var mergeOperation = Guid.NewGuid();
        var merge = new MusicPublication { Merges = [new(domain, first, second, 1, 1)] };
        await music.ApplyDecisionAsync(
            mergeOperation,
            a.SubjectUuid,
            1,
            "merge",
            merge,
            "Same identity",
            "test-operator"
        );
        var table = domain == "songs" ? "repertoire_songs" : "compositions";
        var merged = await State(table, first, artist);
        merged.Revision.Should().Be(2);
        (await Redirect(table, first)).Should().Be(second);
        (
            await music.ApplyDecisionAsync(
                mergeOperation,
                a.SubjectUuid,
                1,
                "merge",
                merge,
                "Same identity",
                "test-operator"
            )
        )
            .Replayed.Should()
            .BeTrue();
        (await State(table, first, artist)).Should().Be(merged);
        var split = new MusicPublication
        {
            RepertoireSongs = domain == "songs" ? [songA with { ExpectedRevision = 2 }] : [],
            Compositions = domain == "compositions" ? [workA with { ExpectedRevision = 2 }] : [],
            Splits = [new(domain, first, 2, domain == "compositions" ? [new(second, 1)] : null)],
        };
        await music.ApplyDecisionAsync(
            Guid.NewGuid(),
            a.SubjectUuid,
            2,
            "split",
            split,
            "Distinct identities",
            "test-operator"
        );
        (await State(table, first, artist)).Revision.Should().Be(3);
        (await Redirect(table, first)).Should().BeNull();
        var oldSnapshot = () =>
            music.ApplyDecisionAsync(
                Guid.NewGuid(),
                a.SubjectUuid,
                3,
                "accept",
                domain == "songs"
                    ? new MusicPublication
                    {
                        RepertoireSongs = [songA with { ExpectedRevision = 2 }],
                    }
                    : new MusicPublication { Compositions = [workA with { ExpectedRevision = 2 }] },
                "Obsolete identity snapshot",
                "test-operator"
            );
        await oldSnapshot.Should().ThrowAsync<NormalizationConflictException>();
    }

    private async Task<NormalizationWorkTicket> Ticket(
        DomainInput input,
        string domain,
        string kind,
        Guid entity,
        Guid[]? dependencies = null,
        bool consumeCompositions = false
    )
    {
        var generation = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleOrDefaultAsync<long?>(
                "SELECT desired_generation FROM normalization.work_subjects WHERE domain=@domain AND subject_kind=@kind AND subject_uuid=@entity",
                new
                {
                    domain,
                    kind,
                    entity,
                }
            )
        );
        var universes = consumeCompositions
            ? new NormalizationCandidateUniverseDependency[]
            {
                new(
                    NormalizationCandidateUniverseKey.Compositions,
                    await Generation(NormalizationCandidateUniverseKey.Compositions)
                ),
            }
            : [];
        return await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            new(domain, kind, entity),
            "policy-1",
            [input.Resource],
            dependencies ?? [],
            generation,
            candidateUniverses: universes
        );
    }

    private async Task<Guid> NewArtist()
    {
        var source = await fixture.Source();
        return await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<Guid>("SELECT uuid FROM artists WHERE id=@ArtistId", source)
        );
    }

    private Task MakePlayable(CatalogSource source) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                """
                INSERT INTO shows(artist_id,date,display_date,uuid,updated_at)
                    VALUES(@ArtistId,'2026-10-17','2026-10-17',@show,now());
                UPDATE sources SET show_id=(SELECT id FROM shows WHERE uuid=@show) WHERE id=@SourceId;
                UPDATE source_tracks SET mp3_url=@url WHERE uuid=@TrackUuid
                """,
                new
                {
                    source.ArtistId,
                    source.SourceId,
                    source.TrackUuid,
                    url = $"https://example.test/music-idempotence/{source.TrackUuid:D}.mp3",
                    show = Guid.NewGuid(),
                }
            )
        );

    private Task<string> PlaybackUrl(Guid trackUuid) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<string>(
                "SELECT mp3_url FROM source_tracks WHERE uuid=@trackUuid",
                new { trackUuid }
            )
        );

    private Task<StoredContentScope> CatalogScope(CatalogSource source) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<StoredContentScope>(
                "SELECT a.uuid AS ArtistUuid,s.uuid AS SourceUuid FROM sources s JOIN artists a ON a.id=s.artist_id WHERE s.id=@SourceId",
                source
            )
        );

    private Task<StoredContentScope> ContentScope(Guid uuid) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<StoredContentScope>(
                "SELECT artist_uuid AS ArtistUuid,source_uuid AS SourceUuid FROM normalization.track_contents WHERE uuid=@uuid",
                new { uuid }
            )
        );

    private Task<Guid> ImportedSong(Guid artist) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<Guid>(
                "INSERT INTO setlist_songs(artist_id,name,slug,upstream_identifier,uuid,updated_at) SELECT id,'Song',@key,@key,@uuid,now() FROM artists WHERE uuid=@artist RETURNING uuid",
                new
                {
                    artist,
                    key = Guid.NewGuid().ToString(),
                    uuid = Guid.NewGuid(),
                }
            )
        );

    private Task<Guid?> Redirect(string table, Guid uuid) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<Guid?>(
                $"SELECT redirect_uuid FROM normalization.{table} WHERE uuid=@uuid",
                new { uuid }
            )
        );

    private Task<long> Generation(NormalizationCandidateUniverseKey universe) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<long>(
                "SELECT COALESCE((SELECT generation FROM normalization.candidate_universe_revisions WHERE domain=@Domain AND scope_uuid=@ScopeUuid),0)",
                universe
            )
        );

    private async Task<AcceptedState> State(string table, Guid uuid, Guid artist)
    {
        var state = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<AcceptedState>(
                $"""
                SELECT r.revision AS "Revision",r.uuid AS "RevisionUuid",
                    (SELECT count(*) FROM normalization.music_revisions WHERE entity_uuid=@uuid) AS "Revisions",
                    (SELECT count(*) FROM normalization.music_evidence_references e JOIN normalization.music_revisions h ON h.uuid=e.revision_uuid WHERE h.entity_uuid=@uuid) AS "Evidence",
                    (SELECT generation FROM normalization.candidate_universe_revisions WHERE domain='compositions' AND scope_uuid=@empty) AS "Compositions",
                    COALESCE((SELECT generation FROM normalization.candidate_universe_revisions WHERE domain='repertoire' AND scope_uuid=@artist),0) AS "Repertoire"
                FROM normalization.{table} x JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid WHERE x.uuid=@uuid
                """,
                new
                {
                    uuid,
                    artist,
                    empty = Guid.Empty,
                }
            )
        );
        return state;
    }

    private async Task<bool> IsPending(Guid subject)
    {
        Guid? after = null;
        while (true)
        {
            var page = await fixture.Work.GetPendingSubjectsAsync(after, 1000);
            if (page.Any(x => x.SubjectUuid == subject))
            {
                return true;
            }
            if (page.Count < 1000)
            {
                return false;
            }
            after = page[^1].SubjectUuid;
        }
    }

    private sealed record StoredContentScope(Guid ArtistUuid, Guid SourceUuid);

    private sealed record AcceptedState(
        long Revision,
        Guid RevisionUuid,
        long Revisions,
        long Evidence,
        long Compositions,
        long Repertoire
    );

    private static RepertoireSongChange Song(Guid artist, NormalizedEvidenceReference evidence) =>
        new()
        {
            Uuid = Guid.NewGuid(),
            ArtistUuid = artist,
            DisplayTitle = "Song",
            Basis = "title_derived",
            Evidence = [evidence],
        };
}
