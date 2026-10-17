using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

// Phish.in supplies recording files and song associations. Those associations do not establish
// ordered musical passages, composition identity or authorship; decoding keeps that distinction.
public sealed partial class NormalizationProviderDecoder
{
    private static ProviderDecodeResult DecodePhishCatalog(ProviderObservationReader reader)
    {
        if (!PhishCatalogData(reader, out var data))
        {
            return reader.Result();
        }

        var route = reader.Input.Resource.ProviderKey;
        if (route is "eras" or "tours")
        {
            return reader.Result();
        }

        if (route is not ("songs" or "venues"))
        {
            reader.Issue("unsupported_catalog_route", "");
            return reader.Result();
        }

        if (data.ValueKind != JsonValueKind.Array)
        {
            reader.Issue("expected_array", "/data");
            return reader.Result();
        }

        var songs = new List<ProviderRepertoireObservation>();
        var venues = new List<ProviderVenueObservation>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var item in data.EnumerateArray())
        {
            var pointer = "/data/" + index++.ToString(CultureInfo.InvariantCulture);
            if (item.ValueKind != JsonValueKind.Object)
            {
                reader.Issue("expected_object", pointer);
                continue;
            }

            var identity = PhishIdentity(
                reader,
                item,
                "id",
                pointer,
                route == "songs" ? "song" : "venue",
                required: true
            );
            if (identity == null)
            {
                continue;
            }

            if (!identities.Add(identity.Identity.ProviderKey))
            {
                reader.Issue("duplicate_provider_identity", pointer + "/id");
            }

            var origin = reader.Origin(
                identity.Identity,
                pointer,
                ProviderObservationScope.CatalogMember
            );
            if (route == "venues")
            {
                venues.Add(PhishVenue(reader, item, pointer, origin, identity));
                continue;
            }

            var title = reader.String(item, "title", pointer);
            if (title == null || string.IsNullOrWhiteSpace(title.Value))
            {
                reader.Issue("missing_title", pointer);
                continue;
            }

            songs.Add(
                new()
                {
                    Origin = origin,
                    Title = title,
                    Identities = [identity],
                    AliasOf = PhishIdentity(reader, item, "alias_for", pointer, "song"),
                }
            );
        }

