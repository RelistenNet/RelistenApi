using System.Diagnostics.CodeAnalysis;
using System.Text;
using Relisten.Accounts.Contracts.Playlists;
using Relisten.Identifiers;

namespace RelistenUserService.Playlists;

public sealed class PlaylistException(
    int status,
    string code,
    string detail,
    string[]? conflictingIds = null
) : Exception(detail)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public string[]? ConflictingIds { get; } = conflictingIds;
}

public static class PlaylistValidation
{
    public const int MaximumPlaylists = 250;
    public const int MaximumEntries = 5_000;
    public const int MaximumOwnedEntries = 25_000;
    public const int MaximumOperations = 500;

    public static CreatePlaylistRequest Normalize(CreatePlaylistRequest request)
    {
        Version(request.ContractVersion);
        Require(
            !request.CommandId.IsEmpty && !request.PlaylistId.IsEmpty,
            "Command and playlist IDs are required."
        );
        Require(
            request.Metadata is not null && request.Segments is not null,
            "Metadata and segments are required."
        );
        var ids = new HashSet<string>(StringComparer.Ordinal) { request.PlaylistId.Value };
        foreach (var segment in request.Segments)
        {
            Require(segment is not null && !segment.SegmentId.IsEmpty, "A segment ID is required.");
            Unique(ids, segment.SegmentId.Value);
            Entries(segment.Entries, ids);
        }
        return request with
        {
            Metadata = new(Name(request.Metadata.Name), Description(request.Metadata.Description)),
        };
    }

