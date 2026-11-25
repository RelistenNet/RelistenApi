using System.Collections.Frozen;
using System.Collections.Generic;
using FluentAssertions;
using Relisten.Search;
using Relisten.Services.Popularity;

namespace RelistenApiTests.Search;

[TestFixture]
public sealed class TestSearchPlayTieBreak
{
    private const string Title = "same title";
    private static readonly SearchInput Input = new(Title);
    private static readonly SearchQuery Query = new(Title);

    [Test]
    public void MorePlayedExactPerformanceWinsAndPopularPartialStaysBelow()
    {
        var first = Performance("one", 20);
        var preferred = Performance("two", 70);
        var partial = Performance("three", 100_000) with { Name = "same title reprise" };

        SearchPlayTieBreak
            .Rank(Input, Query, [first, preferred, partial], CatalogQueryEvidence.Empty)
            .Should()
            .Equal(preferred, first, partial);
    }

    [Test]
    public void SongAndPerformanceCountsDoNotBreakTiesWithEachOther()
    {
        var first = Performance("one", 20);
        var song = Song("two");
        var later = Performance("three", 70);
        var evidence = Evidence(new() { [song.Id] = 10_000 });

        SearchPlayTieBreak
            .Rank(Input, Query, [first, song, later], evidence)
            .Should()
            .Equal(first, song, later);
    }

    [Test]
    public void ATitleCountCanBreakATieWithACanonicalSongCount()
    {
        var first = Song("one");
        var second = Song("two");
        var titleKey = PopularityPerformanceKey.ForStorage(
            PopularityPerformanceKey.RawTitle(Title)
        );
        var evidence = Evidence(new() { [first.Id] = 20 }, new() { [("two", titleKey)] = 70 });

        SearchPlayTieBreak
            .Rank(Input, Query, [first, second], evidence)
            .Should()
            .Equal(second, first);
    }

    [Test]
    public void ASongWithoutATitleCountKeepsItsPlace()
    {
        var first = Song("one");
        var missing = Song("two");
        var later = Song("three");
        var evidence = Evidence(new() { [first.Id] = 20, [later.Id] = 70 });

        SearchPlayTieBreak
            .Rank(Input, Query, [first, missing, later], evidence)
            .Should()
            .Equal(first, missing, later);
    }

    [Test]
    public void ABoostedTieStopsComparisonEvenWithoutAPlayCount()
    {
        var first = Performance("one", 20);
        var second = Performance("two", 70);
        var boosted = Performance("anchor", 0);
        var later = Performance("three", 10);
        var last = Performance("four", 100);
        var artist = new SearchDocument
        {
            Id = "artist_anchor",
            Kind = "artist",
            Name = "anchor",
            ArtistSlug = "anchor",
        };
        var evidence = Evidence([], vocabulary: new SearchVocabulary([(artist, [])], []));

        SearchPlayTieBreak
            .Rank(
                Input with
                {
                    BoostArtistIds = [artist.Id],
                },
                Query,
                [first, second, boosted, later, last],
                evidence
            )
            .Should()
            .Equal(second, first, boosted, later, last);
    }

    private static CatalogQueryEvidence Evidence(
        Dictionary<string, long> songPlays,
        Dictionary<(string, string), long>? titlePlays = null,
        SearchVocabulary? vocabulary = null
    ) =>
        new(
            "entities_fixture",
            vocabulary ?? SearchVocabulary.Empty,
            SearchPassages.Empty,
            songPlays.ToFrozenDictionary(),
            FrozenSet<string>.Empty,
            titlePlays?.ToFrozenDictionary()
        );

    private static SearchDocument Song(string artist) =>
        new()
        {
            Id = "song_" + artist,
            Kind = "song",
            ArtistSlug = artist,
            Name = Title,
        };

    private static SearchDocument Performance(string artist, long starts) =>
        new()
        {
            Id = "performance_" + artist,
            ArtistSlug = artist,
            Kind = "performance",
            Name = Title,
            TrackPlays = starts,
            Preview = new(
                "track_" + artist,
                "source",
                "Set 1",
                1,
                1,
                300,
                starts,
                false,
                false,
                Title,
                "title"
            ),
        };
}
