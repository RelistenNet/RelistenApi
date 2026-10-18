using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

public sealed partial class TestShadowRepertoireAdapter
{
    [Test]
    public void CompoundPunctuationResidueCannotBorrowAnEmptyDictionaryIdentity()
    {
        const string title = "Free > Stash 7/2/97 jam > Mario Bros Theme> #";
        const string audio = "https://audio.example.test/retained-track.mp3";
        var blank = Guid.Parse("b0958a2f-2eb9-9254-e3cc-96cfc5501e6f");
        var punctuation = Guid.NewGuid();
        var free = Guid.NewGuid();
        var stash = Guid.NewGuid();
        var mario = Guid.NewGuid();
        var dictionary = new[]
        {
            Song(blank, ""),
            Song(punctuation, "!!!"),
            Song(free, "Free"),
            Song(stash, "Stash 7/2/97 jam"),
            Song(mario, "Mario Bros Theme"),
        };
        var adapter = new ShadowRepertoireAdapter();
        var performance = new ShadowPerformanceAdapter();
        foreach (var song in dictionary)
        {
            adapter.Observe(song);
            performance.Observe(song);
        }
        var input = Track(title) with
        {
            Record = JsonSerializer.SerializeToElement(
                new
                {
                    kind = "track_sample",
                    row = new
                    {
                        uuid = FileUuid,
                        title,
                        artist_id = 1,
                        source_id = 7L,
                        mp3_url = audio,
                    },
                    source_context = new
                    {
                        id = 7L,
                        uuid = SourceUuid,
                        artist_id = 1,
                    },
                }
            ),
        };
        var original = new TrackTitleExtractor().Extract(ArtistUuid, title);

        var result = adapter.Process(input).Single();
        var track = (ShadowRepertoireTrack)result.Proposal!;

        result.Outcome.Should().Be("unresolved");
        track.Segments.Should().HaveCount(4);
        track
            .Segments.Take(3)
            .Select(segment => segment.Song!.LegacySongUuid)
            .Should()
            .Equal(free, stash, mario);
        var residue = track.Segments[3];
        residue.Outcome.Should().Be("unresolved");
        residue.Reason.Should().Be("shadow_guard_title_needs_interpretation");
        residue.Song.Should().BeNull();
        residue.CandidateSongs.Should().BeEmpty();
        residue.ComparisonDiagnostics.Should().BeEmpty();
        residue
            .Evidence.Should()
            .Be(
                new NormalizationTextEvidence(
                    "title",
                    "/title",
                    "#",
                    title.Length - 1,
                    title.Length
                )
            );
        track.WholeFileDictionaryCandidates.Should().BeEmpty();
        result
            .EntityKeys.Should()
            .BeEquivalentTo(
                new[]
                {
                    ShadowRepertoireAdapter.KnownEntityKey(free),
                    ShadowRepertoireAdapter.KnownEntityKey(stash),
                    ShadowRepertoireAdapter.KnownEntityKey(mario),
                }
            );
        result.EntityKeys.Should().NotContain(ShadowRepertoireAdapter.KnownEntityKey(blank));
        result.EntityKeys.Should().NotContain(ShadowRepertoireAdapter.KnownEntityKey(punctuation));
        track.OriginalTitle.Should().Be(title);
        track.Extraction.Should().BeEquivalentTo(original);
        track
            .Segments.Select(segment => segment.Evidence)
            .Should()
            .Equal(original.Segments.Select(segment => segment.Evidence));
        track.FileUuid.Should().Be(FileUuid);
        track.SourceUuid.Should().Be(SourceUuid);

        var contents = (ShadowFileContents)
            performance.Process(input).Single(item => item.Domain == "file_contents").Proposal!;
        contents.Playback.FileUuid.Should().Be(FileUuid);
        contents.Playback.SourceUuid.Should().Be(SourceUuid);
        contents.Playback.Mp3Url.Should().Be(audio);
        contents.TitleInterpretation.Should().BeEquivalentTo(track);
        input.Row.GetProperty("title").GetString().Should().Be(title);
        input.Row.GetProperty("mp3_url").GetString().Should().Be(audio);
    }

