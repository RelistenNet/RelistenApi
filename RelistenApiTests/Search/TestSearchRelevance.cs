using System.Collections.Frozen;
using System.Text.Json;
using FluentAssertions;
using Relisten.Search;
using Relisten.Services.Popularity;

namespace RelistenApiTests.Search;

[TestFixture]
public sealed class TestSearchRelevance
{
    [TestCase("nye 25")]
    [TestCase("nye '25")]
    [TestCase("nye 2025")]
    public void NewYearsEveWithAYearKeepsTheExactShowDate(string holiday)
    {
        var evidence = Evidence([]);
        var query = SearchInterpretationReader.Read(new("roadrunner " + holiday), evidence, 2026);
        query.Date.Should().Be(20251231);
        query.Terms.Should().Be("roadrunner");
    }

    [Test]
    public void APlaceAndNumberCanStillBeAnExactSongTitle()
    {
        var arena = Venue("arena", "arena");
        arena.Document = arena.Document with { CityLabel = "Chicago" };
        var evidence = Evidence([arena, Song("band", "chicago 25")]);
        var query = SearchInterpretationReader.Read(new("chicago 25"), evidence, 2026);
        query.HasDate.Should().BeFalse();
        query.Terms.Should().Be("chicago 25");
    }

    [Test]
    public void BundledNamesResolveWithOrWithoutTheirImportedRows()
    {
        const string hall = "venue_bvhfka1g";
        var venue = new SearchProjectionRows.Venue(
            hall,
            "Barton Hall",
            "Ithaca",
            "NY",
            "US",
            null,
            Array.Empty<string>()
        );
        var fromFile = SearchReviewedPlaceNames
            .WithShortNames([])
            .Single(name => name.VenueId == hall && name.Name == "Cornell");
        foreach (
            var stored in new[]
            {
                Array.Empty<SearchProjectionRows.ShortPlaceName>(),
                new[] { fromFile },
            }
        )
        {
            var rows = SearchReviewedPlaceNames
                .WithVenueSpellings([venue])
                .ToDictionary(row => row.Id);
            var aliases = SearchPlaceAliases.Build(
                rows,
                SearchReviewedPlaceNames.WithShortNames(stored),
                [],
                []
            );
            aliases.Aliases["cornell"].VenueIds.Should().Equal(hall);
            rows[hall].Aliases.Should().Equal("Cornell");
        }
    }

    [Test]
    public void SaintAbbreviationMatchesSongTitlesWithoutExpandingVenueNames()
    {
        var expanded = Song("first-band", "saint cloud");
        var literal = Song("other-band", "st cloud");
        var evidence = Evidence([expanded, literal, Venue("city", "saint cloud")]);
        var vocabulary = evidence.Vocabulary;

        vocabulary
            .ExactSongIds("st cloud", null)
            .Should()
            .BeEquivalentTo(expanded.Document.Id, literal.Document.Id);
        vocabulary
            .Lookup("st cloud")
            .Select(row => row.Id)
            .Should()
            .BeEquivalentTo(expanded.Document.Id, literal.Document.Id);
        var request = new SearchInput("st cloud");
        var reading = SearchInterpretationReader.Read(request, evidence);
        CatalogSearchRanking
            .Candidate(expanded.Document, request, reading, evidence, new HashSet<string>(), null)
            .Match.Tier.Should()
            .Be(MatchTier.Exact);
    }

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
    public void LeadingArtistWithoutCatalogEvidenceKeepsTheLiteralCompoundQuery()
    {
        var artist = Artist("quincy-blue", "quincy blue");
        var evidence = Evidence([artist]);
        var input = new SearchInput("quincy blue last train");

        var query = SearchInterpretationReader.Read(input, evidence);

        query.ImpliedArtist.Should().BeNull();
        query.Terms.Should().Be("quincy blue last train");
    }

    [TestCase("quincy blue last train", "quincy-blue", "last train")]
    [TestCase("quincy blue starlake light", null, "quincy blue starlake light")]
    public void ExactPlayedTrackTitlesCorroborateAnArtistUnlessTheTitleHasAPlaceReading(
        string text,
        string? artist,
        string terms
    )
    {
        var venue = Venue("starlake", "starlake amphitheatre");
        var evidence = Evidence(
            [
                Artist("quincy-blue", "quincy blue"),
                (venue.Document with { ArtistSlugs = ["blue-pines"] }, venue.Aliases),
                Song("blue-pines", "light"),
            ],
            titlePlays: new Dictionary<(string, string), long>
            {
                [("quincy-blue", PopularityPerformanceKey.StoredRawTitle("last train"))] = 2,
                [("quincy-blue", PopularityPerformanceKey.StoredRawTitle("starlake light"))] = 2,
            }.ToFrozenDictionary()
        );

        var query = SearchInterpretationReader.Read(new SearchInput(text), evidence);

        query.ImpliedArtist.Should().Be(artist);
        query.Terms.Should().Be(terms);
    }

