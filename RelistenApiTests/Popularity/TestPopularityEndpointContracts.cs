using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Relisten;
using Relisten.Api.Models;
using Relisten.Api.Models.Api;
using Relisten.Controllers;

namespace RelistenApiTests.Popularity;

[TestFixture]
public class TestPopularityEndpointContracts
{
    [Test]
    public void ArtistPopularTrendingShows_ShouldDeclareSingleArtistContract()
    {
        var method = typeof(PopularityController).GetMethod(nameof(PopularityController.ArtistPopularTrendingShows));
        method.Should().NotBeNull();

        var route = method!.GetCustomAttribute<HttpGetAttribute>();
        route.Should().NotBeNull();
        route!.Template.Should().Be("v3/artists/{artistIdOrSlug}/shows/popular-trending");

        var produces = method.GetCustomAttributes<ProducesResponseTypeAttribute>().Single();
        produces.Type.Should().Be(typeof(ArtistPopularTrendingShowsResponse));

        typeof(ArtistPopularTrendingShowsResponse).GetProperty(nameof(ArtistPopularTrendingShowsResponse.popular_shows))!
            .PropertyType.Should().Be(typeof(IReadOnlyList<PopularShowListItem>));
        typeof(ArtistPopularTrendingShowsResponse).GetProperty(nameof(ArtistPopularTrendingShowsResponse.trending_shows))!
            .PropertyType.Should().Be(typeof(IReadOnlyList<PopularShowListItem>));
    }

    [Test]
    public void ArtistsPopularTrendingShows_ShouldDeclareListBasedMultiArtistContract()
    {
        var method = typeof(PopularityController).GetMethod(nameof(PopularityController.ArtistsPopularTrendingShows));
        method.Should().NotBeNull();

        var route = method!.GetCustomAttribute<HttpGetAttribute>();
        route.Should().NotBeNull();
        route!.Template.Should().Be("v3/artists/shows/popular-trending");

        var produces = method.GetCustomAttributes<ProducesResponseTypeAttribute>().Single();
        produces.Type.Should().Be(typeof(MultiArtistPopularTrendingShowsResponse));

        typeof(MultiArtistPopularTrendingShowsResponse).GetProperty(nameof(MultiArtistPopularTrendingShowsResponse.artists))!
            .PropertyType.Should().Be(typeof(IReadOnlyList<ArtistPopularTrendingShowsResponse>));
    }

    [Test]
    public void MultiArtistResponseSerialization_ShouldEmitArtistsAsArray()
    {
        var response = new MultiArtistPopularTrendingShowsResponse
        {
            artists = new List<ArtistPopularTrendingShowsResponse>
            {
                new ArtistPopularTrendingShowsResponse
                {
                    artist_uuid = Guid.Parse("10000000-0000-0000-0000-000000000001"),
                    artist_name = "Artist One",
                    popular_shows = new List<PopularShowListItem> { NewShow(1) },
                    trending_shows = new List<PopularShowListItem> { NewShow(2) }
                },
                new ArtistPopularTrendingShowsResponse
                {
                    artist_uuid = Guid.Parse("20000000-0000-0000-0000-000000000002"),
                    artist_name = "Artist Two",
                    popular_shows = new List<PopularShowListItem>(),
                    trending_shows = new List<PopularShowListItem>()
                }
            }
        };

        var json = JsonConvert.SerializeObject(response, RelistenApiJsonOptionsWrapper.ApiV3SerializerSettings);
        var parsed = JObject.Parse(json);

        parsed["artists"]!.Type.Should().Be(JTokenType.Array);
        var artists = (JArray)parsed["artists"]!;
        artists.Count.Should().Be(2);
        artists[0]!["popular_shows"]!.Type.Should().Be(JTokenType.Array);
        artists[0]!["trending_shows"]!.Type.Should().Be(JTokenType.Array);
    }

    private static PopularShowListItem NewShow(int seed)
    {
        return new PopularShowListItem
        {
            show_uuid = Guid.Parse($"00000000-0000-0000-0000-{seed.ToString().PadLeft(12, '0')}"),
            artist_uuid = Guid.Parse("10000000-0000-0000-0000-000000000001"),
            artist_name = "Artist One",
            display_date = "2025-01-01",
            plays_30d = 10,
            plays_48h = 9,
            trend_ratio = 1.0,
            popularity = new PopularityMetrics
            {
                windows = new PopularityWindows
                {
                    days_30d = new PopularityWindowMetrics { plays = 10, hot_score = 3.0 },
                    hours_48h = new PopularityWindowMetrics { plays = 9 }
                }
            }
        };
    }
}
