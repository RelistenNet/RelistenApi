using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

[TestFixture]
public sealed partial class TestShadowRepertoireAdapter
{
    private static readonly Guid ArtistUuid = Guid.Parse("aeec435d-a70d-41b5-93ca-e65791f77839");
    private static readonly Guid FileUuid = Guid.Parse("0296bd66-62a3-4676-9b1b-899286f004b1");
    private static readonly Guid SourceUuid = Guid.Parse("fd00a3cf-0a27-44e7-9fca-50c4fc17c676");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    [Test]
    public void UniqueArtistDictionaryTitleRetainsLegacyIdentityAndActualMatcherEvidence()
    {
        var adapter = new ShadowRepertoireAdapter();
        var songUuid = Guid.NewGuid();
        adapter.Observe(Song(songUuid, "Café Song"));

        var result = adapter.Process(Track("Cafe\u0301 Song")).Single();
        var track = (ShadowRepertoireTrack)result.Proposal!;
        var segment = track.Segments.Single();

        result.Outcome.Should().Be("proposed");
        segment.Song!.LegacySongUuid.Should().Be(songUuid);
        segment.Song.EntityKey.Should().Be(ShadowRepertoireAdapter.KnownEntityKey(songUuid));
        track.FileUuid.Should().Be(FileUuid);
        track.SourceUuid.Should().Be(SourceUuid);
        segment.ComparisonDiagnostics.Single().Evidence.Single().Quote.Should().Be("Café Song");
        segment
            .ComparisonDiagnostics.Single()
            .Comparison.Should()
            .BeEquivalentTo(
                new IdentityCandidateMatcher().CompareRepertoire(
                    new RepertoireMatchRecord(null, ArtistUuid, [new("Cafe\u0301 Song")], []),
                    new RepertoireMatchRecord(songUuid, ArtistUuid, [new("Café Song")], [])
                )
            );
        segment.Evidence.Quote.Should().Be("Cafe\u0301 Song");
    }

    [Test]
    public void ClearUnknownSongGetsArtistScopedProvisionalIdentityWithoutChangingPlaybackOwnership()
    {
        var adapter = new ShadowRepertoireAdapter();
        var first = (ShadowRepertoireTrack)adapter.Process(Track("New Horizon")).Single().Proposal!;
        var otherArtist = Guid.NewGuid();
        var second = (ShadowRepertoireTrack)
            adapter.Process(Track("New Horizon", otherArtist)).Single().Proposal!;

        first.Segments.Single().Song!.LegacySongUuid.Should().BeNull();
        first.Segments.Single().Song!.Basis.Should().Be("title_derived");
        first
            .Segments.Single()
            .Song!.EntityKey.Should()
            .NotBe(second.Segments.Single().Song!.EntityKey);
        first.SourceUuid.Should().Be(SourceUuid);
        first.Extraction!.Segments.Single().StartMs.Should().BeNull();
    }

    [Test]
    public void MissingArtistCannotCreateASharedProvisionalSongIdentity()
    {
        var result = new ShadowRepertoireAdapter().Process(Track("Morning", Guid.Empty)).Single();
        var track = (ShadowRepertoireTrack)result.Proposal!;

        result.Outcome.Should().Be("unresolved");
        track.Segments.Single().Song.Should().BeNull();
        track.Segments.Single().Reason.Should().Be("missing_artist_identity");
        result.EntityKeys.Should().BeEmpty();
    }

    [Test]
    public void DuplicateDictionaryTitlesRemainAmbiguousWhileOtherCompoundMentionIsRetained()
    {
        var adapter = new ShadowRepertoireAdapter();
        adapter.Observe(Song(Guid.NewGuid(), "Echoes"));
        adapter.Observe(Song(Guid.NewGuid(), "Echoes"));

        var result = adapter.Process(Track("Echoes > New Horizon")).Single();
        var track = (ShadowRepertoireTrack)result.Proposal!;

        result.Outcome.Should().Be("unresolved");
        track.Segments[0].Song.Should().BeNull();
        track.Segments[0].CandidateSongs.Should().HaveCount(2);
        track.Segments[0].ComparisonDiagnostics.Should().HaveCount(2);
        track.Segments[1].Outcome.Should().Be("proposed");
        track.Segments[1].Song!.Name.Should().Be("New Horizon");
        track.Extraction!.Segments[0].TransitionAfter.Should().Be(TitleTransition.SegueMarker);
        result.EntityKeys.Should().HaveCount(3);
    }