    [TestCase("")]
    [TestCase("  # ! ...  ")]
    public void EmptyNormalizedDictionaryRowsKeepTheirOwnUuidAndUnorderedMemberships(string name)
    {
        var songUuid = Guid.NewGuid();
        var dictionary = Song(songUuid, name);
        var memberships = Memberships(songUuid, 2, name);
        var adapter = new ShadowRepertoireAdapter();
        adapter.Observe(dictionary);
        adapter.Observe(memberships);
        var artist = new ShadowCatalogArtist(1, ArtistUuid, "Artist", []);

        var result = adapter
            .Process(new(dictionary, null, artist, "catalog:unattributed"))
            .Single();
        var song = (ShadowRepertoireSong)result.Proposal!;

        ShadowRepertoireAdapter.NormalizeTitle(name).Should().BeEmpty();
        result.Outcome.Should().Be("unresolved");
        result.Reason.Should().Be("empty_dictionary_title");
        song.LegacySongUuid.Should().Be(songUuid);
        song.EntityKey.Should().Be(ShadowRepertoireAdapter.KnownEntityKey(songUuid));
        song.Name.Should().Be(name);
        result.EntityKeys.Should().Equal(song.EntityKey);
        result
            .Evidence.Should()
            .Be(
                new ShadowRepertoireDictionaryEvidence(
                    "song_sample:" + songUuid.ToString("D"),
                    "/row/name",
                    name
                )
            );
        var membershipResult = new ShadowPerformanceAdapter()
            .Process(new(memberships, null, artist, "catalog:unattributed"))
            .Single(item => item.Domain == "setlists");
        var account = (ShadowSetlistAccount)membershipResult.Proposal!;
        account.Ordered.Should().BeFalse();
        account
            .Memberships.Should()
            .HaveCount(2)
            .And.OnlyContain(member =>
                member.LegacySongUuid == songUuid && member.SongEntityKey == song.EntityKey
            );
        dictionary.GetProperty("row").GetProperty("name").GetString().Should().Be(name);
        memberships
            .GetProperty("unordered_memberships")[0]
            .GetProperty("song")
            .GetProperty("name")
            .GetString()
            .Should()
            .Be(name);
    }

    [TestCase("#")]
    [TestCase("!!!")]
    [TestCase("??")]
    public void PunctuationOnlyFileTitlesCannotRetrieveAnEmptyWholeFileCandidate(string title)
    {
        var adapter = new ShadowRepertoireAdapter();
        adapter.Observe(Song(Guid.NewGuid(), ""));
        adapter.Observe(Song(Guid.NewGuid(), title));
        var original = new TrackTitleExtractor().Extract(ArtistUuid, title);

        var result = adapter.Process(Track(title)).Single();
        var track = (ShadowRepertoireTrack)result.Proposal!;

        ShadowRepertoireAdapter.NormalizeTitle(title).Should().BeEmpty();
        result.Outcome.Should().Be("unresolved");
        result.EntityKeys.Should().BeEmpty();
        track.WholeFileDictionaryCandidates.Should().BeEmpty();
        track
            .Segments.Should()
            .OnlyContain(segment =>
                segment.Song == null
                && segment.CandidateSongs.Count == 0
                && segment.ComparisonDiagnostics.Count == 0
            );
        track.OriginalTitle.Should().Be(title);
        track.Extraction.Should().BeEquivalentTo(original);
        track
            .Segments.Select(segment => segment.Evidence)
            .Should()
            .Equal(original.Segments.Select(segment => segment.Evidence));
    }

