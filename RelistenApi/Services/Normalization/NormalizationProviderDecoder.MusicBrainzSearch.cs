using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

public sealed partial class NormalizationProviderDecoder
{
    private static ProviderDecodeResult DecodeMusicBrainzSearch(ProviderObservationReader reader)
    {
        if (!Regex.IsMatch(reader.Input.Resource.ProviderKey, "\\A[0-9a-f]{64}\\z"))
        {
            throw new ArgumentException(
                "MusicBrainz search resources require a lowercase SHA-256 descriptor key."
            );
        }

        var root = reader.Root;
        if (root.ValueKind != JsonValueKind.Object)
        {
            reader.Issue("expected_object", "");
            return reader.Result();
        }
        if (root.TryGetProperty("error", out _))
        {
            reader.Issue("unsuccessful_provider_response", "/error");
            return reader.Result();
        }

        var entityType = reader.Input.Resource.ResourceType[..^"-search".Length];
        var created = reader.String(root, "created", "");
        var count = ReadMusicBrainzSearchInteger(reader, root, "count");
        var offset = ReadMusicBrainzSearchInteger(reader, root, "offset");
        var hits = ReadMusicBrainzSearchHits(reader, root, entityType);
        var result = reader.Result();

        return result with
        {
            MusicBrainzSearchPages =
            [
                new()
                {
                    Origin = reader.Origin(
                        reader.Input.Resource,
                        "",
                        ProviderObservationScope.ResourceItem
                    ),
                    Created = created,
                    Count = count,
                    Offset = offset,
                    Hits = hits,
                    Issues = result.Issues,
                },
            ],
        };
    }

    private static ObservedValue<long>? ReadMusicBrainzSearchInteger(
        ProviderObservationReader reader,
        JsonElement root,
        string property
    )
    {
        if (!root.TryGetProperty(property, out var value))
        {
            // A missing property's own pointer cannot resolve in the retained document.
            reader.Issue("missing_search_" + property, "");
            return null;
        }
        var pointer = "/" + property;
        if (
            value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var number)
            || number < 0
        )
        {
            reader.Issue("expected_nonnegative_integer", pointer);
            return null;
        }
        return new(number, reader.Evidence(pointer));
    }

    private static IReadOnlyList<MusicBrainzSearchHitObservation>? ReadMusicBrainzSearchHits(
        ProviderObservationReader reader,
        JsonElement root,
        string entityType
    )
    {
        var property = entityType + "s";
        if (!root.TryGetProperty(property, out _))
        {
            reader.Issue("missing_search_results", "");
            return null;
        }
        var values = MusicBrainzArray(reader, root, property);
        if (values == null)
        {
            return null;
        }

        var result = new List<MusicBrainzSearchHitObservation>();
        var index = 0;
        foreach (var value in values.Value.EnumerateArray())
        {
            var sourceIndex = index++;
            var pointer = "/" + property + "/" + sourceIndex.ToString(CultureInfo.InvariantCulture);
            var entity = ReadMusicBrainzEntity(reader, value, pointer, entityType);
            if (entity == null)
            {
                continue;
            }

            // Keep every original hit, including duplicate MBIDs. Hydration deduplication
            // is a consumer decision and must not discard the originating page positions.
            result.Add(
                new()
                {
                    Evidence = reader.Evidence(pointer),
                    SourceIndex = sourceIndex,
                    Entity = entity,
                    Score = ReadMusicBrainzSearchScore(reader, value, pointer),
                    ArtistCredits =
                        entityType == "recording"
                            ? ReadMusicBrainzArtistCredits(reader, value, pointer)
                            : null,
                    Aliases = ReadMusicBrainzAliases(reader, value, pointer),
                    Iswcs =
                        entityType == "work" ? ReadMusicBrainzIswcs(reader, value, pointer) : null,
                    WorkArtists =
                        entityType == "work"
                            ? ReadMusicBrainzSearchWorkArtists(reader, value, pointer)
                            : null,
                }
            );
        }
        return result;
    }

    private static ObservedValue<int>? ReadMusicBrainzSearchScore(
        ProviderObservationReader reader,
        JsonElement hit,
        string parentPointer
    )
    {
        if (!hit.TryGetProperty("score", out var value))
        {
            return null;
        }

        var pointer = parentPointer + "/score";
        int score;
        if (
            (
                value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out score)
                || value.ValueKind == JsonValueKind.String
                    && int.TryParse(
                        value.GetString(),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out score
                    )
            )
            && score >= 0
        )
        {
            // Search JSON has used both integer and decimal-string scores. Keep the
            // original scalar citation even when reading its unsigned integer value.
            return new(score, reader.Evidence(pointer));
        }

        reader.Issue("expected_nonnegative_integer", pointer);
        return null;
    }

    private static IReadOnlyList<MusicBrainzSearchWorkArtistObservation>? ReadMusicBrainzSearchWorkArtists(
        ProviderObservationReader reader,
        JsonElement work,
        string parentPointer
    )
    {
        var relations = MusicBrainzArray(reader, work, "relations", parentPointer);
        if (relations == null)
        {
            return null;
        }

        var result = new List<MusicBrainzSearchWorkArtistObservation>();
        var index = 0;
        foreach (var relation in relations.Value.EnumerateArray())
        {
            var pointer =
                parentPointer + "/relations/" + (index++).ToString(CultureInfo.InvariantCulture);
            if (relation.ValueKind != JsonValueKind.Object)
            {
                reader.Issue("expected_object", pointer);
                continue;
            }
            var targetType = reader.String(relation, "target-type", pointer);
            MusicBrainzEntityReference? artist = null;
            if (relation.TryGetProperty("artist", out var value))
            {
                if (
                    relation.TryGetProperty("target-type", out var declaredType)
                    && (
                        declaredType.ValueKind != JsonValueKind.String
                        || declaredType.GetString() != "artist"
                    )
                )
                {
                    reader.Issue("relationship_target_type_mismatch", pointer + "/target-type");
                }
                else
                {
                    artist = ReadMusicBrainzEntity(reader, value, pointer + "/artist", "artist");
                }
            }
            else if (targetType == null && relation.TryGetProperty("recording", out var recording))
            {
                // Search responses can omit target-type and embed a recording instead.
                // Validate that literal target without qualifying it as an author.
                ReadMusicBrainzEntity(reader, recording, pointer + "/recording", "recording");
            }
            else if (targetType == null || targetType.Value == "artist")
            {
                reader.Issue("missing_relationship_target", pointer);
            }

            result.Add(
                new()
                {
                    Evidence = reader.Evidence(pointer),
                    Type = reader.String(relation, "type", pointer),
                    Direction = reader.String(relation, "direction", pointer),
                    TargetType = targetType,
                    Artist = artist,
                }
            );
        }
        return result;
    }
}