        return reader.Result() with
        {
            Repertoire = songs,
            Venues = venues,
        };
    }

    private static ProviderDecodeResult DecodePhishShow(ProviderObservationReader reader)
    {
        var show = reader.Root;
        if (show.ValueKind != JsonValueKind.Object)
        {
            reader.Issue("expected_object", "");
            return reader.Result();
        }

        var identity = PhishIdentity(reader, show, "id", "", "show", required: true);
        if (identity == null)
        {
            return reader.Result();
        }

        if (identity.Identity != reader.Input.Resource)
        {
            reader.Issue("resource_identity_mismatch", "/id");
            return reader.Result();
        }

        ProviderVenueObservation? venue = null;
        if (
            show.TryGetProperty("venue", out var venueValue)
            && venueValue.ValueKind != JsonValueKind.Null
        )
        {
            if (venueValue.ValueKind == JsonValueKind.Object)
            {
                venue = PhishVenue(
                    reader,
                    venueValue,
                    "/venue",
                    reader.Origin(
                        reader.Input.Resource,
                        "/venue",
                        ProviderObservationScope.ResourceItem
                    ),
                    PhishIdentity(reader, venueValue, "id", "/venue", "venue", required: true)
                );
            }
            else
            {
                reader.Issue("expected_object", "/venue");
            }
        }

        var text = new List<ProviderTextObservation>(
            reader.TextFields(
                show,
                "",
                new Dictionary<string, string> { ["taper_notes"] = "taper_notes" }
            )
        );
        PhishTags(reader, show, text);
        var files = PhishFiles(reader, show, identity.Identity.ProviderKey);
        var recording = new ProviderRecordingObservation
        {
            Origin = reader.Origin(
                reader.Input.Resource,
                "",
                ProviderObservationScope.ResourceItem
            ),
            LiteralDate = reader.String(show, "date", ""),
            Venue = venue,
            Text = text,
            Files = files,
        };
        return reader.Result() with { Recordings = [recording] };
    }

    private static bool PhishCatalogData(ProviderObservationReader reader, out JsonElement data)
    {
        data = default;
        var root = reader.Root;
        if (root.ValueKind != JsonValueKind.Object)
        {
            reader.Issue("expected_object", "");
            return false;
        }

        if (
            !root.TryGetProperty("success", out var success)
            || success.ValueKind != JsonValueKind.True
        )
        {
            reader.Issue(
                "unsuccessful_provider_response",
                root.TryGetProperty("success", out _) ? "/success" : ""
            );
            return false;
        }

        if (
            !root.TryGetProperty("data", out data)
            || data.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
        )
        {
            reader.Issue("missing_catalog_data", "");
            return false;
        }

        return true;
    }

    private static ProviderVenueObservation PhishVenue(
        ProviderObservationReader reader,
        JsonElement value,
        string pointer,
        ProviderObservationOrigin origin,
        ObservedProviderIdentity? identity
    )
    {
        var name = reader.String(value, "name", pointer);
        return new()
        {
            Origin = origin,
            Names = name == null ? [] : [name],
            Identities = identity == null ? [] : [identity],
            OriginalLocality = reader.String(value, "location", pointer),
            Latitude = value.TryGetProperty("latitude", out var latitude)
                ? reader.Number(latitude, pointer + "/latitude")
                : null,
            Longitude = value.TryGetProperty("longitude", out var longitude)
                ? reader.Number(longitude, pointer + "/longitude")
                : null,
            CoordinateKind = ObservedCoordinateKind.Unknown,
            Text = reader.TextFields(
                value,
                pointer,
                new Dictionary<string, string> { ["past_names"] = "historical_names" }
            ),
        };
    }

    private static IReadOnlyList<ProviderFileObservation> PhishFiles(
        ProviderObservationReader reader,
        JsonElement show,
        string showKey
    )
    {
        if (!show.TryGetProperty("tracks", out var tracks))
        {
            reader.Issue("missing_tracks", "");
            return [];
        }

        if (tracks.ValueKind != JsonValueKind.Array)
        {
            reader.Issue("expected_array", "/tracks");
            return [];
        }

        var files = new List<ProviderFileObservation>();
        var index = 0;
        foreach (var track in tracks.EnumerateArray())
        {
            var pointer = "/tracks/" + index++.ToString(CultureInfo.InvariantCulture);
            if (track.ValueKind != JsonValueKind.Object)
            {
                reader.Issue("expected_object", pointer);
                continue;
            }

            var identity = PhishIdentity(reader, track, "id", pointer, "track", required: true);
            var owner = PhishIdentity(reader, track, "show_id", pointer, "show");
            if (owner != null && owner.Identity.ProviderKey != showKey)
            {
                reader.Issue("track_show_identity_mismatch", pointer + "/show_id");
            }

            var set = reader.String(track, "set", pointer);
            var setName = reader.String(track, "set_name", pointer);
            files.Add(
                new()
                {
                    Origin = reader.Origin(
                        reader.Input.Resource,
                        pointer,
                        ProviderObservationScope.ResourceItem
                    ),
                    Identities = identity == null ? [] : [identity],
                    RecordingIdentity = owner,
                    Title = reader.String(track, "title", pointer),
                    PlaybackLocator = reader.String(track, "mp3", pointer),
                    Position = track.TryGetProperty("position", out var position)
                        ? reader.Integer(position, pointer + "/position")
                        : null,
                    SetLabel = set ?? setName,
                    SongIdentities = PhishSongMembership(reader, track, pointer),
                    Text = setName == null ? [] : [new("set_name", setName)],
                }
            );
        }

        return files;
    }

    private static IReadOnlyList<ObservedProviderIdentity> PhishSongMembership(
        ProviderObservationReader reader,
        JsonElement track,
        string pointer
    )
    {
        if (!track.TryGetProperty("song_ids", out var songIds))
        {
            return [];
        }

        if (songIds.ValueKind != JsonValueKind.Array)
        {
            reader.Issue("expected_array", pointer + "/song_ids");
            return [];
        }

        var identities = new List<ObservedProviderIdentity>();
        var index = 0;
        foreach (var songId in songIds.EnumerateArray())
        {
            var songPointer =
                pointer + "/song_ids/" + index++.ToString(CultureInfo.InvariantCulture);
            var identity = PhishScalarIdentity(reader, songId, songPointer, "song");
            if (identity != null)
            {
                identities.Add(identity);
            }
        }

        return identities;
    }

    private static void PhishTags(
        ProviderObservationReader reader,
        JsonElement show,
        List<ProviderTextObservation> text
    )
    {
        if (!show.TryGetProperty("tags", out var tags))
        {
            return;
        }

        if (tags.ValueKind != JsonValueKind.Array)
        {
            reader.Issue("expected_array", "/tags");
            return;
        }

        var index = 0;
        foreach (var tag in tags.EnumerateArray())
        {
            var pointer = "/tags/" + index++.ToString(CultureInfo.InvariantCulture);
            if (tag.ValueKind != JsonValueKind.Object)
            {
                reader.Issue("expected_object", pointer);
                continue;
            }

            text.AddRange(
                reader.TextFields(
                    tag,
                    pointer,
                    new Dictionary<string, string> { ["name"] = "tag", ["notes"] = "tag_notes" }
                )
            );
        }
    }

    private static ObservedProviderIdentity? PhishIdentity(
        ProviderObservationReader reader,
        JsonElement parent,
        string property,
        string pointer,
        string resourceType,
        bool required = false
    )
    {
        if (!parent.TryGetProperty(property, out var value))
        {
            if (required)
            {
                reader.Issue("missing_provider_identity", pointer);
            }
            return null;
        }

        if (!required && value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return PhishScalarIdentity(
            reader,
            value,
            ProviderObservationReader.Pointer(pointer, property),
            resourceType
        );
    }

    private static ObservedProviderIdentity? PhishScalarIdentity(
        ProviderObservationReader reader,
        JsonElement value,
        string pointer,
        string resourceType
    )
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var id) || id <= 0)
        {
            reader.Issue("invalid_provider_identity", pointer);
            return null;
        }

        return reader.Identity(value, pointer, "phish.in", resourceType);
    }
}
