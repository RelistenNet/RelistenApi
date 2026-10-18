using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

public sealed partial class NormalizationProviderDecoder
{
    private static ProviderDecodeResult DecodeMusicBrainzWork(ProviderObservationReader reader)
    {
        var work = ReadMusicBrainzRoot(reader);
        if (work == null)
        {
            return reader.Result();
        }

        var observation = new MusicBrainzWorkObservation
        {
            Origin = reader.Origin(
                reader.Input.Resource,
                "",
                ProviderObservationScope.ResourceItem
            ),
            Work = work,
            Iswcs = ReadMusicBrainzIswcs(reader, reader.Root),
            Aliases = ReadMusicBrainzAliases(reader, reader.Root),
            Attributes = ReadMusicBrainzWorkAttributes(reader, reader.Root),
            Relationships = ReadMusicBrainzRelationships(reader, reader.Root),
        };
        return reader.Result() with { MusicBrainzWorks = [observation] };
    }

    private static ProviderDecodeResult DecodeMusicBrainzRecording(ProviderObservationReader reader)
    {
        var recording = ReadMusicBrainzRoot(reader);
        if (recording == null)
        {
            return reader.Result();
        }

        var observation = new MusicBrainzRecordingObservation
        {
            Origin = reader.Origin(
                reader.Input.Resource,
                "",
                ProviderObservationScope.ResourceItem
            ),
            Recording = recording,
            ArtistCredits = ReadMusicBrainzArtistCredits(reader, reader.Root),
            Relationships = ReadMusicBrainzRelationships(reader, reader.Root),
        };
        return reader.Result() with { MusicBrainzRecordings = [observation] };
    }

    private static ProviderDecodeResult DecodeMusicBrainzArtist(ProviderObservationReader reader)
    {
        var artist = ReadMusicBrainzRoot(reader);
        if (artist == null)
        {
            return reader.Result();
        }

        var observation = new MusicBrainzArtistObservation
        {
            Origin = reader.Origin(
                reader.Input.Resource,
                "",
                ProviderObservationScope.ResourceItem
            ),
            Artist = artist,
            Aliases = ReadMusicBrainzAliases(reader, reader.Root),
        };
        return reader.Result() with { MusicBrainzArtists = [observation] };
    }

    private static MusicBrainzEntityReference? ReadMusicBrainzRoot(ProviderObservationReader reader)
    {
        var resource = reader.Input.Resource;
        if (
            !Guid.TryParseExact(resource.ProviderKey, "D", out var uuid)
            || uuid == Guid.Empty
            || uuid.ToString("D") != resource.ProviderKey
        )
        {
            throw new ArgumentException(
                "MusicBrainz lookup resources require a lowercase, nonempty MBID in D format."
            );
        }

        var root = reader.Root;
        if (root.ValueKind != JsonValueKind.Object)
        {
            reader.Issue("expected_object", "");
            return null;
        }
        if (root.TryGetProperty("error", out _))
        {
            reader.Issue("unsuccessful_provider_response", "/error");
            return null;
        }

        var entity = ReadMusicBrainzEntity(reader, root, "", resource.ResourceType);
        if (entity == null)
        {
            return null;
        }
        if (entity.Identity.Identity != resource)
        {
            reader.Issue("resource_identity_mismatch", "/id");
            return null;
        }
        return entity;
    }

