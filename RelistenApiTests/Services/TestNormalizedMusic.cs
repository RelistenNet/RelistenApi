using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using DomainInput = RelistenApiTests.Services.NormalizationDomainFixture.DomainInput;

namespace RelistenApiTests.Services;

[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizedMusic
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizedMusicService music = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new NormalizationDomainFixture();
        music = new NormalizedMusicService(fixture.Db, fixture.Work);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task RepeatedPassagesKeepIdsAfterReorderAndFilesCanSpanPassagesWithoutOffsets()
    {
        var input = await fixture.Capture("Song > Song; one performance", "songs");
        var source = await fixture.Source();
        var artist = await Artist(source.ArtistId);
        var performance = await Show(source.ArtistId, "early");
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET show_id=(SELECT id FROM shows WHERE uuid=@performance) WHERE uuid=@SourceUuid",
                new { performance, source.SourceUuid }
            )
        );
        var song = Song(artist, input.Reference);
        var repertoire = await Ticket(input, "songs", "artist_repertoire", artist);
        var songs = new MusicPublication { RepertoireSongs = [song] };
        await music.PublishAsync(repertoire.WorkUuid, songs);
        (await music.PublishAsync(repertoire.WorkUuid, songs)).Replayed.Should().BeTrue();
        var edition = Edition(artist, input.Reference) with
        {
            ShowUuid = performance,
            Ordered = true,
        };
        var one = Occurrence(edition.Uuid, song.Uuid, input.Reference) with
        {
            Position = 1,
            ProviderItemKey = "one",
            TransitionAfter = "segue",
        };
        var reprise = Occurrence(edition.Uuid, song.Uuid, input.Reference) with
        {
            Position = 2,
            ProviderItemKey = "reprise",
        };
        var setlist = await Ticket(input, "songs", "setlist", edition.Uuid, repertoire.SubjectUuid);
        await music.PublishAsync(
            setlist.WorkUuid,
            new MusicPublication { Editions = [edition], Occurrences = [one, reprise] }
        );
        var content = Content(source.TrackUuid, song.Uuid, input.Reference) with
        {
            OccurrenceUuid = one.Uuid,
        };
        var file = await Ticket(
            input,
            "songs",
            "recording",
            source.SourceUuid,
            repertoire.SubjectUuid,
            setlist.SubjectUuid
        );
        await music.PublishAsync(
            file.WorkUuid,
            new MusicPublication
            {
                TrackContents =
                [
                    content,
                    content with
                    {
                        Uuid = Guid.NewGuid(),
                        Position = 2,
                        OccurrenceUuid = reprise.Uuid,
                    },
                ],
            }
        );
        var reorder = await Ticket(input, "songs", "setlist", edition.Uuid, repertoire.SubjectUuid);
        await music.PublishAsync(
            reorder.WorkUuid,
            new MusicPublication
            {
                Occurrences =
                [
                    one with
                    {
                        ExpectedRevision = 1,
                        Position = 2,
                    },
                    reprise with
                    {
                        ExpectedRevision = 1,
                        Position = 1,
                    },
                ],
            }
        );
        (await music.GetOccurrencesAsync(edition.Uuid))
            .Select(x => x.Value.Uuid)
            .Should()
            .Equal(reprise.Uuid, one.Uuid);
        var contents = await music.GetTrackContentsAsync(source.TrackUuid);
        contents.Select(x => x.Value.OccurrenceUuid).Should().Equal(one.Uuid, reprise.Uuid);
        contents.Should().OnlyContain(x => x.Value.StartMs == null && x.Value.EndMs == null);
        (await music.SearchRepertoireAsync(artist))
            .Should()
            .Contain(x => x.Value.Uuid == song.Uuid && x.Value.Basis == "title_derived");
    }

    [Test]
    public async Task FileAlignmentCannotCrossPerformancesAndUnresolvedSongMatchingRemainsAccepted()
    {
        var input = await fixture.Capture("Song; early and late accounts", "songs");
        var source = await fixture.Source();
        var artist = await Artist(source.ArtistId);
        var early = await Show(source.ArtistId, "early");
        var late = await Show(source.ArtistId, "late");
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET show_id=(SELECT id FROM shows WHERE uuid=@early) WHERE uuid=@SourceUuid",
                new { early, source.SourceUuid }
            )
        );
        var song = Song(artist, input.Reference);
        var repertoire = await Ticket(input, "songs", "artist_repertoire", artist);
        await music.PublishAsync(
            repertoire.WorkUuid,
            new MusicPublication { RepertoireSongs = [song] }
        );
        var edition = Edition(artist, input.Reference) with { ShowUuid = late, Ordered = true };
        var occurrence = Occurrence(edition.Uuid, song.Uuid, input.Reference) with { Position = 1 };
        var setlist = await Ticket(input, "songs", "setlist", edition.Uuid, repertoire.SubjectUuid);
        await music.PublishAsync(
            setlist.WorkUuid,
            new MusicPublication { Editions = [edition], Occurrences = [occurrence] }
        );
        var content = Content(source.TrackUuid, song.Uuid, input.Reference) with
        {
            OccurrenceUuid = occurrence.Uuid,
        };
        var file = await Ticket(
            input,
            "songs",
            "recording",
            source.SourceUuid,
            repertoire.SubjectUuid,
            setlist.SubjectUuid
        );
        var crossPerformance = () =>
            music.PublishAsync(file.WorkUuid, new MusicPublication { TrackContents = [content] });
        await crossPerformance.Should().ThrowAsync<ArgumentException>();
        (await music.GetTrackContentsAsync(source.TrackUuid)).Should().BeEmpty();
        await music.PublishAsync(
            file.WorkUuid,
            new MusicPublication { TrackContents = [content with { OccurrenceUuid = null }] }
        );
        (await music.GetTrackContentsAsync(source.TrackUuid))
            .Single()
            .Value.RepertoireSongUuid.Should()
            .Be(song.Uuid);
        var unassigned = await Ticket(
            input,
            "songs",
            "setlist",
            edition.Uuid,
            repertoire.SubjectUuid
        );
        await music.PublishAsync(
            unassigned.WorkUuid,
            new MusicPublication
            {
                Editions = [edition with { ExpectedRevision = 1, ShowUuid = null }],
            }
        );
        var align = await Ticket(
            input,
            "songs",
            "recording",
            source.SourceUuid,
            repertoire.SubjectUuid,
            setlist.SubjectUuid
        );
        await music.PublishAsync(
            align.WorkUuid,
            new MusicPublication { TrackContents = [content with { ExpectedRevision = 1 }] }
        );
        (await music.GetTrackContentsAsync(source.TrackUuid))
            .Single()
            .Value.OccurrenceUuid.Should()
            .Be(occurrence.Uuid);
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET show_id=(SELECT id FROM shows WHERE uuid=@late) WHERE uuid=@SourceUuid",
                new { late, source.SourceUuid }
            )
        );
        var assigned = await Ticket(
            input,
            "songs",
            "setlist",
            edition.Uuid,
            repertoire.SubjectUuid
        );
        await music.PublishAsync(
            assigned.WorkUuid,
            new MusicPublication { Editions = [edition with { ExpectedRevision = 2 }] }
        );
        var matching = await Ticket(
            input,
            "songs",
            "recording",
            source.SourceUuid,
            repertoire.SubjectUuid,
            setlist.SubjectUuid
        );
        await music.PublishAsync(
            matching.WorkUuid,
            new MusicPublication { TrackContents = [content with { ExpectedRevision = 2 }] }
        );
        (await music.GetTrackContentsAsync(source.TrackUuid))
            .Single()
            .Value.OccurrenceUuid.Should()
            .Be(occurrence.Uuid);
    }

    [Test]
    public async Task UnorderedMembershipRejectsInventedPositionsAndEditionWritesRollBackTogether()
    {
        var input = await fixture.Capture("membership has no order", "songs");
        var source = await fixture.Source();
        var artist = await Artist(source.ArtistId);
        var song = Song(artist, input.Reference);
        var repertoire = await Ticket(input, "songs", "artist_repertoire", artist);
        await music.PublishAsync(
            repertoire.WorkUuid,
            new MusicPublication { RepertoireSongs = [song] }
        );
        var edition = Edition(artist, input.Reference);
        var occurrence = Occurrence(edition.Uuid, song.Uuid, input.Reference);
        var ticket = await Ticket(input, "songs", "setlist", edition.Uuid, repertoire.SubjectUuid);
        var bad = () =>
            music.PublishAsync(
                ticket.WorkUuid,
                new MusicPublication
                {
                    Editions = [edition],
                    Occurrences = [occurrence with { Position = 1 }],
                }
            );
        await bad.Should().ThrowAsync<ArgumentException>();
        (await music.GetEditionAsync(edition.Uuid)).Should().BeNull();
        await music.PublishAsync(
            ticket.WorkUuid,
            new MusicPublication { Editions = [edition], Occurrences = [occurrence] }
        );
        (await music.GetEditionAsync(edition.Uuid))!.Value.Ordered.Should().BeFalse();
        (await music.GetOccurrencesAsync(edition.Uuid)).Single().Value.Position.Should().BeNull();
    }

    [Test]
    public async Task ForeignArtistScopeAndKnownDurationBoundsAreValidatedBeforeFileBinding()
    {
        var input = await fixture.Capture("foreign artist", "songs");
        var a = await fixture.Source();
        var b = await fixture.Source();
        var song = Song(await Artist(a.ArtistId), input.Reference);
        var repertoire = await Ticket(input, "songs", "artist_repertoire", song.ArtistUuid);
        await music.PublishAsync(
            repertoire.WorkUuid,
            new MusicPublication { RepertoireSongs = [song] }
        );
        var foreign = await Ticket(
            input,
            "songs",
            "recording",
            b.SourceUuid,
            repertoire.SubjectUuid
        );
        var content = Content(b.TrackUuid, song.Uuid, input.Reference);
        var bad = () =>
            music.PublishAsync(
                foreign.WorkUuid,
                new MusicPublication { TrackContents = [content] }
            );
        await bad.Should().ThrowAsync<ArgumentException>();
        (await music.GetTrackContentsAsync(b.TrackUuid)).Should().BeEmpty();
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE source_tracks SET duration=10 WHERE uuid=@uuid",
                new { uuid = a.TrackUuid }
            )
        );
        var ticket = await Ticket(
            input,
            "songs",
            "recording",
            a.SourceUuid,
            repertoire.SubjectUuid
        );
        var outside = () =>
            music.PublishAsync(
                ticket.WorkUuid,
                new MusicPublication
                {
                    TrackContents =
                    [
                        content with
                        {
                            SourceTrackUuid = a.TrackUuid,
                            StartMs = 11000,
                        },
                    ],
                }
            );
        await outside.Should().ThrowAsync<ArgumentException>();
        await music.PublishAsync(
            ticket.WorkUuid,
            new MusicPublication
            {
                TrackContents =
                [
                    content with
                    {
                        SourceTrackUuid = a.TrackUuid,
                        StartMs = 0,
                        EndMs = 10000,
                    },
                ],
            }
        );
    }

    [Test]
    public async Task QualifiedWorkIdentityLinksArtistTitlesAndPreservesUnresolvedWriterRole()
    {
        var input = await fixture.Capture("work-id 123; written by Pat", "compositions");
        var a = await fixture.Source();
        var b = await fixture.Source();
        var first = Song(await Artist(a.ArtistId), input.Reference) with
        {
            DisplayTitle = "Artist A title",
        };
        var second = Song(await Artist(b.ArtistId), input.Reference) with
        {
            DisplayTitle = "Artist B title",
        };
        var composition = new CompositionChange
        {
            Uuid = Guid.NewGuid(),
            DisplayTitle = "The work",
            Evidence = [input.Reference],
        };
        var identity = new MusicExternalIdentityChange
        {
            Uuid = Guid.NewGuid(),
            CompositionUuid = composition.Uuid,
            Provider = "works",
            ResourceType = "work",
            ProviderKey = "123",
            Evidence = [input.Reference],
        };
        var writer = new WorkCreditChange
        {
            Uuid = Guid.NewGuid(),
            CompositionUuid = composition.Uuid,
            Role = "writer",
            OriginalText = "written by Pat",
            Evidence = [input.Reference],
        };
        var global = await Ticket(input, "compositions", "composition", composition.Uuid);
        await music.PublishAsync(
            global.WorkUuid,
            new MusicPublication
            {
                Compositions = [composition],
                ExternalIdentities = [identity],
                WorkCredits = [writer],
            }
        );
        foreach (var song in new[] { first, second })
        {
            var repertoire = await Ticket(
                input,
                "songs",
                "artist_repertoire",
                song.ArtistUuid,
                global.SubjectUuid
            );
            await music.PublishAsync(
                repertoire.WorkUuid,
                new MusicPublication
                {
                    RepertoireSongs = [song],
                    CompositionLinks =
                    [
                        new()
                        {
                            Uuid = Guid.NewGuid(),
                            RepertoireSongUuid = song.Uuid,
                            CompositionUuid = composition.Uuid,
                            MatchedWorkIdentity = new("works", "work", "123"),
                            Evidence = [input.Reference],
                        },
                    ],
                }
            );
        }
        (await music.GetCompositionLinksAsync(compositionUuid: composition.Uuid))
            .Select(x => x.Value.RepertoireSongUuid)
            .Should()
            .BeEquivalentTo(new[] { first.Uuid, second.Uuid });
        var credit = (await music.GetWorkCreditsAsync(composition.Uuid)).Single().Value;
        credit.Role.Should().Be("writer");
        credit.PersonUuid.Should().BeNull();
        credit.ArtistUuid.Should().BeNull();
        var links = await music.GetCompositionLinksAsync(repertoireSongUuid: first.Uuid);
        var retry = await Ticket(
            input,
            "songs",
            "artist_repertoire",
            first.ArtistUuid,
            global.SubjectUuid
        );
        var match = () =>
            music.PublishAsync(
                retry.WorkUuid,
                new MusicPublication
                {
                    CompositionLinks =
                    [
                        links.Single().Value with
                        {
                            ExpectedRevision = 1,
                            MatchReason = "equal_title",
                        },
                    ],
                }
            );
        await match.Should().ThrowAsync<ArgumentException>();
    }

    [Test]
    public async Task ReviewReplayProtectsTitleAndRejectedClaimsCannotReappearWithNewUuids()
    {
        var input = await fixture.Capture("unresolved title", "songs");
        var source = await fixture.Source();
        var song = Song(await Artist(source.ArtistId), input.Reference);
        var ticket = await Ticket(input, "songs", "artist_repertoire", song.ArtistUuid);
        await music.PublishAsync(
            ticket.WorkUuid,
            new MusicPublication { RepertoireSongs = [song] }
        );
        var correction = new MusicPublication
        {
            RepertoireSongs = [song with { ExpectedRevision = 1, DisplayTitle = "Reviewed title" }],
        };
        var operation = Guid.NewGuid();
        await music.ApplyDecisionAsync(
            operation,
            ticket.SubjectUuid,
            0,
            "replace",
            correction,
            "Provider conflated passages",
            "test-operator"
        );
        (
            await music.ApplyDecisionAsync(
                operation,
                ticket.SubjectUuid,
                0,
                "replace",
                correction,
                "Provider conflated passages",
                "test-operator"
            )
        )
            .Replayed.Should()
            .BeTrue();
        var next = await Ticket(input, "songs", "artist_repertoire", song.ArtistUuid);
        var overwrite = () =>
            music.PublishAsync(
                next.WorkUuid,
                new MusicPublication { RepertoireSongs = [song with { ExpectedRevision = 2 }] }
            );
        await overwrite.Should().ThrowAsync<NormalizationConflictException>();
        (await music.GetRepertoireAsync(song.Uuid))!
            .Value.DisplayTitle.Should()
            .Be("Reviewed title");
        var rejected = Song(song.ArtistUuid, input.Reference) with { DisplayTitle = "Not a song" };
        await music.ApplyDecisionAsync(
            Guid.NewGuid(),
            ticket.SubjectUuid,
            1,
            "reject",
            new MusicPublication { RepertoireSongs = [rejected] },
            "Equipment wording",
            "test-operator"
        );
        var retry = await Ticket(input, "songs", "artist_repertoire", song.ArtistUuid);
        var regenerate = () =>
            music.PublishAsync(
                retry.WorkUuid,
                new MusicPublication { RepertoireSongs = [rejected with { Uuid = Guid.NewGuid() }] }
            );
        await regenerate.Should().ThrowAsync<ArgumentException>();
    }

    [Test]
    public async Task HistoricalContentsSurviveCatalogRemovalAndSegmentIdsCannotMoveToOtherFiles()
    {
        var input = await fixture.Capture("Song", "songs");
        var source = await fixture.Source();
        var song = Song(await Artist(source.ArtistId), input.Reference);
        var repertoire = await Ticket(input, "songs", "artist_repertoire", song.ArtistUuid);
        await music.PublishAsync(
            repertoire.WorkUuid,
            new MusicPublication { RepertoireSongs = [song] }
        );
        var content = Content(source.TrackUuid, song.Uuid, input.Reference);
        var ticket = await Ticket(
            input,
            "songs",
            "recording",
            source.SourceUuid,
            repertoire.SubjectUuid
        );
        await music.PublishAsync(
            ticket.WorkUuid,
            new MusicPublication { TrackContents = [content] }
        );
        var secondTrack = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<Guid>(
                "INSERT INTO source_tracks(source_id,source_set_id,artist_id,track_position,title,slug,mp3_url,uuid,updated_at) SELECT source_id,source_set_id,artist_id,2,title,'other',mp3_url||'?segment='||@uuid::text,@uuid,now() FROM source_tracks WHERE uuid=@original RETURNING uuid",
                new { uuid = Guid.NewGuid(), original = source.TrackUuid }
            )
        );
        var next = await Ticket(
            input,
            "songs",
            "recording",
            source.SourceUuid,
            repertoire.SubjectUuid
        );
        var move = () =>
            music.PublishAsync(
                next.WorkUuid,
                new MusicPublication
                {
                    TrackContents =
                    [
                        content with
                        {
                            ExpectedRevision = 1,
                            SourceTrackUuid = secondTrack,
                        },
                    ],
                }
            );
        await move.Should().ThrowAsync<ArgumentException>();
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "DELETE FROM source_tracks WHERE source_id=@SourceId; DELETE FROM source_sets WHERE source_id=@SourceId; DELETE FROM sources WHERE id=@SourceId",
                source
            )
        );
        (await music.GetTrackContentsAsync(source.TrackUuid))
            .Single()
            .Value.Uuid.Should()
            .Be(content.Uuid);
        await music.PublishAsync(
            next.WorkUuid,
            new MusicPublication
            {
                TrackContents =
                [
                    content with
                    {
                        ExpectedRevision = 1,
                        OriginalText = "Retained Song",
                    },
                ],
            }
        );
        var fresh = await Ticket(
            input,
            "songs",
            "recording",
            source.SourceUuid,
            repertoire.SubjectUuid
        );
        var bind = () =>
            music.PublishAsync(
                fresh.WorkUuid,
                new MusicPublication
                {
                    TrackContents = [content with { Uuid = Guid.NewGuid(), Position = 2 }],
                }
            );
        await bind.Should().ThrowAsync<ArgumentException>();
    }

    [Test]
    public async Task UnconsumedEvidenceAndUnrelatedReviewOwnersCannotWriteAcceptedState()
    {
        var consumed = await fixture.Capture("consumed", "songs");
        var other = await fixture.Capture("unconsumed", "songs");
        var source = await fixture.Source();
        var song = Song(await Artist(source.ArtistId), other.Reference);
        var ticket = await Ticket(consumed, "songs", "artist_repertoire", song.ArtistUuid);
        var stale = () =>
            music.PublishAsync(ticket.WorkUuid, new MusicPublication { RepertoireSongs = [song] });
        await stale.Should().ThrowAsync<ArgumentException>();
        var unrelated = await Ticket(other, "songs", "artist_repertoire", Guid.NewGuid());
        var badReview = () =>
            music.ApplyDecisionAsync(
                Guid.NewGuid(),
                unrelated.SubjectUuid,
                0,
                "accept",
                new MusicPublication { RepertoireSongs = [song] },
                "Wrong owner",
                "test-operator"
            );
        await badReview.Should().ThrowAsync<ArgumentException>();
        (await music.GetRepertoireAsync(song.Uuid)).Should().BeNull();
    }

    [Test]
    public async Task MergeRedirectAndReviewedSplitRetainStableOriginalIdentity()
    {
        var input = await fixture.Capture("two interpretations", "songs");
        var source = await fixture.Source();
        var artist = await Artist(source.ArtistId);
        var a = Song(artist, input.Reference) with
        {
            DisplayTitle = "Old Song",
            Aliases = ["Old Alias"],
        };
        var b = Song(artist, input.Reference) with { DisplayTitle = "New Song" };
        var ticket = await Ticket(input, "songs", "artist_repertoire", artist);
        await music.PublishAsync(
            ticket.WorkUuid,
            new MusicPublication { RepertoireSongs = [a, b] }
        );
        await music.ApplyDecisionAsync(
            Guid.NewGuid(),
            ticket.SubjectUuid,
            0,
            "merge",
            new MusicPublication { Merges = [new MusicMerge("songs", a.Uuid, b.Uuid, 1, 1)] },
            "Same song",
            "test-operator"
        );
        var redirected = await music.GetRepertoireAsync(a.Uuid);
        redirected!.Value.Uuid.Should().Be(b.Uuid);
        redirected.RequestedUuid.Should().Be(a.Uuid);
        redirected.RedirectUuid.Should().Be(b.Uuid);
        (await music.SearchRepertoireAsync(artist, "Old Alias"))
            .Single()
            .Value.Uuid.Should()
            .Be(b.Uuid);
        await music.ApplyDecisionAsync(
            Guid.NewGuid(),
            ticket.SubjectUuid,
            1,
            "split",
            new MusicPublication
            {
                RepertoireSongs = [a with { ExpectedRevision = 2 }],
                Splits = [new MusicSplit("songs", a.Uuid, 2)],
            },
            "Distinct interpretation",
            "test-operator"
        );
        (await music.GetRepertoireAsync(a.Uuid))!.RedirectUuid.Should().BeNull();
    }

    [Test]
    public async Task CompositionMergeRejectsAChangedSurvivorOwnedByAnotherSubject()
    {
        var input = await fixture.Capture("work evidence", "compositions");
        var a = new CompositionChange
        {
            Uuid = Guid.NewGuid(),
            DisplayTitle = "Old work title",
            Evidence = [input.Reference],
        };
        var b = a with { Uuid = Guid.NewGuid(), DisplayTitle = "New work title" };
        var ownerA = await Ticket(input, "compositions", "composition", a.Uuid);
        var ownerB = await Ticket(input, "compositions", "composition", b.Uuid);
        await music.PublishAsync(ownerA.WorkUuid, new MusicPublication { Compositions = [a] });
        await music.PublishAsync(ownerB.WorkUuid, new MusicPublication { Compositions = [b] });
        await music.ApplyDecisionAsync(
            Guid.NewGuid(),
            ownerB.SubjectUuid,
            0,
            "replace",
            new MusicPublication
            {
                Compositions =
                [
                    b with
                    {
                        ExpectedRevision = 1,
                        DisplayTitle = "Corrected work title",
                    },
                ],
            },
            "New evidence",
            "test-operator"
        );
        var merge = new MusicMerge("compositions", a.Uuid, b.Uuid, 1, 1);
        var stale = () =>
            music.ApplyDecisionAsync(
                Guid.NewGuid(),
                ownerA.SubjectUuid,
                0,
                "merge",
                new MusicPublication { Merges = [merge] },
                "Same work",
                "test-operator"
            );
        await stale.Should().ThrowAsync<NormalizationConflictException>();
        (await music.GetCompositionAsync(a.Uuid))!.RedirectUuid.Should().BeNull();
        await music.ApplyDecisionAsync(
            Guid.NewGuid(),
            ownerA.SubjectUuid,
            0,
            "merge",
            new MusicPublication { Merges = [merge with { ExpectedSurvivorRevision = 2 }] },
            "Same work",
            "test-operator"
        );
        (await music.SearchCompositionsAsync("Old work title"))
            .Single()
            .Value.Uuid.Should()
            .Be(b.Uuid);
    }

    [Test]
    public async Task CompositionMergeAndSplitInvalidateSurvivorConsumers()
    {
        var input = await fixture.Capture("two work identities", "compositions");
        var a = new CompositionChange
        {
            Uuid = Guid.NewGuid(),
            DisplayTitle = "Earlier work",
            Evidence = [input.Reference],
        };
        var b = a with { Uuid = Guid.NewGuid(), DisplayTitle = "Surviving work" };
        var ownerA = await Ticket(input, "compositions", "composition", a.Uuid);
        var ownerB = await Ticket(input, "compositions", "composition", b.Uuid);
        await music.PublishAsync(ownerA.WorkUuid, new MusicPublication { Compositions = [a] });
        await music.PublishAsync(ownerB.WorkUuid, new MusicPublication { Compositions = [b] });
        var source = await fixture.Source();
        var song = Song(await Artist(source.ArtistId), input.Reference);
        var initial = await Ticket(
            input,
            "songs",
            "artist_repertoire",
            song.ArtistUuid,
            ownerB.SubjectUuid
        );
        await music.PublishAsync(
            initial.WorkUuid,
            new MusicPublication { RepertoireSongs = [song] }
        );
        var beforeMerge = await Ticket(
            input,
            "songs",
            "artist_repertoire",
            song.ArtistUuid,
            ownerB.SubjectUuid
        );
        var merge = new MusicPublication { Merges = [new("compositions", a.Uuid, b.Uuid, 1, 1)] };
        var mergeOperation = Guid.NewGuid();
        await music.ApplyDecisionAsync(
            mergeOperation,
            ownerA.SubjectUuid,
            0,
            "merge",
            merge,
            "Equivalent works",
            "test-operator"
        );
        (
            await music.ApplyDecisionAsync(
                mergeOperation,
                ownerA.SubjectUuid,
                0,
                "merge",
                merge,
                "Equivalent works",
                "test-operator"
            )
        )
            .Replayed.Should()
            .BeTrue();
        (await fixture.Work.GetPendingSubjectsAsync())
            .Should()
            .Contain(x => x.SubjectUuid == initial.SubjectUuid);
        (
            await music.PublishAsync(
                beforeMerge.WorkUuid,
                new MusicPublication
                {
                    RepertoireSongs =
                    [
                        song with
                        {
                            ExpectedRevision = 1,
                            DisplayTitle = "Stale merge consumer",
                        },
                    ],
                }
            )
        )
            .Status.Should()
            .Be(NormalizationPublicationStatus.Stale);
        (await music.GetRepertoireAsync(song.Uuid))!.Value.DisplayTitle.Should().Be("Song");
        (await fixture.Work.GetSubjectAsync(ownerB.SubjectUuid))!.EditorialRevision.Should().Be(1);
        var refresh = await Ticket(
            input,
            "songs",
            "artist_repertoire",
            song.ArtistUuid,
            ownerB.SubjectUuid
        );
        await music.PublishAsync(refresh.WorkUuid, new MusicPublication());
        var beforeSplit = await Ticket(
            input,
            "songs",
            "artist_repertoire",
            song.ArtistUuid,
            ownerB.SubjectUuid
        );
        var split = new MusicPublication
        {
            Compositions = [a with { ExpectedRevision = 2 }],
            Splits = [new("compositions", a.Uuid, 2, [new(b.Uuid, 1)])],
        };
        await music.ApplyDecisionAsync(
            Guid.NewGuid(),
            ownerA.SubjectUuid,
            1,
            "split",
            split,
            "Distinct works",
            "test-operator"
        );
        (await fixture.Work.GetSubjectAsync(ownerB.SubjectUuid))!.EditorialRevision.Should().Be(2);
        (await fixture.Work.GetPendingSubjectsAsync())
            .Should()
            .Contain(x => x.SubjectUuid == initial.SubjectUuid);
        (await music.PublishAsync(beforeSplit.WorkUuid, new MusicPublication()))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Stale);
        (await music.GetCompositionAsync(a.Uuid))!.RedirectUuid.Should().BeNull();
    }

    [Test]
    public async Task CompositionSplitRequiresRegisteredResultOwnersBeforeTheReview()
    {
        var input = await fixture.Capture("separate works", "compositions");
        var a = new CompositionChange
        {
            Uuid = Guid.NewGuid(),
            DisplayTitle = "Retained work",
            Evidence = [input.Reference],
        };
        var b = a with { Uuid = Guid.NewGuid(), DisplayTitle = "Separated work" };
        var ownerA = await Ticket(input, "compositions", "composition", a.Uuid);
        await music.PublishAsync(ownerA.WorkUuid, new MusicPublication { Compositions = [a] });
        var split = new MusicPublication
        {
            Compositions = [a with { ExpectedRevision = 1 }, b],
            Splits = [new("compositions", a.Uuid, 1, [new(b.Uuid, 0)])],
        };
        var unregistered = () =>
            music.ApplyDecisionAsync(
                Guid.NewGuid(),
                ownerA.SubjectUuid,
                0,
                "split",
                split,
                "Independent work",
                "test-operator"
            );
        await unregistered.Should().ThrowAsync<ArgumentException>();
        (await music.GetCompositionAsync(a.Uuid))!.Revision.Should().Be(1);
        var ownerB = await Ticket(input, "compositions", "composition", b.Uuid);
        await music.ApplyDecisionAsync(
            Guid.NewGuid(),
            ownerA.SubjectUuid,
            0,
            "split",
            split,
            "Independent work",
            "test-operator"
        );
        (await music.GetCompositionAsync(b.Uuid))!.Value.DisplayTitle.Should().Be("Separated work");
        (await fixture.Work.GetSubjectAsync(ownerB.SubjectUuid))!.EditorialRevision.Should().Be(1);
    }

    [Test]
    public async Task ResolvedWorkCreditsConsumeThePersonOwner()
    {
        var input = await fixture.Capture("written by Pat", "compositions");
        var person = new CanonicalPerson(Guid.NewGuid(), "Pat");
        var personOwner = await Ticket(input, "people", "person", person.Uuid);
        var people = new NormalizedPeopleService(fixture.Db, fixture.Work);
        await people.PublishAsync(
            personOwner.WorkUuid,
            new PeoplePublication(
                [person],
                [new(Guid.NewGuid(), person.Uuid, "Pat", input.Reference)],
                [],
                []
            )
        );
        var composition = new CompositionChange
        {
            Uuid = Guid.NewGuid(),
            DisplayTitle = "Work",
            Evidence = [input.Reference],
        };
        var credit = new WorkCreditChange
        {
            Uuid = Guid.NewGuid(),
            CompositionUuid = composition.Uuid,
            PersonUuid = person.Uuid,
            OriginalText = "written by Pat",
            Evidence = [input.Reference],
        };
        var owner = await Ticket(input, "compositions", "composition", composition.Uuid);
        var publication = new MusicPublication
        {
            Compositions = [composition],
            WorkCredits = [credit],
        };
        var missing = () => music.PublishAsync(owner.WorkUuid, publication);
        await missing.Should().ThrowAsync<ArgumentException>();
        (await music.GetCompositionAsync(composition.Uuid)).Should().BeNull();
        var consumed = await Ticket(
            input,
            "compositions",
            "composition",
            composition.Uuid,
            personOwner.SubjectUuid
        );
        await music.PublishAsync(consumed.WorkUuid, publication);
        (await music.GetWorkCreditsAsync(composition.Uuid))
            .Single()
            .Value.PersonUuid.Should()
            .Be(person.Uuid);
    }

    private async Task<NormalizationWorkTicket> Ticket(
        DomainInput input,
        string domain,
        string kind,
        Guid entity,
        params Guid[] dependencies
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
            dependencies,
            generation
        );
    }

    private Task<Guid> Artist(int id) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<Guid>("SELECT uuid FROM artists WHERE id=@id", new { id })
        );

    private Task<Guid> Show(int artist, string key) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<Guid>(
                "INSERT INTO shows(artist_id,date,display_date,uuid,updated_at,performance_key,is_legacy_survivor) VALUES(@artist,'2026-10-17','2026-10-17',@uuid,now(),@key,false) RETURNING uuid",
                new
                {
                    artist,
                    key,
                    uuid = Guid.NewGuid(),
                }
            )
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

    private static SetlistEditionChange Edition(
        Guid artist,
        NormalizedEvidenceReference evidence
    ) =>
        new()
        {
            Uuid = Guid.NewGuid(),
            ArtistUuid = artist,
            LiteralDate = "2026-10-17",
            Provider = "fixture",
            ResourceType = "setlist",
            ProviderKey = Guid.NewGuid().ToString(),
            Evidence = [evidence],
        };

    private static SongOccurrenceChange Occurrence(
        Guid edition,
        Guid song,
        NormalizedEvidenceReference evidence
    ) =>
        new()
        {
            Uuid = Guid.NewGuid(),
            EditionUuid = edition,
            RepertoireSongUuid = song,
            OriginalText = "Song",
            Evidence = [evidence],
        };

    private static TrackContentChange Content(
        Guid track,
        Guid song,
        NormalizedEvidenceReference evidence
    ) =>
        new()
        {
            Uuid = Guid.NewGuid(),
            SourceTrackUuid = track,
            RepertoireSongUuid = song,
            Position = 1,
            OriginalText = "Song",
            Evidence = [evidence],
        };
}