    [Test]
    public void SetlistMembershipAddsKnownSongEvenWithoutStandaloneSongSampleAndSurvivesDocumentDisposal()
    {
        var adapter = new ShadowRepertoireAdapter();
        var songUuid = Guid.NewGuid();
        var setlistUuid = Guid.NewGuid();
        using (
            var document = JsonDocument.Parse(
                JsonSerializer.Serialize(
                    new
                    {
                        kind = "setlist_sample",
                        row = new { uuid = setlistUuid },
                        unordered_memberships = new[]
                        {
                            new
                            {
                                song = new
                                {
                                    uuid = songUuid,
                                    artist_id = 1,
                                    name = "Reprise",
                                },
                            },
                        },
                    }
                )
            )
        )
        {
            adapter.Observe(document.RootElement);
        }

        var track = (ShadowRepertoireTrack)adapter.Process(Track("Reprise")).Single().Proposal!;

        track.Segments.Single().Song!.LegacySongUuid.Should().Be(songUuid);
        track
            .Segments.Single()
            .ComparisonDiagnostics.Single()
            .Evidence.Single()
            .JsonPointer.Should()
            .Be("/unordered_memberships/0/song/name");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RepeatedMembershipsUseTheDirectDictionaryWitnessRegardlessOfObservationOrder(
        bool dictionaryFirst
    )
    {
        var adapter = new ShadowRepertoireAdapter();
        var songUuid = Guid.NewGuid();
        var dictionary = Song(songUuid, "Reprise");
        if (dictionaryFirst)
        {
            adapter.Observe(dictionary);
        }
        var setlist = Memberships(songUuid, 1200);
        adapter.Observe(setlist);
        if (!dictionaryFirst)
        {
            adapter.Observe(dictionary);
        }

        var track = (ShadowRepertoireTrack)adapter.Process(Track("Reprise")).Single().Proposal!;

        var candidate = track.Segments.Single().ComparisonDiagnostics.Single();
        candidate.Song.LegacySongUuid.Should().Be(songUuid);
        candidate
            .Evidence.Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                new ShadowRepertoireDictionaryEvidence(
                    "song_sample:" + songUuid.ToString("D"),
                    "/row/name",
                    "Reprise"
                )
            );
        track.WholeFileDictionaryCandidates.Single().Evidence.Should().Equal(candidate.Evidence);
    }

    [Test]
    public void CompactMembershipWitnessDoesNotRemoveRepeatedSetlistMemberships()
    {
        var adapter = new ShadowRepertoireAdapter();
        var songUuid = Guid.NewGuid();
        var setlist = Memberships(songUuid, 1200);
        adapter.Observe(setlist);

        var track = (ShadowRepertoireTrack)adapter.Process(Track("Reprise")).Single().Proposal!;
        var membershipResult = new ShadowPerformanceAdapter()
            .Process(new(setlist, null, new(1, ArtistUuid, "Artist", []), "catalog:unattributed"))
            .Single(result => result.Domain == "setlists");

        track
            .Segments.Single()
            .ComparisonDiagnostics.Single()
            .Evidence.Should()
            .ContainSingle()
            .Which.JsonPointer.Should()
            .Be("/unordered_memberships/0/song/name");
        var account = (ShadowSetlistAccount)membershipResult.Proposal!;
        account
            .Memberships.Should()
            .HaveCount(1200)
            .And.OnlyContain(member => member.LegacySongUuid == songUuid);
        account.Ordered.Should().BeFalse();
    }

