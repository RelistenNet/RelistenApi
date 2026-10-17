using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace RelistenApiTests.Normalization;

[TestFixture]
public sealed class TestNormalizationProviderDecoderImportedCatalog
{
    private readonly NormalizationProviderDecoder decoder = new();

    [Test]
    public void ImportedVenueKeepsRawLocationNamesAndUnclassifiedCoordinates()
    {
        var uuid = Guid.NewGuid();
        var envelope = VenueEnvelope(uuid);
        var row = envelope["venue"]!;
        row["name"] = "Madison Square Garden 🎤";
        row["location"] = "New York, NY / Garden complex; entrance via 8th Ave";
        row["past_names"] = "Felt Forum; Theatre at MSG (1990–1996)";
        row["upstream_identifier"] = "jerrygarcia.com_Garden::New York, USA";
        row["latitude"] = 40.7505;
        row["longitude"] = -73.9934;
        var input = Input("imported_venue", uuid, envelope);

        var result = decoder.Decode(input);

        var venue = result.Venues.Should().ContainSingle().Subject;
        venue.Origin.Scope.Should().Be(ProviderObservationScope.LegacySnapshot);
        venue
            .Origin.Item.Should()
            .Be(new NormalizationResourceKey("legacy_catalog", "venue", uuid.ToString("D")));
        venue.Identities.Should().ContainSingle().Which.Identity.Should().Be(venue.Origin.Item);
        venue.Names.Should().ContainSingle().Which.Value.Should().Be("Madison Square Garden 🎤");
        venue
            .OriginalLocality!.Value.Should()
            .Be("New York, NY / Garden complex; entrance via 8th Ave");
        venue.City.Should().BeNull();
        venue.Region.Should().BeNull();
        venue.Country.Should().BeNull();
        venue.CoordinateKind.Should().Be(ObservedCoordinateKind.Unknown);
        venue.Latitude!.Value.Should().Be(40.7505);
        venue.Longitude!.Value.Should().Be(-73.9934);
        venue
            .Text.Single(text => text.Field == "past_names")
            .Text.Value.Should()
            .Be("Felt Forum; Theatre at MSG (1990–1996)");
        venue
            .Text.Single(text => text.Field == "catalog_upstream_identifier")
            .Text.Value.Should()
            .Be("jerrygarcia.com_Garden::New York, USA");
        result.Recordings.Should().BeEmpty();
        result.Repertoire.Should().BeEmpty();
        result.Setlists.Should().BeEmpty();
        AssertEveryCitation(input, result);
        AssertText(input, venue.Names.Single());
        AssertText(input, venue.OriginalLocality!);
        foreach (var text in venue.Text)
        {
            AssertText(input, text.Text);
        }
    }

    [Test]
    public void NullVenueFieldsPreserveTheStoredIdentityWithoutSubstituteNames()
    {
        var uuid = Guid.NewGuid();
        var envelope = VenueEnvelope(uuid);
        envelope["venue"]!["artist_uuid"] = null;
        envelope["venue"]!["name"] = null;
        var input = Input("imported_venue", uuid, envelope);

        var venue = decoder.Decode(input).Venues.Single();

        venue.Identities.Single().Identity.ProviderKey.Should().Be(uuid.ToString("D"));
        venue.Names.Should().BeEmpty();
        venue.OriginalLocality.Should().BeNull();
        venue.Latitude.Should().BeNull();
        venue.Longitude.Should().BeNull();
        venue.Text.Should().BeEmpty();
    }

