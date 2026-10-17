using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Relisten.Ingestion.Providers.TapersSection;

/// <summary>
/// Immutable selected provider evidence. PayloadJson retains JSON types and field presence;
/// ContentHash excludes retrieval time so repeated observations reuse the same database row.
/// </summary>
public sealed record ArchiveObservationProjection(
    ArchiveItemId Item,
    int ProjectionVersion,
    string ContentHash,
    string PayloadJson,
    bool IsAvailabilityOnly
);

/// <summary>
/// Captures evidence before Archive's tolerant legacy DTO collapses scalar/array differences.
/// The projection deliberately excludes account information, reviews and unrelated root fields.
/// </summary>
public static class ArchiveObservationProjector
{
    public const int ProjectionVersion = 1;
    public const int MaximumResponseBytes = 64 * 1024 * 1024;

    private static readonly string[] MetadataFields =
    [
        "identifier",
        "mediatype",
        "creator",
        "title",
        "date",
        "year",
        "collection",
        "description",
        "notes",
        "venue",
        "coverage",
        "source",
        "lineage",
        "taper",
        "transferer",
        "subject",
        "access-restricted-item",
    ];

    private static readonly string[] FileFields =
    [
        "name",
        "format",
        "source",
        "original",
        "md5",
        "sha1",
        "crc32",
        "size",
        "length",
        "track",
        "title",
        "private",
    ];

    private static readonly string[] AvailabilityFields =
    [
        "is_dark",
        "is_restricted",
        "restricted",
        "error",
        "errcode",
    ];

    public static ArchiveObservationProjection Project(ArchiveItemId item, string rawJson)
    {
        if (Encoding.UTF8.GetByteCount(rawJson) > MaximumResponseBytes)
        {
            throw new InvalidDataException("Archive metadata exceeded the 64 MiB response limit.");
        }

        var root = ParseObject(rawJson);

        var payload = SelectFields(root, AvailabilityFields);
        var unavailable =
            IsTrue(root["is_dark"])
            || IsTrue(root["is_restricted"])
            || IsTrue(root["restricted"])
            || root["errcode"]?.Type is JTokenType.Integer
            || !string.IsNullOrEmpty((string?)root["error"]);
        var metadata = root["metadata"] as JObject;
        if (!unavailable && (string?)metadata?["identifier"] != item.Value)
        {
            throw new InvalidDataException("Archive metadata did not identify the requested item.");
        }

        if (metadata != null)
        {
            payload["metadata"] = SelectFields(metadata, MetadataFields);
            unavailable |= IsTrue(metadata["access-restricted-item"]);
        }

        if (root.TryGetValue("files", out var files))
        {
            if (files is not JArray fileArray || fileArray.Any(file => file is not JObject))
            {
                throw new InvalidDataException("Archive metadata contained an invalid file list.");
            }

            payload["files"] = new JArray(
                fileArray.Cast<JObject>().Select(file => SelectFields(file, FileFields))
            );
        }

        var json = Canonicalize(payload).ToString(Formatting.None);
        return new ArchiveObservationProjection(
            item,
            ProjectionVersion,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))),
            json,
            unavailable
        );
    }

    // Newtonsoft otherwise converts ISO strings to Date tokens, erasing the supplied JSON type.
    internal static JObject ParseObject(string json)
    {
        using var reader = new JsonTextReader(new StringReader(json))
        {
            DateParseHandling = DateParseHandling.None,
        };
        var root = JObject.Load(
            reader,
            new JsonLoadSettings
            {
                DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
            }
        );
        if (reader.Read())
        {
            throw new InvalidDataException("Archive metadata contained trailing JSON content.");
        }

        return root;
    }

    internal static bool IsTrue(JToken? value) =>
        value?.Type == JTokenType.Boolean && value.Value<bool>()
        || value?.Type is JTokenType.String or JTokenType.Integer
            && (
                value.ToString() == "1"
                || value.ToString().Equals("true", StringComparison.OrdinalIgnoreCase)
            );

    private static JObject SelectFields(JObject source, string[] fields)
    {
        var result = new JObject();
        foreach (var field in fields)
        {
            if (source.TryGetValue(field, out var value))
            {
                result[field] = value.DeepClone();
            }
        }

        return result;
    }

    private static JToken Canonicalize(JToken value) =>
        value switch
        {
            JObject obj => new JObject(
                obj.Properties()
                    .OrderBy(property => property.Name, StringComparer.Ordinal)
                    .Select(property => new JProperty(property.Name, Canonicalize(property.Value)))
            ),
            JArray array => new JArray(array.Select(Canonicalize)),
            _ => value.DeepClone(),
        };
}
