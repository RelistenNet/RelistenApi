using System.Text;
using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace RelistenApiTests.Normalization;

[TestFixture]
public sealed class TestNormalizationVenueSupportIntake
{
    private static readonly Guid PlaceUuid = Guid.Parse("a94461b0-d1c2-4a94-8d23-a12efb1804a7");
    private static readonly NormalizedEvidenceReference Citation = new(Guid.NewGuid(), "/venue");
    private readonly NormalizationVenueSupportIntake intake = new();

    [Test]
    public void ConcreteProviderEvidenceQualifiesOnlyTheOriginalPlaceIdentifier()
    {
        var place = Place();
        var (claim, original) = Source();

        var result = intake.PrepareSource(claim, original, null, place);

        result.Match.Kind.Should().BeEmpty();
        result.Match.ExternalIdentities.Single().IdentifiesEntity.Should().BeTrue();
        result.Evidence.Should().Contain(new NormalizedEvidenceReference(place.Uuid, "/id"));
        result.Match.LocationAssertions!.Single().Location.LocalityKey.Should().BeNull();
    }

    [Test]
    public void ASameNamePlaceResponseCannotAddAnIdentifierToTheSource()
    {
        var (claim, original) = Source();
        var result = intake.PrepareSource(
            claim,
            original with
            {
                ExternalIdentities = [],
            },
            null,
            Place()
        );

        result.Match.Kind.Should().BeEmpty();
        result.Match.ExternalIdentities.Should().BeEmpty();
    }

    [Test]
    public void APlaceResponseForAnotherIdentifierDoesNotQualifyTheClaim()
    {
        var (claim, original) = Source();
        var result = intake.PrepareSource(claim, original, null, Place(Guid.NewGuid()));

        result.Match.ExternalIdentities.Single().IdentifiesEntity.Should().BeFalse();
        result.Match.Kind.Should().BeEmpty();
    }

