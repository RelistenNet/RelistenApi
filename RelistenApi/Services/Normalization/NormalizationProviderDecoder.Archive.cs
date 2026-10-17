using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

// Archive fields retain provider wording. File references and work-credit prose do not
// establish playable catalog bindings, canonical authorship or derivative file titles.
public sealed partial class NormalizationProviderDecoder
{
    private static readonly IReadOnlyDictionary<string, string> ArchiveTextFields = new Dictionary<
        string,
        string
    >
    {
        ["title"] = "title",
        ["taper"] = "taper",
        ["transferer"] = "transferrer",
        ["description"] = "description",
        ["notes"] = "notes",
        ["source"] = "source",
        ["lineage"] = "lineage",
    };

    private static readonly IReadOnlyDictionary<string, string> ArchiveFileTextFields =
        new Dictionary<string, string>
        {
            ["format"] = "format",
            ["taper"] = "taper",
            ["transferer"] = "transferrer",
            ["description"] = "description",
            ["notes"] = "notes",
            ["source"] = "source",
            ["lineage"] = "lineage",
        };

    private static ProviderDecodeResult DecodeArchive(ProviderObservationReader reader)
    {
        var root = reader.Root;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Archive input must be a metadata response object.");
        }
        if (
            root.TryGetProperty("errcode", out var errorCode)
                && errorCode.ValueKind != JsonValueKind.Null
            || root.TryGetProperty("error", out var error)
                && error.ValueKind != JsonValueKind.Null
                && (error.ValueKind != JsonValueKind.String || error.GetString() != "")
        )
        {
            throw new ArgumentException("Archive input contains a provider error.");
        }

        var dark = root.TryGetProperty("is_dark", out var darkValue) && ArchiveIsTrue(darkValue);
        var hasMetadata = root.TryGetProperty("metadata", out var metadata);
        if (hasMetadata && metadata.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
        {
            throw new ArgumentException("Archive input has an invalid metadata object.");
        }
        if (
            metadata.ValueKind == JsonValueKind.Object
            && metadata.TryGetProperty("identifier", out var identifier)
        )
        {
            if (
                identifier.ValueKind != JsonValueKind.String
                || identifier.GetString() != reader.Input.Resource.ProviderKey
            )
            {
                throw new ArgumentException("Archive input does not identify the requested item.");
            }
        }
        else if (!dark)
        {
            throw new ArgumentException("Archive input does not identify the requested item.");
        }

        if (dark)
        {
            reader.Issue("resource_unavailable", "/is_dark");
            return reader.Result();
        }

        var text = reader.TextFields(metadata, "/metadata", ArchiveTextFields).ToList();
        ArchiveUnclassifiedWorkFields(reader, metadata, "/metadata", text);
        var creators = ArchiveStrings(reader, metadata, "creator", "/metadata");
        var dates = ArchiveStrings(reader, metadata, "date", "/metadata");
        var names = ArchiveStrings(reader, metadata, "venue", "/metadata");
        var localities = ArchiveStrings(reader, metadata, "coverage", "/metadata");
        text.AddRange(dates.Select(value => new ProviderTextObservation("date", value)));
        text.AddRange(names.Select(value => new ProviderTextObservation("venue", value)));
        text.AddRange(localities.Select(value => new ProviderTextObservation("coverage", value)));
        if (dates.Count > 1)
        {
            reader.Issue("multiple_recording_dates", "/metadata/date");
        }
        if (localities.Count > 1)
        {
            reader.Issue("multiple_venue_localities", "/metadata/coverage");
        }

        var venue =
            names.Count == 0 && localities.Count == 0
                ? null
                : new ProviderVenueObservation
                {
                    Origin = reader.Origin(
                        reader.Input.Resource,
                        "/metadata",
                        ProviderObservationScope.ResourceItem
                    ),
                    Names = names,
                    OriginalLocality = localities.Count == 1 ? localities[0] : null,
                };
        var files = ArchiveFiles(reader, root);
        var recording = new ProviderRecordingObservation
        {
            Origin = reader.Origin(
                reader.Input.Resource,
                "/metadata",
                ProviderObservationScope.ResourceItem
            ),
            Performers = creators
                .Select(name => new ProviderPerformerObservation { Name = name })
                .ToArray(),
            LiteralDate = dates.Count == 1 ? dates[0] : null,
            Venue = venue,
            Text = text,
            Files = files,
        };
        return reader.Result() with
        {
            Recordings = [recording],
            Venues = venue == null ? [] : [venue],
        };
    }