    [Test]
    public void AnExactPlaceAndSongCanUseAnArtistNameAsThePlace()
    {
        var venue = Venue("winterland", "winterland ballroom");
        var evidence = Evidence([
            Artist("winterland", "winterland"),
            (venue.Document with { ArtistSlugs = ["grateful-dead"] }, venue.Aliases),
            Song("grateful-dead", "dark star"),
        ]);

        var query = SearchInterpretationReader.Read(
            new SearchInput("winterland dark star 11/11/73"),
            evidence
        );

        query.ImpliedArtist.Should().BeNull();
        query.Date.Should().Be(19731111);
        query.Terms.Should().Be("winterland dark star");
    }

    [TestCase("round the wheel")]
    [TestCase("red rocks")]
    public void StringCheeseInitialismSuppliesArtistContextAndKeepsALiteralReading(string remainder)
    {
        var artist = Artist("sci", "string cheese incident");
        var evidence = Evidence([artist]);
        var bare = new SearchInput("sci");
        var compound = new SearchInput("sci " + remainder);

        var identity = SearchInterpretationReader.Read(bare, evidence);
        var query = SearchInterpretationReader.Read(compound, evidence);
        var options = CatalogReading.Options(compound, query, new HashSet<string>(), evidence);

        SearchRanker.KnownItems(identity, bare, evidence)[0].Id.Should().Be(artist.Document.Id);
        query.ImpliedArtist.Should().Be("sci");
        query.Terms.Should().Be(remainder);
        options
            .Should()
            .Contain(option => option.Query.Artist == "sci" && option.Query.Terms == remainder);
        options
            .Should()
            .Contain(option => option.Query.Artist == null && option.Query.Terms == compound.Text);
    }

    [Test]
    public void ArtistInitialismOffersAScopedReadingBesideTheUnfilteredQuery()
    {
        var artist = Artist("brooklyn-tide-makers", "brooklyn & the tide makers");
        var evidence = Evidence([
            (artist.Document with { Name = SearchText.Fold(artist.Document.Name) }, artist.Aliases),
            Song("another-artist", "a btm rehearsal"),
        ]);
        var input = new SearchInput("btm clear dawn 1974-10-12");
        var query = SearchInterpretationReader.Read(input, evidence);
        var options = CatalogReading.Options(input, query, new HashSet<string>(), evidence);

        query.Artist.Should().BeNull();
        query.Terms.Should().Be("btm clear dawn");
        options.Single(option => option.Option.Id == "literal").Query.Artist.Should().BeNull();
        var artistOption = options.Single(option => option.Query.Artist == "brooklyn-tide-makers");
        artistOption.Query.Terms.Should().Be("clear dawn");
        artistOption.Query.Date.Should().Be(19741012);
    }

    [TestCase("um 2/15/02", "umphreys", "", 20020215)]
    [TestCase("2/15/02 um", null, "um", 20020215)]
    [TestCase("panic red rocks", "wsp", "red rocks", null)]
    [TestCase("red rocks panic", null, "red rocks panic", null)]
    [TestCase("hurt bird bath um", null, "hurt bird bath um", null)]
    [TestCase("widespread panic 1997", "wsp", "", null)]
    [TestCase("dribble bisco", "disco-biscuits", "dribble", null)]
    public void ReviewedBandNicknamesHonorTheirPositionInTheOriginalQuery(
        string text,
        string? artist,
        string terms,
        int? date
    )
    {
        var evidence = Evidence([
            Artist("umphreys", "umphreys mcgee"),
            Song("umphreys", "hurt bird bath"),
            Artist("wsp", "widespread panic"),
            Artist("disco-biscuits", "the disco biscuits"),
        ]);

        var query = SearchInterpretationReader.Read(new(text), evidence);

        query.Artist.Should().Be(artist);
        query.Terms.Should().Be(terms);
        query.Date.Should().Be(date);
    }

    [TestCase("ujb", null, null, "furthur,phil-lesh", false)]
    [TestCase("ujb", null, "phil-lesh", "phil-lesh", true)]
    [TestCase("furthur ujb", null, "phil-lesh", "furthur", true)]
    [TestCase("ujb", "furthur", "phil-lesh", "furthur", true)]
    [TestCase("dso biodtl", null, null, "dark-star-orchestra", true)]
    public void ReviewedSongNicknameUsesArtistContextBeforeLibraryAffinity(
        string text,
        string? selected,
        string? boost,
        string expectedArtists,
        bool contextual
    )
    {
        var evidence = Evidence([
            Artist("furthur", "furthur"),
            Artist("phil-lesh", "phil lesh and friends"),
            Artist("dark-star-orchestra", "dark star orchestra"),
            Artist("union-jazz-band", "union jazz band"),
        ]);
        var input = new SearchInput(
            text,
            ArtistId: selected == null ? null : "artist_" + selected,
            BoostArtistIds: boost == null ? null : ["artist_" + boost]
        );
        var query = SearchInterpretationReader.Read(input, evidence);
        var reading = evidence.Vocabulary.ReadReviewedSong(input, query);

        reading.Should().NotBeNull();
        reading!
            .Targets.Select(song => song.Artist)
            .Should()
            .BeEquivalentTo(expectedArtists.Split(','));
        reading.Contextual.Should().Be(contextual);
        evidence.Vocabulary.ReadArtistInitialism("ujb", null).Should().BeNull();
    }

