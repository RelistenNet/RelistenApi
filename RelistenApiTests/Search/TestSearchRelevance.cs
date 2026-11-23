using System.Collections.Frozen;
using FluentAssertions;
using Relisten.Search;

namespace RelistenApiTests.Search;

[TestFixture]
public sealed class TestSearchRelevance
{
    [Test]
    public void FullArtistNameLeadsEvenWhenItsFirstWordIsAlsoAnArtistAlias()
    {
        var billy = Artist("billy-strings", "billy strings", "billy");
        var bands = Artist("strings", "strings");
        var input = new SearchInput("billy strings");
        var evidence = Evidence([billy, bands]);

        var query = SearchInterpretationReader.Read(input, evidence);

        query.Terms.Should().Be("billy strings");
        SearchRanker.KnownItems(query, input, evidence)[0].Id.Should().Be(billy.Document.Id);
    }

    [Test]
    public void WinterlandCanBeAPassageLocationEvenWhenAnArtistUsesTheSameName()
    {
        var rule = SearchVocabularyRules.Passages.Single(rule =>
            rule.Phrases.Contains("help slip frank")
        );
        var passage = new SearchPassage(
            "grateful-dead",
            "show_winterland",
            "source_winterland",
            "Set 2",
            19770609,
            "Winterland",
            ["help", "slip", "frank"],
            ["track_help", "track_slip", "track_frank"],
            []
        );
        var evidence = Evidence(
            [
                Artist("winterland", "winterland"),
                Venue("winterland", "winterland"),
                Song("winterland", "help slip frank"),
            ],
            new SearchPassages([new(rule, [new(passage, new[] { "winterland" }.ToFrozenSet())])])
        );

        var query = SearchInterpretationReader.Read(new("winterland help slip frank"), evidence);

        query.Terms.Should().Be("winterland help slip frank");
        evidence.Passages.Find(query).Should().Equal(passage);
    }

    [Test]
    public void TokyoMatchBeatsTheMorePlayedFukuokaPerformance()
    {
        var input = new SearchInput("tokyo twist");
        var query = SearchInterpretationReader.Read(input, Evidence([Venue("tokyo", "tokyo")]));
        var request = PerformanceRequest(query, input);
        request.Q.Should().Be("tokyo twist");
        request.DropTokensThreshold.Should().Be(0);

        // Typesense supplies the text scores. Both performances share the title,
        // but the Tokyo performance matches the place in the query as well.
        var fukuoka = new SearchDocument
        {
            Id = "perf_fukuoka",
            ShowId = "show_fukuoka",
            Kind = "performance",
            Name = "twist",
            Score = 10,
            TrackPlays = 741,
            Popularity = 7,
        };
        var tokyo = fukuoka with
        {
            Id = "perf_tokyo",
            ShowId = "show_tokyo",
            Score = 20,
            TrackPlays = 1,
            Popularity = 1,
        };

        SearchRanker
            .Primary(query, request.Q, SearchScope.All, [], [fukuoka, tokyo], page: 1)
            .Select(result => result.Id)
            .Should()
            .Equal(tokyo.Id, fukuoka.Id);
    }

    [TestCase("lake tahoe outdoor arena", 6, null, false, true)]
    [TestCase("arena in another city", 6, null, false, false)]
    [TestCase("lake tahoe outdoor arena", 70, null, false, false)]
    [TestCase("lake tahoe outdoor arena", 6, "holly-bowling", false, false)]
    [TestCase("lake tahoe outdoor arena", 6, null, true, false)]
    public void VenueAndPlaybackCanOutrankAnExactCoverTitle(
        string place,
        long exactPerformancePlays,
        string? selectedArtist,
        bool matrix,
        bool preferPerformance
    )
    {
        var (song, cover, original) = TahoeResults(exactPerformancePlays);
        original = original with { Place = place };
        var input = new SearchInput(song.Name);
        var query = new SearchQuery(song.Name, SelectedArtist: selectedArtist, Matrix: matrix);

        var ranked = SearchRanker.PreferPlayedVenueMatch(
            input,
            query,
            [song, cover, original],
            Evidence([TahoeVenue()])
        );

        ranked
            .Should()
            .Equal(preferPerformance ? [original, song, cover] : [song, cover, original]);
    }

    [TestCase("holly-bowling", false)]
    [TestCase("phish", true)]
    public void VenueMatchPreservesAnExactSongFromABoostedArtist(
        string boostedArtist,
        bool preferPerformance
    )
    {
        var (song, cover, original) = TahoeResults();
        var artist = Artist(boostedArtist, boostedArtist.Replace('-', ' '));
        var input = new SearchInput(song.Name, BoostArtistIds: [artist.Document.Id]);

        var ranked = SearchRanker.PreferPlayedVenueMatch(
            input,
            new SearchQuery(song.Name),
            [song, cover, original],
            Evidence([TahoeVenue(), artist])
        );

        ranked
            .Should()
            .Equal(preferPerformance ? [original, song, cover] : [song, cover, original]);
    }

