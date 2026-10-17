using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

// Imported dictionaries preserve catalog wording and stored row identities. Their producer clocks
// belong to checked catalog binding; decoding does not establish freshness or accept these names.
public sealed partial class NormalizationProviderDecoder
{
    private static readonly IReadOnlyDictionary<string, string> ImportedVenueTextFields =
        new Dictionary<string, string>
        {
            ["past_names"] = "past_names",
            ["upstream_identifier"] = "catalog_upstream_identifier",
            ["slug"] = "catalog_slug",
        };

    private static readonly IReadOnlyDictionary<string, string> ImportedRepertoireTextFields =
        new Dictionary<string, string>
        {
            ["slug"] = "catalog_slug",
            ["upstream_identifier"] = "catalog_upstream_identifier",
        };

    private static ProviderDecodeResult DecodeImportedVenue(ProviderObservationReader reader)
    {
        var root = ImportedCatalogEnvelope(reader);
        if (!root.TryGetProperty("venue", out var venue))
        {
            throw new ArgumentException("Imported venue snapshot requires its catalog row.");
        }
        var uuid = ImportedCatalogUuid(venue, "uuid");
        RequireImportedResourceUuid(reader, uuid);
        if (
            venue.TryGetProperty("artist_uuid", out var artist)
            && artist.ValueKind != JsonValueKind.Null
        )
        {
            ImportedCatalogUuid(venue, "artist_uuid");
        }
        var identity = new ObservedProviderIdentity(
            new("legacy_catalog", "venue", uuid.ToString("D")),
            reader.Evidence("/venue/uuid")
        );
        var name = reader.String(venue, "name", "/venue");
        var observation = new ProviderVenueObservation
        {
            Origin = reader.Origin(
                identity.Identity,
                "/venue",
                ProviderObservationScope.LegacySnapshot
            ),
            Identities = [identity],
            Names = name == null ? [] : [name],
            OriginalLocality = reader.String(venue, "location", "/venue"),
            Latitude = venue.TryGetProperty("latitude", out var latitude)
                ? reader.Number(latitude, "/venue/latitude")
                : null,
            Longitude = venue.TryGetProperty("longitude", out var longitude)
                ? reader.Number(longitude, "/venue/longitude")
                : null,
            CoordinateKind = ObservedCoordinateKind.Unknown,
            Text = reader.TextFields(venue, "/venue", ImportedVenueTextFields),
        };
        return reader.Result() with { Venues = [observation] };
    }

    private static ProviderDecodeResult DecodeImportedRepertoire(ProviderObservationReader reader)
    {
        var root = ImportedCatalogEnvelope(reader);
        var artistUuid = ImportedCatalogUuid(root, "artist_uuid");
        RequireImportedResourceUuid(reader, artistUuid);
        if (!root.TryGetProperty("songs", out var songs) || songs.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException(
                "Imported repertoire snapshot requires its catalog song array."
            );
        }

        var observations = new List<ProviderRepertoireObservation>();
        var identities = new HashSet<Guid>();
        foreach (var song in songs.EnumerateArray())
        {
            var pointer = "/songs/" + observations.Count.ToString(CultureInfo.InvariantCulture);
            var uuid = ImportedCatalogUuid(song, "uuid");
            if (!identities.Add(uuid))
            {
                throw new ArgumentException(
                    "Imported repertoire snapshot repeats a catalog song identity."
                );
            }
            var identity = new ObservedProviderIdentity(
                new("legacy_catalog", "repertoire_song", uuid.ToString("D")),
                reader.Evidence(pointer + "/uuid")
            );
            observations.Add(
                new()
                {
                    Origin = reader.Origin(
                        identity.Identity,
                        pointer,
                        ProviderObservationScope.LegacySnapshot
                    ),
                    Identities = [identity],
                    Title = reader.String(song, "name", pointer),
                    Text = reader.TextFields(song, pointer, ImportedRepertoireTextFields),
                }
            );
        }

        return reader.Result() with
        {
            Repertoire = observations,
        };
    }

    private static JsonElement ImportedCatalogEnvelope(ProviderObservationReader reader)
    {
        var root = reader.Root;
        if (
            root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("schema_version", out var version)
            || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out var number)
            || number != 1
        )
        {
            throw new ArgumentException("Imported catalog snapshot requires schema version 1.");
        }
        return root;
    }

    private static Guid ImportedCatalogUuid(JsonElement row, string property)
    {
        if (
            row.ValueKind != JsonValueKind.Object
            || !row.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.String
            || !Guid.TryParseExact(value.GetString(), "D", out var uuid)
            || uuid == Guid.Empty
        )
        {
            throw new ArgumentException("Imported catalog snapshot requires valid catalog UUIDs.");
        }
        return uuid;
    }

    private static void RequireImportedResourceUuid(ProviderObservationReader reader, Guid uuid)
    {
        if (
            !Guid.TryParseExact(reader.Input.Resource.ProviderKey, "D", out var expected)
            || expected != uuid
        )
        {
            throw new ArgumentException(
                "Imported catalog snapshot does not identify its requested resource."
            );
        }
    }
}