    [Test]
    public void SongsScopeKeepsAnImportedSongWhenTheBoostedAliasHasOnlyTracks()
    {
        var imported = Guid.NewGuid();
        var vocabulary = new SearchVocabulary(
            [Artist("furthur", "furthur"), Artist("phil-lesh", "phil lesh and friends")],
            [],
            reviewedSetlistSongs: new Dictionary<(long, string), Guid>
            {
                [FurthurUncleJohnsBandMembership()] = imported,
            }
        );
        var input = new SearchInput("ujb", SearchScope.Songs, BoostArtistIds: ["artist_phil-lesh"]);

        var reading = vocabulary.ReadReviewedSong(input, new SearchQuery("ujb"));

        reading.Should().NotBeNull();
        var song = reading!.Targets.Should().ContainSingle().Subject;
        song.Artist.Should().Be("furthur");
        song.EntityId.Should()
            .Be(Relisten.Identifiers.UuidTypeId.Format("imported_song", imported));
        reading.Contextual.Should().BeFalse();
    }

    private static (long, string) FurthurUncleJohnsBandMembership()
    {
        using var resource = typeof(SearchVocabulary).Assembly.GetManifestResourceStream(
            "Relisten.Search.ReviewedNicknames.json"
        )!;
        using var data = System.Text.Json.JsonDocument.Parse(resource);
        var rule = data
            .RootElement.GetProperty("song_nicknames")
            .EnumerateArray()
            .Single(row =>
                row.GetProperty("artist_slug").GetString() == "furthur"
                && row.GetProperty("nickname").GetString() == "ujb"
            );
        var songId = rule.GetProperty("song_id").GetString()!;
        return (
            long.Parse(
                songId.AsSpan("setlist:".Length),
                System.Globalization.CultureInfo.InvariantCulture
            ),
            "furthur"
        );
    }

    [Test]
    public void AReviewedNicknameCarriesToIdenticallyNamedSongsAndRawTracks()
    {
        var origin = NicknameSong("origin", "Mississippi Half-Step", "song_origin");
        var imported = NicknameSong("cover", "MISSISSIPPI Half Step", "imported_song_cover");
        var raw = NicknameSong("live-only", "Mississippi Half Step", null);
        var differentTitle = NicknameSong(
            "different",
            "Mississippi Half Step Reprise",
            "song_other"
        );

        var targets = SearchReviewedNicknames.ExpandSongTargets(
            [("mhs", origin.Target)],
            [origin, imported, raw, differentTitle]
        );

        targets["mhs"].Should().BeEquivalentTo([origin.Target, imported.Target, raw.Target]);
    }

    [TestCase("lope", null, null)]
    [TestCase("lope", "phish", "phish")]
    [TestCase("lope", "antelope-cover", "antelope-cover")]
    [TestCase("antelope cover lope", null, "antelope-cover")]
    public void ContextOnlySongNicknamesRequireAnArtistEvenWhenInherited(
        string text,
        string? boosted,
        string? expected
    )
    {
        var evidence = Evidence([
            Artist("phish", "phish"),
            Artist("antelope-cover", "antelope cover"),
            Song("antelope-cover", "run like an antelope"),
        ]);
        var input = new SearchInput(
            text,
            BoostArtistIds: boosted == null ? null : ["artist_" + boosted]
        );
        var query = SearchInterpretationReader.Read(input, evidence);
        var reading = evidence.Vocabulary.ReadReviewedSong(input, query);
        if (expected == null)
        {
            reading.Should().BeNull();
        }
        else
        {
            reading.Should().NotBeNull();
            reading!.Targets.Should().OnlyContain(song => song.Artist == expected);
            reading.Contextual.Should().BeTrue();
        }
    }

    [TestCase("title")]
    [TestCase("stored_alias")]
    [TestCase("approved_nickname")]
    public void ALocalSongClaimBlocksAnInheritedNickname(string claim)
    {
        var origin = NicknameSong("origin", "Midnight Signal", "song_origin");
        var cover = NicknameSong("cover", "Midnight Signal", "song_cover");
        var other = NicknameSong(
            "cover",
            claim == "title" ? "ms" : "Morning Stillness",
            "song_distinct",
            claim == "stored_alias" ? ["ms"] : []
        );
        var approved = new List<(string, SearchReviewedNicknames.SongTarget)>
        {
            ("ms", origin.Target),
        };
        if (claim == "approved_nickname")
        {
            approved.Add(("ms", other.Target));
        }

        var targets = SearchReviewedNicknames.ExpandSongTargets(approved, [origin, cover, other]);

        targets["ms"].Should().NotContain(cover.Target);
        targets["ms"].Should().BeEquivalentTo(approved.Select(row => row.Item2));
    }

    private static SearchReviewedNicknames.CatalogSong NicknameSong(
        string artist,
        string title,
        string? id,
        params string[] aliases
    ) => new(new(artist, SearchText.Fold(title), id, id == null), aliases);

