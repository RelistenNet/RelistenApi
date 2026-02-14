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
            .PropertyType.Should().Be(typeof(IReadOnlyList<Show>));
        typeof(ArtistPopularTrendingShowsResponse).GetProperty(nameof(ArtistPopularTrendingShowsResponse.trending_shows))!
            .PropertyType.Should().Be(typeof(IReadOnlyList<Show>));
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
                    popular_shows = new List<Show> { NewShow(1) },
                    trending_shows = new List<Show> { NewShow(2) }
                },
                new ArtistPopularTrendingShowsResponse
                {
                    artist_uuid = Guid.Parse("20000000-0000-0000-0000-000000000002"),
                    artist_name = "Artist Two",
                    popular_shows = new List<Show>(),
                    trending_shows = new List<Show>()
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

    [Test]
    public void ShowPopularityEndpoints_ShouldDefaultToTop25()
    {
        typeof(PopularityController).GetMethod(nameof(PopularityController.PopularShows))!
            .GetParameters()
            .Single(parameter => parameter.Name == "limit")
            .DefaultValue
            .Should()
            .Be(25);

        typeof(PopularityController).GetMethod(nameof(PopularityController.TrendingShows))!
            .GetParameters()
            .Single(parameter => parameter.Name == "limit")
            .DefaultValue
            .Should()
            .Be(25);

        typeof(PopularityController).GetMethod(nameof(PopularityController.ArtistPopularTrendingShows))!
            .GetParameters()
            .Single(parameter => parameter.Name == "limit")
            .DefaultValue
            .Should()
            .Be(25);

        typeof(PopularityController).GetMethod(nameof(PopularityController.ArtistsPopularTrendingShows))!
            .GetParameters()
            .Single(parameter => parameter.Name == "limit")
            .DefaultValue
            .Should()
            .Be(25);
    }

    [Test]
    public void NormalizeShowLimit_ShouldClampAt25()
    {
        var normalize = typeof(PopularityController).GetMethod("NormalizeShowLimit",
            BindingFlags.NonPublic | BindingFlags.Static);

        normalize.Should().NotBeNull();
        normalize!.Invoke(null, new object[] { 50 }).Should().Be(25);
        normalize.Invoke(null, new object[] { 20 }).Should().Be(20);
        normalize.Invoke(null, new object[] { 0 }).Should().Be(25);
    }

    [Test]
    public void ShowPopularityEndpoints_ShouldReturnShowContract()
    {
        typeof(PopularityController).GetMethod(nameof(PopularityController.PopularShows))!
            .GetCustomAttributes<ProducesResponseTypeAttribute>()
            .Single()
            .Type
            .Should()
            .Be(typeof(Show[]));

        typeof(PopularityController).GetMethod(nameof(PopularityController.TrendingShows))!
            .GetCustomAttributes<ProducesResponseTypeAttribute>()
            .Single()
            .Type
            .Should()
            .Be(typeof(Show[]));
    }

    private static Show NewShow(int seed)
    {
        return new Show
        {
            uuid = Guid.Parse($"00000000-0000-0000-0000-{seed.ToString().PadLeft(12, '0')}"),
            artist_uuid = Guid.Parse("10000000-0000-0000-0000-000000000001"),
            display_date = "2025-01-01",
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
