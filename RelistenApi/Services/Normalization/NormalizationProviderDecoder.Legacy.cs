using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

public sealed partial class NormalizationProviderDecoder
{
    private static readonly IReadOnlyDictionary<string, string> LegacyRecordingTextFields =
        new Dictionary<string, string>
        {
            ["description"] = "description",
            ["taper_notes"] = "taper_notes",
            ["source"] = "source",
            ["taper"] = "taper",
            ["transferrer"] = "transferrer",
            ["lineage"] = "lineage",
            ["display_date"] = "display_date",
            ["upstream_identifier"] = "catalog_upstream_identifier",
        };

    private static ProviderDecodeResult DecodeLegacyRecording(ProviderObservationReader reader)
    {
        var root = reader.Root;
        if (
            root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("representation", out var representation)
            || representation.ValueKind != JsonValueKind.String
            || representation.GetString() != "legacy_catalog"
            || !root.TryGetProperty("source", out var source)
            || source.ValueKind != JsonValueKind.Object
            || !source.TryGetProperty("uuid", out var uuid)
            || uuid.ValueKind != JsonValueKind.String
            || !Guid.TryParse(uuid.GetString(), out var sourceUuid)
            || sourceUuid == Guid.Empty
            || !Guid.TryParse(reader.Input.Resource.ProviderKey, out var expectedUuid)
            || sourceUuid != expectedUuid
        )
        {
            throw new ArgumentException(
                "Legacy snapshot does not identify the requested catalog recording."
            );
        }

        var performers = LegacyPerformers(reader, root, source);
        var sourceVenue = LegacyVenue(reader, root, "source_venue");
        var showVenue = LegacyVenue(reader, root, "show_venue");
        var text = reader.TextFields(source, "/source", LegacyRecordingTextFields).ToList();
        if (root.TryGetProperty("show", out var show) && show.ValueKind == JsonValueKind.Object)
        {
            text.AddRange(
                reader.TextFields(
                    show,
                    "/show",
                    new Dictionary<string, string>
                    {
                        ["uuid"] = "catalog_show_uuid",
                        ["date"] = "show_date",
                        ["display_date"] = "show_display_date",
                    }
                )
            );
            if (LegacyId(source, "show_id") != LegacyId(show, "id"))
            {
                reader.Issue("recording_show_context_mismatch", "/show");
            }
            var sourceDate = reader.String(source, "display_date", "/source");
            var showDate = reader.String(show, "display_date", "/show");
            if (sourceDate != null && showDate != null && sourceDate.Value != showDate.Value)
            {
                reader.Issue("source_show_date_wording_differs", "/show/display_date");
            }
        }
        else
        {
            reader.Issue(
                "missing_recording_show_context",
                root.TryGetProperty("show", out _) ? "/show" : ""
            );
        }

        // Artist mappings describe configured importers, not the origin of this source. Their
        // provider IDs must never be attached to this recording or its files as inferred identities.
        var recording = new ProviderRecordingObservation
        {
            Origin = reader.Origin(
                reader.Input.Resource,
                "/source",
                ProviderObservationScope.LegacySnapshot
            ),
            Performers = performers,
            LiteralDate = reader.String(source, "display_date", "/source"),
            Venue = sourceVenue,
            Text = text,
            Files = LegacyFiles(reader, root, source),
        };
        return reader.Result() with
        {
            Recordings = [recording],
            Venues = new[] { sourceVenue, showVenue }.OfType<ProviderVenueObservation>().ToArray(),
        };
    }

    private static IReadOnlyList<ProviderPerformerObservation> LegacyPerformers(
        ProviderObservationReader reader,
        JsonElement root,
        JsonElement source
    )
    {
        if (
            !root.TryGetProperty("artist", out var artist)
            || artist.ValueKind != JsonValueKind.Object
        )
        {
            reader.Issue(
                "missing_recording_artist_context",
                root.TryGetProperty("artist", out _) ? "/artist" : ""
            );
            return [];
        }
        if (LegacyId(source, "artist_id") != LegacyId(artist, "id"))
        {
            reader.Issue("recording_artist_context_mismatch", "/artist");
        }
        var identity = LegacyUuidIdentity(reader, artist, "/artist", "artist");
        return
        [
            new()
            {
                Name = reader.String(artist, "name", "/artist"),
                Identities = identity == null ? [] : [identity],
            },
        ];
    }