    [Test]
    public void InitialismsThatCollideWithArtistNamesTitlesOrOrdinaryWordsStayLiteral()
    {
        var documents = new[]
        {
            Artist("brooklyn-tide-makers", "brooklyn tide makers"),
            Artist("autumn-river-ensemble", "autumn river ensemble"),
            Artist("canyon-jumpers-fleet", "canyon jumpers fleet"),
            Artist("coal-jade-frame", "coal jade frame"),
            Artist("north-bay-duo", "north bay duo"),
            Artist("nbd", "nbd"),
            Artist("little-quiet-bird", "little quiet bird"),
            Song("another-artist", "btm"),
        };
        var vocabulary = new SearchVocabulary(documents, [], new HashSet<string> { "lqb" });

        foreach (var token in new[] { "btm", "are", "cjf", "nbd", "lqb" })
        {
            vocabulary.ReadArtistInitialism(token + " dawn", null).Should().BeNull(token);
        }
    }

    [Test]
    public void AWellRepresentedSongReservesItsInitialsWithoutCreatingAnAlias()
    {
        var song = Song("river", "copper light train");
        song.Document = song.Document with { Popularity = 180 };
        var evidence = Evidence([
            song,
            Artist("canyon-lantern-trio", "canyon lantern trio"),
            Artist("golden-echo-river", "golden echo river"),
        ]);

        evidence.Vocabulary.ReadArtistInitialism("clt", null).Should().BeNull();
        evidence.Vocabulary.Lookup("clt", "river", curated: true).Should().BeEmpty();
        evidence
            .Vocabulary.ReadArtistInitialism("ger", null)!
            .Value.Artist.ArtistSlug.Should()
            .Be("golden-echo-river");
    }

    [Test]
    public void AShortInitialismNeedsARegisteredAliasAndAnInternalArticleCanMakeAnInitialismAmbiguous()
    {
        var duo = Artist("blue-understory", "blue understory", "bu");
        var evidence = Evidence([
            duo,
            Artist("west-zephyr", "west zephyr"),
            Artist("dawn-the-birds", "dawn the birds"),
            Artist("delta-train-band", "delta train band"),
        ]);

        evidence.Vocabulary.ReadArtistInitialism("wz", null).Should().BeNull();
        evidence.Vocabulary.ReadArtistInitialism("dtb", null).Should().BeNull();
        evidence.Vocabulary.FullArtist("bu", null).Artist.Should().Be("blue-understory");
    }

    [TestCase(true)]
    [TestCase(false)]
    public void AStoredSongAliasKeepsTheSongFindableWithArtistContext(bool selected)
    {
        var artist = Artist("river", "river improvisers");
        var song = Song("river", "copper light train");
        song.Aliases = ["clt"];
        var evidence = Evidence([
            artist,
            song,
            Artist("canyon-lantern-trio", "canyon lantern trio"),
        ]);
        var input = new SearchInput(
            "clt",
            ArtistId: selected ? artist.Document.Id : null,
            BoostArtistIds: selected ? null : [artist.Document.Id]
        );
        var query = SearchInterpretationReader.Read(input, evidence);

        CatalogReading
            .Options(input, query, new HashSet<string>(), evidence)
            .Should()
            .NotContain(option => option.Query.Artist == "canyon-lantern-trio");
        SearchRanker.KnownItems(query, input, evidence).First().Id.Should().Be(song.Document.Id);
    }

    [Test]
    public void UniqueCompleteArtistPrefixGetsIdentityStrength()
    {
        var artist = Artist("the-blue-river-band", "the blue river band");
        var evidence = Evidence([artist]);
        var input = new SearchInput("the blue river");
        var query = SearchInterpretationReader.Read(input, evidence);
        var known = SearchRanker.KnownItems(query, input, evidence).First();
        var candidate = CatalogSearchRanking.Candidate(
            known,
            input,
            query,
            evidence,
            new HashSet<string>(),
            null
        );

        candidate.Row.Should().BeOfType<SearchDocument>().Which.Id.Should().Be(artist.Document.Id);
        candidate.Match.Tier.Should().Be(MatchTier.Exact);
        var competingTitle = new RankedCandidate(
            Song("another-artist", "the blue river").Document,
            new(MatchTier.Exact, ["title"], 1),
            SameKindCount: 1000
        );
        RankedCandidate
            .Order([competingTitle, candidate], mixed: true)
            .First()
            .Row.Key.Should()
            .Be(candidate.Row.Key);
    }

    [TestCase("the blue riv")]
    [TestCase("the blue")]
    [TestCase("the blue river")]
    public void IncompleteAmbiguousOrCompetingArtistPrefixKeepsItsRegularTier(string text)
    {
        var artist = Artist("the-blue-river-band", "the blue river band");
        var evidence = Evidence([
            artist,
            Artist("the-blue-pines", "the blue pines"),
            Song("the-blue-pines", "the blue river"),
        ]);
        var input = new SearchInput(text);
        var query = SearchInterpretationReader.Read(input, evidence);
        var candidate = CatalogSearchRanking.Candidate(
            artist.Document,
            input,
            query,
            evidence,
            new HashSet<string>(),
            null
        );

        candidate.Match.Tier.Should().Be(MatchTier.Prefix);
    }

    [TestCase("dead & co sphere")]
    [TestCase("dead and co sphere")]
    [TestCase("dead & company sphere")]
    public void DeadAndCoLeadsInsteadOfTheGratefulDeadsOneWordAlias(string text)
    {
        var evidence = Evidence([
            Artist("grateful-dead", "grateful dead"),
            Artist("dead-and-co", "dead and company"),
        ]);

        var query = SearchInterpretationReader.Read(new SearchInput(text), evidence);

        query.Artist.Should().Be("dead-and-co");
        query.Terms.Should().Be("sphere");
    }