    [Test]
    public void RepertoirePreservesDistinctRowsWithRepeatedNullAndEmptyTitles()
    {
        var artist = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var unnamed = Guid.NewGuid();
        var empty = Guid.NewGuid();
        var envelope = RepertoireEnvelope(
            artist,
            Song(first, "Dark Star"),
            Song(second, "Dark Star"),
            Song(unnamed, null, "not-a-title", "phish.in:3"),
            Song(empty, "")
        );
        var input = Input("artist_repertoire", artist, envelope);

        var result = decoder.Decode(input);

        result.Resource.Should().Be(input.Resource);
        result.Repertoire.Should().HaveCount(4);
        result
            .Repertoire.Select(song => song.Title?.Value)
            .Should()
            .Equal("Dark Star", "Dark Star", null, "");
        result
            .Repertoire.Select(song => song.Origin.Item.ProviderKey)
            .Should()
            .Equal(
                first.ToString("D"),
                second.ToString("D"),
                unnamed.ToString("D"),
                empty.ToString("D")
            );
        result
            .Repertoire.Should()
            .OnlyContain(song =>
                song.Origin.Scope == ProviderObservationScope.LegacySnapshot
                && song.Origin.Item.ResourceType == "repertoire_song"
                && song.Origin.Item != input.Resource
                && song.Origin.Evidence.InputRevisionUuid == input.Uuid
                && song.Aliases.Count == 0
                && song.AliasOf == null
                && song.WorkIdentities.Count == 0
                && song.WorkCredits.Count == 0
                && !song.CompleteAuthorship
            );
        var unknown = result.Repertoire[2];
        unknown.Title.Should().BeNull();
        unknown.Identities.Should().ContainSingle().Which.Identity.Should().Be(unknown.Origin.Item);
        unknown
            .Text.Single(text => text.Field == "catalog_slug")
            .Text.Value.Should()
            .Be("not-a-title");
        unknown
            .Text.Single(text => text.Field == "catalog_upstream_identifier")
            .Text.Value.Should()
            .Be("phish.in:3");
        result.Setlists.Should().BeEmpty();
        result.Recordings.Should().BeEmpty();
        AssertEveryCitation(input, result);
        AssertText(input, result.Repertoire[0].Title!);
        foreach (var text in unknown.Text)
        {
            AssertText(input, text.Text);
        }
    }

    [Test]
    public void EmptyRepertoireIsAnExplicitEmptyDictionary()
    {
        var artist = Guid.NewGuid();
        var input = Input("artist_repertoire", artist, RepertoireEnvelope(artist));

        var result = decoder.Decode(input);

        result.Repertoire.Should().BeEmpty();
        result.Issues.Should().BeEmpty();
        result.Resource.Should().Be(input.Resource);
    }

    [TestCase("imported_venue")]
    [TestCase("artist_repertoire")]
    public void ResourceIdentityMustEqualTheEnvelopeIdentity(string resourceType)
    {
        var uuid = Guid.NewGuid();
        var envelope =
            resourceType == "imported_venue" ? VenueEnvelope(uuid) : RepertoireEnvelope(uuid);
        var input = Input(resourceType, Guid.NewGuid(), envelope);

        Action decode = () => decoder.Decode(input);

        decode.Should().Throw<ArgumentException>();
    }

    [TestCase("imported_venue")]
    [TestCase("artist_repertoire")]
    public void UnsupportedEnvelopeSchemaCannotReuseTheCurrentDecoder(string resourceType)
    {
        var uuid = Guid.NewGuid();
        var envelope =
            resourceType == "imported_venue" ? VenueEnvelope(uuid) : RepertoireEnvelope(uuid);
        envelope["schema_version"] = 2;

        Action decode = () => decoder.Decode(Input(resourceType, uuid, envelope));

        decode.Should().Throw<ArgumentException>();
    }

    [Test]
    public void DuplicateSongRowsAreRejectedRatherThanCollapsedIntoOneIdentity()
    {
        var artist = Guid.NewGuid();
        var song = Guid.NewGuid();
        var input = Input(
            "artist_repertoire",
            artist,
            RepertoireEnvelope(artist, Song(song, "First"), Song(song, "Changed"))
        );

        Action decode = () => decoder.Decode(input);

        decode.Should().Throw<ArgumentException>();
    }