    [TestCase(false)]
    [TestCase(true)]
    public void LiteralMembershipAliasSurvivesThePreferredDirectDictionaryWitness(
        bool dictionaryFirst
    )
    {
        var adapter = new ShadowRepertoireAdapter();
        var songUuid = Guid.NewGuid();
        var dictionary = Song(songUuid, "Stage Banter!");
        var memberships = Memberships(songUuid, 1200, "Stage Banter");
        foreach (
            var record in dictionaryFirst
                ? new[] { dictionary, memberships }
                : new[] { memberships, dictionary }
        )
        {
            adapter.Observe(record);
        }

        foreach (var title in new[] { "Stage Banter", "Stage Banter!" })
        {
            var result = adapter.Process(Track(title)).Single();
            var track = (ShadowRepertoireTrack)result.Proposal!;
            var candidate = track.Segments.Single().ComparisonDiagnostics.Single();

            result.Outcome.Should().Be("proposed");
            result.EntityKeys.Should().Equal(ShadowRepertoireAdapter.KnownEntityKey(songUuid));
            candidate.Song.Name.Should().Be("Stage Banter!");
            candidate.Song.LegacySongUuid.Should().Be(songUuid);
            candidate.Evidence.Should().HaveCount(2);
            candidate
                .Evidence.Should()
                .Contain(
                    new ShadowRepertoireDictionaryEvidence(
                        "song_sample:" + songUuid.ToString("D"),
                        "/row/name",
                        "Stage Banter!"
                    )
                );
            candidate
                .Evidence.Should()
                .Contain(
                    new ShadowRepertoireDictionaryEvidence(
                        "setlist_sample:"
                            + memberships
                                .GetProperty("row")
                                .GetProperty("uuid")
                                .GetGuid()
                                .ToString("D"),
                        "/unordered_memberships/0/song/name",
                        "Stage Banter"
                    )
                );
        }
    }

