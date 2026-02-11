using FluentAssertions;
using NUnit.Framework;
using Relisten.Api.Models;
using Relisten.Services.Classification;

namespace RelistenApiTests.Classification;

/// <summary>
/// Tests for VenueCanonicalizer normalization and matching utilities.
/// These are static/internal methods — no DB or LLM needed.
/// </summary>
[TestFixture]
public class TestVenueCanonicalizer
{
    #region Venue Name Normalization

    [Test]
    public void NormalizeVenueName_StripsThePrefix()
    {
        VenueCanonicalizer.NormalizeVenueName("The Fillmore").Should().Be("fillmore");
    }

    [Test]
    public void NormalizeVenueName_StripsParenthetical()
    {
        VenueCanonicalizer.NormalizeVenueName("Fox Theatre (Atlanta)").Should().Be("fox theatre");
    }

    [Test]
    public void NormalizeVenueName_HandlesNormalName()
    {
        VenueCanonicalizer.NormalizeVenueName("Red Rocks Amphitheatre").Should().Be("red rocks amphitheatre");
    }

    [Test]
    public void NormalizeVenueName_TrimsWhitespace()
    {
        VenueCanonicalizer.NormalizeVenueName("  Madison Square Garden  ").Should().Be("madison square garden");
    }

    #endregion

    #region Location Normalization

    [Test]
    public void NormalizeLocation_NormalizesStateNames()
    {
        VenueCanonicalizer.NormalizeLocation("Atlanta, Georgia").Should().Be("atlanta, ga");
    }

    [Test]
    public void NormalizeLocation_PreservesAbbreviations()
    {
        VenueCanonicalizer.NormalizeLocation("Atlanta, GA").Should().Be("atlanta, ga");
    }

    [Test]
    public void NormalizeLocation_HandlesMultipleParts()
    {
        VenueCanonicalizer.NormalizeLocation("Morrison, Colorado, USA").Should().Be("morrison, co, usa");
    }

    [Test]
    public void NormalizeLocation_HandlesEmptyString()
    {
        VenueCanonicalizer.NormalizeLocation("").Should().Be("");
        VenueCanonicalizer.NormalizeLocation(null!).Should().Be("");
    }

    [Test]
    public void NormalizeLocation_TrimsWhitespace()
    {
        VenueCanonicalizer.NormalizeLocation("  New York ,  New York  ").Should().Be("ny, ny");
    }

    #endregion

    #region Haversine Distance

    [Test]
    public void HaversineDistance_SamePoint_ReturnsZero()
    {
        VenueCanonicalizer.HaversineDistance(40.7128, -74.0060, 40.7128, -74.0060)
            .Should().BeApproximately(0.0, 0.001);
    }

    [Test]
    public void HaversineDistance_NYC_to_LA_ReturnsReasonableDistance()
    {
        // NYC to LA is roughly 3944 km
        var distance = VenueCanonicalizer.HaversineDistance(
            40.7128, -74.0060,  // NYC
            34.0522, -118.2437  // LA
        );
        distance.Should().BeInRange(3900, 4000);
    }

    [Test]
    public void HaversineDistance_NearbyPoints_ReturnsSmallDistance()
    {
        // Two points ~1km apart in Denver area
        var distance = VenueCanonicalizer.HaversineDistance(
            39.7392, -104.9903,
            39.7480, -104.9903
        );
        distance.Should().BeLessThan(2.0);
    }

    #endregion

    #region Location Similarity

    [Test]
    public void CalculateLocationSimilarity_IdenticalLocations_ReturnsHigh()
    {
        VenueCanonicalizer.CalculateLocationSimilarity("atlanta, ga", "atlanta, ga")
            .Should().BeGreaterThanOrEqualTo(0.95f);
    }

    [Test]
    public void CalculateLocationSimilarity_DifferentLocations_ReturnsLow()
    {
        VenueCanonicalizer.CalculateLocationSimilarity("atlanta, ga", "detroit, mi")
            .Should().BeLessThan(0.5f);
    }

    [Test]
    public void CalculateLocationSimilarity_EmptyLocation_ReturnsPartialCredit()
    {
        // Unknown locations get partial credit so they don't block matching
        VenueCanonicalizer.CalculateLocationSimilarity("", "atlanta, ga")
            .Should().BeGreaterThan(0.0f);
    }

