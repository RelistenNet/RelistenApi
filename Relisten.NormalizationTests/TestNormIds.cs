using System.Text.Json;
using FluentAssertions;
using Relisten.Normalization;

namespace Relisten.NormalizationTests;

public class TestNormIds
{
    [Test]
    public void ANewIdHasItsKindsPrefixAndEightLowercaseBase32Characters()
    {
        VenueId.New().Value.Should().MatchRegex("^venue_[0-9a-hjkmnp-tv-z]{8}$");
        SongId.New().Value.Should().MatchRegex("^song_[0-9a-hjkmnp-tv-z]{8}$");
        Enumerable.Range(0, 1000).Select(_ => VenueId.New()).Distinct().Should().HaveCount(1000);
    }

    [TestCase("song_k3m9x2qa")]
    [TestCase("venue_K3M9X2QA")]
    [TestCase("venue_k3m9x2q")]
    [TestCase("venue_k3m9x2qai")]
    [TestCase("venue_k3m9x2qi")]
    [TestCase("01a17eb8-dfa2-7542-89f1-c5783060a4c4")]
    public void AnythingElseIsNotAVenueId(string value)
    {
        FluentActions.Invoking(() => VenueId.Parse(value)).Should().Throw<FormatException>();
    }

    [Test]
    public void AnIdTravelsAsItsString()
    {
        var venue = VenueId.Parse("venue_k3m9x2qa");

        var json = JsonSerializer.Serialize(new { venue, song = (SongId?)null });

        json.Should().Be("""{"venue":"venue_k3m9x2qa","song":null}""");
        JsonSerializer.Deserialize<VenueId>("\"venue_k3m9x2qa\"").Should().Be(venue);
        FluentActions
            .Invoking(() => JsonSerializer.Deserialize<VenueId>("\"song_k3m9x2qa\""))
            .Should()
            .Throw<FormatException>();
    }
}
