using FluentAssertions;
using Relisten.Search;

namespace RelistenApiTests.Search;

[TestFixture]
public sealed class TestUnifiedSearchRanking
{
    [Test]
    public void ExactLibraryTitlesAndWellPlayedCatalogMatchesLeadPublicPlaylistTitles()
    {
        var library = Playlist("library", MatchTier.Exact, 4);
        var publicPlaylist = Playlist("public", MatchTier.Exact, 0) with { Popularity = 1 };
        var secondHit = Playlist("prefix", MatchTier.Prefix, 0) with { Popularity = 1 };
        var song = Catalog("song", MatchTier.Exact, 2) with { SameKindCount = 100_000 };
        var performance = Catalog("performance", MatchTier.Exact, 1) with
        {
            Popularity = .95,
            SameKindCount = 1000,
        };
        var keys = RankedCandidate
            .Order([publicPlaylist, secondHit, song, performance, library], mixed: true)
            .Select(candidate => candidate.Row.Key);

        keys.Should().ContainInOrder("library", "song", "public", "prefix");
        keys.Should().ContainInOrder("library", "performance", "public", "prefix");
    }

    [TestCase(true)]
    [TestCase(false)]
    public void AllLimitsVersionsWithoutCountingTheirParentRows(bool parentFirst)
    {
        var song = Catalog("song", MatchTier.Exact, 2);
        song = song with { Row = (TestRankItem)song.Row with { Id = "the-song" } };
        var show = Catalog("show", MatchTier.Exact, 2);
        show = show with
        {
            Row = (TestRankItem)show.Row with { Id = "one-show", ShowId = "one-show" },
        };
        var performances = Enumerable
            .Range(1, 4)
            .Select(number =>
            {
                var candidate = Catalog("performance", MatchTier.Exact, 1);
                return candidate with
                {
                    Row = (TestRankItem)candidate.Row with
                    {
                        Key = $"version-{number}",
                        SongId = "the-song",
                    },
                };
            });
        var recordings = Enumerable
            .Range(1, 4)
            .Select(number =>
            {
                var candidate = Catalog("recording", MatchTier.Partial, 0);
                return candidate with
                {
                    Row = (TestRankItem)candidate.Row with
                    {
                        Key = $"recording-{number}",
                        ShowId = "one-show",
                    },
                };
            });
        var playlist = Playlist("public", MatchTier.Exact, 0);
        var ranked = (parentFirst ? performances.Prepend(song) : performances.Append(song))
            .Append(playlist)
            .Concat(parentFirst ? recordings.Prepend(show) : recordings.Append(show))
            .ToArray();
        var (limited, more) = SearchDiversity.Limit(ranked);

        limited
            .Select(candidate => candidate.Row.Key)
            .Should()
            .Equal(
                ranked
                    .Select(candidate => candidate.Row.Key)
                    .Where(key => key is not ("version-4" or "recording-4"))
            );
        more.Should().BeEquivalentTo("shows", "sources");
    }

    [Test]
    public void UnmappedPerformancesGroupByArtistAndFoldedTitle()
    {
        var raw = Catalog("performance", MatchTier.Exact, 1);
        var versions = Enumerable
            .Range(1, 4)
            .Select(number =>
                raw with
                {
                    Row = (TestRankItem)raw.Row with
                    {
                        Key = $"raw-{number}",
                        ArtistSlug = "one-artist",
                        Name = number % 2 == 0 ? "Dark Star →" : "dark star",
                    },
                }
            );
        var other = raw with
        {
            Row = (TestRankItem)raw.Row with
            {
                Key = "other",
                ArtistSlug = "another-artist",
                Name = "Dark Star",
            },
        };
        SearchDiversity
            .Limit(versions.Append(other).ToArray())
            .Results.Select(candidate => candidate.Row.Key)
            .Should()
            .Equal("raw-1", "raw-2", "raw-3", "other");
    }