    #endregion

    #region Slug Generation

    [Test]
    public void NormalizeVenueSlug_GeneratesMatchableSlug()
    {
        VenueCanonicalizer.NormalizeVenueSlug("The Fox Theatre")
            .Should().Be("fox-theatre");
    }

    [Test]
    public void NormalizeVenueSlug_FoxTheatresProduceSameSlug()
    {
        // Both "Fox Theatre" venues should produce the same slug
        // (distinguished by location, not name)
        var slug1 = VenueCanonicalizer.NormalizeVenueSlug("Fox Theatre");
        var slug2 = VenueCanonicalizer.NormalizeVenueSlug("The Fox Theatre");
        slug1.Should().Be(slug2);
    }

    [Test]
    public void NormalizeVenueSlug_DifferentVenuesDifferentSlugs()
    {
        var slug1 = VenueCanonicalizer.NormalizeVenueSlug("Red Rocks Amphitheatre");
        var slug2 = VenueCanonicalizer.NormalizeVenueSlug("Fox Theatre");
        slug1.Should().NotBe(slug2);
    }

    #endregion

    #region Fox Theatre Real-Data Tests

    // ========================================================================
    // Real venue data from the live music scene. These tests prove the
    // canonicalizer correctly handles the "Fox Theatre problem": multiple
    // physically distinct venues that share the same (or very similar) name.
    //
    // Real Fox Theatre venues used:
    //   - Fox Theatre, Atlanta, GA        (33.7725, -84.3863)
    //   - Fox Theatre, Detroit, MI        (42.3364, -83.0533)
    //   - Fox Theatre, Boulder, CO        (40.0189, -105.2775)
    //   - Fox Theatre, St. Louis, MO      (38.6353, -90.2320)
    //   - Fox Theater, Oakland, CA        (37.8085, -122.2704)
    //     (note: "Theater" not "Theatre")
    // ========================================================================

    /// <summary>
    /// Helper: build a VenueCanonicalizer with no DB/LLM dependencies.
    /// Only used for testing the matching layer methods directly.
    /// </summary>
    private static VenueCanonicalizer CreateTestCanonicalizer()
    {
        return new VenueCanonicalizer(null!, null!, new Microsoft.Extensions.Logging.Abstractions.NullLogger<VenueCanonicalizer>());
    }

    private static Venue MakeVenue(int id, string name, string location, double? lat = null, double? lng = null)
    {
        return new Venue
        {
            id = id,
            name = name,
            location = location,
            latitude = lat,
            longitude = lng,
            upstream_identifier = $"venue-{id}",
            slug = name.ToLowerInvariant().Replace(" ", "-"),
            artist_id = 1,
            artist_uuid = Guid.Empty,
            uuid = Guid.NewGuid()
        };
    }

    private static CanonicalVenue MakeCanonical(int id, string name, string location, double? lat = null, double? lng = null)
    {
        return new CanonicalVenue
        {
            id = id,
            name = name,
            location = location,
            latitude = lat,
            longitude = lng,
            slug = VenueCanonicalizer.NormalizeVenueSlug(name),
            uuid = Guid.NewGuid(),
            created_at = DateTime.UtcNow,
            updated_at = DateTime.UtcNow
        };
    }

    // -- Slug+Location Layer (Layer 1) --

    [Test]
    public void FoxTheatre_SlugMatch_AtlantaMatchesAtlanta()
    {
        var canonicalizer = CreateTestCanonicalizer();
        var canonicalAtlanta = MakeCanonical(1, "Fox Theatre", "Atlanta, GA", 33.7725, -84.3863);
        var canonicalDetroit = MakeCanonical(2, "Fox Theatre", "Detroit, MI", 42.3364, -83.0533);

        var bySlug = new Dictionary<string, List<CanonicalVenue>>
        {
            ["fox-theatre"] = new() { canonicalAtlanta, canonicalDetroit }
        };

        // Grateful Dead's "Fox Theatre" in Atlanta should match the Atlanta canonical
        var venue = MakeVenue(100, "Fox Theatre", "Atlanta, Georgia", 33.7725, -84.3863);
        var slug = VenueCanonicalizer.NormalizeVenueSlug(venue.name);
        var normalizedLoc = VenueCanonicalizer.NormalizeLocation(venue.location);

        var match = canonicalizer.TrySlugLocationMatch(slug, normalizedLoc, venue, bySlug);

        match.Should().NotBeNull();
        match!.id.Should().Be(1, "should match the Atlanta canonical, not Detroit");
        match.location.Should().Be("Atlanta, GA");
    }