    private static IReadOnlyList<ProviderFileObservation> ArchiveFiles(
        ProviderObservationReader reader,
        JsonElement root
    )
    {
        if (!root.TryGetProperty("files", out var files) || files.ValueKind == JsonValueKind.Null)
        {
            return [];
        }
        if (files.ValueKind != JsonValueKind.Array)
        {
            reader.Issue("expected_file_array", "/files");
            return [];
        }

        var result = new List<ProviderFileObservation>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var file in files.EnumerateArray())
        {
            var pointer = ProviderObservationReader.Pointer(
                "/files",
                index++.ToString(CultureInfo.InvariantCulture)
            );
            if (file.ValueKind != JsonValueKind.Object)
            {
                reader.Issue("expected_file_object", pointer);
                continue;
            }

            var filename = reader.String(file, "name", pointer);
            IReadOnlyList<ObservedProviderIdentity> identities = [];
            if (filename != null && !string.IsNullOrWhiteSpace(filename.Value))
            {
                var fileIdentity = new NormalizationResourceKey(
                    "archive.org",
                    "item_file",
                    JsonSerializer.Serialize(
                        new[] { reader.Input.Resource.ProviderKey, filename.Value }
                    )
                );
                identities = [new(fileIdentity, filename.Evidence)];
                if (!names.Add(filename.Value))
                {
                    reader.Issue(
                        "duplicate_file_identity",
                        ProviderObservationReader.Pointer(pointer, "name")
                    );
                }
            }
            else
            {
                reader.Issue("missing_file_identity", pointer);
            }

            var text = reader.TextFields(file, pointer, ArchiveFileTextFields).ToList();
            if (
                file.TryGetProperty("track", out var rawTrack)
                && rawTrack.ValueKind == JsonValueKind.String
            )
            {
                text.Add(
                    new(
                        "track",
                        reader.String(
                            rawTrack,
                            ProviderObservationReader.Pointer(pointer, "track")
                        )!
                    )
                );
            }
            ArchiveUnclassifiedWorkFields(reader, file, pointer, text);
            result.Add(
                new ProviderFileObservation
                {
                    Origin = reader.Origin(
                        reader.Input.Resource,
                        pointer,
                        ProviderObservationScope.ResourceItem
                    ),
                    Identities = identities,
                    Filename = filename,
                    OriginalFilename = reader.String(file, "original", pointer),
                    Title = reader.String(file, "title", pointer),
                    Position = ArchiveTrackPosition(reader, file, pointer),
                    Text = text,
                }
            );
        }
        return result;
    }

    private static ObservedValue<int>? ArchiveTrackPosition(
        ProviderObservationReader reader,
        JsonElement file,
        string pointer
    )
    {
        if (!file.TryGetProperty("track", out var track) || track.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        var trackPointer = ProviderObservationReader.Pointer(pointer, "track");
        if (track.ValueKind == JsonValueKind.Number)
        {
            return reader.Integer(track, trackPointer);
        }
        if (
            track.ValueKind == JsonValueKind.String
            && int.TryParse(
                track.GetString(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var number
            )
        )
        {
            return new(number, reader.Evidence(trackPointer));
        }
        reader.Issue("unstructured_track_number", trackPointer);
        return null;
    }

    private static IReadOnlyList<ObservedValue<string>> ArchiveStrings(
        ProviderObservationReader reader,
        JsonElement parent,
        string name,
        string pointer
    ) =>
        parent.TryGetProperty(name, out var value)
            ? reader.Strings(value, ProviderObservationReader.Pointer(pointer, name))
            : [];

    private static void ArchiveUnclassifiedWorkFields(
        ProviderObservationReader reader,
        JsonElement parent,
        string pointer,
        List<ProviderTextObservation> text
    )
    {
        // Archive item fields do not define a universal work-credit schema. Preserve wording
        // without treating an item uploader's "creator" or arbitrary composer field as authorship.
        foreach (
            var field in new[]
            {
                "writer",
                "writers",
                "composer",
                "composers",
                "songwriter",
                "lyricist",
            }
        )
        {
            if (
                !parent.TryGetProperty(field, out var value)
                || value.ValueKind == JsonValueKind.Null
            )
            {
                continue;
            }
            var fieldPointer = ProviderObservationReader.Pointer(pointer, field);
            reader.Issue("unsupported_work_credit_field", fieldPointer);
            text.AddRange(
                reader
                    .Strings(value, fieldPointer)
                    .Select(wording => new ProviderTextObservation(
                        "provider_field:" + field,
                        wording
                    ))
            );
        }
    }

    private static bool ArchiveIsTrue(JsonElement value) =>
        value.ValueKind == JsonValueKind.True
        || value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number)
            && number == 1
        || value.ValueKind == JsonValueKind.String
            && (
                value.GetString() == "1"
                || string.Equals(value.GetString(), "true", StringComparison.OrdinalIgnoreCase)
            );
}