    private static MusicBrainzEntityReference? ReadMusicBrainzEntity(
        ProviderObservationReader reader,
        JsonElement value,
        string pointer,
        string entityType
    )
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            reader.Issue("expected_object", pointer);
            return null;
        }
        var identity = ReadMusicBrainzIdentity(
            reader,
            value,
            "id",
            pointer,
            entityType,
            required: true
        );
        if (identity == null)
        {
            return null;
        }
        return new()
        {
            Evidence = reader.Evidence(pointer),
            Identity = identity,
            Name = reader.String(value, entityType == "artist" ? "name" : "title", pointer),
            SortName = reader.String(value, "sort-name", pointer),
            Type = reader.String(value, "type", pointer),
            TypeIdentity = ReadMusicBrainzIdentity(
                reader,
                value,
                "type-id",
                pointer,
                entityType + "_type"
            ),
            Disambiguation = reader.String(value, "disambiguation", pointer),
        };
    }

    private static ObservedProviderIdentity? ReadMusicBrainzIdentity(
        ProviderObservationReader reader,
        JsonElement parent,
        string property,
        string pointer,
        string entityType,
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
        var location = ProviderObservationReader.Pointer(pointer, property);
        if (!required && value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (
            value.ValueKind != JsonValueKind.String
            || !Guid.TryParseExact(value.GetString(), "D", out var uuid)
            || uuid == Guid.Empty
        )
        {
            reader.Issue("invalid_provider_identity", location);
            return null;
        }
        return new(
            new("musicbrainz.org", entityType, uuid.ToString("D")),
            reader.Evidence(location)
        );
    }

    // Nullable lists distinguish uncaptured/invalid fields from an explicitly returned empty list.
    // Each item keeps its object citation so fields unknown to this decoder remain recoverable.
    private static IReadOnlyList<MusicBrainzRelationshipObservation>? ReadMusicBrainzRelationships(
        ProviderObservationReader reader,
        JsonElement root
    )
    {
        var values = MusicBrainzArray(reader, root, "relations");
        if (values == null)
        {
            return null;
        }
        var result = new List<MusicBrainzRelationshipObservation>();
        var index = 0;
        foreach (var value in values.Value.EnumerateArray())
        {
            var pointer = "/relations/" + (index++).ToString(CultureInfo.InvariantCulture);
            if (value.ValueKind != JsonValueKind.Object)
            {
                reader.Issue("expected_object", pointer);
                continue;
            }
            var targetType = reader.String(value, "target-type", pointer);
            MusicBrainzEntityReference? target = null;
            if (targetType?.Value is "artist" or "work" or "recording")
            {
                if (value.TryGetProperty(targetType.Value, out var targetValue))
                {
                    target = ReadMusicBrainzEntity(
                        reader,
                        targetValue,
                        ProviderObservationReader.Pointer(pointer, targetType.Value),
                        targetType.Value
                    );
                }
                else
                {
                    reader.Issue("missing_relationship_target", pointer);
                }
            }
            result.Add(
                new()
                {
                    Evidence = reader.Evidence(pointer),
                    Type = reader.String(value, "type", pointer),
                    TypeIdentity = ReadMusicBrainzIdentity(
                        reader,
                        value,
                        "type-id",
                        pointer,
                        "relationship_type"
                    ),
                    Direction = reader.String(value, "direction", pointer),
                    TargetType = targetType,
                    Target = target,
                    SourceCredit = reader.String(value, "source-credit", pointer),
                    TargetCredit = reader.String(value, "target-credit", pointer),
                    Begin = reader.String(value, "begin", pointer),
                    End = reader.String(value, "end", pointer),
                    Ended = MusicBrainzBoolean(reader, value, "ended", pointer),
                    Attributes = MusicBrainzStrings(reader, value, "attributes", pointer),
                    AttributeIdentities = ReadMusicBrainzAttributeIdentities(
                        reader,
                        value,
                        pointer
                    ),
                    AttributeValues = ReadMusicBrainzAttributeValues(reader, value, pointer),
                }
            );
        }
        return result;
    }

    private static IReadOnlyList<MusicBrainzAliasObservation>? ReadMusicBrainzAliases(
        ProviderObservationReader reader,
        JsonElement root,
        string parentPointer = ""
    )
    {
        var values = MusicBrainzArray(reader, root, "aliases", parentPointer);
        if (values == null)
        {
            return null;
        }
        var result = new List<MusicBrainzAliasObservation>();
        var index = 0;
        foreach (var value in values.Value.EnumerateArray())
        {
            var pointer =
                parentPointer + "/aliases/" + (index++).ToString(CultureInfo.InvariantCulture);
            if (value.ValueKind != JsonValueKind.Object)
            {
                reader.Issue("expected_object", pointer);
                continue;
            }
            result.Add(
                new()
                {
                    Evidence = reader.Evidence(pointer),
                    Name = reader.String(value, "name", pointer),
                    SortName = reader.String(value, "sort-name", pointer),
                    Locale = reader.String(value, "locale", pointer),
                    Type = reader.String(value, "type", pointer),
                    Primary = MusicBrainzBoolean(reader, value, "primary", pointer),
                }
            );
        }
        return result;
    }

    private static IReadOnlyList<MusicBrainzIswcObservation>? ReadMusicBrainzIswcs(
        ProviderObservationReader reader,
        JsonElement root,
        string parentPointer = ""
    )
    {
        var values = MusicBrainzStrings(reader, root, "iswcs", parentPointer);
        if (values == null)
        {
            return null;
        }
        var result = new List<MusicBrainzIswcObservation>();
        foreach (var value in values)
        {
            var identity = MusicBrainzIswcKey(value.Value);
            if (identity == null)
            {
                reader.Issue("invalid_provider_identity", value.Evidence.JsonPointer);
                continue;
            }
            result.Add(new(value, new(new("iswc", "work", identity), value.Evidence)));
        }
        return result;
    }

    private static string? MusicBrainzIswcKey(string value)
    {
        // ISO 15707 treats separators as display formatting. Check the supported layouts
        // before removing separators so malformed punctuation cannot become a strong ID.
        if (
            !Regex.IsMatch(
                value,
                @"\A(?:T[0-9]{10}|T-[0-9]{9}-[0-9]|T-[0-9]{3}\.[0-9]{3}\.[0-9]{3}-[0-9])\z",
                RegexOptions.CultureInvariant
            )
        )
        {
            return null;
        }
        var compact = value.Replace("-", "").Replace(".", "");
        if (compact.AsSpan(1, 9).SequenceEqual("000000000"))
        {
            return null;
        }

        // Annex B: prefix contributes one; work digits have weights one through nine.
        // The check digit makes the sum divisible by ten. This checks syntax, not issuance.
        var sum = 1;
        for (var position = 1; position <= 9; position++)
        {
            sum += position * (compact[position] - '0');
        }
        return (sum + compact[10] - '0') % 10 == 0 ? compact : null;
    }

    private static IReadOnlyList<MusicBrainzWorkAttributeObservation>? ReadMusicBrainzWorkAttributes(
        ProviderObservationReader reader,
        JsonElement root
    )
    {
        var values = MusicBrainzArray(reader, root, "attributes");
        if (values == null)
        {
            return null;
        }
        var result = new List<MusicBrainzWorkAttributeObservation>();
        var index = 0;
        foreach (var value in values.Value.EnumerateArray())
        {
            var pointer = "/attributes/" + (index++).ToString(CultureInfo.InvariantCulture);
            if (value.ValueKind != JsonValueKind.Object)
            {
                reader.Issue("expected_object", pointer);
                continue;
            }
            result.Add(
                new()
                {
                    Evidence = reader.Evidence(pointer),
                    Type = reader.String(value, "type", pointer),
                    TypeIdentity = ReadMusicBrainzIdentity(
                        reader,
                        value,
                        "type-id",
                        pointer,
                        "work_attribute_type"
                    ),
                    Value = reader.String(value, "value", pointer),
                }
            );
        }
        return result;
    }

    private static IReadOnlyList<MusicBrainzArtistCreditObservation>? ReadMusicBrainzArtistCredits(
        ProviderObservationReader reader,
        JsonElement root,
        string parentPointer = ""
    )
    {
        var values = MusicBrainzArray(reader, root, "artist-credit", parentPointer);
        if (values == null)
        {
            return null;
        }
        var result = new List<MusicBrainzArtistCreditObservation>();
        var index = 0;
        foreach (var value in values.Value.EnumerateArray())
        {
            var pointer =
                parentPointer
                + "/artist-credit/"
                + (index++).ToString(CultureInfo.InvariantCulture);
            if (value.ValueKind != JsonValueKind.Object)
            {
                reader.Issue("expected_object", pointer);
                continue;
            }
            MusicBrainzEntityReference? artist = null;
            if (value.TryGetProperty("artist", out var target))
            {
                artist = ReadMusicBrainzEntity(reader, target, pointer + "/artist", "artist");
            }
            else
            {
                reader.Issue("missing_relationship_target", pointer);
            }
            result.Add(
                new()
                {
                    Evidence = reader.Evidence(pointer),
                    Name = reader.String(value, "name", pointer),
                    JoinPhrase = reader.String(value, "joinphrase", pointer),
                    Artist = artist,
                }
            );
        }
        return result;
    }

    private static IReadOnlyDictionary<
        string,
        ObservedProviderIdentity
    >? ReadMusicBrainzAttributeIdentities(
        ProviderObservationReader reader,
        JsonElement parent,
        string pointer
    )
    {
        var value = MusicBrainzObject(reader, parent, "attribute-ids", pointer);
        if (value == null)
        {
            return null;
        }
        var result = new Dictionary<string, ObservedProviderIdentity>();
        var location = pointer + "/attribute-ids";
        foreach (var property in value.Value.EnumerateObject())
        {
            var identity = ReadMusicBrainzIdentity(
                reader,
                value.Value,
                property.Name,
                location,
                "relationship_attribute",
                required: true
            );
            if (identity != null)
            {
                result[property.Name] = identity;
            }
        }
        return result;
    }

    private static IReadOnlyDictionary<
        string,
        ObservedValue<string>
    >? ReadMusicBrainzAttributeValues(
        ProviderObservationReader reader,
        JsonElement parent,
        string pointer
    )
    {
        var value = MusicBrainzObject(reader, parent, "attribute-values", pointer);
        if (value == null)
        {
            return null;
        }
        var result = new Dictionary<string, ObservedValue<string>>();
        foreach (var property in value.Value.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                reader.Issue(
                    "expected_text",
                    ProviderObservationReader.Pointer(pointer + "/attribute-values", property.Name)
                );
                continue;
            }
            result[property.Name] = reader.String(
                property.Value,
                ProviderObservationReader.Pointer(pointer + "/attribute-values", property.Name)
            )!;
        }
        return result;
    }

    private static JsonElement? MusicBrainzArray(
        ProviderObservationReader reader,
        JsonElement parent,
        string property,
        string pointer = ""
    ) => MusicBrainzCollection(reader, parent, property, pointer, JsonValueKind.Array);

    private static JsonElement? MusicBrainzObject(
        ProviderObservationReader reader,
        JsonElement parent,
        string property,
        string pointer
    ) => MusicBrainzCollection(reader, parent, property, pointer, JsonValueKind.Object);

    private static JsonElement? MusicBrainzCollection(
        ProviderObservationReader reader,
        JsonElement parent,
        string property,
        string pointer,
        JsonValueKind kind
    )
    {
        if (!parent.TryGetProperty(property, out var value))
        {
            return null;
        }
        if (value.ValueKind != kind)
        {
            reader.Issue(
                kind == JsonValueKind.Array ? "expected_array" : "expected_object",
                ProviderObservationReader.Pointer(pointer, property)
            );
            return null;
        }
        return value;
    }

    private static IReadOnlyList<ObservedValue<string>>? MusicBrainzStrings(
        ProviderObservationReader reader,
        JsonElement parent,
        string property,
        string pointer
    )
    {
        var values = MusicBrainzArray(reader, parent, property, pointer);
        if (values == null)
        {
            return null;
        }
        var result = new List<ObservedValue<string>>();
        var index = 0;
        foreach (var value in values.Value.EnumerateArray())
        {
            var location = ProviderObservationReader.Pointer(
                ProviderObservationReader.Pointer(pointer, property),
                (index++).ToString(CultureInfo.InvariantCulture)
            );
            if (value.ValueKind != JsonValueKind.String)
            {
                reader.Issue("expected_text", location);
                continue;
            }
            result.Add(reader.String(value, location)!);
        }
        return result;
    }

    private static ObservedValue<bool>? MusicBrainzBoolean(
        ProviderObservationReader reader,
        JsonElement parent,
        string property,
        string pointer
    )
    {
        if (
            !parent.TryGetProperty(property, out var value)
            || value.ValueKind == JsonValueKind.Null
        )
        {
            return null;
        }
        var location = ProviderObservationReader.Pointer(pointer, property);
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            reader.Issue("expected_boolean", location);
            return null;
        }
        return new(value.GetBoolean(), reader.Evidence(location));
    }
}