    [TestCase(TrackContentKind.Song, "unresolved", "shadow_guard_title_needs_interpretation")]
    [TestCase(TrackContentKind.Unknown, "unresolved", "extractor_reports_unknown_content")]
    [TestCase(TrackContentKind.Nonmusic, "not_applicable", "extractor_reports_non_song_content")]
    [TestCase(TrackContentKind.Speech, "not_applicable", "extractor_reports_non_song_content")]
    public void EmptyNormalizedMentionsPreserveExplicitProducerContentReasons(
        TrackContentKind kind,
        string outcome,
        string reason
    )
    {
        var adapter = new ShadowRepertoireAdapter();
        adapter.Observe(Song(Guid.NewGuid(), "#"));
        var evidence = new NormalizationTextEvidence("setlist", "/songs/0/title", "#", 0, 1);
        var mention = new TrackTitleSegment(
            1,
            kind,
            kind == TrackContentKind.Song ? "#" : null,
            evidence,
            TitleCoverage.Unknown,
            TitleTransition.Unknown
        );

        var result = adapter.ResolveTitleMention(1, ArtistUuid, mention);

        result.Outcome.Should().Be(outcome);
        result.Reason.Should().Be(reason);
        result.Kind.Should().Be(kind);
        result.Evidence.Should().Be(evidence);
        result.Song.Should().BeNull();
        result.CandidateSongs.Should().BeEmpty();
        result.ComparisonDiagnostics.Should().BeEmpty();
    }

    [TestCase("茉莉花")]
    [TestCase("Катюша")]
    [TestCase("さくら")]
    public void UnicodeLetterTitlesStillRetrieveTheirKnownDictionarySongs(string title)
    {
        var songUuid = Guid.NewGuid();
        var adapter = new ShadowRepertoireAdapter();
        adapter.Observe(Song(songUuid, title));
        var original = new TrackTitleExtractor().Extract(ArtistUuid, title);

        var result = adapter.Process(Track(title)).Single();
        var track = (ShadowRepertoireTrack)result.Proposal!;

        ShadowRepertoireAdapter.NormalizeTitle(title).Should().NotBeEmpty();
        result.Outcome.Should().Be("proposed");
        track.Segments.Should().ContainSingle().Which.Song!.LegacySongUuid.Should().Be(songUuid);
        track.Segments.Single().Evidence.Should().Be(original.Segments.Single().Evidence);
        track.Extraction.Should().BeEquivalentTo(original);
        result.EntityKeys.Should().Equal(ShadowRepertoireAdapter.KnownEntityKey(songUuid));
    }

    [TestCase("#41", "#41", "41")]
    [TestCase("#41", "41", "41")]
    [TestCase("AC/DC", "AC/DC", "ac dc")]
    [TestCase("AC/DC", "ac dc", "ac dc")]
    [TestCase("What's New?", "what's new", "what s new")]
    public void NamedPunctuatedTitlesKeepTheirNonemptyNormalizedDictionaryCorrespondence(
        string dictionaryTitle,
        string title,
        string normalized
    )
    {
        var songUuid = Guid.NewGuid();
        var adapter = new ShadowRepertoireAdapter();
        adapter.Observe(Song(songUuid, dictionaryTitle));
        var original = new TrackTitleExtractor().Extract(ArtistUuid, title);

        var result = adapter.Process(Track(title)).Single();
        var track = (ShadowRepertoireTrack)result.Proposal!;
        var segment = track.Segments.Should().ContainSingle().Which;

        ShadowRepertoireAdapter.NormalizeTitle(dictionaryTitle).Should().Be(normalized);
        ShadowRepertoireAdapter.NormalizeTitle(title).Should().Be(normalized);
        result.Outcome.Should().Be("proposed");
        segment.Song!.LegacySongUuid.Should().Be(songUuid);
        segment
            .ComparisonDiagnostics.Should()
            .ContainSingle()
            .Which.Evidence.Should()
            .Equal(
                new ShadowRepertoireDictionaryEvidence(
                    "song_sample:" + songUuid.ToString("D"),
                    "/row/name",
                    dictionaryTitle
                )
            );
        segment.Evidence.Should().Be(original.Segments.Single().Evidence);
        track.Extraction.Should().BeEquivalentTo(original);
        result.EntityKeys.Should().Equal(ShadowRepertoireAdapter.KnownEntityKey(songUuid));
    }
}
