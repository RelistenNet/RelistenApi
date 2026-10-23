using FluentAssertions;
using Relisten.Normalization;
using Relisten.Normalization.Venues;

namespace Relisten.NormalizationTests;

public class TestVenueMatcher
{
    private static LegacyVenue Row(
        string name,
        string location,
        int uses = 10,
        string? id = null
    ) =>
        new(Guid.NewGuid(), Guid.NewGuid(), name, location, uses, 1990, 2000)
        {
            ExternalId = VenueState.ParseExternalId(id),
        };

    private static VenueState State(
        IReadOnlyList<LegacyVenue> rows,
        IReadOnlyList<CatalogueVenue>? venues = null,
        IReadOnlyList<(string Name, Guid Venue)>? names = null
    ) =>
        new()
        {
            Unmapped = rows,
            Venues = (venues ?? []).ToDictionary(v => v.Uuid),
            Names = (names ?? []).ToLookup(n => VenueNormalizer.Key(n.Name)!, n => n.Venue),
            ExternalIds = new Dictionary<(string, string), Guid>(),
        };

    [Test]
    public void OneSpellingInATownIsOneNewVenueAcrossArtists()
    {
        var rows = new[]
        {
            Row("Red Rocks Amphitheatre", "Morrison, CO", 40),
            Row("Red Rocks Amphitheater", "Morrison, Colorado", 12),
            Row("The Red Rocks Amphitheatre", "Morrison, CO", 3),
        };

        var plan = VenueMatcher.Match(State(rows));

        plan.NewVenues.Should().ContainSingle().Which.Name.Should().Be("Red Rocks Amphitheatre");
        plan.Evidence.Select(e => e.VenueUuid).Distinct().Should().ContainSingle();
        plan.Left.Should().BeEmpty();
    }

    [Test]
    public void NamesakesInTwoTownsAreTwoVenues()
    {
        var plan = VenueMatcher.Match(
            State([Row("Fox Theatre", "Oakland, CA"), Row("Fox Theatre", "Boulder, CO")])
        );

        plan.NewVenues.Select(v => v.City).Should().BeEquivalentTo("Oakland", "Boulder");
    }

    [Test]
    public void TwoSpellingsInATownAreLeftForTheModel()
    {
        var rows = new[]
        {
            Row("Madison Square Garden", "New York, NY"),
            Row("MSG", "NY, NY"),
            Row("The Theater at Madison Square Garden", "New York, New York"),
        };

        var plan = VenueMatcher.Match(State(rows));

        plan.NewVenues.Should().BeEmpty();
        plan.Left.Should().HaveCount(3);
    }

    [Test]
    public void AKnownVenueTakesItsSpellingsAndItsInitials()
    {
        var garden = new CatalogueVenue(
            Guid.NewGuid(),
            "Madison Square Garden",
            "site",
            null,
            "New York",
            "NY",
            "US"
        );
        var rows = new[]
        {
            Row("Madison Square Garden", "New York City, NY"),
            Row("MSG", "NY, NY"),
        };

        var plan = VenueMatcher.Match(
            State(rows, [garden], [("Madison Square Garden", garden.Uuid)])
        );

        plan.Evidence.Should().OnlyContain(e => e.VenueUuid == garden.Uuid);
        plan.Evidence.Select(e => e.Confidence).Should().BeEquivalentTo([0.95f, 0.9f]);
        plan.Names.Select(n => n.Name).Should().Equal("MSG");
    }

    [Test]
    public void RowsSharingAnExternalIdAreOneVenueWhateverTheirSpelling()
    {
        var rows = new[]
        {
            Row("Tweeter Center", "Camden, NJ", id: "setlistfm:23d6b4e3"),
            Row("Tweeter Center at the Waterfront", "Camden, NJ", id: "setlistfm:23d6b4e3"),
        };

        var plan = VenueMatcher.Match(State(rows));

        plan.NewVenues.Should().ContainSingle();
        plan.ExternalIds.Should().ContainSingle().Which.Key.Should().Be("23d6b4e3");
    }

    [Test]
    public void APlaceholderIsUnplaced()
    {
        VenueMatcher
            .Match(State([Row("Unknown Venue", "Unknown Location")]))
            .Unplaced.Should()
            .HaveCount(1);
    }
}
