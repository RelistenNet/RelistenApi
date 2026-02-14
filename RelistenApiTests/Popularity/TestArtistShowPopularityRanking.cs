using System;
using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using Relisten.Api.Models;
using Relisten.Services.Popularity;

namespace RelistenApiTests.Popularity;

[TestFixture]
public class TestArtistShowPopularityRanking
{
    [Test]
    public void RankPopularArtistShows_ShouldSortByHotScoreThenPlays30d()
    {
        var showA = NewShow(1, hotScore: 10, plays30d: 200, plays48h: 15, trendRatio: 1.2);
        var showB = NewShow(2, hotScore: 15, plays30d: 120, plays48h: 12, trendRatio: 1.1);
        var showC = NewShow(3, hotScore: 10, plays30d: 500, plays48h: 20, trendRatio: 1.3);

        var ranked = PopularityService.RankPopularArtistShows(new List<Show> { showA, showB, showC }, 3);

        ranked.Should().HaveCount(3);
        ranked[0].uuid.Should().Be(showB.uuid);
        ranked[1].uuid.Should().Be(showC.uuid);
        ranked[2].uuid.Should().Be(showA.uuid);
    }

    [Test]
    public void RankTrendingArtistShows_ShouldFilterByFloorsAndSortByTrendRatio()
    {
        var below48hFloor = NewShow(1, hotScore: 8, plays30d: 100, plays48h: 8, trendRatio: 10.0);
        var below30dFloor = NewShow(2, hotScore: 8, plays30d: 6, plays48h: 20, trendRatio: 9.0);
        var qualifyingLowTrend = NewShow(3, hotScore: 10, plays30d: 80, plays48h: 11, trendRatio: 1.5);
        var qualifyingHighTrend = NewShow(4, hotScore: 6, plays30d: 400, plays48h: 30, trendRatio: 3.2);

        var ranked = PopularityService.RankTrendingArtistShows(new List<Show>
        {
            below48hFloor,
            below30dFloor,
            qualifyingLowTrend,
            qualifyingHighTrend
        }, 10);

        ranked.Should().HaveCount(2);
        ranked[0].uuid.Should().Be(qualifyingHighTrend.uuid);
        ranked[1].uuid.Should().Be(qualifyingLowTrend.uuid);
    }

    [Test]
    public void CreateArtistPopularTrendingShowsResponse_ShouldReturnEmptyListsWhenNoCandidates()
    {
        var artist = new Artist
        {
            uuid = Guid.NewGuid(),
            name = "Test Artist",
            musicbrainz_id = string.Empty,
            slug = "test-artist",
            sort_name = "Test Artist",
            upstream_sources = Array.Empty<ArtistUpstreamSource>(),
            features = new Features()
        };

        var response =
            PopularityService.CreateArtistPopularTrendingShowsResponse(artist, new List<Show>(), 50);

        response.artist_uuid.Should().Be(artist.uuid);
        response.artist_name.Should().Be(artist.name);
        response.popular_shows.Should().BeEmpty();
        response.trending_shows.Should().BeEmpty();
    }

    [Test]
    public void CreateArtistPopularTrendingShowsResponse_ShouldReturnDeterministicPopularAndTrendingOrdering()
    {
        var artist = NewArtist();
        var popularOnly = NewShow(1, hotScore: 22, plays30d: 1000, plays48h: 8, trendRatio: 10.0);
        var popularAndTrendingTop = NewShow(2, hotScore: 18, plays30d: 550, plays48h: 40, trendRatio: 3.5);
        var popularAndTrendingSecond = NewShow(3, hotScore: 12, plays30d: 450, plays48h: 20, trendRatio: 2.2);
        var lowPopularity = NewShow(4, hotScore: 6, plays30d: 120, plays48h: 12, trendRatio: 1.1);

        var response = PopularityService.CreateArtistPopularTrendingShowsResponse(artist,
            new List<Show>
            {
                popularOnly,
                popularAndTrendingTop,
                popularAndTrendingSecond,
                lowPopularity
            }, 10);

        response.popular_shows.Should().HaveCount(4);
        response.popular_shows[0].uuid.Should().Be(popularOnly.uuid);
        response.popular_shows[1].uuid.Should().Be(popularAndTrendingTop.uuid);
        response.popular_shows[2].uuid.Should().Be(popularAndTrendingSecond.uuid);
        response.popular_shows[3].uuid.Should().Be(lowPopularity.uuid);

        response.trending_shows.Should().HaveCount(3);
        response.trending_shows[0].uuid.Should().Be(popularAndTrendingTop.uuid);
        response.trending_shows[1].uuid.Should().Be(popularAndTrendingSecond.uuid);
        response.trending_shows[2].uuid.Should().Be(lowPopularity.uuid);
    }

    [Test]
    public void CreateArtistPopularTrendingShowsResponse_ShouldReturnEmptyTrendingWhenCandidatesAreLowPlay()
    {
        var artist = NewArtist();
        var lowPlayA = NewShow(5, hotScore: 9, plays30d: 6, plays48h: 30, trendRatio: 4.0);
        var lowPlayB = NewShow(6, hotScore: 8, plays30d: 300, plays48h: 8, trendRatio: 2.5);

        Action act = () => PopularityService.CreateArtistPopularTrendingShowsResponse(artist,
            new List<Show> { lowPlayA, lowPlayB }, 10);

        act.Should().NotThrow();

        var response = PopularityService.CreateArtistPopularTrendingShowsResponse(artist,
            new List<Show> { lowPlayA, lowPlayB }, 10);

        response.popular_shows.Should().HaveCount(2);
        response.trending_shows.Should().BeEmpty();
    }

    private static Show NewShow(int seed, double hotScore, long plays30d, long plays48h, double trendRatio)
    {
        return new Show
        {
            uuid = Guid.Parse($"00000000-0000-0000-0000-{seed.ToString().PadLeft(12, '0')}"),
            artist_uuid = Guid.Parse("10000000-0000-0000-0000-000000000001"),
            display_date = "2025-01-01",
            popularity = new PopularityMetrics
            {
                trend_ratio = trendRatio,
                windows = new PopularityWindows
                {
                    days_30d = new PopularityWindowMetrics { hot_score = hotScore, plays = plays30d },
                    hours_48h = new PopularityWindowMetrics { plays = plays48h }
                }
            }
        };
    }

    private static Artist NewArtist()
    {
        return new Artist
        {
            uuid = Guid.NewGuid(),
            name = "Fixture Artist",
            musicbrainz_id = string.Empty,
            slug = "fixture-artist",
            sort_name = "Fixture Artist",
            upstream_sources = Array.Empty<ArtistUpstreamSource>(),
            features = new Features()
        };
    }
}