    public static PlaylistBatchRequest Normalize(PlaylistBatchRequest request)
    {
        Version(request.ContractVersion);
        Require(request.Operations is { Count: > 0 }, "operations must contain at least one item.");
        if (request.Operations.Count > MaximumOperations)
        {
            throw new PlaylistException(
                422,
                "limit_exceeded",
                "At most 500 operations may be submitted."
            );
        }

        var operations = new List<PlaylistOperation>();
        var operationIds = new HashSet<string>(StringComparer.Ordinal);
        var createdIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operation in request.Operations)
        {
            Require(
                operation is not null
                    && !operation.OperationId.IsEmpty
                    && operation.BaseRevision >= 0,
                "Invalid operation envelope."
            );
            Unique(operationIds, operation.OperationId.Value);
            var normalized = operation.Payload;
            switch (operation.Payload)
            {
                case UpdatePlaylistMetadata metadata:
                    normalized = metadata with
                    {
                        Name = metadata.Name.IsPresent
                            ? new(true, Name(metadata.Name.Value))
                            : default,
                        Description = metadata.Description.IsPresent
                            ? new(true, Description(metadata.Description.Value))
                            : default,
                    };
                    break;
                case InsertPlaylistSegment insert:
                    Require(!insert.SegmentId.IsEmpty, "segment_id is required.");
                    Unique(createdIds, insert.SegmentId.Value);
                    Require(
                        insert.AfterSegmentId != insert.SegmentId
                            && insert.BeforeSegmentId != insert.SegmentId,
                        "A segment cannot anchor itself."
                    );
                    Entries(insert.Entries, createdIds);
                    break;
                case InsertPlaylistEntries insert:
                    Require(!insert.SegmentId.IsEmpty, "segment_id is required.");
                    Entries(insert.Entries, createdIds);
                    break;
                case MovePlaylistSegment move:
                    Require(
                        !move.SegmentId.IsEmpty
                            && move.AfterSegmentId != move.SegmentId
                            && move.BeforeSegmentId != move.SegmentId,
                        "Invalid segment move or self anchor."
                    );
                    break;
                case DeletePlaylistSegment delete:
                    Require(!delete.SegmentId.IsEmpty, "segment_id is required.");
                    break;
                case MovePlaylistEntry move:
                    Require(
                        !move.EntryId.IsEmpty && move.Destination is not null,
                        "Entry and destination are required."
                    );
                    var destination = move.Destination;
                    Require(
                        destination.SegmentId.HasValue != destination.NewSegmentId.HasValue,
                        "Exactly one destination is required."
                    );
                    if (destination.NewSegmentId is { } newId)
                    {
                        Require(
                            !newId.IsEmpty
                                && destination.AfterEntryId is null
                                && destination.BeforeEntryId is null,
                            "New segments take segment anchors only."
                        );
                        Require(
                            destination.AfterSegmentId != newId
                                && destination.BeforeSegmentId != newId,
                            "A segment cannot anchor itself."
                        );
                        Unique(createdIds, newId.Value);
                    }
                    else
                    {
                        Require(
                            destination.SegmentId is { IsEmpty: false }
                                && destination.AfterSegmentId is null
                                && destination.BeforeSegmentId is null,
                            "Existing segments take entry anchors only."
                        );
                        Require(
                            destination.AfterEntryId != move.EntryId
                                && destination.BeforeEntryId != move.EntryId,
                            "An entry cannot anchor itself."
                        );
                    }
                    break;
                case DeletePlaylistEntry delete:
                    Require(!delete.EntryId.IsEmpty, "entry_id is required.");
                    break;
                default:
                    throw Invalid("Unknown operation payload.");
            }
            operations.Add(operation with { Payload = normalized });
        }
        return request with { Operations = operations };
    }

    public static void Validate(ArchivePlaylistRequest request)
    {
        Version(request.ContractVersion);
        Require(!request.CommandId.IsEmpty, "command_id is required.");
    }

    public static IEnumerable<string> CreatedIds(PlaylistOperation operation) =>
        operation.Payload switch
        {
            InsertPlaylistSegment insert => new[] { insert.SegmentId.Value }.Concat(
                insert.Entries.Select(entry => entry.EntryId.Value)
            ),
            InsertPlaylistEntries insert => insert.Entries.Select(entry => entry.EntryId.Value),
            MovePlaylistEntry { Destination.NewSegmentId: { } id } => [id.Value],
            _ => [],
        };

    private static void Entries(IReadOnlyList<PlaylistEntryInput>? entries, HashSet<string> ids)
    {
        Require(
            entries is { Count: > 0 and <= MaximumEntries },
            "entries must contain 1–5,000 items."
        );
        foreach (var entry in entries!)
        {
            Require(
                entry is not null
                    && !entry.EntryId.IsEmpty
                    && TrackId.TryParse(entry.TrackId, out _),
                "Entries require canonical playlist_entry_ and track_ IDs."
            );
            Unique(ids, entry!.EntryId.Value);
        }
    }

    private static string Name(string? text)
    {
        Require(text is not null, "name cannot be null.");
        var value = NormalizeText(text!);
        Require(
            value.EnumerateRunes().Count() is >= 1 and <= 100,
            "name must contain 1–100 characters."
        );
        return value;
    }

    private static string? Description(string? text)
    {
        var value = text is null ? null : NormalizeText(text);
        Require(
            value is null || value.EnumerateRunes().Count() <= 1_000,
            "description may contain at most 1,000 characters."
        );
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string NormalizeText(string text)
    {
        Require(!text.Contains('\0'), "Text cannot contain NUL characters.");
        try
        {
            return text.Trim().Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            throw Invalid("Text must contain valid Unicode.");
        }
    }

    private static void Version(int version)
    {
        if (version != 1)
        {
            throw new PlaylistException(
                422,
                "invalid_contract_version",
                "contract_version must be 1."
            );
        }
    }

    private static void Unique(HashSet<string> ids, string id) =>
        Require(ids.Add(id), "A submitted ID occurs more than once.");

    private static void Require([DoesNotReturnIf(false)] bool condition, string detail)
    {
        if (!condition)
        {
            throw Invalid(detail);
        }
    }

    private static PlaylistException Invalid(string detail) =>
        new(422, "invalid_playlist_operation", detail);
}