    [TestCase(0)]
    [TestCase(.35)]
    public void AllKeepsReviewEvidenceBelowCatalogResults(double reviewRelevance)
    {
        var show = Catalog("show", MatchTier.Partial, 0) with
        {
            Row = new TestRankItem("show") { Key = "show", SourceId = "source" },
            Match = new(MatchTier.Partial, [], 0),
            Popularity = .99,
            SameKindCount = 30_000,
        };
        var review = Catalog("review", MatchTier.Partial, 0) with
        {
            Match = new(MatchTier.Partial, ["excerpt"], reviewRelevance),
        };

        RankedCandidate
            .Order([review, show], mixed: true)
            .Select(candidate => candidate.Row.Key)
            .Should()
            .Equal("show", "review");
        RankedCandidate.Order([review], mixed: true).Should().Equal(review);
    }

    [Test]
    public void ZeroScoreTiesUseNormalizedPopularityThenStableKey()
    {
        RankedCandidate NoCoverage(string kind, string key, double popularity, long count) =>
            new(
                new TestRankItem(kind) { Key = key },
                new(MatchTier.Partial, [], 0),
                Popularity: popularity,
                SameKindCount: count
            );

        var play = NoCoverage("performance", "a", .3, 100_000);
        var firstShow = NoCoverage("show", "b", .9, 100);
        var secondShow = NoCoverage("show", "c", .9, 200);
        RankedCandidate
            .Order([secondShow, play, firstShow], mixed: true)
            .Select(candidate => candidate.Row.Key)
            .Should()
            .Equal("b", "c", "a");
    }

    [Test]
    public void EquallyPopularExactSongsChooseTheSameLeadInEitherInputOrder()
    {
        var song = Catalog("song", MatchTier.Exact, 2);
        var first = song with { Row = ((TestRankItem)song.Row) with { Key = "song:a" } };
        var second = song with { Row = ((TestRankItem)song.Row) with { Key = "song:b" } };

        RankedCandidate.Order([second, first], mixed: true).First().Row.Key.Should().Be("song:a");
        RankedCandidate.Order([first, second], mixed: true).First().Row.Key.Should().Be("song:a");
    }

    [Test]
    public void TitleCoverageWinsOverAPopularWeakMatchInTheSameTier()
    {
        var weak = Playlist("weak", MatchTier.Partial, 0) with
        {
            Popularity = 1,
            Match = MatchEvidence.Read("rain song", ("description", "the rain in spain", .35)),
        };
        var strong = Catalog("song", MatchTier.Partial, 0) with
        {
            Match = MatchEvidence.Read("rain song", ("title", "The Rain Song", 1)),
        };
        RankedCandidate.Order([weak, strong]).First().Row.Key.Should().Be("song");
    }

    [Test]
    public void PopularityCanBreakAnEqualPrefixTieAcrossKinds()
    {
        var song = Catalog("song", MatchTier.Prefix, 0) with { Popularity = .6 };
        var playlist = Playlist("playlist", MatchTier.Prefix, 0) with { Popularity = .9 };
        RankedCandidate.Order([song, playlist]).First().Row.Key.Should().Be("playlist");
        RankedCandidate
            .Order([song with { Popularity = 1 }, playlist])
            .First()
            .Row.Key.Should()
            .Be("song");
    }

    [Test]
    public void PlaybackLiftsAWellMatchedVersionPastAnObscureExactTitle()
    {
        var obscure = Catalog("performance", MatchTier.Exact, 1) with
        {
            SameKindCount = 13,
            Popularity = .97,
        };
        var popular = Catalog("show", MatchTier.Prefix, 0) with
        {
            SameKindCount = 20_000,
            Popularity = .99,
        };
        var incidental = popular with
        {
            Row = (TestRankItem)popular.Row with { Key = "incidental" },
            Match = new(MatchTier.Partial, ["place"], .25),
        };

        RankedCandidate
            .Order([incidental, popular, obscure], mixed: true)
            .Select(candidate => candidate.Row.Key)
            .Should()
            .Equal("show", "performance", "incidental");
    }

