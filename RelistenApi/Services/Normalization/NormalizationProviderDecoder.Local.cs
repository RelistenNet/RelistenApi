using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

public sealed partial class NormalizationProviderDecoder
{
    private static ProviderDecodeResult DecodeLocalRoot(ProviderObservationReader reader)
    {
        var root = reader.Root;
        if (
            root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data", out var dates)
            || dates.ValueKind != JsonValueKind.Object
        )
        {
            throw new InvalidDataException("Local metadata has no date directory map.");
        }

        var recordings = new List<ProviderRecordingObservation>();
        foreach (var date in dates.EnumerateObject())
        {
            var datePointer = ProviderObservationReader.Pointer("/data", date.Name);
            if (date.Value.ValueKind != JsonValueKind.Array)
            {
                reader.Issue("expected_directory_list", datePointer);
                continue;
            }

            var index = 0;
            foreach (var metadata in date.Value.EnumerateArray())
            {
                var pointer = datePointer + "/" + index++;
                if (metadata.ValueKind != JsonValueKind.Object)
                {
                    reader.Issue("expected_directory_metadata", pointer);
                    continue;
                }

                var directory = reader.String(metadata, "dir", pointer);
                if (directory == null || string.IsNullOrWhiteSpace(directory.Value))
                {
                    reader.Issue("missing_directory_identity", pointer);
                    continue;
                }

                var resource = new NormalizationResourceKey(
                    "local",
                    "directory_metadata",
                    JsonSerializer.Serialize(
                        new[] { reader.Input.Resource.ProviderKey, directory.Value }
                    )
                );
                var origin = reader.Origin(
                    resource,
                    pointer,
                    ProviderObservationScope.CatalogMember
                );
                var literalDate = reader.String(metadata, "date", pointer);
                if (literalDate == null)
                {
                    // A JSON property name has no scalar JSON Pointer. Cite its containing map,
                    // which preserves the date key without pretending the key was a value.
                    literalDate = new(date.Name, reader.Evidence("/data"));
                }
                else if (literalDate.Value != date.Name)
                {
                    reader.Issue("conflicting_directory_date", pointer);
                }

                recordings.Add(
                    new()
                    {
                        Origin = origin,
                        LiteralDate = literalDate,
                        Venue = LocalVenue(reader, metadata, pointer, origin),
                        Text = reader.TextFields(
                            metadata,
                            pointer,
                            new Dictionary<string, string> { ["taper_notes"] = "taper_notes" }
                        ),
                    }
                );
            }
        }
        return reader.Result() with { Recordings = recordings };
    }

    private static ProviderDecodeResult DecodeLocalDirectory(ProviderObservationReader reader)
    {
        // The directory response does not supply its recording's date. Only the retained
        // root map can establish that association; the binder must consume both inputs.
        using var key = ReadLocalDirectoryKey(reader.Input.Resource.ProviderKey);
        if (
            key.RootElement.ValueKind != JsonValueKind.Array
            || key.RootElement.GetArrayLength() != 2
            || key.RootElement.EnumerateArray()
                .Any(part =>
                    part.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(part.GetString())
                )
        )
        {
            throw new InvalidDataException("Local directory resource has an invalid key.");
        }

        var root = reader.Root;
        if (
            root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("tracks", out var tracks)
            || tracks.ValueKind != JsonValueKind.Array
            || !root.TryGetProperty("txts", out var notes)
            || notes.ValueKind != JsonValueKind.Array
        )
        {
            throw new InvalidDataException("Local directory metadata has invalid files or notes.");
        }

        var origin = reader.Origin(
            reader.Input.Resource,
            "",
            ProviderObservationScope.ResourceItem
        );
        var files = new List<ProviderFileObservation>();
        var index = 0;
        foreach (var track in tracks.EnumerateArray())
        {
            var pointer = "/tracks/" + index++;
            if (track.ValueKind != JsonValueKind.Object)
            {
                reader.Issue("expected_file_metadata", pointer);
                continue;
            }
            var locator = reader.String(track, "mp3", pointer);
            var title = reader.String(track, "title", pointer);
            ObservedValue<int>? position = null;
            var text = new List<ProviderTextObservation>();
            if (
                track.TryGetProperty("tags", out var tags)
                && tags.ValueKind == JsonValueKind.Object
            )
            {
                var taggedTitle = reader.String(tags, "title", pointer + "/tags");
                if (taggedTitle != null)
                {
                    if (title != null && title.Value != taggedTitle.Value)
                    {
                        text.Add(new("alternate_title", title));
                        reader.Issue("conflicting_file_title", pointer);
                    }
                    title = taggedTitle;
                }
                if (
                    tags.TryGetProperty("track", out var numbering)
                    && numbering.ValueKind == JsonValueKind.Object
                    && numbering.TryGetProperty("no", out var number)
                )
                {
                    position = reader.Integer(number, pointer + "/tags/track/no");
                }
                else if (
                    numbering.ValueKind
                    is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object)
                )
                {
                    reader.Issue("expected_track_numbering_object", pointer + "/tags/track");
                }
            }
            else if (tags.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            {
                reader.Issue("expected_tags_object", pointer + "/tags");
            }
            if (position == null && track.TryGetProperty("position", out var fallback))
            {
                position = reader.Integer(fallback, pointer + "/position");
            }

            files.Add(
                new()
                {
                    Origin = origin with { Evidence = reader.Evidence(pointer) },
                    PlaybackLocator = locator,
                    Filename = locator,
                    Title = title,
                    Position = position,
                    Text = text,
                }
            );
            if (locator == null)
            {
                reader.Issue("missing_file_locator", pointer);
            }
        }

        var descriptions = reader
            .Strings(notes, "/txts")
            .Select(note => new ProviderTextObservation("description", note))
            .ToArray();
        return reader.Result() with
        {
            Recordings =
            [
                new()
                {
                    Origin = origin,
                    Text = descriptions,
                    Files = files,
                },
            ],
        };
    }

    private static JsonDocument ReadLocalDirectoryKey(string key)
    {
        try
        {
            return JsonDocument.Parse(key);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Local directory resource has an invalid key.");
        }
    }

    private static ProviderVenueObservation? LocalVenue(
        ProviderObservationReader reader,
        JsonElement metadata,
        string pointer,
        ProviderObservationOrigin origin
    )
    {
        var name = reader.String(metadata, "venue", pointer);
        var city = reader.String(metadata, "city", pointer);
        var region = reader.String(metadata, "state", pointer);
        if (name == null && city == null && region == null)
        {
            return null;
        }
        return new()
        {
            Origin = origin,
            Names = name == null ? [] : [name],
            City = city,
            Region = region,
        };
    }
}
