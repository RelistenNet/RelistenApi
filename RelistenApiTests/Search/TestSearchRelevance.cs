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

    [TestCase("lake tahoe outdoor arena", 402, 6, null, true)]
    [TestCase("arena in another city", 4000, 6, null, false)]
    [TestCase("lake tahoe outdoor arena", 402, 70, null, false)]
    [TestCase("lake tahoe outdoor arena", 402, 6, "holly-bowling", false)]
    [TestCase("lake tahoe outdoor arena", 402, 6, null, false, true)]
    public void VenueAndPlaybackCanOutrankAnExactCoverTitle(
        string place,
        long trackPlays,
        long exactPerformancePlays,
        string? selectedArtist,
        bool preferPerformance,
        bool matrix = false
    )
    {
        const string terms = "tahoe tweezer";
        var song = Song("holly-bowling", terms).Document;
        var performance = new SearchDocument
        {
            Id = "perf_tahoe",
            Kind = "performance",
            Name = "tweezer",
            ArtistSlug = "phish",
            Place = place,
            TrackPlays = trackPlays,
            Preview = new(
                "track_tweezer",
                "source_tahoe",
                "Set 2",
                1,
                1,
                2176,
                trackPlays,
                true,
                false,
                "Tweezer"
            ),
        };
        var exactPerformance = performance with
        {
            Id = "perf_cutting_room",
            ArtistSlug = song.ArtistSlug,
            Name = terms,
            Place = "the cutting room",
            TrackPlays = exactPerformancePlays,
            Preview = performance.Preview! with
            {
                TrackId = "track_cutting_room",
                SourceId = "source_cutting_room",
                Plays = exactPerformancePlays,
                Label = "Tahoe Tweezer",
            },
        };

        var ranked = SearchRanker.PreferPlayedVenueMatch(
            new SearchInput(terms),
            new SearchQuery(terms, SelectedArtist: selectedArtist, Matrix: matrix),
            [song, exactPerformance, performance],
            Evidence([])
        );

        ranked
            .Should()
            .Equal(
                preferPerformance
                    ? [performance, song, exactPerformance]
                    : [song, exactPerformance, performance]
            );
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

    private static CatalogQueryEvidence Evidence(
        (SearchDocument, string[])[] documents,
        SearchPassages? passages = null
    ) =>
        new(
            "entities_fixture",
            new SearchVocabulary(documents, []),
            passages ?? SearchPassages.Empty,
            FrozenDictionary<string, long>.Empty,
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