    [Test]
    public void NamedArtistAndTheirCatalogLeadCoincidentalTitlesInExplore()
    {
        var incidental = Catalog("performance", MatchTier.Exact, 2) with { SameKindCount = 20 };
        var artist = Catalog("artist", MatchTier.Prefix, 0) with { NamedArtist = true };
        var show = Catalog("show", MatchTier.Prefix, 0) with { NamedArtist = true };

        RankedCandidate
            .Order([incidental, show, artist], mixed: true)
            .Select(candidate => candidate.Row.Key)
            .Should()
            .Equal("artist", "show", "performance");
    }

    [Test]
    public void FrequentlyPlayedSongAndItsPerformanceLeadAThinSongMatch()
    {
        var song = Catalog("song", MatchTier.Exact, 2) with
        {
            SameKindCount = 5000,
            Popularity = .99,
        };
        var cover = song with
        {
            Row = (TestRankItem)song.Row with { Key = "cover" },
            SameKindCount = 3000,
            Popularity = .96,
        };
        var performance = Catalog("performance", MatchTier.Exact, 1) with
        {
            SameKindCount = 4000,
            Popularity = .99,
        };

        RankedCandidate
            .Order([cover, performance, song], mixed: true)
            .Select(candidate => candidate.Row.Key)
            .Should()
            .Equal("song", "performance", "cover");
    }

    [Test]
    public void SequencePopularityCompetesWithPerformancesInAll()
    {
        RankedCandidate Sequence(string key, long starts, double percentile) =>
            new(
                new TestRankItem("sequence") { Key = key, TrackPlays = starts },
                new(MatchTier.Exact, ["segue"], 1),
                2,
                Popularity: percentile,
                SameKindCount: starts
            );
        var performance = Catalog("performance", MatchTier.Exact, 1) with
        {
            SameKindCount = 200,
            Popularity = .9,
        };

        RankedCandidate
            .Order(
                [Sequence("cold", 0, 0), performance, Sequence("popular", 2000, .98)],
                mixed: true
            )
            .Select(candidate => candidate.Row.Key)
            .Should()
            .Equal("popular", "performance", "cold");
    }

    [Test]
    public void AllUsesTheCanonicalVenueWhenAnImportedAliasNamesTheSamePlace()
    {
        var document = new SearchDocument
        {
            Id = "venue_hall",
            Kind = "venue",
            Name = "union hall",
            CityLabel = "Portland",
            LocationLabel = "Portland, OR, US",
        };
        var vocabulary = new SearchVocabulary([(document, ["the union"])], []);
        RankedCandidate Venue(string id, string title, string region) =>
            new(
                new SearchDocument
                {
                    Kind = "venue",
                    Id = id,
                    VenueId = id,
                    Name = title,
                    CityLabel = "Portland",
                    LocationLabel = $"Portland, {region}",
                },
                new(MatchTier.Exact, ["title"], 1)
            );
        var samePlace = Venue("imported_venue_union", "the union", "OR");
        var differentPlace = Venue("imported_venue_east", "the union", "ME");
        var canonical = Venue(document.Id, document.Name, "OR");

        VenueSearchIdentity
            .Collapse([samePlace, differentPlace, canonical], vocabulary)
            .Select(candidate => candidate.Row.Key)
            .Should()
            .Equal($"venue:{document.Id}", "venue:imported_venue_east");
    }

    [Test]
    public void AddTracksKeepsAnUnplayableExactMatchAheadOfTheBestPlayableRow()
    {
        var song = Catalog("song", MatchTier.Exact, 2);
        var playable = Catalog("performance", MatchTier.Prefix, 0) with
        {
            Row = (TestRankItem)Catalog("performance", MatchTier.Prefix, 0).Row with
            {
                Playable = true,
            },
        };
        var venue = Catalog("venue", MatchTier.Exact, 2);
        RankedCandidate
            .Order([venue, playable, song], addTracks: true)
            .Select(candidate => candidate.Row.Key)
            .Should()
            .Equal("song", "performance", "venue");
    }