    [TestCase("the")]
    [TestCase("the dallas")]
    public void VenueMatchRequiresTheWholePlacePhraseToBeRecognized(string place)
    {
        var (song, _, original) = TahoeResults();
        song = song with { Name = place + " tweezer" };
        original = original with { Place = "the dallas amphitheater" };
        var venue = Venue("amphitheater", "the dallas amphitheater", "dallas");

        var ranked = SearchRanker.PreferPlayedVenueMatch(
            new SearchInput(song.Name),
            new SearchQuery(song.Name),
            [song, original],
            Evidence([venue])
        );

        ranked.Should().Equal(song, original);
    }

    [Test]
    public void TahoeShorthandResolvesToHarveys()
    {
        var venue = TahoeVenue();

        Evidence([venue])
            .Vocabulary.RecognizeVenues("tahoe tweezer")
            .Should()
            .Equal(venue.Document.Id);
    }

    [Test]
    public void VenueMatchCountsSongWidePlaysEvenWhenTheVisibleCoverHasFew()
    {
        var (song, cover, original) = TahoeResults();
        var evidence = Evidence(
            [TahoeVenue()],
            songPlays: new Dictionary<string, long> { [song.Id] = 70 }.ToFrozenDictionary()
        );

        var ranked = SearchRanker.PreferPlayedVenueMatch(
            new SearchInput(song.Name),
            new SearchQuery(song.Name),
            [song, cover, original],
            evidence
        );

        ranked.Should().Equal(song, cover, original);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void VenueMatchSkipsSoundchecksAndPerformancesWithoutPreviews(bool soundcheck)
    {
        var (song, cover, original) = TahoeResults();
        original = original with
        {
            Soundcheck = soundcheck,
            Preview = soundcheck ? original.Preview! with { Soundcheck = true } : null,
        };

        var ranked = SearchRanker.PreferPlayedVenueMatch(
            new SearchInput(song.Name),
            new SearchQuery(song.Name),
            [song, cover, original],
            Evidence([TahoeVenue()])
        );

        ranked.Should().Equal(song, cover, original);
    }

    [Test]
    public void VenueMatchSkipsAPerformanceFromTheExactSongsOwnArtist()
    {
        var (song, cover, original) = TahoeResults();
        original = original with { ArtistSlug = song.ArtistSlug };

        var ranked = SearchRanker.PreferPlayedVenueMatch(
            new SearchInput(song.Name),
            new SearchQuery(song.Name),
            [song, cover, original],
            Evidence([TahoeVenue()])
        );

        ranked.Should().Equal(song, cover, original);
    }

    [Test]
    public void ExplicitJradBeatsListenerAffinityForTheGratefulDead()
    {
        var dead = Artist("grateful-dead", "grateful dead");
        var jrad = Artist("jrad", "joe russos almost dead", "jrad");
        var deadSong = Song(dead.Document.ArtistSlug, "help on the way");
        var jradSong = Song(jrad.Document.ArtistSlug, "help on the way");
        var evidence = Evidence([
            dead,
            jrad,
            jradSong,
            (deadSong.Document with { Popularity = 5_000 }, []),
        ]);
        var input = new SearchInput("jrad help on the way", BoostArtistIds: [dead.Document.Id]);

        var query = SearchInterpretationReader.Read(input, evidence);

        SearchRanker
            .KnownItems(query, input, evidence)
            .Select(item => item.Id)
            .Should()
            .Equal(jradSong.Document.Id);
        PerformanceRequest(query, input).FilterBy.Should().Contain("artist_slug:=`jrad`");
    }

    [Test]
    public void HamptonWithInteriorYearAndSongAliasKeepsAllThreeConstraints()
    {
        var evidence = Evidence([
            Artist("phish", "phish"),
            Venue("hampton", "hampton coliseum", "hampton"),
        ]);
        var input = new SearchInput("hampton 97 halleys");

        var query = SearchInterpretationReader.Read(input, evidence);
        var request = PerformanceRequest(query, input);

        query.Year.Should().Be(1997);
        request.Q.Should().Be("hampton halleys comet");
        request.FilterBy.Should().Contain("artist_slug:=`phish`").And.Contain("19970101");
    }

    [TestCase("phish twist 27", "twist", "twist 27")]
    [TestCase("hampton 27 twist", "hampton twist", "hampton 27 twist")]
    public void TwoDigitYearBecomesEligibleWhenTheCalendarReachesIt(
        string text,
        string currentTerms,
        string futureTerms
    )
    {
        var evidence = Evidence([Artist("phish", "phish"), Venue("hampton", "hampton")]);
        var input = new SearchInput(text);

        var before = SearchInterpretationReader.Read(input, evidence, currentYear: 2026);
        before.Year.Should().BeNull();
        before.Terms.Should().Be(futureTerms);

        var after = SearchInterpretationReader.Read(input, evidence, currentYear: 2027);
        after.Year.Should().Be(2027);
        after.Terms.Should().Be(currentTerms);
    }

    [Test]
    public void UnparseableInteriorDigitsDoNotBecomeAYear()
    {
        var evidence = Evidence([Venue("hampton", "hampton")]);
        var query = SearchInterpretationReader.Read(new("hampton ٢٧ twist"), evidence, 2027);

        query.Year.Should().BeNull();
    }

    [Test]
    public void AdjacentSongsOnlyFormAPassageWhenEveryTransitionIsMarked()
    {
        var first = new SearchPassages.PartRow(
            "grateful-dead",
            Guid.NewGuid(),
            new DateOnly(1977, 6, 9),
            "Winterland",
            "San Francisco",
            Guid.NewGuid(),
            1,
            "Set 2",
            1,
            Guid.NewGuid(),
            true,
            0,
            "help",
            true
        );
        var middle = first with { TrackUuid = Guid.NewGuid(), Position = 2, SongId = "slip" };
        var final = middle with { Part = 1, SongId = "frank", SegueAfter = false };

        SearchPassages
            .MatchesSongs([first, middle, final], ["help", "slip", "frank"])
            .Should()
            .BeTrue();
        SearchPassages
            .MatchesSongs(
                [first, middle with { SegueAfter = false }, final],
                ["help", "slip", "frank"]
            )
            .Should()
            .BeFalse();
    }

    private static SearchRequest PerformanceRequest(SearchQuery query, SearchInput input) =>
        SearchRequests
            .Build(query, input.Scope, input.Typing)
            .Single(request => request.Collection == SearchIndexSchema.Performances);

    private static (
        SearchDocument Song,
        SearchDocument Cover,
        SearchDocument Original
    ) TahoeResults(long coverPlays = 6)
    {
        var song = Song("holly-bowling", "tahoe tweezer").Document;
        var original = new SearchDocument
        {
            Id = "perf_tahoe",
            Kind = "performance",
            Name = "tweezer",
            ArtistSlug = "phish",
            Place = "lake tahoe outdoor arena at harveys",
            TrackPlays = 402,
            Preview = new(
                "track_tweezer",
                "source_tahoe",
                "Set 2",
                1,
                1,
                2176,
                402,
                true,
                false,
                "Tweezer"
            ),
        };
        var cover = original with
        {
            Id = "perf_cutting_room",
            ArtistSlug = song.ArtistSlug,
            Name = song.Name,
            Place = "the cutting room",
            TrackPlays = coverPlays,
            Preview = original.Preview! with
            {
                TrackId = "track_cutting_room",
                SourceId = "source_cutting_room",
                Plays = coverPlays,
                Label = "Tahoe Tweezer",
            },
        };

        return (song, cover, original);
    }

    private static (SearchDocument Document, string[] Aliases) TahoeVenue()
    {
        var (venue, aliases) = Venue("harveys", "lake tahoe outdoor arena at harveys");
        return (venue with { ArtistSlugs = ["phish"] }, aliases);
    }

    private static CatalogQueryEvidence Evidence(
        (SearchDocument, string[])[] documents,
        SearchPassages? passages = null,
        FrozenDictionary<string, long>? songPlays = null
    ) =>
        new(
            "entities_fixture",
            new SearchVocabulary(documents, []),
            passages ?? SearchPassages.Empty,
            songPlays ?? FrozenDictionary<string, long>.Empty,
            FrozenSet<string>.Empty
        );

    private static (SearchDocument Document, string[] Aliases) Artist(
        string slug,
        string name,
        params string[] aliases
    ) =>
        (
            new()
            {
                Id = "artist_" + slug,
                Kind = "artist",
                Name = name,
                ArtistSlug = slug,
                Popularity = 20,
            },
            aliases
        );

    private static (SearchDocument Document, string[] Aliases) Song(string artist, string name) =>
        (
            new()
            {
                Id = "song_" + artist,
                Kind = "song",
                Name = name,
                ArtistSlug = artist,
            },
            []
        );

    private static (SearchDocument Document, string[] Aliases) Venue(
        string slug,
        string name,
        params string[] aliases
    )
    {
        var id = "venue_" + slug;
        return (
            new()
            {
                Id = id,
                Kind = "venue",
                Name = name,
                VenueId = id,
            },
            aliases
        );
    }
}
