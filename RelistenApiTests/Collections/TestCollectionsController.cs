using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Relisten;
using Relisten.Api.Models;
using Relisten.Controllers;

namespace RelistenApiTests.Collections;

[TestFixture]
public class TestCollectionsController
{
    [Test]
    public void ExposesRequiredCollectionBrowseRoutesOnly()
    {
        var routes = typeof(CollectionsController).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .SelectMany(method => method.GetCustomAttributes<HttpGetAttribute>())
            .SelectMany(attribute => attribute.Template == null ? [] : new[] { attribute.Template })
            .ToList();

        routes.Should().Contain("v3/collections");
        routes.Should().Contain("v3/collections/{collectionUuidOrSlug}");
        routes.Should().Contain("v3/collections/{collectionUuidOrSlug}/artists");
        routes.Should().Contain("v3/collections/{collectionUuidOrSlug}/years");
        routes.Should().Contain("v3/collections/{collectionUuidOrSlug}/years/{yearUuidOrYear}");
        routes.Should().Contain("v3/collections/{collectionUuidOrSlug}/shows/popular-trending");
        routes.Should().Contain("v3/collections/{collectionUuidOrSlug}/shows/recently-added");
        routes.Should().Contain("v3/collections/{collectionUuidOrSlug}/shows/on-this-day");

        routes.Should().NotContain(route => route.Contains("/venues"));
        routes.Should().NotContain(route => route.Contains("/songs"));
        routes.Should().NotContain(route => route.Contains("/shows/{show"));
    }

    [Test]
    public void PopularTrendingResponseUsesStableSnakeCaseShape()
    {
        var response = new CollectionPopularTrendingShowsResponse
        {
            collection_uuid = Guid.NewGuid(),
            collection_slug = "aadam-jacobs",
            collection_name = "Aadam Jacobs Collection",
            popular_shows = [],
            trending_shows = []
        };

        var json = JObject.Parse(JsonConvert.SerializeObject(response,
            RelistenApiJsonOptionsWrapper.ApiV3SerializerSettings));

        json.Properties().Select(p => p.Name).Should().BeEquivalentTo(
            "collection_uuid",
            "collection_slug",
            "collection_name",
            "popular_shows",
            "trending_shows");
    }
}
