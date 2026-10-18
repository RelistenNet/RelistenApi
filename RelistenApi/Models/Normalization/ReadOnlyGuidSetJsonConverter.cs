using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Relisten.Api.Models.Normalization;

/// <summary>
/// Retains the existing GUID-array representation while materializing an immutable-contract
/// set in durable facts snapshots. Default System.Text.Json cannot instantiate IReadOnlySet.
/// </summary>
public sealed class ReadOnlyGuidSetJsonConverter : JsonConverter<IReadOnlySet<Guid>>
{
    public override IReadOnlySet<Guid> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    ) =>
        JsonSerializer.Deserialize<HashSet<Guid>>(ref reader, options)
        ?? throw new JsonException("Author identifiers must be a GUID array.");

    public override void Write(
        Utf8JsonWriter writer,
        IReadOnlySet<Guid> value,
        JsonSerializerOptions options
    )
    {
        // Preserve enumeration order: introducing sorting would change existing snapshot
        // hashes even though the public representation and membership were unchanged.
        writer.WriteStartArray();
        foreach (var uuid in value)
        {
            writer.WriteStringValue(uuid);
        }
        writer.WriteEndArray();
    }
}