    [Test]
    public void FoxTheatre_SlugMatch_DetroitMatchesDetroit()
    {
        var canonicalizer = CreateTestCanonicalizer();
        var canonicalAtlanta = MakeCanonical(1, "Fox Theatre", "Atlanta, GA", 33.7725, -84.3863);
        var canonicalDetroit = MakeCanonical(2, "Fox Theatre", "Detroit, MI", 42.3364, -83.0533);

        var bySlug = new Dictionary<string, List<CanonicalVenue>>
        {
            ["fox-theatre"] = new() { canonicalAtlanta, canonicalDetroit }
        };

        var venue = MakeVenue(101, "The Fox Theatre", "Detroit, Michigan", 42.3364, -83.0533);
        var slug = VenueCanonicalizer.NormalizeVenueSlug(venue.name);
        var normalizedLoc = VenueCanonicalizer.NormalizeLocation(venue.location);

        var match = canonicalizer.TrySlugLocationMatch(slug, normalizedLoc, venue, bySlug);

        match.Should().NotBeNull();
        match!.id.Should().Be(2, "should match the Detroit canonical, not Atlanta");
    }

    [Test]
    public void FoxTheatre_SlugMatch_BoulderDoesNotMatchAtlantaOrDetroit()
    {
        var canonicalizer = CreateTestCanonicalizer();
        var canonicalAtlanta = MakeCanonical(1, "Fox Theatre", "Atlanta, GA", 33.7725, -84.3863);
        var canonicalDetroit = MakeCanonical(2, "Fox Theatre", "Detroit, MI", 42.3364, -83.0533);

        var bySlug = new Dictionary<string, List<CanonicalVenue>>
        {
            ["fox-theatre"] = new() { canonicalAtlanta, canonicalDetroit }
        };

        // Boulder Fox Theatre — neither Atlanta nor Detroit location matches
        var venue = MakeVenue(102, "Fox Theatre", "Boulder, CO", 40.0189, -105.2775);
        var slug = VenueCanonicalizer.NormalizeVenueSlug(venue.name);
        var normalizedLoc = VenueCanonicalizer.NormalizeLocation(venue.location);

        var match = canonicalizer.TrySlugLocationMatch(slug, normalizedLoc, venue, bySlug);

        // Location text "boulder, co" doesn't match "Atlanta, GA" or "Detroit, MI"
        // and geo distance is >5km from both, so no slug+location match
        match.Should().BeNull("Boulder is a different Fox Theatre — should NOT match Atlanta or Detroit");
    }

    [Test]
    public void FoxTheatre_SlugMatch_SameVenueDifferentArtists()
    {
        // Two different artists both played "Fox Theatre, Atlanta, GA"
        // Both should match the same canonical venue
        var canonicalizer = CreateTestCanonicalizer();
        var canonicalAtlanta = MakeCanonical(1, "Fox Theatre", "Atlanta, GA", 33.7725, -84.3863);

        var bySlug = new Dictionary<string, List<CanonicalVenue>>
        {
            ["fox-theatre"] = new() { canonicalAtlanta }
        };

        var venueArtist1 = MakeVenue(200, "Fox Theatre", "Atlanta, GA", 33.7725, -84.3863);
        var venueArtist2 = MakeVenue(201, "The Fox Theatre", "Atlanta, Georgia", 33.7726, -84.3862);

        var slug1 = VenueCanonicalizer.NormalizeVenueSlug(venueArtist1.name);
        var slug2 = VenueCanonicalizer.NormalizeVenueSlug(venueArtist2.name);
        var loc1 = VenueCanonicalizer.NormalizeLocation(venueArtist1.location);
        var loc2 = VenueCanonicalizer.NormalizeLocation(venueArtist2.location);

        var match1 = canonicalizer.TrySlugLocationMatch(slug1, loc1, venueArtist1, bySlug);
        var match2 = canonicalizer.TrySlugLocationMatch(slug2, loc2, venueArtist2, bySlug);

        match1.Should().NotBeNull();
        match2.Should().NotBeNull();
        match1!.id.Should().Be(match2!.id, "both artists' Atlanta Fox Theatre should map to the same canonical");
    }

