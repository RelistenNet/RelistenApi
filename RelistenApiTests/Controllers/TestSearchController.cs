using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Relisten.Api.Models;
using Relisten.Controllers;

namespace RelistenApiTests.Controllers;

[TestFixture]
public class TestSearchController
{
    [Test]
    public async Task Search_WhenQueryIsMissing_ReturnsCompleteEmptyResults()
    {
        var result = await new SearchController(null!, null!, null!, null!).Search(null);

        var results = ExtractResults(result);
        AssertEmptyResultsAreComplete(results);
    }

    [Test]
    public async Task Search_WhenQueryIsTooShort_ReturnsCompleteEmptyResults()
    {
        var result = await new SearchController(null!, null!, null!, null!).Search("tw");

        var results = ExtractResults(result);
        AssertEmptyResultsAreComplete(results);
    }

    [Test]
    public async Task Search_WhenAllBucketsAreDisabled_ReturnsCompleteEmptyResults()
    {
        var result = await new SearchController(null!, null!, null!, null!).Search(
            "tweezer",
            artist_uuid: null,
            artists: false,
            shows: false,
            songs: false,
            sources: false,
            tours: false,
            venues: false);

        var results = ExtractResults(result);
        AssertEmptyResultsAreComplete(results);
    }

    [Test]
    public void Search_UsesArtistUuidQueryParameter()
    {
        var parameters = typeof(SearchController).GetMethod(nameof(SearchController.Search))!.GetParameters();

        parameters.Select(parameter => parameter.Name).Should().Contain("artist_uuid");
        parameters.Select(parameter => parameter.Name).Should().NotContain("artist_id");
        parameters.Single(parameter => parameter.Name == "artist_uuid").ParameterType.Should().Be(typeof(Guid?));
    }

    private static SearchResults ExtractResults(IActionResult result)
    {
        result.Should().BeOfType<JsonResult>();
        return ((JsonResult) result).Value.Should().BeOfType<SearchResults>().Subject;
    }

    private static void AssertEmptyResultsAreComplete(SearchResults results)
    {
        results.Artists.Should().BeEmpty();
        results.Shows.Should().BeEmpty();
        results.Songs.Should().BeEmpty();
        results.Sources.Should().BeEmpty();
        results.Tours.Should().BeEmpty();
        results.Venues.Should().BeEmpty();
    }
}
