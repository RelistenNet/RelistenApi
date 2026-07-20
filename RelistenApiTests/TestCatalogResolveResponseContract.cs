using FluentAssertions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Relisten;
using Relisten.Api.Models;
using Relisten.Api.Models.Api;
using Relisten.Data;

namespace RelistenApiTests;

[TestFixture]
public sealed class TestCatalogResolveResponseContract
{
    [Test]
    public void Normalized_entities_use_the_standard_mobile_hydration_models()
    {
        ElementType(nameof(CatalogResolveEntities.artists)).Should().Be<ArtistWithCounts>();
        ElementType(nameof(CatalogResolveEntities.shows)).Should().Be<Show>();
        ElementType(nameof(CatalogResolveEntities.sources)).Should().Be<SourceFull>();
        ElementType(nameof(CatalogResolveEntities.source_tracks)).Should().Be<SourceTrack>();
        ElementType(nameof(CatalogResolveEntities.songs)).Should().Be<SetlistSongWithPlayCount>();
        ElementType(nameof(CatalogResolveEntities.tours)).Should().Be<TourWithShowCount>();
        ElementType(nameof(CatalogResolveEntities.venues)).Should().Be<VenueWithShowCount>();
        ElementType(nameof(CatalogResolveEntities.years)).Should().Be<Year>();
        ElementType(nameof(CatalogResolveEntities.source_sets)).Should().Be<SourceSet>();
    }

    [Test]
    public void V3_serialization_keeps_catalog_numeric_ids_out_of_the_wire_contract()
    {
        var response = new CatalogResolveResponse
        {
            contract_version = 1,
            entities = new CatalogResolveEntities
            {
                artists =
                [
                    new ArtistWithCounts
                    {
                        id = 42,
                        uuid = Guid.NewGuid(),
                        created_at = DateTime.UtcNow,
                        updated_at = DateTime.UtcNow,
                        musicbrainz_id = "musicbrainz",
                        name = "Artist",
                        slug = "artist",
                        sort_name = "Artist",
                        features = new Features { id = 84, artist_id = 42 },
                        upstream_sources = []
                    }
                ]
            }
        };

        var json = JsonConvert.SerializeObject(
            response,
            RelistenApiJsonOptionsWrapper.ApiV3SerializerSettings);
        var parsed = JObject.Parse(json);

        parsed.SelectTokens("$..id").Should().BeEmpty();
        parsed.SelectTokens("$..upstream_source_id").Should().BeEmpty();
        parsed.SelectToken("$.entities.artists[0].uuid").Should().NotBeNull();
        parsed.SelectToken("$.entities.artists[0].features").Should().NotBeNull();
        parsed.SelectToken("$.entities.years").Should().NotBeNull();
        parsed.SelectToken("$.entities.source_sets").Should().NotBeNull();
    }

    [Test]
    public void Mixed_references_derive_availability_from_target_entity_dtos()
    {
        var existingArtistUuid = Guid.NewGuid();
        var existingShowUuid = Guid.NewGuid();
        var missingShowUuid = Guid.NewGuid();
        var references = new CatalogReference[]
        {
            new("artist", existingArtistUuid),
            new("show", existingShowUuid),
            new("show", missingShowUuid)
        };
        var entities = new CatalogResolveEntities
        {
            artists =
            [
                new ArtistWithCounts {uuid = existingArtistUuid},
                new ArtistWithCounts {uuid = missingShowUuid}
            ],
            shows = [new Show {uuid = existingShowUuid}]
        };

        var resolved = CatalogReferenceResolver.BuildResolvedReferences(references, entities);

        resolved.Select(reference => (
                reference.catalog_type,
                reference.catalog_uuid,
                reference.availability))
            .Should().Equal(
                ("artist", existingArtistUuid, "available"),
                ("show", existingShowUuid, "available"),
                ("show", missingShowUuid, "unavailable"));
    }

    private static Type ElementType(string propertyName) =>
        typeof(CatalogResolveEntities).GetProperty(propertyName)!
            .PropertyType
            .GetGenericArguments()
            .Single();
}
