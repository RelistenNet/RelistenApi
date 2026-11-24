using System.Text.Json;
using FluentAssertions;
using Relisten.Search;

namespace RelistenApiTests.Search;

[TestFixture]
public sealed class TestReviewedSearchData
{
    [Test]
    public void ReviewedAliasesAndRoutesHaveUnambiguousUsableKeys()
    {
        static JsonDocument Data(string name)
        {
            using var stream = typeof(CatalogSearchCoordinator).Assembly.GetManifestResourceStream(
                $"Relisten.Search.{name}.json"
            )!;
            return JsonDocument.Parse(stream);
        }
        static string Text(JsonElement row, string field) => row.GetProperty(field).GetString()!;
        static void Date(int day) =>
            DateOnly.ParseExact(
                day.ToString(),
                "yyyyMMdd",
                System.Globalization.CultureInfo.InvariantCulture
            );

        using var placements = Data("ReviewedPlaceAliases");
        var places = ReviewedPlaceAliases.Read(
            System.Text.Encoding.UTF8.GetBytes(placements.RootElement.GetRawText())
        );
        places
            .VenueNames.Select(name => (name.VenueId, name.Normalized))
            .Should()
            .OnlyHaveUniqueItems();
        places
            .CityNames.Select(name => (name.City, name.Region, name.Country, name.Normalized))
            .Should()
            .OnlyHaveUniqueItems();
        places
            .CityNames.Should()
            .OnlyContain(name => name.Region.Length > 0 && name.Country.Length == 2);

        using var nicknames = Data("ReviewedNicknames");
        nicknames
            .RootElement.GetProperty("artist_nicknames")
            .EnumerateArray()
            .Select(row => (Text(row, "artist_slug"), SearchText.Fold(Text(row, "nickname"))))
            .Should()
            .OnlyHaveUniqueItems();
        nicknames
            .RootElement.GetProperty("song_nicknames")
            .EnumerateArray()
            .Select(row =>
                (
                    Text(row, "artist_slug"),
                    Text(row, "song_id"),
                    SearchText.Fold(Text(row, "nickname"))
                )
            )
            .Should()
            .OnlyHaveUniqueItems();
        // Loading the IDs also validates the bundled nickname definitions.
        _ = SearchReviewedNicknames.SetlistSongIds;

        SearchReviewedNamedShows
            .Routes.Select(route => (route.Artist, route.Terms))
            .Should()
            .OnlyHaveUniqueItems();
        foreach (var route in SearchReviewedNamedShows.Routes)
        {
            Date(route.Date);
        }
        foreach (var run in SearchReviewedNamedShows.Runs)
        {
            Date(run.StartDate);
            Date(run.EndDate);
            run.EndDate.Should().BeGreaterThanOrEqualTo(run.StartDate);
        }
        SearchReviewedNamedShows
            .Passages.SelectMany(passage =>
                passage.Phrases.Select(phrase => (passage.Artist, phrase))
            )
            .Should()
            .OnlyHaveUniqueItems();
        SearchReviewedNamedShows
            .Passages.Should()
            .OnlyContain(passage => passage.SongTitles.Length >= 2);
    }
}