    // -- Geo Proximity Layer (Layer 2) --

    [Test]
    public void FoxTheatre_GeoMatch_NearbyCoordinatesMatchAtlanta()
    {
        var canonicalizer = CreateTestCanonicalizer();
        var canonicalAtlanta = MakeCanonical(1, "Fox Theatre", "Atlanta, GA", 33.7725, -84.3863);
        var canonicalDetroit = MakeCanonical(2, "Fox Theatre", "Detroit, MI", 42.3364, -83.0533);
        var canonicals = new List<CanonicalVenue> { canonicalAtlanta, canonicalDetroit };

        // Venue with slightly different coords but same area (within 5km)
        var venue = MakeVenue(103, "Fox Theatre", "Atlanta, GA", 33.7730, -84.3870);

        var match = canonicalizer.TryGeoProximityMatch(venue, canonicals);

        match.Should().NotBeNull();
        match!.id.Should().Be(1, "nearby coords should geo-match to Atlanta canonical");
    }

    [Test]
    public void FoxTheatre_GeoMatch_FarCoordinatesDoNotMatch()
    {
        var canonicalizer = CreateTestCanonicalizer();
        var canonicalAtlanta = MakeCanonical(1, "Fox Theatre", "Atlanta, GA", 33.7725, -84.3863);
        var canonicals = new List<CanonicalVenue> { canonicalAtlanta };

        // St. Louis Fox is ~750km from Atlanta Fox — way beyond 5km threshold
        var venue = MakeVenue(104, "Fox Theatre", "St. Louis, MO", 38.6353, -90.2320);

        var match = canonicalizer.TryGeoProximityMatch(venue, canonicals);

        match.Should().BeNull("St. Louis is ~750km from Atlanta — not a geo match");
    }

    [Test]
    public void FoxTheatre_GeoMatch_DifferentNameNearbyDoesNotMatch()
    {
        var canonicalizer = CreateTestCanonicalizer();
        // Tabernacle is in Atlanta, very close to Fox Theatre, but different venue
        var canonicalTabernacle = MakeCanonical(10, "The Tabernacle", "Atlanta, GA", 33.7588, -84.3916);
        var canonicals = new List<CanonicalVenue> { canonicalTabernacle };

        // Fox Theatre in Atlanta — close to Tabernacle but different name
        var venue = MakeVenue(105, "Fox Theatre", "Atlanta, GA", 33.7725, -84.3863);

        var match = canonicalizer.TryGeoProximityMatch(venue, canonicals);

        // Even though both are in Atlanta, the name similarity check (>=0.7) should prevent matching
        // "fox-theatre" vs "tabernacle" slug similarity is very low
        match.Should().BeNull("different venue names should not match even when geographically close");
    }

    // -- Fuzzy Name+Location Layer (Layer 3) --

    [Test]
    public void FoxTheatre_FuzzyMatch_TheaterVsTheatreSpellingVariant()
    {
        var canonicalizer = CreateTestCanonicalizer();
        // Oakland uses "Theater" (American spelling), not "Theatre"
        var canonicalOakland = MakeCanonical(5, "Fox Theater", "Oakland, CA", 37.8085, -122.2704);
        var canonicals = new List<CanonicalVenue> { canonicalOakland };

        // Incoming venue from an artist with the alternate spelling
        var venue = MakeVenue(106, "Fox Theatre", "Oakland, California");

        var match = canonicalizer.TryFuzzyNameLocationMatch(venue, canonicals);

        match.Should().NotBeNull("'Fox Theatre' and 'Fox Theater' should fuzzy-match with same location");
        match!.id.Should().Be(5);
    }

    [Test]
    public void FoxTheatre_FuzzyMatch_DoesNotCrossMatchDifferentCities()
    {
        var canonicalizer = CreateTestCanonicalizer();
        var canonicalAtlanta = MakeCanonical(1, "Fox Theatre", "Atlanta, GA", 33.7725, -84.3863);
        var canonicals = new List<CanonicalVenue> { canonicalAtlanta };

        // Boulder Fox — same name, different city. Should NOT fuzzy-match
        var venue = MakeVenue(107, "Fox Theatre", "Boulder, CO");

        var match = canonicalizer.TryFuzzyNameLocationMatch(venue, canonicals);

        match.Should().BeNull("same venue name in a different city should not fuzzy-match");
    }