    private static ProviderVenueObservation? LegacyVenue(
        ProviderObservationReader reader,
        JsonElement root,
        string property
    )
    {
        if (!root.TryGetProperty(property, out var venue) || venue.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        var pointer = "/" + property;
        if (venue.ValueKind != JsonValueKind.Object)
        {
            reader.Issue("expected_venue_object", pointer);
            return null;
        }
        var identity = LegacyUuidIdentity(reader, venue, pointer, "venue");
        var origin = reader.Origin(
            identity?.Identity ?? reader.Input.Resource,
            pointer,
            ProviderObservationScope.LegacySnapshot
        );
        var name = reader.String(venue, "name", pointer);
        return new()
        {
            Origin = origin,
            Names = name == null ? [] : [name],
            Identities = identity == null ? [] : [identity],
            OriginalLocality = reader.String(venue, "location", pointer),
            Latitude = venue.TryGetProperty("latitude", out var latitude)
                ? reader.Number(latitude, pointer + "/latitude")
                : null,
            Longitude = venue.TryGetProperty("longitude", out var longitude)
                ? reader.Number(longitude, pointer + "/longitude")
                : null,
            CoordinateKind = ObservedCoordinateKind.Unknown,
            Text = reader.TextFields(
                venue,
                pointer,
                new Dictionary<string, string>
                {
                    ["past_names"] = "past_names",
                    ["upstream_identifier"] = "catalog_upstream_identifier",
                }
            ),
        };
    }

    private static IReadOnlyList<ProviderFileObservation> LegacyFiles(
        ProviderObservationReader reader,
        JsonElement root,
        JsonElement source
    )
    {
        if (
            !root.TryGetProperty("tracks", out var tracks)
            || tracks.ValueKind != JsonValueKind.Array
        )
        {
            reader.Issue(
                "expected_file_array",
                root.TryGetProperty("tracks", out _) ? "/tracks" : ""
            );
            return [];
        }

        var result = new List<ProviderFileObservation>();
        var identities = new HashSet<NormalizationResourceKey>();
        var index = 0;
        foreach (var entry in tracks.EnumerateArray())
        {
            var pointer = "/tracks/" + index++;
            if (
                entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("track", out var track)
                || track.ValueKind != JsonValueKind.Object
            )
            {
                reader.Issue("expected_file_object", pointer);
                continue;
            }
            var trackPointer = pointer + "/track";
            var identity = LegacyUuidIdentity(reader, track, trackPointer, "track");
            if (identity != null && !identities.Add(identity.Identity))
            {
                reader.Issue("duplicate_file_identity", trackPointer + "/uuid");
            }
            LegacyFileContext(reader, entry, track, source, pointer);
            var set =
                entry.TryGetProperty("set_context", out var setValue)
                && setValue.ValueKind == JsonValueKind.Object
                    ? setValue
                    : default;
            result.Add(
                new()
                {
                    Origin = reader.Origin(
                        identity?.Identity ?? reader.Input.Resource,
                        trackPointer,
                        ProviderObservationScope.LegacySnapshot
                    ),
                    Identities = identity == null ? [] : [identity],
                    Title = reader.String(track, "title", trackPointer),
                    PlaybackLocator = reader.String(track, "mp3_url", trackPointer),
                    Position = track.TryGetProperty("track_position", out var position)
                        ? reader.Integer(position, trackPointer + "/track_position")
                        : null,
                    SetLabel =
                        set.ValueKind == JsonValueKind.Object
                            ? reader.String(set, "name", pointer + "/set_context")
                            : null,
                    Text = reader.TextFields(
                        track,
                        trackPointer,
                        new Dictionary<string, string>
                        {
                            ["flac_url"] = "flac_url",
                            ["mp3_md5"] = "mp3_md5",
                            ["flac_md5"] = "flac_md5",
                            ["slug"] = "catalog_slug",
                        }
                    ),
                }
            );
        }
        return result;
    }

    private static void LegacyFileContext(
        ProviderObservationReader reader,
        JsonElement entry,
        JsonElement track,
        JsonElement source,
        string pointer
    )
    {
        if (
            track.TryGetProperty("is_orphaned", out var orphaned)
            && orphaned.ValueKind == JsonValueKind.True
        )
        {
            reader.Issue("orphaned_catalog_track", pointer + "/track/is_orphaned");
        }
        foreach (
            var (contextProperty, trackProperty, kind) in new[]
            {
                ("source_context", "source_id", "source"),
                ("set_context", "source_set_id", "set"),
                ("artist_context", "artist_id", "artist"),
            }
        )
        {
            var contextPointer = pointer + "/" + contextProperty;
            if (
                !entry.TryGetProperty(contextProperty, out var context)
                || context.ValueKind != JsonValueKind.Object
            )
            {
                reader.Issue(
                    "missing_track_" + kind + "_context",
                    entry.TryGetProperty(contextProperty, out _) ? contextPointer : pointer
                );
                continue;
            }
            if (
                LegacyId(track, trackProperty) is not { } trackId
                || LegacyId(context, "id") != trackId
            )
            {
                reader.Issue("track_" + kind + "_context_mismatch", contextPointer);
            }
            var expected =
                kind == "artist" ? LegacyId(source, "artist_id") : LegacyId(source, "id");
            var actual = kind == "set" ? LegacyId(context, "source_id") : LegacyId(context, "id");
            if (actual != expected)
            {
                reader.Issue("track_" + kind + "_recording_mismatch", contextPointer);
            }
        }
    }

    private static long? LegacyId(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var id)
            ? id
            : null;

    private static ObservedProviderIdentity? LegacyUuidIdentity(
        ProviderObservationReader reader,
        JsonElement parent,
        string pointer,
        string resourceType
    )
    {
        var value = reader.String(parent, "uuid", pointer);
        if (value == null || !Guid.TryParse(value.Value, out var uuid) || uuid == Guid.Empty)
        {
            reader.Issue(
                "invalid_catalog_identity",
                parent.TryGetProperty("uuid", out _) ? pointer + "/uuid" : pointer
            );
            return null;
        }
        return new(new("legacy_catalog", resourceType, uuid.ToString("D")), value.Evidence);
    }
}
