using FluentAssertions;
using Relisten.Normalization.Venues;

namespace Relisten.NormalizationTests;

/// Legacy venue names and locations from production (2026-10-23).
public class TestVenueNormalizer
{
    [TestCase("Madison Square Garden", "madison square garden")]
    [TestCase("The Fillmore", "fillmore")]
    [TestCase("Fox Theatre", "fox theater")]
    [TestCase("Red Rocks Amphitheatre", "red rocks amphitheater")]
    [TestCase("The Theatre at Madison Square Garden", "theater at madison square garden")]
    [TestCase("Wells Fargo Centre", "wells fargo center")]
    [TestCase("Unknown Location", null)]
    [TestCase("TBA", null)]
    [TestCase("The", "the")]
    public void NamesFoldToOneKey(string name, string? expected) =>
        VenueNormalizer.Key(name).Should().Be(expected);

    [TestCase("New York, NY", "new york|ny|US")]
    [TestCase("New York, New York", "new york|ny|US")]
    [TestCase("New York City, NY", "new york|ny|US")]
    [TestCase("NYC, NY", "new york|ny|US")]
    [TestCase("NY, NY", "new york|ny|US")]
    [TestCase("Chicago, Illinois", "chicago|il|US")]
    [TestCase("Montreal, Quebec", "montreal|qc|CA")]
    [TestCase("Cambridge, MA, USA", "cambridge|ma|US")]
    [TestCase("London, England", "london||GB")]
    [TestCase("Portland, ME", "portland|me|US")]
    [TestCase("Portland, OR", "portland|or|US")]
    [TestCase("Tokyo, Kanto", "tokyo|kanto|")]
    [TestCase("Richmond, NSW, Australia", "richmond|nsw|AU")]
    [TestCase("Richmond, VIC, Australia", "richmond|vic|AU")]
    public void LocationsParseToALocality(string location, string expected) =>
        VenueNormalizer.ParseLocation(location)!.Key.Should().Be(expected);

    [TestCase("Unknown Location")]
    [TestCase("")]
    [TestCase(null)]
    public void PlaceholdersHaveNoLocality(string? location) =>
        VenueNormalizer.ParseLocation(location).Should().BeNull();

    [TestCase("msg", "madison square garden", true)]
    [TestCase("msg", "theater at madison square garden", false)]
    [TestCase("bgca", "bill graham civic auditorium", true)]
    [TestCase("madison", "madison square garden", false)]
    public void AShortNameIsALongNamesInitials(string shortKey, string fullKey, bool expected) =>
        VenueNormalizer.IsInitialsOf(shortKey, fullKey).Should().Be(expected);
}
