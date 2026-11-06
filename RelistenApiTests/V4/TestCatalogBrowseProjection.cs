using FluentAssertions;
using Relisten.Api.Models.Api.V4;
using Relisten.Catalog;

namespace RelistenApiTests.V4;

[TestFixture]
public sealed class TestCatalogBrowseProjection
{
    [TestCaseSource(nameof(FeatureMasks))]
    public void Features_use_fixed_decimal_bit_values(CatalogFeatures features, string expected)
    {
        var artist = CatalogBrowseProjection.Artist(new CatalogArtist { Features = features });

        artist.Features.Should().Be(expected);
    }

    // Literal expectations protect the public bit positions independently of the enum.
    private static IEnumerable<TestCaseData> FeatureMasks() =>
        [
            new(new CatalogFeatures(), "0"),
            new(new CatalogFeatures { descriptions = true }, "1"),
            new(new CatalogFeatures { eras = true }, "2"),
            new(new CatalogFeatures { multiple_sources = true }, "4"),
            new(new CatalogFeatures { reviews = true }, "8"),
            new(new CatalogFeatures { ratings = true }, "16"),
            new(new CatalogFeatures { tours = true }, "32"),
            new(new CatalogFeatures { taper_notes = true }, "64"),
            new(new CatalogFeatures { source_information = true }, "128"),
            new(new CatalogFeatures { sets = true }, "256"),
            new(new CatalogFeatures { per_show_venues = true }, "512"),
            new(new CatalogFeatures { per_source_venues = true }, "1024"),
            new(new CatalogFeatures { venue_coords = true }, "2048"),
            new(new CatalogFeatures { songs = true }, "4096"),
            new(new CatalogFeatures { years = true }, "8192"),
            new(new CatalogFeatures { track_md5s = true }, "16384"),
            new(new CatalogFeatures { review_titles = true }, "32768"),
            new(new CatalogFeatures { jam_charts = true }, "65536"),
            new(new CatalogFeatures { setlist_data_incomplete = true }, "131072"),
            new(new CatalogFeatures { track_names = true }, "262144"),
            new(new CatalogFeatures { venue_past_names = true }, "524288"),
            new(new CatalogFeatures { reviews_have_ratings = true }, "1048576"),
            new(new CatalogFeatures { track_durations = true }, "2097152"),
            new(new CatalogFeatures { can_have_flac = true }, "4194304"),
            new(new CatalogFeatures { norm_catalogue = true }, "8388608"),
            new(
                new CatalogFeatures
                {
                    descriptions = true,
                    track_durations = true,
                    norm_catalogue = true,
                },
                "10485761"
            ),
        ];
}
