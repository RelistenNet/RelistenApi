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
        plan.Left.Should().ContainSingle().Which.Should().HaveCount(3);
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

    [Test]
    public void ATownWithoutItsStateTakesTheOnlyStateItHas()
    {
        var rows = VenueState.FillRegions(
            [
                Row("The Theatre at MSG", "New York City"),
                Row("Madison Square Garden", "New York, NY"),
                Row("Crystal Ballroom", "Portland"),
            ],
            [new Locality("Portland", "OR", "US"), new Locality("Portland", "ME", "US")]
        );

        rows.Select(r => r.Locality!.Key)
            .Should()
            .Equal("new york|ny|US", "new york|ny|US", "portland||");
    }

    [Test]
    public void ARowWithoutATownJoinsOnlyAVenueWhoseNameIsUnique()
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
        var capitolA = new CatalogueVenue(
            Guid.NewGuid(),
            "Capitol Theatre",
            "site",
            null,
            "Port Chester",
            "NY",
            "US"
        );
        var capitolB = new CatalogueVenue(
            Guid.NewGuid(),
            "Capitol Theatre",
            "site",
            null,
            "Passaic",
            "NJ",
            "US"
        );
        var rows = new[]
        {
            Row("Madison Square Garden", "Unknown Location"),
            Row("Capitol Theatre", "Unknown Location"),
        };

        var plan = VenueMatcher.Match(
            State(
                rows,
                [garden, capitolA, capitolB],
                [
                    ("Madison Square Garden", garden.Uuid),
                    ("Capitol Theatre", capitolA.Uuid),
                    ("Capitol Theatre", capitolB.Uuid),
                ]
            )
        );

        plan.Evidence.Should().ContainSingle().Which.VenueUuid.Should().Be(garden.Uuid);
        // Two Capitol Theatres: the model decides with both candidates in front of it.
        plan.Left.Single().Select(r => r.Name).Should().Equal("Capitol Theatre");
    }

    [Test]
    public void ARareTownSpellingFoldsIntoTheCommonOne()
    {
        var rows = VenueState.FoldCities([
            Row("1st Bank Center", "Broomfield, CO", 30),
            Row("1st Bank Center", "brommfield, CO", 2),
            Row("Fox Theatre", "Boulder, CO", 30),
        ]);

        rows.Select(r => r.Locality!.City).Should().Equal("Broomfield", "Broomfield", "Boulder");
    }

    [TestCase("msg", "madison square garden", true)]
    [TestCase("tweeter center", "tweeter center at the waterfront", true)]
    [TestCase("hammerstien ballroom", "hammerstein ballroom", true)]
    [TestCase("fillmore east", "fillmore west", false)]
    [TestCase("fox theater", "orpheum theater", false)]
    public void NamesThatCouldBeOnePlace(string a, string b, bool expected) =>
        VenueMatcher.Alike(a, b).Should().Be(expected);

    [Test]
    public void SeatsInTheLocationDontMakeAnotherGarden()
    {
        var rows = new[]
        {
            Row("Madison Square Garden", "New York, NY", 50),
            Row("Madison Square Garden", "Stand, Seat 2", 1),
            Row("Madison Square Garden", "Row R, On Aisle)", 1),
        };

        var plan = VenueMatcher.Match(State(rows));

        plan.NewVenues.Should().ContainSingle();
        plan.Evidence.Select(e => e.VenueUuid).Distinct().Should().ContainSingle();
    }
}