    [Test]
    public void AProviderResponseCannotChangeItsRequestedPlaceIdentity()
    {
        var (claim, original) = Source();
        var place = Place();
        place = new()
        {
            Uuid = place.Uuid,
            Resource = place.Resource,
            CaptureVersion = place.CaptureVersion,
            Payload = Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new { id = Guid.NewGuid(), type = "Venue" })
            ),
        };

        var act = () => intake.PrepareSource(claim, original, null, place);
        act.Should().Throw<ArgumentException>();
    }

    [TestCase("City")]
    [TestCase("Area")]
    [TestCase("Studio")]
    public void UnsupportedPlaceTypesDoNotDefaultToPhysicalVenueIdentity(string type)
    {
        var (claim, original) = Source();
        intake
            .PrepareSource(claim, original, null, Place(type: type))
            .Match.ExternalIdentities.Single()
            .IdentifiesEntity.Should()
            .BeFalse();
    }

    [Test]
    public void CompleteStructuredLocalityEvidenceResolvesGeographyWithoutPinningTheVenue()
    {
        var (claim, original) = Source();
        var geography = Geography([GeoRow(5391959)]);

        var result = intake.PrepareSource(claim, original, geography, null);

        result
            .Match.LocationAssertions!.Single()
            .Location.Should()
            .Be(new MatchingVenueLocation("US", "geonames:5391959", "1805 Geary Boulevard"));
        result.Match.Kind.Should().BeEmpty();
        result.Match.ExternalIdentities.Single().IdentifiesEntity.Should().BeFalse();
        result
            .Evidence.Should()
            .Contain(new NormalizedEvidenceReference(geography.Uuid, "/geonames/0"));
    }

    [Test]
    public void StructuredConstraintsCanRejectOtherNamedPlacesInTheCompleteResponse()
    {
        var (claim, original) = Source();
        var geography = Geography([GeoRow(5391959), GeoRow(123, "CO")]);

        intake
            .PrepareSource(claim, original, geography, null)
            .Match.LocationAssertions!.Single()
            .Location.LocalityKey.Should()
            .Be("geonames:5391959");
    }

    [Test]
    public void DistinctPlacesWithinTheSameStructuredConstraintsRemainAmbiguous()
    {
        var (claim, original) = Source();
        var geography = Geography([GeoRow(5391959), GeoRow(123)]);

        intake
            .PrepareSource(claim, original, geography, null)
            .Match.LocationAssertions!.Single()
            .Location.LocalityKey.Should()
            .BeNull();
    }

    [Test]
    public void ATruncatedGeographyResponseCannotEstablishUniqueGeography()
    {
        var (claim, original) = Source();
        var geography = Geography([GeoRow(5391959)], total: 200);

        var act = () => intake.PrepareSource(claim, original, geography, null);
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void ASearchForDifferentStructuredConstraintsCannotSupportThisSource()
    {
        var (claim, original) = Source();
        var geography = Geography([GeoRow(5391959)]);
        claim = claim with
        {
            Observation = claim.Observation with { Region = new("NY", Citation) },
        };

        intake
            .PrepareSource(claim, original, geography, null)
            .Match.LocationAssertions!.Single()
            .Location.LocalityKey.Should()
            .BeNull();
    }

    [Test]
    public void RawLocalityAndCentroidCoordinatesDoNotSupplyMissingStructuredGeography()
    {
        var (claim, original) = Source();
        claim = claim with
        {
            Observation = claim.Observation with
            {
                City = null,
                Region = null,
                Country = null,
                OriginalLocality = new("San Francisco, CA", Citation),
                Latitude = new(37.77, Citation),
                Longitude = new(-122.43, Citation),
                CoordinateKind = ObservedCoordinateKind.City,
            },
        };

        intake
            .PrepareSource(claim, original, Geography([GeoRow(5391959)]), null)
            .Match.LocationAssertions!.Single()
            .Location.LocalityKey.Should()
            .BeNull();
    }

    [Test]
    public void UnsupportedCaptureVersionsFailRatherThanReusingAnotherInterpretation()
    {
        var (claim, original) = Source();
        var geography = Geography([GeoRow(5391959)]);
        geography = new()
        {
            Uuid = geography.Uuid,
            Resource = geography.Resource,
            CaptureVersion = "changed",
            Payload = geography.Payload,
        };

        var act = () => intake.PrepareSource(claim, original, geography, null);
        act.Should().Throw<ArgumentException>();
    }

    private static (VenueResolutionClaim Claim, VenueMatchRecord Original) Source()
    {
        var identity = new MatchingExternalIdentity(
            "musicbrainz.org",
            "place",
            PlaceUuid.ToString("D")
        );
        var observation = new ProviderVenueObservation
        {
            Names = [new("Fillmore", Citation)],
            City = new("San Francisco", Citation),
            Region = new("CA", Citation),
            Country = new("US", Citation),
            Address = new("1805 Geary Boulevard", Citation),
            Identities =
            [
                new(new(identity.Provider, identity.ResourceType, identity.ProviderKey), Citation),
            ],
        };
        return (
            new(Guid.NewGuid(), false, observation, Citation),
            new(null, "", null, [new("Fillmore")], [identity], null, LocationAssertions: [])
        );
    }

    private static object GeoRow(long id, string region = "CA") =>
        new
        {
            geonameId = id,
            name = "San Francisco",
            toponymName = "San Francisco",
            countryCode = "US",
            adminCode1 = region,
            fcl = "P",
            fcode = "PPLA2",
            lat = "37.77",
            lng = "-122.43",
        };

    private static NormalizationInputRevision Geography(object[] rows, int? total = null) =>
        new()
        {
            Uuid = Guid.NewGuid(),
            Resource = NormalizationVenueSupportIntake.GeographyResource(
                "San Francisco",
                "US",
                "CA"
            ),
            CaptureVersion = NormalizationVenueSupportIntake.GeographyCaptureVersion,
            Payload = Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(
                    new { totalResultsCount = total ?? rows.Length, geonames = rows }
                )
            ),
        };

    private static NormalizationInputRevision Place(Guid? id = null, string type = "Venue") =>
        new()
        {
            Uuid = Guid.NewGuid(),
            Resource = new("musicbrainz.org", "place", (id ?? PlaceUuid).ToString("D")),
            CaptureVersion = NormalizationVenueSupportIntake.PlaceCaptureVersion,
            Payload = Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(
                    new
                    {
                        id = id ?? PlaceUuid,
                        type,
                        name = "Fillmore",
                    }
                )
            ),
        };
}