    [Test]
    public void WinterlandCanBeAPassageLocationEvenWhenAnArtistUsesTheSameName()
    {
        var rule = SearchReviewedNamedShows.Passages.Single(rule =>
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
            ["Help on the Way", "Slipknot!", "Franklin's Tower"],
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
    public void MostPlayedSeguesReachTheShortlistBeforeNewerShowsAndDuplicateTapes()
    {
        var rule = SearchReviewedNamedShows.Passages.Single(rule =>
            rule.Phrases.Contains("scarlet fire")
        );
        var initial = new SearchPassage(
            "grateful-dead",
            "show_latest",
            "source_0",
            "Set 2",
            19770508,
            "Barton Hall",
            ["song_a", "song_b"],
            ["Scarlet Begonias", "Fire On The Mountain"],
            ["track_a0", "track_b0"],
            []
        );
        var tapes = Enumerable
            .Range(0, 9)
            .Select(number =>
                initial with
                {
                    SourceId = $"source_{number}",
                    TrackIds = [$"track_a{number}", $"track_b{number}"],
                    ShowSongStarts = 8,
                    RecordingPlays = number,
                }
            )
            .ToArray();
        var olderShow = initial with
        {
            ShowId = "show_older",
            Date = 19770507,
            ShowSongStarts = 200,
        };
        var recentShows = Enumerable
            .Range(1, 8)
            .Select(number =>
                initial with
                {
                    ShowId = $"show_recent_{number}",
                    Date = 19770509,
                    ShowSongStarts = 1,
                }
            );
        var place = FrozenSet<string>.Empty;
        var passages = new SearchPassages([
            new(
                rule,
                [
                    .. tapes.Select(tape => new SearchPassages.PassageMatch(tape, place)),
                    .. recentShows.Select(show => new SearchPassages.PassageMatch(show, place)),
                    new(olderShow, place),
                ]
            ),
        ]);

        var hits = passages.Find(new SearchQuery("scarlet fire"));

        hits.Should().NotContain(hit => hit.ShowSongStarts == 0);
        hits.Select(hit => hit.ShowId).Should().OnlyHaveUniqueItems();
        hits[0].ShowId.Should().Be(olderShow.ShowId);
        hits[1].ShowId.Should().Be(initial.ShowId);
        hits[0].PopularityPercentile.Should().BeGreaterThan(hits[1].PopularityPercentile);
        var chosen = (SearchPassage)CatalogSearchRanking.Passage(hits[1], "query").Row;
        chosen.SourceId.Should().Be("source_8");
        chosen.TrackIds.Should().Equal("track_a8", "track_b8");
        chosen.ShowSongStarts.Should().Be(8);
        chosen.Label.Should().Be("Scarlet Begonias → Fire On The Mountain");
        chosen.Key.Should().Be(CatalogSearchRanking.Passage(initial, "query").Row.Key);
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

    [TestCase(true, 6, null, false, true)]
    [TestCase(false, 6, null, false, false)]
    [TestCase(true, 70, null, false, false)]
    [TestCase(true, 6, "holly-bowling", false, false)]
    [TestCase(true, 6, null, true, false)]
    public void VenueAndPlaybackCanOutrankAnExactCoverTitle(
        bool atRequestedVenue,
        long exactPerformancePlays,
        string? selectedArtist,
        bool matrix,
        bool preferPerformance
    )
    {
        var (song, cover, original) = TahoeResults(exactPerformancePlays);
        if (!atRequestedVenue)
        {
            original = original with { VenueId = "venue_other" };
        }
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
    public void DecodedTypoEvidenceAddsPartialCoverageWithoutPromotingTheTier()
    {
        const string text = "harbr";
        var input = new SearchInput(text);
        var query = new SearchQuery(text);
        var request = PerformanceRequest(query, input);
        using var response = JsonDocument.Parse(
            """
            {"results":[{"found":2,"hits":[
                {"document":{"id":"minor-edit","kind":"performance","name":"harbor"},
                 "text_match_info":{"tokens_matched":1,"num_tokens_dropped":0,"typo_prefix_score":2}},
                {"document":{"id":"prefix","kind":"performance","name":"harbor"},
                 "text_match_info":{"tokens_matched":1,"num_tokens_dropped":0,"typo_prefix_score":1}}
            ]}]}
            """
        );
        var documents = SearchRequests.Decode(response, [request])[0].Documents;
        var evidence = Evidence([]);
        var corrected = CatalogSearchRanking.Candidate(
            documents[0],
            input,
            query,
            evidence,
            new HashSet<string>(),
            null
        );
        var unrelated = CatalogSearchRanking.Candidate(
            documents[1],
            input,
            query,
            evidence,
            new HashSet<string>(),
            null
        );

        corrected.Match.Tier.Should().Be(MatchTier.Partial);
        corrected.Match.Relevance.Should().BeGreaterThan(0).And.BeLessThan(1);
        unrelated.Match.Relevance.Should().Be(0);
    }

    [Test]
    public void PlaceAndExactSongBeatsWordsInTheWrongFields()
    {
        var (venue, aliases) = Venue("arena", "arena");
        venue = venue with { CityLabel = "Denver" };
        var vocabulary = new SearchVocabulary([(venue, aliases)], []);
        var (_, _, previewedPerformance) = TahoeResults();
        var festival = new SearchDocument
        {
            Id = "festival",
            Kind = "show",
            Name = "ghost canyon fest",
        };
        var otherTitle = previewedPerformance with
        {
            Id = "long-title",
            Name = "the ghost of ralphs mom",
            VenueId = venue.Id,
        };
        var otherCity = previewedPerformance with { Id = "wrong-city", Name = "ghost" };
        var exact = otherCity with { Id = "denver-ghost", VenueId = venue.Id, TrackPlays = 0 };
        SearchDocument[] baseline = [festival, otherTitle, otherCity, exact];

        var ranked = SearchRanker.PreferPlaceAndSong(
            new SearchInput("denver ghost", AddTracks: true),
            new SearchQuery("denver ghost"),
            baseline,
            vocabulary
        );

        ranked.Should().Equal(exact, festival, otherTitle, otherCity);
    }

    [Test]
    public void RejectedPlaceAndSongSearchKeepsTheOrdinaryMatches()
    {
        var arena = Venue("bill-graham", "bill graham civic auditorium");
        arena.Document = arena.Document with
        {
            CityLabel = "San Francisco",
            ArtistSlugs = ["phish"],
        };
        var aliases = new Dictionary<string, SearchPlaceAlias>
        {
            ["sf"] = new("San Francisco, CA", new[] { arena.Document.Id }.ToFrozenSet(), true),
        };
        var evidence = Evidence(
            [arena, Song("phish", "light")],
            placeAliases: aliases.ToFrozenDictionary()
        );
        var input = new SearchInput("sf light");
        var query = new SearchQuery(input.Text);
        var ordinary = PerformanceRequest(query, input);
        var match = SearchRequests.ReadPlaceSong(input, query, evidence.Vocabulary);
        match.Should().NotBeNull();
        var exact = SearchRequests.PlaceSongPerformances(query, match!);
        using var response = JsonDocument.Parse(
            """
            {"results":[
                {"found":1,"hits":[{"document":{"id":"partial","kind":"performance","name":"blue light"}}]},
                {"code":400,"error":"Rejected filter"}
            ]}
            """
        );

        var replies = SearchRequests.Decode(response, [ordinary, exact]);

        replies[0].Documents.Select(result => result.Id).Should().Equal("partial");
        replies[1].Documents.Should().BeEmpty();
    }

    [Test]
    public void PlaceAndSongCompletesTheLastWordAndChecksWhereTheArtistPlayed()
    {
        var arena = Venue("warehouse", "the warehouse");
        arena.Document = arena.Document with { CityLabel = "New Orleans", ArtistSlugs = ["phish"] };
        var evidence = Evidence([arena, Song("phish", "light")]);

        var match = evidence.Vocabulary.ExactPlaceSong("light new orl", null);

        match.Should().NotBeNull();
        match!.VenueIds.Should().Equal(arena.Document.Id);

        var another = Venue("park", "park theatre");
        another.Document = another.Document with
        {
            CityLabel = "New Orlando",
            ArtistSlugs = ["another-band"],
        };
        Evidence([arena, another, Song("phish", "light")])
            .Vocabulary.ExactPlaceSong("light new orl", null)!
            .VenueIds.Should()
            .Equal(arena.Document.Id);
    }

    [Test]
    public void VenueAndCityNicknameIdentifyTheSamePlace()
    {
        var inCity = Venue("spectrum-pa", "spectrum");
        inCity.Document = inCity.Document with { CityLabel = "Philadelphia" };
        var elsewhere = Venue("spectrum-other", "spectrum");
        elsewhere.Document = elsewhere.Document with { CityLabel = "New Orleans" };
        var aliases = SearchPlaceAliases.Build(
            new Dictionary<string, SearchProjectionRows.Venue>
            {
                [inCity.Document.Id] = new(
                    inCity.Document.Id,
                    "Spectrum",
                    "Philadelphia",
                    "PA",
                    "US",
                    null,
                    Array.Empty<string>()
                ),
                [elsewhere.Document.Id] = new(
                    elsewhere.Document.Id,
                    "Spectrum",
                    "New Orleans",
                    "LA",
                    "US",
                    null,
                    Array.Empty<string>()
                ),
            },
            [],
            [
                new("philadelphia", "PA", "US", "Philly", "philly"),
                new("new orleans", "LA", "US", "NOLA", "nola"),
            ],
            []
        );
        var vocabulary = Evidence([inCity, elsewhere], placeAliases: aliases.Aliases).Vocabulary;

        vocabulary.VenuesQualifiedByCity("philly spectrum").Should().Equal(inCity.Document.Id);
        vocabulary.VenuesQualifiedByCity("spectrum nola").Should().Equal(elsewhere.Document.Id);
        vocabulary.VenuesQualifiedByCity("spectrum ghost").Should().BeEmpty();
    }

    [Test]
    public void WeakShortNamesStayLiteralUnlessTheExactAliasWasApproved()
    {
        var approved = Venue("building-a", "some pavilion", "fire");
        var machine = Venue(
            "building-b",
            "beacon theatre",
            "the",
            "civic center",
            "ghost",
            "the beacon"
        );
        machine.Document = machine.Document with { ArtistSlugs = ["phish"] };
        var readings = SearchPlaceAliases.Build(
            new[] { approved.Document, machine.Document }.ToDictionary(
                venue => venue.Id,
                venue => new SearchProjectionRows.Venue(
                    venue.Id,
                    venue.Name,
                    null,
                    null,
                    null,
                    null,
                    Array.Empty<string>()
                )
            ),
            [
                new(approved.Document.Id, "Fire", "fire", true),
                new(machine.Document.Id, "The", "the", false),
                new(machine.Document.Id, "Civic Center", "civic center", false),
                new(machine.Document.Id, "Ghost", "ghost", false),
                new(machine.Document.Id, "The Beacon", "beacon", false),
            ],
            [],
            [],
            new[] { "fire", "ghost" }.ToFrozenSet()
        );
        var vocabulary = new SearchVocabulary(
            [approved, machine, Song("phish", "ghost")],
            [],
            placeAliases: readings.Aliases,
            noUnqualifiedReading: readings.NoUnqualifiedReading
        );

        vocabulary.ReadPlaceAlias("eyes of the world").Should().BeNull();
        vocabulary.RecognizeVenues("eyes of the world").Should().BeEmpty();
        vocabulary.ReadPlaceAlias("civic center").Should().BeNull();
        vocabulary
            .Lookup("civic center", null)
            .Should()
            .NotContain(row => row.Id == machine.Document.Id);
        vocabulary.ReadPlaceAlias("ghost").Should().BeNull();
        vocabulary.ExactSongIds("ghost", null).Should().NotBeEmpty();
        vocabulary.ReadPlaceAlias("beacon").Should().BeNull();
        vocabulary.ExactPlaceSong("beacon ghost", null).Should().BeNull();
        vocabulary
            .ReadPlaceAlias("the beacon")!
            .Value.Place.VenueIds.Should()
            .Equal(machine.Document.Id);
        vocabulary
            .ReadPlaceAlias("fire")!
            .Value.Place.VenueIds.Should()
            .Equal(approved.Document.Id);
    }

    [Test]
    public void AnAliasSharedByTwoVenuesNeedsContextThatSelectsOne()
    {
        var east = Venue("fillmore-east", "fillmore east", "fillmore");
        var west = Venue("fillmore-west", "fillmore");
        east.Document = east.Document with
        {
            CityLabel = "New York",
            ArtistSlugs = ["grateful-dead"],
        };
        west.Document = west.Document with
        {
            CityLabel = "San Francisco",
            ArtistSlugs = ["jefferson-airplane"],
        };
        var readings = SearchPlaceAliases.Build(
            new[] { east.Document, west.Document }.ToDictionary(
                venue => venue.Id,
                venue => new SearchProjectionRows.Venue(
                    venue.Id,
                    venue.Name,
                    venue.CityLabel,
                    venue.Id == east.Document.Id ? "NY" : "CA",
                    "US",
                    null,
                    Array.Empty<string>()
                )
            ),
            [new(east.Document.Id, "Fillmore", "fillmore", false)],
            [new("new york", "NY", "US", "New York City", "new york city")],
            []
        );
        var vocabulary = new SearchVocabulary(
            [east, west, Artist("grateful-dead", "grateful dead")],
            [],
            placeAliases: readings.Aliases,
            noUnqualifiedReading: readings.NoUnqualifiedReading
        );

        vocabulary.ReadPlaceAlias("fillmore").Should().BeNull();
        vocabulary.RecognizeVenues("fillmore").Should().BeEmpty();
        vocabulary
            .ReadPlaceAlias("fillmore san francisco")!
            .Value.Place.VenueIds.Should()
            .Equal(west.Document.Id);
        vocabulary
            .ReadPlaceAlias("fillmore new york city")!
            .Value.Place.VenueIds.Should()
            .Equal(east.Document.Id);
        vocabulary
            .ReadPlaceAlias("grateful dead fillmore")!
            .Value.Place.VenueIds.Should()
            .Equal(east.Document.Id);
    }

    [TestCase("radio city music hall", "radio city")]
    [TestCase("winterland ballroom", "winterland")]
    public void ACanonicalPlacePrefixCanPairWithASongWhenItsAliasAloneCollides(
        string venueName,
        string shortName
    )
    {
        var hall = Venue("hall", venueName, shortName);
        hall.Document = hall.Document with { ArtistSlugs = ["phish"] };
        var readings = SearchPlaceAliases.Build(
            new Dictionary<string, SearchProjectionRows.Venue>
            {
                [hall.Document.Id] = new(
                    hall.Document.Id,
                    hall.Document.Name,
                    "New York",
                    "NY",
                    "US",
                    null,
                    Array.Empty<string>()
                ),
            },
            [new(hall.Document.Id, shortName, shortName, false)],
            [],
            [],
            new[] { shortName }.ToFrozenSet()
        );
        var vocabulary = new SearchVocabulary(
            [hall, Song("cover-band", shortName), Song("phish", "ghost")],
            [],
            placeAliases: readings.Aliases,
            noUnqualifiedReading: readings.NoUnqualifiedReading
        );

        vocabulary.ReadPlaceAlias(shortName).Should().BeNull();
        vocabulary
            .ExactPlaceSong($"{shortName} ghost", null)!
            .VenueIds.Should()
            .Equal(hall.Document.Id);
    }

    [Test]
    public void PlaceAndSongRespectsADismissedVenueAndALiteralSong()
    {
        var (song, cover, original) = TahoeResults();
        var venue = TahoeVenue();
        original = original with { VenueId = venue.Document.Id };
        var vocabulary = new SearchVocabulary([venue], []);
        const string text = "tahoe tweezer";
        var query = new SearchQuery(text);
        var jam = cover with { Name = "tahoe tweezer jam" };
        SearchDocument[] literal = [jam, song, original];
        SearchDocument[] dismissed = [jam, original];

        SearchRanker
            .PreferPlaceAndSong(new SearchInput(text), query, literal, vocabulary)
            .Should()
            .Equal(literal);
        SearchRanker
            .PreferPlaceAndSong(
                new SearchInput(text, AddTracks: true, Ignore: new[] { "venue" }.ToFrozenSet()),
                query,
                dismissed,
                vocabulary
            )
            .Should()
            .Equal(dismissed);
    }

    [Test]
    public void TahoeShorthandResolvesToHarveys()
    {
        var venue = TahoeVenue();

        Evidence([venue, Song("phish", "tweezer")])
            .Vocabulary.ExactPlaceSong("tahoe tweezer", null)!
            .VenueIds.Should()
            .Equal(venue.Document.Id);
    }

    [TestCase(true, false)]
    [TestCase(false, true)]
    public void PlaceAndSongPrefixFindsThePlayedVersionAheadOfALiteralTitlePrefix(
        bool typing,
        bool addTracks
    )
    {
        var (song, cover, original) = TahoeResults();
        var venue = TahoeVenue();
        var phishSong = Song("phish", "tweezer");
        var aliases = new Dictionary<string, SearchPlaceAlias>
        {
            ["tahoe"] = new("Tahoe", new[] { venue.Document.Id }.ToFrozenSet(), false),
        };
        var evidence = Evidence(
            [venue, phishSong, (song, [])],
            placeAliases: aliases.ToFrozenDictionary()
        );
        var input = new SearchInput("tahoe twe", Typing: typing, AddTracks: addTracks);
        var query = new SearchQuery(input.Text);
        var reading = SearchRequests.ReadPlaceSong(input, query, evidence.Vocabulary);

        reading.Should().NotBeNull();
        reading!.SongIds.Should().Equal(phishSong.Document.Id);
        reading.VenueIds.Should().Equal(venue.Document.Id);
        var ranked = RankedCandidate.Order(
            [
                CatalogSearchRanking.Candidate(
                    cover with
                    {
                        PopularityPercentile = .98,
                    },
                    input,
                    query,
                    evidence,
                    FrozenSet<string>.Empty,
                    "query"
                ),
                CatalogSearchRanking.VerifiedPlaceSong(
                    CatalogSearchRanking.Candidate(
                        original with
                        {
                            PopularityPercentile = .99,
                        },
                        input,
                        query,
                        evidence,
                        FrozenSet<string>.Empty,
                        "query"
                    ),
                    reading.Tier
                ),
            ],
            addTracks,
            chronological: false,
            order: "regular",
            mixed: true
        );
        ranked.Select(candidate => candidate.Row.Key).Should().Equal(original.Key, cover.Key);
        SearchRequests
            .ReadPlaceSong(input with { ReadingId = "literal" }, query, evidence.Vocabulary)
            .Should()
            .BeNull();
    }

    [Test]
    public void ShortenedVenueNameCanQualifyASongPrefixWithoutAnAlias()
    {
        var (hall, aliases) = Venue("hall", "radio city music hall");
        var song = Song("phish", "ghost");
        var evidence = Evidence([(hall with { ArtistSlugs = ["phish"] }, aliases), song]);
        var input = new SearchInput("radio city gho");
        var reading = SearchRequests.ReadPlaceSong(input, new(input.Text), evidence.Vocabulary);

        reading.Should().NotBeNull();
        reading!.VenueIds.Should().Equal(hall.VenueId);
        reading.SongIds.Should().Equal(song.Document.Id);
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
            VenueId = TahoeVenue().Document.Id,
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
                "Tweezer",
                "tweezer"
            ),
        };
        var cover = original with
        {
            Id = "perf_cutting_room",
            ArtistSlug = song.ArtistSlug,
            Name = song.Name,
            Place = "the cutting room",
            VenueId = "venue_cutting_room",
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
        FrozenDictionary<string, long>? songPlays = null,
        FrozenDictionary<string, SearchPlaceAlias>? placeAliases = null,
        FrozenDictionary<(string, string), long>? titlePlays = null
    ) =>
        new(
            "entities_fixture",
            new SearchVocabulary(documents, [], placeAliases: placeAliases),
            passages ?? SearchPassages.Empty,
            songPlays ?? FrozenDictionary<string, long>.Empty,
            FrozenSet<string>.Empty,
            titlePlays
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