    [Test]
    public void FoxTheatre_FuzzyMatch_FullStateNameMatchesAbbreviation()
    {
        var canonicalizer = CreateTestCanonicalizer();
        var canonicalDetroit = MakeCanonical(2, "Fox Theatre", "Detroit, MI", 42.3364, -83.0533);
        var canonicals = new List<CanonicalVenue> { canonicalDetroit };

        // Incoming venue uses full state name instead of abbreviation
        var venue = MakeVenue(108, "Fox Theatre", "Detroit, Michigan");

        var match = canonicalizer.TryFuzzyNameLocationMatch(venue, canonicals);

        match.Should().NotBeNull("'Detroit, Michigan' should normalize to match 'Detroit, MI'");
        match!.id.Should().Be(2);
    }

    // -- End-to-end matching scenarios --

    [Test]
    public void FoxTheatre_EndToEnd_FiveDistinctFoxVenuesStayDistinct()
    {
        // Prove that when all 5 Fox Theatre/Theater canonicals exist,
        // incoming venues from each city map to the correct one
        var canonicalizer = CreateTestCanonicalizer();
        var atlanta  = MakeCanonical(1, "Fox Theatre", "Atlanta, GA",  33.7725, -84.3863);
        var detroit  = MakeCanonical(2, "Fox Theatre", "Detroit, MI",  42.3364, -83.0533);
        var boulder  = MakeCanonical(3, "Fox Theatre", "Boulder, CO",  40.0189, -105.2775);
        var stLouis  = MakeCanonical(4, "Fox Theatre", "St. Louis, MO", 38.6353, -90.2320);
        var oakland  = MakeCanonical(5, "Fox Theater", "Oakland, CA",  37.8085, -122.2704);

        var allCanonicals = new List<CanonicalVenue> { atlanta, detroit, boulder, stLouis, oakland };
        var bySlug = allCanonicals
            .GroupBy(c => c.slug)
            .ToDictionary(g => g.Key, g => g.ToList());

        // For each city, create an incoming venue and verify it matches the right canonical
        var testCases = new[]
        {
            (venue: MakeVenue(200, "The Fox Theatre", "Atlanta, Georgia", 33.7726, -84.3862), expectedId: 1),
            (venue: MakeVenue(201, "Fox Theatre", "Detroit, Michigan", 42.3360, -83.0530),    expectedId: 2),
            (venue: MakeVenue(202, "Fox Theatre", "Boulder, Colorado", 40.0190, -105.2770),   expectedId: 3),
            (venue: MakeVenue(203, "Fox Theatre", "St. Louis, Missouri", 38.6350, -90.2325),  expectedId: 4),
        };

        foreach (var (venue, expectedId) in testCases)
        {
            var slug = VenueCanonicalizer.NormalizeVenueSlug(venue.name);
            var normalizedLoc = VenueCanonicalizer.NormalizeLocation(venue.location);

            // Try Layer 1 first
            var match = canonicalizer.TrySlugLocationMatch(slug, normalizedLoc, venue, bySlug);

            // Fall back to Layer 2 if Layer 1 didn't match
            if (match == null && venue.latitude.HasValue && venue.longitude.HasValue)
                match = canonicalizer.TryGeoProximityMatch(venue, allCanonicals);

            // Fall back to Layer 3
            if (match == null)
                match = canonicalizer.TryFuzzyNameLocationMatch(venue, allCanonicals);

            match.Should().NotBeNull($"venue '{venue.name}' in '{venue.location}' should find a match");
            match!.id.Should().Be(expectedId,
                $"'{venue.name}' in '{venue.location}' should match canonical id {expectedId} ({allCanonicals.First(c => c.id == expectedId).location})");
        }
    }

