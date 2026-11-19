using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Relisten.Accounts.Contracts.Playlists;

namespace RelistenUserService.Playlists;

/// <summary>
/// Durable v1 semantic serialization. Keep this order explicit: reordering a C# record must
/// not change the hash of an already-committed command. Call only after normalization.
/// </summary>
public static class PlaylistHasher
{
    public static byte[] Hash(CreatePlaylistRequest request)
    {
        var lines = Header("create", request.ContractVersion, request.PlaylistId, "create");
        Value(lines, request.Metadata.Name);
        Value(lines, request.Metadata.Description);
        lines.Add($"L:{request.Segments.Count}");
        foreach (var segment in request.Segments)
        {
            lines.Add($"S:{segment.SegmentId}|{segment.Entries.Count}");
            Description(lines, segment.Description);
            EntryLines(lines, segment.Entries);
        }
        return Digest(lines);
    }

    public static byte[] Hash(PlaylistId playlistId, ArchivePlaylistRequest request)
    {
        var lines = Header("archive", request.ContractVersion, playlistId, "archive");
        Value(lines, request.Archived);
        return Digest(lines);
    }

    public static byte[] Hash(PlaylistId playlistId, int version, PlaylistOperation operation)
    {
        var lines = Header("operation", version, playlistId, operation.OperationType);
        switch (operation.Payload)
        {
            case UpdatePlaylistMetadata metadata:
                Optional(lines, metadata.Name);
                Optional(lines, metadata.Description);
                break;
            case InsertPlaylistSegment insert:
                Value(lines, insert.SegmentId.Value);
                Entries(lines, insert.Entries);
                Value(lines, insert.AfterSegmentId?.Value);
                Value(lines, insert.BeforeSegmentId?.Value);
                if (insert.Description is not null)
                {
                    // A plain value here could be mistaken for an anchor after a described entry.
                    lines.Add(
                        "segment_description:" + JsonSerializer.Serialize(insert.Description)
                    );
                }
                break;
            case UpdatePlaylistSegment update:
                Value(lines, update.SegmentId.Value);
                Optional(lines, update.Description);
                break;
            case MovePlaylistSegment move:
                Value(lines, move.SegmentId.Value);
                Value(lines, move.AfterSegmentId?.Value);
                Value(lines, move.BeforeSegmentId?.Value);
                break;
            case DeletePlaylistSegment delete:
                Value(lines, delete.SegmentId.Value);
                break;
            case InsertPlaylistEntries insert:
                Value(lines, insert.SegmentId.Value);
                Entries(lines, insert.Entries);
                Value(lines, insert.AfterEntryId?.Value);
                Value(lines, insert.BeforeEntryId?.Value);
                break;
            case MovePlaylistEntry move:
                Value(lines, move.EntryId.Value);
                var destination = move.Destination;
                lines.Add(
                    destination.NewSegmentId is { } newId
                        ? $"D:new_segment|{newId}"
                        : $"D:segment|{destination.SegmentId}"
                );
                if (destination.NewSegmentId is not null)
                {
                    Value(lines, destination.AfterSegmentId?.Value);
                    Value(lines, destination.BeforeSegmentId?.Value);
                }
                else
                {
                    Value(lines, destination.AfterEntryId?.Value);
                    Value(lines, destination.BeforeEntryId?.Value);
                }
                break;
            case DeletePlaylistEntry delete:
                Value(lines, delete.EntryId.Value);
                break;
            case UpdatePlaylistEntry update:
                Value(lines, update.EntryId.Value);
                Optional(lines, update.Description);
                break;
            default:
                throw new InvalidOperationException("Unknown playlist operation.");
        }
        return Digest(lines);
    }

    private static List<string> Header(string domain, int version, PlaylistId id, string type) =>
        [
            $"relisten-playlist-{domain}-v1",
            version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            id.Value,
            type,
        ];

    private static void Optional(List<string> lines, OptionalText value)
    {
        if (value.IsPresent)
        {
            Value(lines, value.Value);
        }
        else
        {
            lines.Add("A");
        }
    }

    private static void Value<T>(List<string> lines, T value) =>
        lines.Add(value is null ? "N" : "V:" + JsonSerializer.Serialize(value));

    private static void Entries(List<string> lines, IReadOnlyList<PlaylistEntryInput> entries)
    {
        lines.Add($"L:{entries.Count}");
        EntryLines(lines, entries);
    }

    private static void EntryLines(List<string> lines, IReadOnlyList<PlaylistEntryInput> entries)
    {
        foreach (var entry in entries)
        {
            lines.Add($"{entry.EntryId}|{entry.TrackId ?? "N"}");
            Description(lines, entry.Description);
        }
    }

    private static void Description(List<string> lines, string? description)
    {
        // Omit the whole line so pre-rollout create and insert receipts keep their hash.
        if (description is not null)
        {
            Value(lines, description);
        }
    }

    private static byte[] Digest(List<string> lines) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines)));
}
