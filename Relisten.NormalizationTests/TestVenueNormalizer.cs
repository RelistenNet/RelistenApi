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
    [TestCase("Phoenix AZ", "phoenix|az|US")]
    [TestCase("Morrison, Colorade, USA", "morrison|co|US")]
    [TestCase("Morrison, Colorade", "morrison|co|US")]
    [TestCase("Huber Heights Ohio", "huber heights|oh|US")]
    [TestCase("Broomfield. CO", "broomfield|co|US")]
    [TestCase("Dayton Ohio/Huber Heights Ohio", "dayton|oh|US")]
    [TestCase("Richmond, VIC, Australia", "richmond|vic|AU")]
    [TestCase("Nashville, Tenn.", "nashville|tn|US")]
    [TestCase("Dallas, TX USA", "dallas|tx|US")]
    [TestCase("Lorsch, GER", "lorsch||DE")]
    [TestCase("Manchester, TN, Bonnaroo 2015", "manchester|tn|US")]
    [TestCase("Bozeman, MT", "bozeman|mt|US")]
    [TestCase("Albany, N.Y.", "albany|ny|US")]
    [TestCase("Montage Mountain, Scranton, PA", "scranton|pa|US")]
    [TestCase("1600 Main St, Philadelphia, PA", "philadelphia|pa|US")]
    [TestCase("Montage Mountain, Scranton, PA, USA", "scranton|pa|US")]
    public void LocationsParseToALocality(string location, string expected) =>
        VenueNormalizer.ParseLocation(location)!.Key.Should().Be(expected);

    [TestCase("Stand, Seat 2")]
    [TestCase("Row R, On Aisle)")]
    [TestCase("2022-02-06")]
    [TestCase("CO")]
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

    [TestCase("Pepsi Center - Denver, CO", "denver|co|US")]
    [TestCase("Shorefront Park, Patchogue, NY", "patchogue|ny|US")]
    [TestCase("LOCKN' Festival, Arrington, VA", "arrington|va|US")]
    [TestCase("HOB Cleveland", null)]
    [TestCase("Madison Square Garden", null)]
    [TestCase("Ballroom, U. of Co.", null)]
    public void ARowWithoutALocationCanCarryItInItsName(string name, string? expected) =>
        (VenueNormalizer.LocationInName(name)?.Key).Should().Be(expected);
}