    [TestCase("target")]
    [TestCase("imported_song_target")]
    public void OneExactSongKeepsItsVersionsInExploreAndItsIdentityNearTheTopInAddTracks(
        string songId
    )
    {
        var song = new RankedCandidate(
            new TestRankItem("song")
            {
                Key = "song",
                SongId = songId,
                Name = "Song",
                ArtistSlug = "band",
            },
            new(MatchTier.Exact, ["title"], 1)
        );
        var versions = Enumerable
            .Range(1, 6)
            .Select(index => new RankedCandidate(
                new TestRankItem("performance")
                {
                    Key = $"version-{index}",
                    SongId = songId == "target" ? songId : "",
                    Name = "Song",
                    ArtistSlug = "band",
                    Playable = true,
                },
                new(MatchTier.Exact, ["title"], 1)
            ))
            .ToArray();
        var show = Catalog("show", MatchTier.Partial, 0);
        RankedCandidate[] ranked = [versions[0], song, show, .. versions.Skip(1)];
        SearchDiversity
            .ExplorePage(ranked)
            .Results.Take(7)
            .Select(row => row.Row.Key)
            .Should()
            .Equal(new[] { song.Row.Key }.Concat(versions.Select(row => row.Row.Key)));
        SearchDiversity
            .AddTracksPage(ranked)
            .Take(2)
            .Select(row => row.Row.Key)
            .Should()
            .Equal(versions[0].Row.Key, song.Row.Key);
        var otherSong = song with
        {
            Row = ((TestRankItem)song.Row) with { Key = "other", SongId = "other" },
        };
        SearchDiversity
            .ExplorePage([.. ranked, otherSong])
            .Results.Count(row => row.Playable)
            .Should()
            .Be(3);
    }

    [Test]
    public void AddTracksShowsTheVersionsBeforeDrillInsAndRelatedVersions()
    {
        RankedCandidate Version(string key, bool related = false) =>
            new(
                new TestRankItem("performance")
                {
                    Key = key,
                    SongId = "the-song",
                    Playable = true,
                },
                new(MatchTier.Exact, ["title"], 1),
                Popularity: related ? 1 : 0,
                Related: related
            );
        var versions = Enumerable.Range(1, 8).Select(number => Version($"file-{number}")).ToArray();
        var show = Catalog("show", MatchTier.Exact, 2);
        var otherShow = show with { Row = ((TestRankItem)show.Row) with { Key = "other-show" } };
        var song = Catalog("song", MatchTier.Exact, 2);
        var related = Version("cover", related: true);
        var ranked = RankedCandidate.Order(
            [show, song, otherShow, related, .. versions],
            addTracks: true,
            mixed: true
        );
        var page = SearchDiversity.AddTracksPage(ranked);

        page.Take(versions.Length)
            .Select(candidate => candidate.Row.Key)
            .Should()
            .BeEquivalentTo(versions.Select(candidate => candidate.Row.Key));
        page.Should().ContainSingle(candidate => candidate.Row.Kind == "show");
        page.Should().Contain(candidate => candidate.Row.Kind == "song");
        page.Last().Row.Key.Should().Be("cover");
    }

    private sealed record TestRankItem(string Kind) : ISearchRankItem
    {
        public string Key { get; init; } = "";
        public string Id { get; init; } = "";
        public string SongId { get; init; } = "";
        public string ShowId { get; init; } = "";
        public string SourceId { get; init; } = "";
        public string ArtistSlug { get; init; } = "";
        public string Name { get; init; } = "";
        public long TrackPlays { get; init; }
        public bool Playable { get; init; }
    }

    private static RankedCandidate Catalog(string kind, MatchTier tier, int priority) =>
        new(new TestRankItem(kind) { Key = kind }, new(tier, ["title"], 1), priority);

    private static RankedCandidate Playlist(string key, MatchTier tier, int priority) =>
        new(new TestRankItem("playlist") { Key = key }, new(tier, ["title"], 1), priority);
}