    [TestCase("empty")]
    [TestCase("invalid")]
    [TestCase("missing")]
    [TestCase("nonobject")]
    public void InvalidCatalogSongIdentityCannotBeReplacedByItsSlug(string defect)
    {
        var artist = Guid.NewGuid();
        var song = Song(Guid.NewGuid(), "Title", "looks-valid", "provider-id");
        if (defect == "missing")
        {
            song.Remove("uuid");
        }
        else
        {
            song["uuid"] =
                defect == "empty" ? Guid.Empty.ToString("D") : "sensitive-provider-content";
        }
        var envelope = RepertoireEnvelope(artist, song);
        if (defect == "nonobject")
        {
            envelope["songs"]![0] = "sensitive-provider-content";
        }

        Action decode = () => decoder.Decode(Input("artist_repertoire", artist, envelope));

        decode
            .Should()
            .Throw<ArgumentException>()
            .Which.Message.Should()
            .NotContain("sensitive-provider-content");
    }

    [Test]
    public void InvalidVenueOwnerIsNotSilentlyTreatedAsAnUnownedVenue()
    {
        var uuid = Guid.NewGuid();
        var envelope = VenueEnvelope(uuid);
        envelope["venue"]!["artist_uuid"] = "invalid";

        Action decode = () => decoder.Decode(Input("imported_venue", uuid, envelope));

        decode.Should().Throw<ArgumentException>();
    }

    private static JsonObject VenueEnvelope(Guid uuid) =>
        new()
        {
            ["schema_version"] = 1,
            ["venue"] = new JsonObject
            {
                ["uuid"] = uuid.ToString("D"),
                ["artist_uuid"] = Guid.NewGuid().ToString("D"),
                ["name"] = "Imported venue",
                ["location"] = null,
                ["upstream_identifier"] = null,
                ["slug"] = null,
                ["past_names"] = null,
                ["latitude"] = null,
                ["longitude"] = null,
            },
            ["catalog_inputs"] = new JsonArray(),
            ["imported_venues"] = new JsonArray(
                new JsonObject { ["venue_uuid"] = uuid.ToString("D"), ["generation"] = 7 }
            ),
        };

    private static JsonObject RepertoireEnvelope(Guid artist, params JsonObject[] songs) =>
        new()
        {
            ["schema_version"] = 1,
            ["artist_uuid"] = artist.ToString("D"),
            ["songs"] = new JsonArray(songs.Cast<JsonNode>().ToArray()),
            ["catalog_inputs"] = new JsonArray(
                new JsonObject { ["artist_uuid"] = artist.ToString("D"), ["generation"] = 3 }
            ),
        };

    private static JsonObject Song(
        Guid uuid,
        string? name,
        string? slug = null,
        string? upstreamIdentifier = null
    ) =>
        new()
        {
            ["uuid"] = uuid.ToString("D"),
            ["name"] = name,
            ["slug"] = slug,
            ["upstream_identifier"] = upstreamIdentifier,
        };

    private static NormalizationInputRevision Input(
        string resourceType,
        Guid uuid,
        JsonObject envelope
    ) =>
        new()
        {
            Uuid = Guid.NewGuid(),
            Resource = new("legacy_catalog", resourceType, uuid.ToString("D")),
            Payload = Encoding.UTF8.GetBytes(envelope.ToJsonString()),
            MediaType = "application/json",
            CaptureVersion =
                resourceType == "imported_venue"
                    ? "legacy-imported-venue-v1"
                    : "legacy-artist-repertoire-v1",
        };

    private static void AssertText(
        NormalizationInputRevision input,
        ObservedValue<string> observation
    )
    {
        using var document = NormalizationRetainedDocument.Open(input);
        document
            .Resolve(observation.Evidence.JsonPointer)
            .GetString()
            .Should()
            .Be(observation.Value);
    }

    private static void AssertEveryCitation(
        NormalizationInputRevision input,
        ProviderDecodeResult result
    )
    {
        using var document = NormalizationRetainedDocument.Open(input);
        Walk(JsonSerializer.SerializeToElement(result));

        void Walk(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (
                    value.TryGetProperty("InputRevisionUuid", out _)
                    && value.TryGetProperty("JsonPointer", out _)
                )
                {
                    document.ValidateEvidence(value.Deserialize<NormalizedEvidenceReference>()!);
                    return;
                }
                foreach (var property in value.EnumerateObject())
                {
                    Walk(property.Value);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in value.EnumerateArray())
                {
                    Walk(entry);
                }
            }
        }
    }
}