    [Test]
    public void FoxTheatre_EndToEnd_OaklandTheaterMatchesDespiteSpelling()
    {
        // Oakland uses "Theater" (no trailing 'e') — different slug from "Theatre" venues
        var canonicalizer = CreateTestCanonicalizer();
        var atlanta = MakeCanonical(1, "Fox Theatre", "Atlanta, GA", 33.7725, -84.3863);
        var oakland = MakeCanonical(5, "Fox Theater", "Oakland, CA", 37.8085, -122.2704);

        var allCanonicals = new List<CanonicalVenue> { atlanta, oakland };
        var bySlug = allCanonicals
            .GroupBy(c => c.slug)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Incoming venue spells it "Theatre" but is in Oakland
        var venue = MakeVenue(204, "Fox Theatre", "Oakland, California", 37.8080, -122.2700);
        var slug = VenueCanonicalizer.NormalizeVenueSlug(venue.name);
        var normalizedLoc = VenueCanonicalizer.NormalizeLocation(venue.location);

        // Layer 1: slug "fox-theatre" won't match "fox-theater" (different slugs)
        var match = canonicalizer.TrySlugLocationMatch(slug, normalizedLoc, venue, bySlug);

        if (match == null && venue.latitude.HasValue && venue.longitude.HasValue)
            match = canonicalizer.TryGeoProximityMatch(venue, allCanonicals);

        if (match == null)
            match = canonicalizer.TryFuzzyNameLocationMatch(venue, allCanonicals);

        match.Should().NotBeNull("Oakland Fox Theatre/Theater should match despite spelling difference");
        match!.id.Should().Be(5, "should match Oakland canonical, not Atlanta");
    }

    [Test]
    public void FoxTheatre_EndToEnd_UnknownFoxCreatesNewCanonical()
    {
        // A Fox Theatre in a city we haven't seen before should NOT match any existing canonical
        var canonicalizer = CreateTestCanonicalizer();
        var atlanta = MakeCanonical(1, "Fox Theatre", "Atlanta, GA", 33.7725, -84.3863);
        var detroit = MakeCanonical(2, "Fox Theatre", "Detroit, MI", 42.3364, -83.0533);

        var allCanonicals = new List<CanonicalVenue> { atlanta, detroit };
        var bySlug = allCanonicals
            .GroupBy(c => c.slug)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Tucson, AZ Fox Theatre — never seen before
        var venue = MakeVenue(205, "Fox Tucson Theatre", "Tucson, AZ", 32.2217, -110.9665);
        var slug = VenueCanonicalizer.NormalizeVenueSlug(venue.name);
        var normalizedLoc = VenueCanonicalizer.NormalizeLocation(venue.location);

        var match = canonicalizer.TrySlugLocationMatch(slug, normalizedLoc, venue, bySlug);
        if (match == null && venue.latitude.HasValue)
            match = canonicalizer.TryGeoProximityMatch(venue, allCanonicals);
        if (match == null)
            match = canonicalizer.TryFuzzyNameLocationMatch(venue, allCanonicals);

        match.Should().BeNull("a Fox Theatre in a new city should not match existing canonicals — it needs its own");
    }

    [Test]
    public void RealVenues_RedRocksFromMultipleArtists_MatchSameCanonical()
    {
        // Red Rocks Amphitheatre — every artist's page has a slightly different entry
        var canonicalizer = CreateTestCanonicalizer();
        var canonical = MakeCanonical(10, "Red Rocks Amphitheatre", "Morrison, CO", 39.6654, -105.2057);

        var allCanonicals = new List<CanonicalVenue> { canonical };
        var bySlug = allCanonicals
            .GroupBy(c => c.slug)
            .ToDictionary(g => g.Key, g => g.ToList());

        var variants = new[]
        {
            MakeVenue(300, "Red Rocks Amphitheatre", "Morrison, CO", 39.6654, -105.2057),
            MakeVenue(301, "Red Rocks Amphitheatre", "Morrison, Colorado", 39.6655, -105.2056),
            MakeVenue(302, "Red Rocks Amphitheatre", "Morrison, Colorado, USA", 39.6654, -105.2057),
        };

        foreach (var venue in variants)
        {
            var slug = VenueCanonicalizer.NormalizeVenueSlug(venue.name);
            var normalizedLoc = VenueCanonicalizer.NormalizeLocation(venue.location);

            var match = canonicalizer.TrySlugLocationMatch(slug, normalizedLoc, venue, bySlug);
            if (match == null && venue.latitude.HasValue)
                match = canonicalizer.TryGeoProximityMatch(venue, allCanonicals);
            if (match == null)
                match = canonicalizer.TryFuzzyNameLocationMatch(venue, allCanonicals);

            match.Should().NotBeNull($"'{venue.location}' variant should match the canonical Red Rocks");
            match!.id.Should().Be(10);
        }
    }

