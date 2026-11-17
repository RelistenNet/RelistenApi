using System.Text.Json;
using System.Text.Json.Serialization;
using Relisten.Accounts.Contracts.Identifiers;

namespace Relisten.Accounts.Contracts.Playlists;

/// <summary>Distinguishes an omitted metadata patch from an explicit null.</summary>
[JsonConverter(typeof(OptionalTextConverter))]
public readonly record struct OptionalText(bool IsPresent, string? Value);

public sealed class OptionalTextConverter : JsonConverter<OptionalText>
{
    public override bool HandleNull => true;

    public override OptionalText Read(
        ref Utf8JsonReader reader,
        Type type,
        JsonSerializerOptions options
    ) =>
        reader.TokenType switch
        {
            JsonTokenType.Null => new(true, null),
            JsonTokenType.String => new(true, reader.GetString()),
            _ => throw new JsonException("Expected text or null."),
        };

    public override void Write(
        Utf8JsonWriter writer,
        OptionalText value,
        JsonSerializerOptions options
    ) => writer.WriteStringValue(value.Value);
}

public sealed record PlaylistMetadata(string Name, string? Description = null);

public sealed record PlaylistEntryInput(PlaylistEntryId EntryId, string TrackId);

public sealed record PlaylistSegmentInput(
    PlaylistSegmentId SegmentId,
    IReadOnlyList<PlaylistEntryInput> Entries
);

public sealed record CreatePlaylistRequest(
    int ContractVersion,
    CommandId CommandId,
    PlaylistId PlaylistId,
    PlaylistMetadata Metadata,
    IReadOnlyList<PlaylistSegmentInput> Segments
);

public sealed record ArchivePlaylistRequest(
    int ContractVersion,
    CommandId CommandId,
    bool Archived
);

public sealed record PlaylistBatchRequest(
    int ContractVersion,
    IReadOnlyList<PlaylistOperation> Operations
);

// The discriminator and typed payload are read together; arbitrary JSON never reaches the domain.
[JsonConverter(typeof(PlaylistOperationConverter))]
public sealed record PlaylistOperation(
    PlaylistOperationId OperationId,
    long BaseRevision,
    string OperationType,
    PlaylistOperationPayload Payload
);

public abstract record PlaylistOperationPayload;

public sealed record UpdatePlaylistMetadata(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        OptionalText Name = default,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        OptionalText Description = default
) : PlaylistOperationPayload;

public sealed record InsertPlaylistSegment(
    PlaylistSegmentId SegmentId,
    IReadOnlyList<PlaylistEntryInput> Entries,
    PlaylistSegmentId? AfterSegmentId = null,
    PlaylistSegmentId? BeforeSegmentId = null
) : PlaylistOperationPayload;

public sealed record MovePlaylistSegment(
    PlaylistSegmentId SegmentId,
    PlaylistSegmentId? AfterSegmentId = null,
    PlaylistSegmentId? BeforeSegmentId = null
) : PlaylistOperationPayload;

public sealed record DeletePlaylistSegment(PlaylistSegmentId SegmentId) : PlaylistOperationPayload;

public sealed record InsertPlaylistEntries(
    PlaylistSegmentId SegmentId,
    IReadOnlyList<PlaylistEntryInput> Entries,
    PlaylistEntryId? AfterEntryId = null,
    PlaylistEntryId? BeforeEntryId = null
) : PlaylistOperationPayload;

public sealed record MovePlaylistEntry(
    PlaylistEntryId EntryId,
    PlaylistEntryDestination Destination
) : PlaylistOperationPayload;

public sealed record DeletePlaylistEntry(PlaylistEntryId EntryId) : PlaylistOperationPayload;

public sealed record PlaylistEntryDestination(
    PlaylistSegmentId? SegmentId = null,
    PlaylistSegmentId? NewSegmentId = null,
    PlaylistEntryId? AfterEntryId = null,
    PlaylistEntryId? BeforeEntryId = null,
    PlaylistSegmentId? AfterSegmentId = null,
    PlaylistSegmentId? BeforeSegmentId = null
);

public sealed class PlaylistOperationConverter : JsonConverter<PlaylistOperation>
{
    private sealed record Envelope(
        PlaylistOperationId OperationId,
        long BaseRevision,
        string OperationType,
        JsonElement Payload
    );

    public override PlaylistOperation Read(
        ref Utf8JsonReader reader,
        Type type,
        JsonSerializerOptions options
    )
    {
        var envelope =
            JsonSerializer.Deserialize<Envelope>(ref reader, options)
            ?? throw new JsonException("Expected an operation.");
        var payloadType = envelope.OperationType switch
        {
            "update_metadata" => typeof(UpdatePlaylistMetadata),
            "insert_segment" => typeof(InsertPlaylistSegment),
            "move_segment" => typeof(MovePlaylistSegment),
            "delete_segment" => typeof(DeletePlaylistSegment),
            "insert_entries" => typeof(InsertPlaylistEntries),
            "move_entry" => typeof(MovePlaylistEntry),
            "delete_entry" => typeof(DeletePlaylistEntry),
            _ => throw new JsonException("Unknown operation_type."),
        };
        var payload =
            (PlaylistOperationPayload?)envelope.Payload.Deserialize(payloadType, options)
            ?? throw new JsonException("Expected an operation payload.");
        return new(envelope.OperationId, envelope.BaseRevision, envelope.OperationType, payload);
    }

    public override void Write(
        Utf8JsonWriter writer,
        PlaylistOperation value,
        JsonSerializerOptions options
    )
    {
        writer.WriteStartObject();
        writer.WritePropertyName("operation_id");
        JsonSerializer.Serialize(writer, value.OperationId, options);
        writer.WriteNumber("base_revision", value.BaseRevision);
        writer.WriteString("operation_type", value.OperationType);
        writer.WritePropertyName("payload");
        JsonSerializer.Serialize(writer, value.Payload, value.Payload.GetType(), options);
        writer.WriteEndObject();
    }
}
