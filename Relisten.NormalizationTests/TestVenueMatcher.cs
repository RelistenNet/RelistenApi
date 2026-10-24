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
    [TestCase("winterland arena", "winterland ballroom", true)]
    [TestCase("red rock ampitheater", "red rocks amphitheater", true)]
    [TestCase("fillmore west", "fillmore auditorium", false)]
    [TestCase("fox theater", "orpheum theater", false)]
    [TestCase("dobson ice arena", "dobson arena", true)]
    [TestCase("pnc bank arts center", "pnc bank center", true)]
    [TestCase("northrop auditorium u of minn", "northrop auditorium u of minnesota", true)]
    [TestCase("starr hill music hall", "star hill music hall", true)]
    [TestCase("garage", "garrage", true)]
    [TestCase("13 13 club", "13x13 club", true)]
    [TestCase("event center borgata casino", "borgata hotel and casino event center", true)]
    [TestCase(
        "alpha delta pi university of pennsylvania",
        "pi kappa alpha university of pennsylvania",
        false
    )]
    [TestCase("this tent bonnaroo", "that tent bonnaroo", false)]
    [TestCase("terminal island correctional fac", "terminal island correctional facility", true)]
    [TestCase("smiths old bar", "smiths olde bar", true)]
    [TestCase("elbo room", "elbow room", true)]
    [TestCase("coliseum arena", "coliseum arean", true)]
    [TestCase("woodmen of world hall", "woodsman of world hall", true)]
    [TestCase("tree house brewing", "tree house brewery", true)]
    [TestCase("cafe 210 west", "cafe 10 west", false)]
    [TestCase("vic", "vic theater", true)]
    [TestCase("continential arena", "continental airlines arena", true)]
    public void NamesThatCouldBeOnePlace(string a, string b, bool expected)
    {
        VenueMatcher.Alike(a, b).Should().Be(expected);
        VenueMatcher.Alike(b, a).Should().Be(expected);
    }

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

    [Test]
    public void AnIdLearnedInThePassPlacesEveryRowCarryingIt()
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
        // "The Garden" comes first and only shares the id the second row teaches the venue.
        var rows = new[]
        {
            Row("The Garden", "New York, NY", id: "setlistfm:3bd6bc5c"),
            Row("Madison Square Garden", "New York, NY", id: "setlistfm:3bd6bc5c"),
        };

        var plan = VenueMatcher.Match(
            State(rows, [garden], [("Madison Square Garden", garden.Uuid)])
        );

        plan.NewVenues.Should().BeEmpty();
        plan.Evidence.Should().OnlyContain(e => e.VenueUuid == garden.Uuid).And.HaveCount(2);
    }

    [Test]
    public void ANameKnownInANeighbouringTownGoesToTheModel()
    {
        var known = new CatalogueVenue(
            Guid.NewGuid(),
            "Higher Ground",
            "site",
            null,
            "Burlington",
            "VT",
            "US"
        );

        var plan = VenueMatcher.Match(
            State(
                [Row("Higher Ground", "South Burlington, VT")],
                [known],
                [("Higher Ground", known.Uuid)]
            )
        );

        plan.NewVenues.Should().BeEmpty();
        plan.Left.Should().ContainSingle();
    }

    [Test]
    public void SpacingDoesNotMakeAnotherVenue()
    {
        var plan = VenueMatcher.Match(
            State([
                Row("1st Bank Center", "Broomfield, CO", 20),
                Row("1stBank Center", "Broomfield, CO", 5),
            ])
        );

        // One name in two spacings, in one town: one venue.
        plan.NewVenues.Should().ContainSingle();
        plan.Evidence.Select(e => e.VenueUuid).Distinct().Should().ContainSingle();
        plan.Left.Should().BeEmpty();
    }

    [Test]
    public void ATypodStateJoinsTheVenueInTheRealOne()
    {
        var rows = new[]
        {
            Row("Red Rocks Amphitheatre", "Morrison, CO", 130),
            Row("Red Rocks Amphitheatre", "Morrison, CA", 1),
        };

        var plan = VenueMatcher.Match(State(VenueState.WithoutStateTypos(rows)));

        plan.NewVenues.Should().ContainSingle().Which.Region.Should().Be("CO");
        plan.Evidence.Select(e => e.VenueUuid).Distinct().Should().ContainSingle();
    }

    [Test]
    public void AStateCodeThatIsACountryCodeNamesTheCountryTheTownIsIn()
    {
        var rows = VenueState.CountryCodes(
            [
                Row("Paradiso", "Amsterdam, NL"),
                Row("Paradiso", "Amsterdam, Netherlands"),
                Row("Field House", "Toronto, CA"),
                Row("Massey Hall", "Toronto, ON, Canada"),
                Row("Tivoli Gardens", "Copenhagen, DE"),
                Row("Tivoli Gardens", "Copenhagen, Denmark"),
                Row("Paris Theater", "Paris, TX"),
                Row("Olympia", "Paris, France"),
            ],
            []
        );

        rows.Select(r => r.Locality!.Key)
            .Should()
            .Equal(
                "amsterdam||NL",
                "amsterdam||NL",
                "toronto|on|CA",
                "toronto|on|CA",
                // DE is Germany's code, not Denmark's; TX names no country.
                "copenhagen|de|US",
                "copenhagen||DK",
                "paris|tx|US",
                "paris||FR"
            );
    }

    [Test]
    public void OneNameInATownOfOneNameInTwoStatesIsTheModels()
    {
        var plan = VenueMatcher.Match(
            State([
                Row("The Caverns Amphitheater", "Pelham, TN", 3),
                Row("The Caverns Amphitheater", "Pelham, AL", 2),
            ])
        );

        plan.NewVenues.Should().BeEmpty();
        plan.Left.Should().ContainSingle();
    }

    [Test]
    public void LettersWithPeriodsAreOneWord()
    {
        var plan = VenueMatcher.Match(
            State([
                Row("U.S. Cellular Center", "Asheville, NC", 6),
                Row("US Cellular Center", "Asheville, NC", 2),
            ])
        );

        plan.NewVenues.Should().ContainSingle();
        VenueNormalizer.Key("McFarlin Auditorium, S.M.U.").Should().Be("mcfarlin auditorium smu");
        VenueNormalizer.Key("Ohio U").Should().Be("ohio university");
    }

    [TestCase("Wakarusa - Main Stage", "Main Stage - Wakarusa")]
    [TestCase("Notre Dame University", "University of Notre Dame")]
    [TestCase("Water St. Music Hall", "Water Street Music Hall")]
    [TestCase("Rock Island Brewing Co.", "Rock Island Brewing Company")]
    [TestCase("Graffiti's", "Graffiti")]
    [TestCase("Oak Mountain Ampitheatre", "Oak Mountain Amphitheatre")]
    [TestCase("The Pavilion at the Toyota Music Factory", "The Pavilion at Toyota Music Factory")]
    [TestCase("1stBank Center", "1st Bank Center")]
    public void SpellingsOfOneNameAreOneName(string a, string b) =>
        VenueNormalizer
            .SameName(VenueNormalizer.Key(a)!, VenueNormalizer.Key(b)!)
            .Should()
            .BeTrue();

    [TestCase("Fillmore East", "Fillmore West")]
    [TestCase("This Tent", "This Other Tent")]
    [TestCase("Walnut Valley Festival Stage 1", "Walnut Valley Festival Stage 2")]
    public void OtherNamesAreNot(string a, string b) =>
        VenueNormalizer
            .SameName(VenueNormalizer.Key(a)!, VenueNormalizer.Key(b)!)
            .Should()
            .BeFalse();

    [Test]
    public void AnAbbreviatedWordIsOneFamilyForTheModel()
    {
        var plan = VenueMatcher.Match(
            State([
                Row("Terminal Island Correctional Fac.", "San Pedro, CA, USA", 1),
                Row("Terminal Island Correctional Facility", "San Pedro, CA", 1),
            ])
        );

        plan.NewVenues.Should().BeEmpty();
        plan.Left.Should().ContainSingle();
    }

    [TestCase("Schottenstein Center, OSU", "Jerome Schottenstein Center", "Columbus", true)]
    [TestCase("Shrine Exposition Hall", "Shrine Exhibition Hall", "Los Angeles", true)]
    [TestCase("Portland Civic Auditorium", "Portland Memorial Coliseum", "Portland", false)]
    [TestCase("Cafe 210 West", "Cafe 10 West", "State College", false)]
    public void NamesSharingAPlaceWordInATownAreOneFamily(
        string a,
        string b,
        string town,
        bool shared
    ) =>
        VenueMatcher
            .ShareADistinctiveWord(
                VenueNormalizer.Key(a)!,
                VenueNormalizer.Key(b)!,
                VenueNormalizer.Normalize(town).Split(' ').ToHashSet()
            )
            .Should()
            .Be(shared);

    [Test]
    public void ABracketedNoteIsNotPartOfTheName() =>
        VenueNormalizer
            .Key("Sugar Creek Music Festival [24bit]")
            .Should()
            .Be("sugar creek music festival");

    [Test]
    public void TheTownsNameInAVenueNameIsOneFamily()
    {
        var plan = VenueMatcher.Match(
            State([
                Row("Hampton Coliseum", "Hampton, VA", 30),
                Row("Coliseum", "Hampton, VA", 2),
                Row("Prudental Center Newark NJ", "Newark, NJ", 1),
                Row("Prudential Center", "Newark, NJ", 5),
            ])
        );

        plan.NewVenues.Should().BeEmpty();
        plan.Left.Should().HaveCount(2);
    }

    [Test]
    public void OneNameInTwoStatesIsTwoFamilies()
    {
        var families = VenueMatcher.Families([
            Row("House of Blues", "Chicago, IL"),
            Row("House of Blues", "Dallas, TX"),
            Row("House of Blues", "Unknown"),
        ]);

        families.Should().HaveCount(3);
    }

    [Test]
    public void ATownNoOtherVenueIsFiledUnderTakesTheNamesTown()
    {
        var rows = VenueState.WithoutLoneTowns([
            Row("Czar's", "St. Joseph, MI", 4),
            Row("Silver Beach", "St. Joseph, MI", 4),
            Row("Czar's", "Days Inn St Joseph, MI", 1),
            Row("Cat's Cradle", "Carrboro, NC", 13),
            Row("Cat's Cradle", "Chapel Hill, NC", 3),
            Row("Varsity Theatre", "Chapel Hill, NC", 3),
        ]);

        rows.Select(r => r.Locality!.City)
            .Should()
            .Equal(
                "St. Joseph",
                "St. Joseph",
                "St. Joseph",
                "Carrboro",
                "Chapel Hill",
                "Chapel Hill"
            );
    }

    [Test]
    public void AStateCodeALetterOffAcrossABorderIsATypo()
    {
        var rows = VenueState.WithoutCodeTypos([
            Row("Civic Auditorium", "Omaha, NE", 20),
            Row("The Music Box", "Omaha, NB", 2),
            Row("Symphony Hall", "Springfield, MA", 20),
            Row("Shrine Mosque", "Springfield, MO", 2),
        ]);

        rows.Select(r => r.Locality!.Key)
            .Should()
            .Equal("omaha|ne|US", "omaha|ne|US", "springfield|ma|US", "springfield|mo|US");
    }

    [Test]
    public void AVenueNameInFrontOfATownIsNotPartOfIt()
    {
        var rows = VenueState.WithoutPlacesInTowns([
            Row("Sherman Theater", "Stroudsburg, PA", 11),
            Row("The Sherman Theater Stroudsburg, PA", "The Sherman Theater Stroudsburg, PA", 2),
            Row("Higher Ground", "South Burlington, VT"),
            Row("Flynn Theater", "Burlington, VT"),
            Row("Kravis Center", "West Palm Beach, FL"),
            Row("Sunfest", "West Palm Beach, FL"),
            Row("Royal Poinciana", "Palm Beach, FL"),
        ]);

        rows.Select(r => r.Locality!.Key)
            .Should()
            .Equal(
                "stroudsburg|pa|US",
                "stroudsburg|pa|US",
                "south burlington|vt|US",
                "burlington|vt|US",
                "west palm beach|fl|US",
                "west palm beach|fl|US",
                "palm beach|fl|US"
            );
    }

    [Test]
    public void ShowsInSeparateYearsLookLikeAMove()
    {
        // Cat's Cradle: Chapel Hill until 1992, Carrboro since 1998, one show filed under the
        // wrong town in 1990.
        VenueMatch
            .YearsNote([
                [1990, 1998, 1999, 2000, 2001, 2002, 2003, 2004, 2016, 2019],
                [1988, 1989, 1990, 1991, 1992],
            ])
            .Should()
            .Contain("a move?");
        // Palace Theatres in three towns, all years: not a move.
        VenueMatch
            .YearsNote([
                [1995, 1997, 2001, 2005],
                [1996, 1999, 2003, 2008],
            ])
            .Should()
            .Contain("overlap.");
        // Too few shows to say.
        VenueMatch
            .YearsNote([
                [2003, 2005, 2009],
                [2025],
            ])
            .Should()
            .BeEmpty();
    }

    [Test]
    public void ATapeTakesTheVenueTheOtherTapesOfItsShowsAreOn()
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
        var peel = new CatalogueVenue(
            Guid.NewGuid(),
            "The Orange Peel",
            "site",
            null,
            "Asheville",
            "NC",
            "US"
        );
        var msg = Row("MSG", "Unknown Location");
        var instore = Row("Good Music and Other Stuff", "Asheville, NC");
        var fieldhouse = Row("BSU Field House", "Bozeman, MT");
        var breeden = Row("Brick Breeden Fieldhouse", "Bozeman, Montana");
        var state = new VenueState
        {
            Unmapped = [msg, instore, fieldhouse, breeden],
            Venues = new Dictionary<Guid, CatalogueVenue>
            {
                [garden.Uuid] = garden,
                [peel.Uuid] = peel,
            },
            Names = new[]
            {
                ("madison square garden", garden.Uuid),
                ("orange peel", peel.Uuid),
            }.ToLookup(n => n.Item1, n => n.Item2),
            ExternalIds = new Dictionary<(string, string), Guid>(),
            ShowDates = new Dictionary<Guid, int> { [msg.Uuid] = 2, [instore.Uuid] = 1 },
            SameShows = new Dictionary<Guid, IReadOnlyDictionary<Guid, int>>
            {
                [msg.Uuid] = new Dictionary<Guid, int> { [garden.Uuid] = 2 },
                [instore.Uuid] = new Dictionary<Guid, int> { [peel.Uuid] = 1 },
            },
            SameShowRows = [(fieldhouse.Uuid, breeden.Uuid)],
        };

        var plan = VenueMatcher.Match(state);

        plan.Evidence.Should()
            .ContainSingle(e => e.LegacyVenueUuid == msg.Uuid && e.VenueUuid == garden.Uuid);
        plan.Evidence.Should()
            .NotContain(e => e.LegacyVenueUuid == instore.Uuid && e.VenueUuid == peel.Uuid);
        // Two names of one show's place: one family, for the model.
        plan.Left.Should().Contain(f => f.Contains(fieldhouse) && f.Contains(breeden));
    }
}
