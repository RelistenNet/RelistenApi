using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;

namespace Relisten.Normalization;

/// <summary>Preserves normalization's text tokens across SQL, JSON and model schemas.</summary>
/// <remarks>
/// SQL writers format parameters explicitly. Dapper reads the single-word scalar
/// vocabularies by enum member name; qualifier text arrays require explicit parsing.
/// </remarks>
public static class NormalizationEnums
{
    /// <summary>Whether a type belongs to this persisted vocabulary.</summary>
    public static bool IsSupported(Type type) =>
        type == typeof(SongKind)
        || type == typeof(VenueKind)
        || type == typeof(VenueNameKind)
        || type == typeof(VenueCoordinatePrecision)
        || type == typeof(TrackQualifier);

    public static string Format<T>(T value)
        where T : struct, Enum =>
        Vocabulary<T>.ByValue.TryGetValue(value, out var token)
            ? token
            : throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "Undefined normalization value"
            );

    public static T Parse<T>(string token)
        where T : struct, Enum =>
        TryParse<T>(token, out var value)
            ? value
            : throw new FormatException($"'{token}' is not a {typeof(T).Name}");

    public static bool TryParse<T>(string? token, out T value)
        where T : struct, Enum
    {
        if (token is not null)
        {
            return Vocabulary<T>.ByToken.TryGetValue(token, out value);
        }

        value = default;
        return false;
    }

    /// <summary>The exact allowed tokens, in declaration order, for structured model output.</summary>
    public static IReadOnlyList<string> Values<T>()
        where T : struct, Enum => Vocabulary<T>.Tokens;

    private static class Vocabulary<T>
        where T : struct, Enum
    {
        internal static readonly Dictionary<T, string> ByValue = Enum.GetValues<T>()
            .ToDictionary(
                value => value,
                value =>
                    typeof(T)
                        .GetField(value.ToString())!
                        .GetCustomAttribute<EnumMemberAttribute>()
                        ?.Value
                    ?? throw new InvalidOperationException(
                        $"{typeof(T).Name}.{value} has no wire token"
                    )
            );
        internal static readonly Dictionary<string, T> ByToken = ByValue.ToDictionary(
            pair => pair.Value,
            pair => pair.Key,
            StringComparer.Ordinal
        );
        internal static readonly IReadOnlyList<string> Tokens = Array.AsReadOnly(
            ByValue.Values.ToArray()
        );
    }

    // The record properties retain their historical strings under Temporal's default
    // System.Text.Json payload converter. Numeric enum payloads are never admitted.
    internal sealed class JsonConverter<T> : System.Text.Json.Serialization.JsonConverter<T>
        where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (
                reader.TokenType == JsonTokenType.String
                && TryParse<T>(reader.GetString(), out var value)
            )
            {
                return value;
            }

            throw new JsonException($"Expected a {typeof(T).Name} normalization token");
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WriteStringValue(Format(value));
    }
}
