using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using DomainInput = RelistenApiTests.Services.NormalizationDomainFixture.DomainInput;

namespace RelistenApiTests.Services;

[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationMusicCandidateUniverses
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

    [Test]
    public async Task DictionaryAssignmentsAndExternalIdsAdvanceBothPoolsAndReplayDoesNot()
    {
        var input = await fixture.Capture("Stored song", "songs");
        var source = await fixture.Source();
        var artist = await Artist(source.ArtistId);
        var imported = await ImportedSong(source.ArtistId);
        var song = Song(artist, input.Reference);
        var before = await Pools(artist);
        var publications = new MusicPublication[]
        {
            new() { RepertoireSongs = [song] },
            new()
            {
                ImportedSongs =
                [
                    new()
                    {
                        Uuid = Guid.NewGuid(),
                        ImportedSongUuid = imported,
                        RepertoireSongUuid = song.Uuid,
                        Evidence = [input.Reference],
                    },
                ],
            },
            new()
            {
                ExternalIdentities =
                [
                    new()
                    {
                        Uuid = Guid.NewGuid(),
                        RepertoireSongUuid = song.Uuid,
                        Provider = "fixture",
                        ResourceType = "artist-song",
                        ProviderKey = Guid.NewGuid().ToString(),
                        Evidence = [input.Reference],
                    },
                ],
            },
        };
        foreach (var publication in publications)
        {
            var ticket = await Ticket(input, "songs", "artist_repertoire", artist);
            await music.PublishAsync(ticket.WorkUuid, publication);
            var accepted = await Pools(artist);
            accepted.Repertoire.Should().BeGreaterThan(before.Repertoire);
            accepted.Compositions.Should().BeGreaterThan(before.Compositions);
            (await music.PublishAsync(ticket.WorkUuid, publication)).Replayed.Should().BeTrue();
            (await Pools(artist)).Should().Be(accepted);
            before = accepted;
        }
        (await music.GetImportedSongsAsync(song.Uuid))
            .Single()
            .Value.ImportedSongUuid.Should()
            .Be(imported);
    }

    [Test]
    public async Task AcceptedCompositionLinkAndRetractionAdvanceBothPools()
    {
        var input = await fixture.Capture("Song and work", "compositions");
        var source = await fixture.Source();
        var artist = await Artist(source.ArtistId);
        var work = new CompositionChange
        {
            Uuid = Guid.NewGuid(),
            DisplayTitle = "Work",
            Evidence = [input.Reference],
        };
        var owner = await Ticket(input, "compositions", "composition", work.Uuid);
        await music.PublishAsync(owner.WorkUuid, new MusicPublication { Compositions = [work] });
        var song = Song(artist, input.Reference);
        var dictionary = await Ticket(
            input,
            "songs",
            "artist_repertoire",
            artist,
            dependencies: [owner.SubjectUuid]
        );
        await music.PublishAsync(
            dictionary.WorkUuid,
            new MusicPublication { RepertoireSongs = [song] }
        );
        var link = new RepertoireCompositionChange
        {
            Uuid = Guid.NewGuid(),
            RepertoireSongUuid = song.Uuid,
            CompositionUuid = work.Uuid,
            Evidence = [input.Reference],
        };
        var before = await Pools(artist);
        await music.ApplyDecisionAsync(
            Guid.NewGuid(),
            dictionary.SubjectUuid,
            0,
            "accept",
            new MusicPublication { CompositionLinks = [link] },
            "Explicit work correspondence",
            "test-operator"
        );
        var linked = await Pools(artist);
        linked.Repertoire.Should().BeGreaterThan(before.Repertoire);
        linked.Compositions.Should().BeGreaterThan(before.Compositions);
        await music.ApplyDecisionAsync(
            Guid.NewGuid(),
            dictionary.SubjectUuid,
            1,
            "retract",
            new MusicPublication
            {
                CompositionLinks = [link with { ExpectedRevision = 1, Retracted = true }],
            },
            "Incorrect work correspondence",
            "test-operator"
        );
        var retracted = await Pools(artist);
        retracted.Repertoire.Should().BeGreaterThan(linked.Repertoire);
        retracted.Compositions.Should().BeGreaterThan(linked.Compositions);
        (await music.GetCompositionLinksAsync(repertoireSongUuid: song.Uuid)).Should().BeEmpty();
    }

    [Test]
    public async Task RepertoireMergeAndSplitAdvanceBothPools()
    {
        var input = await fixture.Capture("Two interpretations", "songs");
        var source = await fixture.Source();
        var artist = await Artist(source.ArtistId);
        var first = Song(artist, input.Reference);
        var second = first with { Uuid = Guid.NewGuid(), DisplayTitle = "Alternative title" };
        var owner = await Ticket(input, "songs", "artist_repertoire", artist);
        await music.PublishAsync(
            owner.WorkUuid,
            new MusicPublication { RepertoireSongs = [first, second] }
        );
        var before = await Pools(artist);
        var merge = new MusicPublication { Merges = [new("songs", first.Uuid, second.Uuid, 1, 1)] };
        var operation = Guid.NewGuid();
        await music.ApplyDecisionAsync(
            operation,
            owner.SubjectUuid,
            0,
            "merge",
            merge,
            "Same song",
            "test-operator"
        );
        var merged = await Pools(artist);
        merged.Repertoire.Should().BeGreaterThan(before.Repertoire);
        merged.Compositions.Should().BeGreaterThan(before.Compositions);
        (
            await music.ApplyDecisionAsync(
                operation,
                owner.SubjectUuid,
                0,
                "merge",
                merge,
                "Same song",
                "test-operator"
            )
        )
            .Replayed.Should()
            .BeTrue();
        (await Pools(artist)).Should().Be(merged);
        await music.ApplyDecisionAsync(
            Guid.NewGuid(),
            owner.SubjectUuid,
            1,
            "split",
            new MusicPublication
            {
                RepertoireSongs = [first with { ExpectedRevision = 2 }],
                Splits = [new("songs", first.Uuid, 2)],
            },
            "Distinct interpretations",
            "test-operator"
        );
        var split = await Pools(artist);
        split.Repertoire.Should().BeGreaterThan(merged.Repertoire);
        split.Compositions.Should().BeGreaterThan(merged.Compositions);
        (await music.GetRepertoireAsync(first.Uuid))!.RedirectUuid.Should().BeNull();
    }

    [Test]
    public async Task WorkAuthorChangesAdvanceCompositionPoolWithoutInventingArtistRepertoireScope()
    {
        var input = await fixture.Capture("Written by Pat", "compositions");
        var source = await fixture.Source();
        var artist = await Artist(source.ArtistId);
        var work = new CompositionChange
        {
            Uuid = Guid.NewGuid(),
            DisplayTitle = "Work",
            Evidence = [input.Reference],
        };
        var credit = new WorkCreditChange
        {
            Uuid = Guid.NewGuid(),
            CompositionUuid = work.Uuid,
            OriginalText = "Written by Pat",
            Evidence = [input.Reference],
        };
        var owner = await Ticket(input, "compositions", "composition", work.Uuid);
        await music.PublishAsync(
            owner.WorkUuid,
            new MusicPublication { Compositions = [work], WorkCredits = [credit] }
        );
        var next = await Ticket(input, "compositions", "composition", work.Uuid);
        var before = await Pools(artist);
        await music.PublishAsync(
            next.WorkUuid,
            new MusicPublication
            {
                WorkCredits = [credit with { ExpectedRevision = 1, ArtistUuid = artist }],
            }
        );
        var artistCredit = await Pools(artist);
        artistCredit.Compositions.Should().BeGreaterThan(before.Compositions);
        artistCredit.Repertoire.Should().Be(before.Repertoire);
        var person = new CanonicalPerson(Guid.NewGuid(), "Pat");
        var personOwner = await Ticket(input, "people", "person", person.Uuid);
        await new NormalizedPeopleService(fixture.Db, fixture.Work).PublishAsync(
            personOwner.WorkUuid,
            new PeoplePublication(
                [person],
                [new(Guid.NewGuid(), person.Uuid, "Pat", input.Reference)],
                [],
                []
            )
        );
        var resolved = await Ticket(
            input,
            "compositions",
            "composition",
            work.Uuid,
            dependencies: [personOwner.SubjectUuid]
        );
        var beforePerson = await Pools(artist);
        await music.PublishAsync(
            resolved.WorkUuid,
            new MusicPublication
            {
                WorkCredits = [credit with { ExpectedRevision = 2, PersonUuid = person.Uuid }],
            }
        );
        var personCredit = await Pools(artist);
        personCredit.Compositions.Should().BeGreaterThan(beforePerson.Compositions);
        personCredit.Repertoire.Should().Be(beforePerson.Repertoire);
        var accepted = (await music.GetWorkCreditsAsync(work.Uuid)).Single().Value;
        accepted.PersonUuid.Should().Be(person.Uuid);
        accepted.ArtistUuid.Should().BeNull();
    }

    [Test]
    public async Task FailedPublicationRollsBackPoolChangesAndCanRetry()
    {
        var input = await fixture.Capture("Song", "songs");
        var a = await fixture.Source();
        var b = await fixture.Source();
        var artist = await Artist(a.ArtistId);
        var importedOtherArtist = await ImportedSong(b.ArtistId);
        var song = Song(artist, input.Reference);
        var owner = await Ticket(input, "songs", "artist_repertoire", artist);
        var before = await Pools(artist);
        var failing = () =>
            music.PublishAsync(
                owner.WorkUuid,
                new MusicPublication
                {
                    RepertoireSongs = [song],
                    ImportedSongs =
                    [
                        new()
                        {
                            Uuid = Guid.NewGuid(),
                            ImportedSongUuid = importedOtherArtist,
                            RepertoireSongUuid = song.Uuid,
                            Evidence = [input.Reference],
                        },
                    ],
                }
            );
        await failing.Should().ThrowAsync<ArgumentException>();
        (await Pools(artist)).Should().Be(before);
        (await music.GetRepertoireAsync(song.Uuid)).Should().BeNull();
        await music.PublishAsync(owner.WorkUuid, new MusicPublication { RepertoireSongs = [song] });
        var retried = await Pools(artist);
        retried.Repertoire.Should().BeGreaterThan(before.Repertoire);
        retried.Compositions.Should().BeGreaterThan(before.Compositions);
    }

    [Test]
    public async Task RejectionChangesEditorialStateWithoutChangingAcceptedCandidateFacts()
    {
        var input = await fixture.Capture("Equipment is not a song", "songs");
        var source = await fixture.Source();
        var artist = await Artist(source.ArtistId);
        var owner = await Ticket(input, "songs", "artist_repertoire", artist);
        var before = await Pools(artist);
        await music.ApplyDecisionAsync(
            Guid.NewGuid(),
            owner.SubjectUuid,
            0,
            "reject",
            new MusicPublication { RepertoireSongs = [Song(artist, input.Reference)] },
            "Equipment wording",
            "test-operator"
        );
        (await Pools(artist)).Should().Be(before);
        (await fixture.Work.GetSubjectAsync(owner.SubjectUuid))!.EditorialRevision.Should().Be(1);
    }

    [Test]
    public async Task OwnPublicationAcknowledgesItsEpochAndAnotherArtistsNewRivalMakesWorkStale()
    {
        var input = await fixture.Capture("Candidate pool", "songs");
        var a = await fixture.Source();
        var b = await fixture.Source();
        var artistA = await Artist(a.ArtistId);
        var artistB = await Artist(b.ArtistId);
        var observed = await Generation(NormalizationCandidateUniverseKey.Compositions);
        var owner = await Ticket(
            input,
            "songs",
            "artist_repertoire",
            artistA,
            universes: [new(NormalizationCandidateUniverseKey.Compositions, observed)]
        );
        var song = Song(artistA, input.Reference);
        await music.PublishAsync(owner.WorkUuid, new MusicPublication { RepertoireSongs = [song] });
        (await IsPending(owner.SubjectUuid)).Should().BeFalse();
        var consumed = await Generation(NormalizationCandidateUniverseKey.Compositions);
        var beforeRival = await Ticket(
            input,
            "songs",
            "artist_repertoire",
            artistA,
            universes: [new(NormalizationCandidateUniverseKey.Compositions, consumed)]
        );
        var rival = await Ticket(input, "songs", "artist_repertoire", artistB);
        await music.PublishAsync(
            rival.WorkUuid,
            new MusicPublication { RepertoireSongs = [Song(artistB, input.Reference)] }
        );
        (await IsPending(owner.SubjectUuid)).Should().BeTrue();
        (
            await music.PublishAsync(
                beforeRival.WorkUuid,
                new MusicPublication
                {
                    RepertoireSongs =
                    [
                        song with
                        {
                            ExpectedRevision = 1,
                            DisplayTitle = "Obsolete candidate result",
                        },
                    ],
                }
            )
        )
            .Status.Should()
            .Be(NormalizationPublicationStatus.Stale);
        (await music.GetRepertoireAsync(song.Uuid))!.Value.DisplayTitle.Should().Be("Song");
    }

    [Test]
    public async Task SetlistAndFileUsageDoNotChangeMatchingFactPools()
    {
        var input = await fixture.Capture("Song", "songs");
        var source = await fixture.Source();
        var artist = await Artist(source.ArtistId);
        var song = Song(artist, input.Reference);
        var dictionary = await Ticket(input, "songs", "artist_repertoire", artist);
        await music.PublishAsync(
            dictionary.WorkUuid,
            new MusicPublication { RepertoireSongs = [song] }
        );
        var edition = new SetlistEditionChange
        {
            Uuid = Guid.NewGuid(),
            ArtistUuid = artist,
            LiteralDate = "2026-10-17",
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
            RepertoireSongUuid = song.Uuid,
            OriginalText = "Song",
            Evidence = [input.Reference],
        };
        var setlist = await Ticket(
            input,
            "songs",
            "setlist",
            edition.Uuid,
            dependencies: [dictionary.SubjectUuid]
        );
        var before = await Pools(artist);
        await music.PublishAsync(
            setlist.WorkUuid,
            new MusicPublication { Editions = [edition], Occurrences = [occurrence] }
        );
        var recording = await Ticket(
            input,
            "songs",
            "recording",
            source.SourceUuid,
            dependencies: [dictionary.SubjectUuid, setlist.SubjectUuid]
        );
        await music.PublishAsync(
            recording.WorkUuid,
            new MusicPublication
            {
                TrackContents =
                [
                    new()
                    {
                        Uuid = Guid.NewGuid(),
                        SourceTrackUuid = source.TrackUuid,
                        Position = 1,
                        OriginalText = "Song",
                        RepertoireSongUuid = song.Uuid,
                        OccurrenceUuid = occurrence.Uuid,
                        Evidence = [input.Reference],
                    },
                ],
            }
        );
        (await Pools(artist)).Should().Be(before);
        (await music.GetTrackContentsAsync(source.TrackUuid)).Should().ContainSingle();
    }

    private async Task<NormalizationWorkTicket> Ticket(
        DomainInput input,
        string domain,
        string kind,
        Guid entity,
        Guid[]? dependencies = null,
        NormalizationCandidateUniverseDependency[]? universes = null
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

    private Task<Guid> Artist(int id) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<Guid>("SELECT uuid FROM artists WHERE id=@id", new { id })
        );

    private Task<Guid> ImportedSong(int artist) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<Guid>(
                "INSERT INTO setlist_songs(artist_id,name,slug,upstream_identifier,uuid,updated_at) VALUES(@artist,'Stored song',@key,@key,@uuid,now()) RETURNING uuid",
                new
                {
                    artist,
                    key = Guid.NewGuid().ToString(),
                    uuid = Guid.NewGuid(),
                }
            )
        );

    private Task<long> Generation(NormalizationCandidateUniverseKey universe) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<long>(
                "SELECT COALESCE((SELECT generation FROM normalization.candidate_universe_revisions WHERE domain=@Domain AND scope_uuid=@ScopeUuid),0)",
                universe
            )
        );

    private async Task<(long Compositions, long Repertoire)> Pools(Guid artist) =>
        (
            await Generation(NormalizationCandidateUniverseKey.Compositions),
            await Generation(NormalizationCandidateUniverseKey.ForRepertoire(artist))
        );

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
