using System;
using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using Relisten.Api.Models;
using Relisten.Api.Models.Api;
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

        var ranked = PopularityService.RankPopularArtistShows(new List<PopularShowListItem> { showA, showB, showC }, 3);

        ranked.Should().HaveCount(3);
        ranked[0].show_uuid.Should().Be(showB.show_uuid);
        ranked[1].show_uuid.Should().Be(showC.show_uuid);
        ranked[2].show_uuid.Should().Be(showA.show_uuid);
        ranked[0].rank.Should().Be(1);
        ranked[1].rank.Should().Be(2);
        ranked[2].rank.Should().Be(3);
    }

    [Test]
    public void RankTrendingArtistShows_ShouldFilterByFloorsAndSortByTrendRatio()
    {
        var below48hFloor = NewShow(1, hotScore: 8, plays30d: 100, plays48h: 8, trendRatio: 10.0);
        var below30dFloor = NewShow(2, hotScore: 8, plays30d: 6, plays48h: 20, trendRatio: 9.0);
        var qualifyingLowTrend = NewShow(3, hotScore: 10, plays30d: 80, plays48h: 11, trendRatio: 1.5);
        var qualifyingHighTrend = NewShow(4, hotScore: 6, plays30d: 400, plays48h: 30, trendRatio: 3.2);

        var ranked = PopularityService.RankTrendingArtistShows(new List<PopularShowListItem>
        {
            below48hFloor,
            below30dFloor,
            qualifyingLowTrend,
            qualifyingHighTrend
        }, 10);

        ranked.Should().HaveCount(2);
        ranked[0].show_uuid.Should().Be(qualifyingHighTrend.show_uuid);
        ranked[1].show_uuid.Should().Be(qualifyingLowTrend.show_uuid);
        ranked[0].rank.Should().Be(1);
        ranked[1].rank.Should().Be(2);
    }

    [Test]
    public void CreateArtistPopularTrendingShowsResponse_ShouldReturnEmptyArraysWhenNoCandidates()
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
            PopularityService.CreateArtistPopularTrendingShowsResponse(artist, Array.Empty<PopularShowListItem>(), 50);

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
            new List<PopularShowListItem>
            {
                popularOnly,
                popularAndTrendingTop,
                popularAndTrendingSecond,
                lowPopularity
            }, 10);

        response.popular_shows.Should().HaveCount(4);
        response.popular_shows[0].show_uuid.Should().Be(popularOnly.show_uuid);
        response.popular_shows[1].show_uuid.Should().Be(popularAndTrendingTop.show_uuid);
        response.popular_shows[2].show_uuid.Should().Be(popularAndTrendingSecond.show_uuid);
        response.popular_shows[3].show_uuid.Should().Be(lowPopularity.show_uuid);
        response.popular_shows[0].rank.Should().Be(1);
        response.popular_shows[3].rank.Should().Be(4);

        response.trending_shows.Should().HaveCount(3);
        response.trending_shows[0].show_uuid.Should().Be(popularAndTrendingTop.show_uuid);
        response.trending_shows[1].show_uuid.Should().Be(popularAndTrendingSecond.show_uuid);
        response.trending_shows[2].show_uuid.Should().Be(lowPopularity.show_uuid);
        response.trending_shows[0].rank.Should().Be(1);
        response.trending_shows[2].rank.Should().Be(3);
    }

    [Test]
    public void CreateArtistPopularTrendingShowsResponse_ShouldReturnEmptyTrendingWhenCandidatesAreLowPlay()
    {
        var artist = NewArtist();
        var lowPlayA = NewShow(5, hotScore: 9, plays30d: 6, plays48h: 30, trendRatio: 4.0);
        var lowPlayB = NewShow(6, hotScore: 8, plays30d: 300, plays48h: 8, trendRatio: 2.5);

        Action act = () => PopularityService.CreateArtistPopularTrendingShowsResponse(artist,
            new List<PopularShowListItem> { lowPlayA, lowPlayB }, 10);

        act.Should().NotThrow();

        var response = PopularityService.CreateArtistPopularTrendingShowsResponse(artist,
            new List<PopularShowListItem> { lowPlayA, lowPlayB }, 10);

        response.popular_shows.Should().HaveCount(2);
        response.trending_shows.Should().BeEmpty();
    }

    private static PopularShowListItem NewShow(int seed, double hotScore, long plays30d, long plays48h, double trendRatio)
    {
        return new PopularShowListItem
        {
            show_uuid = Guid.Parse($"00000000-0000-0000-0000-{seed.ToString().PadLeft(12, '0')}"),
            artist_uuid = Guid.Parse("10000000-0000-0000-0000-000000000001"),
            artist_name = "Test Artist",
            display_date = "2025-01-01",
            plays_30d = plays30d,
            plays_48h = plays48h,
            trend_ratio = trendRatio,
            popularity = new PopularityMetrics
            {
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