    [Test]
    public void RealVenues_MadisonSquareGarden_MatchesDespiteNameVariants()
    {
        var canonicalizer = CreateTestCanonicalizer();
        var canonical = MakeCanonical(20, "Madison Square Garden", "New York, NY", 40.7505, -73.9934);

        var allCanonicals = new List<CanonicalVenue> { canonical };
        var bySlug = allCanonicals
            .GroupBy(c => c.slug)
            .ToDictionary(g => g.Key, g => g.ToList());

        var variants = new[]
        {
            MakeVenue(400, "Madison Square Garden", "New York, NY", 40.7505, -73.9934),
            MakeVenue(401, "Madison Square Garden", "New York, New York", 40.7506, -73.9935),
        };

        foreach (var venue in variants)
        {
            var slug = VenueCanonicalizer.NormalizeVenueSlug(venue.name);
            var normalizedLoc = VenueCanonicalizer.NormalizeLocation(venue.location);

            var match = canonicalizer.TrySlugLocationMatch(slug, normalizedLoc, venue, bySlug);
            if (match == null && venue.latitude.HasValue)
                match = canonicalizer.TryGeoProximityMatch(venue, allCanonicals);
            if (match == null)
                match = canonicalizer.TryFuzzyNameLocationMatch(venue, allCanonicals);

            match.Should().NotBeNull($"MSG variant '{venue.location}' should match");
            match!.id.Should().Be(20);
        }
    }

    [Test]
    public void RealVenues_FillmoreWestVsEast_StayDistinct()
    {
        var canonicalizer = CreateTestCanonicalizer();
        var fillmoreWest = MakeCanonical(30, "The Fillmore", "San Francisco, CA", 37.7842, -122.4330);
        var fillmoreEast = MakeCanonical(31, "Fillmore East", "New York, NY", 40.7282, -73.9907);

        var allCanonicals = new List<CanonicalVenue> { fillmoreWest, fillmoreEast };
        var bySlug = allCanonicals
            .GroupBy(c => c.slug)
            .ToDictionary(g => g.Key, g => g.ToList());

        var venueSF = MakeVenue(500, "The Fillmore", "San Francisco, California", 37.7842, -122.4330);
        var venueNY = MakeVenue(501, "Fillmore East", "New York, NY", 40.7282, -73.9907);

        // SF Fillmore
        var slugSF = VenueCanonicalizer.NormalizeVenueSlug(venueSF.name);
        var locSF = VenueCanonicalizer.NormalizeLocation(venueSF.location);
        var matchSF = canonicalizer.TrySlugLocationMatch(slugSF, locSF, venueSF, bySlug);
        if (matchSF == null) matchSF = canonicalizer.TryGeoProximityMatch(venueSF, allCanonicals);
        if (matchSF == null) matchSF = canonicalizer.TryFuzzyNameLocationMatch(venueSF, allCanonicals);

        // NY Fillmore East
        var slugNY = VenueCanonicalizer.NormalizeVenueSlug(venueNY.name);
        var locNY = VenueCanonicalizer.NormalizeLocation(venueNY.location);
        var matchNY = canonicalizer.TrySlugLocationMatch(slugNY, locNY, venueNY, bySlug);
        if (matchNY == null) matchNY = canonicalizer.TryGeoProximityMatch(venueNY, allCanonicals);
        if (matchNY == null) matchNY = canonicalizer.TryFuzzyNameLocationMatch(venueNY, allCanonicals);

        matchSF.Should().NotBeNull();
        matchNY.Should().NotBeNull();
        matchSF!.id.Should().Be(30, "SF Fillmore should match The Fillmore in SF");
        matchNY!.id.Should().Be(31, "Fillmore East should match Fillmore East in NY");
        matchSF.id.Should().NotBe(matchNY.id, "Fillmore West and Fillmore East must be distinct");
    }

    #endregion
}