    [Test]
    public void CaseWhitespaceAndCanonicalUnicodeVariantsShareOneLiteralWitness()
    {
        var adapter = new ShadowRepertoireAdapter();
        var songUuid = Guid.NewGuid();
        adapter.Observe(Memberships(songUuid, 1200, "  CAFE\u0301 SONG  "));
        adapter.Observe(Song(songUuid, "Café Song"));

        var track = (ShadowRepertoireTrack)adapter.Process(Track("café song")).Single().Proposal!;
        var candidate = track.Segments.Single().ComparisonDiagnostics.Single();

        candidate.Song.LegacySongUuid.Should().Be(songUuid);
        candidate.Song.Name.Should().Be("Café Song");
        candidate
            .Evidence.Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                new ShadowRepertoireDictionaryEvidence(
                    "song_sample:" + songUuid.ToString("D"),
                    "/row/name",
                    "Café Song"
                )
            );
    }

    [Test]
    public void UnresolvedProducerKeepsSupportedCompoundMentionsWithoutClaimingWholeFileResolution()
    {
        var result = new ShadowRepertoireAdapter().Process(Track("Unknown > Morning")).Single();
        var track = (ShadowRepertoireTrack)result.Proposal!;

        result.Outcome.Should().Be("unresolved");
        track.IsResolvedByExtractor.Should().BeFalse();
        track.Segments[0].Outcome.Should().Be("unresolved");
        track.Segments[0].Song.Should().BeNull();
        track.Segments[1].Song!.Name.Should().Be("Morning");
        result
            .EntityKeys.Should()
            .ContainSingle()
            .Which.Should()
            .Be(track.Segments[1].Song!.EntityKey);
    }

    [TestCase("Tuning", "not_applicable")]
    [TestCase("Banter,", "not_applicable")]
    [TestCase("band introductions", "not_applicable")]
    [TestCase("Unknown", "unresolved")]
    [TestCase("Jam", "unresolved")]
    [TestCase("T08", "unresolved")]
    [TestCase("Morning/Evening", "unresolved")]
    [TestCase("2010-01-01.some-recording.t03", "unresolved")]
    public void ControlAndUnclearTitlesDoNotCreateProvisionalSongs(string title, string expected)
    {
        var result = new ShadowRepertoireAdapter().Process(Track(title)).Single();
        var track = (ShadowRepertoireTrack)result.Proposal!;

        result.Outcome.Should().Be(expected);
        track.Segments.Should().OnlyContain(segment => segment.Song == null);
        result.EntityKeys.Should().BeEmpty();
    }

    [Test]
    public void WholeFileDictionaryIdentityDoesNotReplaceCompoundTitleMentions()
    {
        var adapter = new ShadowRepertoireAdapter();
        var compoundUuid = Guid.NewGuid();
        adapter.Observe(Song(compoundUuid, "Morning > Evening"));

        var track = (ShadowRepertoireTrack)
            adapter.Process(Track("Morning > Evening")).Single().Proposal!;

        track.WholeFileDictionaryCandidates.Single().Song.LegacySongUuid.Should().Be(compoundUuid);
        track.Segments.Should().HaveCount(2);
        track.Segments.Should().OnlyContain(segment => segment.Song!.LegacySongUuid == null);
        track
            .Segments.Select(segment => segment.Evidence.Quote)
            .Should()
            .Equal("Morning", "Evening");
    }

    [Test]
    public void ProviderSetlistMentionUsesSharedDictionaryWithoutInventingAPlaybackFile()
    {
        var adapter = new ShadowRepertoireAdapter();
        var songUuid = Guid.NewGuid();
        adapter.Observe(Song(songUuid, "Morning"));
        var evidence = new NormalizationTextEvidence("setlist", "/songs/0/title", "Morning", 0, 7);
        var mention = new TrackTitleSegment(
            1,
            TrackContentKind.Song,
            "Morning",
            evidence,
            TitleCoverage.Unknown,
            TitleTransition.Unknown
        );

        var result = adapter.ResolveTitleMention(1, ArtistUuid, mention);
        var otherArtist = adapter.ResolveTitleMention(2, Guid.NewGuid(), mention);

        result.Song!.LegacySongUuid.Should().Be(songUuid);
        result.Evidence.Should().Be(evidence);
        otherArtist.Song!.LegacySongUuid.Should().BeNull();
        otherArtist.Song!.Basis.Should().Be("title_derived");
    }

    [Test]
    public void ImportedControlLabelDoesNotTurnDecoratedBanterIntoASong()
    {
        var adapter = new ShadowRepertoireAdapter();
        adapter.Observe(Song(Guid.NewGuid(), "Banter"));

        var result = adapter.Process(Track("Banter,")).Single();
        var track = (ShadowRepertoireTrack)result.Proposal!;

        result.Outcome.Should().Be("not_applicable");
        track.Segments.Single().Song.Should().BeNull();
        track.WholeFileDictionaryCandidates.Should().ContainSingle();
    }

    [Test]
    public void MissingExtractionStaysUnresolvedAndWrongFileOwnershipFailsInsteadOfReusingAnotherTitle()
    {
        var adapter = new ShadowRepertoireAdapter();
        var input = Track("New Horizon");
        var missing = adapter.Process(input with { Extraction = null }).Single();
        missing.Outcome.Should().Be("unresolved");
        ((ShadowRepertoireTrack)missing.Proposal!).Extraction.Should().BeNull();

        var wrong = JsonSerializer.SerializeToElement(
            new
            {
                resourceUuid = Guid.NewGuid(),
                artistUuid = ArtistUuid,
                titleExtraction = new TrackTitleExtractor().Extract(ArtistUuid, "New Horizon"),
            },
            Json
        );
        var run = () => adapter.Process(input with { Extraction = wrong }).ToArray();
        run.Should().Throw<InvalidDataException>();
    }

    [Test]
    public void AlteredUnicodeSpanCannotSupportAProvisionalSong()
    {
        var extraction = new TrackTitleExtractor().Extract(ArtistUuid, "🌙 New Horizon");
        var badSegment = extraction.Segments.Single() with
        {
            Evidence = extraction.Segments.Single().Evidence with { StartCodePoint = 1 },
        };
        var bad = JsonSerializer.SerializeToElement(
            new
            {
                resourceUuid = FileUuid,
                artistUuid = ArtistUuid,
                titleExtraction = extraction with
                {
                    Segments = new[] { badSegment },
                },
            },
            Json
        );

        var run = () =>
            new ShadowRepertoireAdapter()
                .Process(Track("🌙 New Horizon") with { Extraction = bad })
                .ToArray();
        run.Should().Throw<InvalidDataException>();
    }

    [Test]
    public void DifferentSegmentTitleCannotUseAnotherTitlesValidEvidence()
    {
        var extraction = new TrackTitleExtractor().Extract(ArtistUuid, "Morning");
        var bad = JsonSerializer.SerializeToElement(
            new
            {
                resourceUuid = FileUuid,
                artistUuid = ArtistUuid,
                titleExtraction = extraction with
                {
                    Segments = new[] { extraction.Segments.Single() with { Title = "Evening" } },
                },
            },
            Json
        );

        var run = () =>
            new ShadowRepertoireAdapter()
                .Process(Track("Morning") with { Extraction = bad })
                .ToArray();
        run.Should().Throw<InvalidDataException>();
    }

    [TestCase("banter - inviting out Funky Dawgs", "not_applicable")]
    [TestCase("14 Stage Banter", "not_applicable")]
    [TestCase("gatrios2011-12-17s2t05.matrix.kindrecordings.mp3", "unresolved")]
    [TestCase("01 Set 1 Part 1", "unresolved")]
    [TestCase("Music Never Stopped (09:23.55)", "unresolved")]
    [TestCase("Banter - A Story", "unresolved")]
    [TestCase("LOM 750409l side 2 fast.mp3", "unresolved")]
    [TestCase("ABC19750409s2t05.flac", "unresolved")]
    [TestCase("750409 track 03.wav", "unresolved")]
    [TestCase("intro and soundcheck", "unresolved")]
    [TestCase("soundcheck / introduction", "unresolved")]
    [TestCase("introduction / interview", "unresolved")]
    [TestCase("sound check & interview", "unresolved")]
    [TestCase("untitled", "unresolved")]
    [TestCase("Untitled Track #2", "unresolved")]
    public void ObservedRecordingLabelsDoNotCreateProvisionalSongs(string title, string outcome)
    {
        var input = Track(title);
        var original = new TrackTitleExtractor().Extract(ArtistUuid, title);
        var result = new ShadowRepertoireAdapter().Process(input).Single();
        var track = (ShadowRepertoireTrack)result.Proposal!;

        result.Outcome.Should().Be(outcome);
        result.EntityKeys.Should().BeEmpty();
        track.Segments.Should().OnlyContain(segment => segment.Song == null);
        track.OriginalTitle.Should().Be(title);
        track.Extraction.Should().BeEquivalentTo(original);
        track.FileUuid.Should().Be(FileUuid);
        track.SourceUuid.Should().Be(SourceUuid);
        track
            .Segments.Select(segment => segment.Evidence)
            .Should()
            .BeEquivalentTo(original.Segments.Select(segment => segment.Evidence));
    }

    [TestCase("soundcheck")]
    [TestCase("interview")]
    public void PartialControlPassageKeepsItsSpanAndMusicalNeighbors(string label)
    {
        var adapter = new ShadowRepertoireAdapter();
        var morning = Guid.NewGuid();
        var evening = Guid.NewGuid();
        adapter.Observe(Song(morning, "Morning"));
        adapter.Observe(Song(evening, "Evening"));
        var title = "Morning > " + label + " (partial) > Evening";
        var original = new TrackTitleExtractor().Extract(ArtistUuid, title);

        var result = adapter.Process(Track(title)).Single();
        var track = (ShadowRepertoireTrack)result.Proposal!;

        result.Outcome.Should().Be("unresolved");
        track.Segments.Should().HaveCount(3);
        track.Segments[0].Song!.LegacySongUuid.Should().Be(morning);
        track.Segments[1].Song.Should().BeNull();
        track.Segments[1].Outcome.Should().Be("unresolved");
        track.Segments[1].Reason.Should().Be("shadow_guard_generic_control_passage");
        track.Segments[1].Evidence.Quote.Should().Be(label);
        track.Segments[2].Song!.LegacySongUuid.Should().Be(evening);
        track.Extraction.Should().BeEquivalentTo(original);
        track.Extraction!.Segments[1].Coverage.Should().Be(TitleCoverage.Partial);
        track.FileUuid.Should().Be(FileUuid);
        track.SourceUuid.Should().Be(SourceUuid);
        result
            .EntityKeys.Should()
            .BeEquivalentTo([
                ShadowRepertoireAdapter.KnownEntityKey(morning),
                ShadowRepertoireAdapter.KnownEntityKey(evening),
            ]);
    }

    [Test]
    public void NumericFootnoteCannotBecomeAnotherSongAfterAKnownSong()
    {
        var adapter = new ShadowRepertoireAdapter();
        var song = Guid.NewGuid();
        adapter.Observe(Song(song, "Little Betty Boop"));
        var result = adapter.Process(Track("Little Betty Boop->(1,6)")).Single();
        var track = (ShadowRepertoireTrack)result.Proposal!;

        result.Outcome.Should().Be("unresolved");
        track.Segments.Should().HaveCount(2);
        track.Segments[0].Song!.LegacySongUuid.Should().Be(song);
        track.Segments[1].Song.Should().BeNull();
        track.Segments[1].Evidence.Quote.Should().Be("(1,6)");
        track.Segments[1].Outcome.Should().Be("unresolved");
        result.EntityKeys.Should().Equal(ShadowRepertoireAdapter.KnownEntityKey(song));
    }

    [Test]
    public void DurationOnOneCompoundMentionDoesNotHideTheOtherKnownSong()
    {
        var adapter = new ShadowRepertoireAdapter();
        var song = Guid.NewGuid();
        adapter.Observe(Song(song, "Lazy Lightnin'"));
        var title = "Lazy Lightnin' > Supplication (12:55)";
        var result = adapter.Process(Track(title)).Single();
        var track = (ShadowRepertoireTrack)result.Proposal!;

        result.Outcome.Should().Be("unresolved");
        track.Segments[0].Song!.LegacySongUuid.Should().Be(song);
        track.Segments[1].Song.Should().BeNull();
        track.Segments[1].Evidence.Quote.Should().Be("Supplication (12:55)");
        track.Extraction!.OriginalTitle.Should().Be(title);
        result.EntityKeys.Should().Equal(ShadowRepertoireAdapter.KnownEntityKey(song));
    }

    [Test]
    public void TransitionResidueStaysUnresolvedWithoutChangingTheProducerSpan()
    {
        var result = new ShadowRepertoireAdapter()
            .Process(Track("Here We Go <-> In the Air Tonight"))
            .Single();
        var track = (ShadowRepertoireTrack)result.Proposal!;

        result.Outcome.Should().Be("unresolved");
        track.Segments[0].Song.Should().BeNull();
        track.Segments[0].Evidence.Quote.Should().Be("Here We Go <");
        track.Segments[1].Song!.Name.Should().Be("In the Air Tonight");
        track.Extraction!.Segments[0].Evidence.Quote.Should().Be("Here We Go <");
    }

    [TestCase("banter - inviting out Funky Dawgs")]
    [TestCase("14 Stage Banter")]
    [TestCase("gatrios2011-12-17s2t05.matrix.kindrecordings")]
    [TestCase("Set 1 Part 1")]
    [TestCase("(1,6)")]
    [TestCase("Music Never Stopped (09:23.55)")]
    [TestCase("Stage Banter")]
    [TestCase("intro and soundcheck")]
    [TestCase("soundcheck")]
    [TestCase("interview")]
    [TestCase("untitled")]
    [TestCase("LOM 750409l side 2 fast")]
    public void LiteralDictionaryIdentitySurvivesRecordingLabelGuards(string title)
    {
        var adapter = new ShadowRepertoireAdapter();
        var song = Guid.NewGuid();
        adapter.Observe(Song(song, title));
        var result = adapter.Process(Track(title)).Single();
        var track = (ShadowRepertoireTrack)result.Proposal!;

        result.Outcome.Should().Be("proposed");
        track.Segments.Single().Song!.LegacySongUuid.Should().Be(song);
        track.Segments.Single().Song!.Basis.Should().Be("captured_artist_dictionary");
        track
            .Extraction.Should()
            .BeEquivalentTo(new TrackTitleExtractor().Extract(ArtistUuid, title));
        result.EntityKeys.Should().Contain(ShadowRepertoireAdapter.KnownEntityKey(song));
    }

    [TestCase("Banter Song")]
    [TestCase("Stage Banter Blues")]
    [TestCase("Set Me Free")]
    [TestCase("Set the Controls for the Heart of the Sun")]
    [TestCase("Part of Me")]
    [TestCase("The Matrix")]
    [TestCase("Soundcheck Blues")]
    [TestCase("An Interview")]
    [TestCase("Interview with the Moon")]
    [TestCase("Untitled Melody")]
    [TestCase("Talk Talk")]
    public void WordsThatAlsoAppearInRecordingLabelsDoNotSuppressMusicalTitles(string title)
    {
        var result = new ShadowRepertoireAdapter().Process(Track(title)).Single();
        var track = (ShadowRepertoireTrack)result.Proposal!;

        result.Outcome.Should().Be("proposed");
        track.Segments.Single().Song!.Name.Should().Be(title);
        track.Segments.Single().Song!.Basis.Should().Be("title_derived");
    }

    [Test]
    public void ExistingNormalizedDictionaryCorrespondenceSurvivesTransitionResidue()
    {
        var adapter = new ShadowRepertoireAdapter();
        var song = Guid.NewGuid();
        adapter.Observe(Song(song, "Here We Go"));
        var result = adapter.Process(Track("Here We Go <-> In the Air Tonight")).Single();
        var track = (ShadowRepertoireTrack)result.Proposal!;

        result.Outcome.Should().Be("proposed");
        track.Segments[0].Song!.LegacySongUuid.Should().Be(song);
        track.Segments[0].Evidence.Quote.Should().Be("Here We Go <");
        track.Segments[1].Song!.Name.Should().Be("In the Air Tonight");
    }

    private static JsonElement Song(Guid uuid, string name) =>
        JsonSerializer.SerializeToElement(
            new
            {
                kind = "song_sample",
                row = new
                {
                    uuid,
                    name,
                    artist_id = 1,
                },
            }
        );

    private static JsonElement Memberships(Guid songUuid, int count, string title = "Reprise") =>
        JsonSerializer.SerializeToElement(
            new
            {
                kind = "setlist_sample",
                row = new { uuid = Guid.NewGuid(), artist_id = 1 },
                unordered_memberships = Enumerable
                    .Range(0, count)
                    .Select(_ => new
                    {
                        song_id = 1,
                        song = new
                        {
                            uuid = songUuid,
                            artist_id = 1,
                            name = title,
                        },
                    })
                    .ToArray(),
            }
        );

    private static ShadowCatalogInput Track(string title, Guid? artistUuid = null)
    {
        var artist = artistUuid ?? ArtistUuid;
        var record = JsonSerializer.SerializeToElement(
            new
            {
                kind = "track_sample",
                row = new
                {
                    uuid = FileUuid,
                    title,
                    artist_id = 1,
                },
                source_context = new { uuid = SourceUuid },
            }
        );
        var extraction = JsonSerializer.SerializeToElement(
            new
            {
                resourceUuid = FileUuid,
                artistUuid = artist,
                titleExtraction = new TrackTitleExtractor().Extract(artist, title),
            },
            Json
        );
        return new(record, extraction, new(1, artist, "Artist", ["archive.org"]), "archive.org");
    }
}
