using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Relisten.Accounts.Contracts.Playlists;
using RelistenUserService.Authentication;
using RelistenUserService.Library;
using RelistenUserService.Persistence;
using RelistenUserService.Playlists;

namespace RelistenUserService.Mcp;

internal sealed class AccountMcpTools(
    McpToolAuthorization authorization,
    LibraryReadService library,
    PlaylistReadService playlists,
    PlaylistWriteService writes,
    AccountsDbContext db
)
{
    private const string UserRead = """[{"type":"oauth2","scopes":["user.read"]}]""";
    private const string LibraryRead = """[{"type":"oauth2","scopes":["library.read"]}]""";
    private const string LibraryWrite = """[{"type":"oauth2","scopes":["library.write"]}]""";

    [McpServerTool(
        Name = "whoami",
        ReadOnly = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(Profile)
    )]
    [McpMeta("securitySchemes", JsonValue = UserRead)]
    [McpMeta("openai/profile", true)]
    [Description("Return the stable ID and display name of the connected Relisten account.")]
    public async Task<CallToolResult> WhoAmI(CancellationToken cancellationToken)
    {
        var (session, error) = await authorization.AuthorizeAsync(
            RelistenScopes.UserRead,
            cancellationToken
        );
        return error ?? McpResults.Data(new Profile(session!.UserId.Value, session.User.Username));
    }

    public sealed record Profile(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name
    );

    [McpServerTool(Name = "my_library", ReadOnly = true, OpenWorld = false)]
    [McpMeta("securitySchemes", JsonValue = LibraryRead)]
    [Description(
        "List favorite catalog targets and owned playlist summaries. The cursor pages favorites first, then playlists."
    )]
    public Task<CallToolResult> MyLibrary(
        int cursor = 0,
        int limit = 25,
        CancellationToken cancellationToken = default
    )
    {
        if (cursor < 0 || limit is < 1 or > 50)
        {
            return Task.FromResult(
                McpResults.Error("Use a nonnegative cursor and a limit between 1 and 50.")
            );
        }
        return WithAccount(
            RelistenScopes.LibraryRead,
            cancellationToken,
            async id =>
            {
                var snapshot = await library.GetSnapshotAsync(id, cancellationToken);
                var favorites = snapshot.Favorites.Count;
                var playlistPage = snapshot
                    .Playlists.Skip(Math.Max(0, cursor - favorites))
                    .Take(limit - Math.Clamp(favorites - cursor, 0, limit));
                return new
                {
                    favorites = snapshot.Favorites.Skip(cursor).Take(limit),
                    playlists = playlistPage.Select(playlist => new
                    {
                        playlist.PlaylistId,
                        playlist.Name,
                        playlist.TrackCount,
                        playlist.Archived,
                        access = "owner",
                    }),
                    next_cursor = (long)cursor + limit < (long)favorites + snapshot.Playlists.Count
                        ? (int?)(cursor + limit)
                        : null,
                };
            }
        );
    }

    [McpServerTool(Name = "read_playlist", ReadOnly = true, OpenWorld = false)]
    [McpMeta("securitySchemes", JsonValue = LibraryRead)]
    [Description(
        "Read the ordered segments, entries and notes of an owned playlist. Page long playlists by segment."
    )]
    public Task<CallToolResult> ReadPlaylist(
        string id,
        int cursor = 0,
        int limit = 10,
        CancellationToken cancellationToken = default
    )
    {
        if (cursor < 0 || limit is < 1 or > 25)
        {
            return Task.FromResult(
                McpResults.Error("Use a nonnegative cursor and a limit between 1 and 25.")
            );
        }
        return WithAccount(
            RelistenScopes.LibraryRead,
            cancellationToken,
            async actor =>
            {
                var playlist = await playlists.SnapshotAsync(
                    actor,
                    PlaylistId.Parse(id),
                    cancellationToken
                );
                return new
                {
                    playlist.PlaylistId,
                    playlist.Metadata,
                    access = playlist.Role,
                    playlist.Revision,
                    segments = playlist.Segments.Skip(cursor).Take(limit),
                    next_cursor = (long)cursor + limit < playlist.Segments.Count
                        ? (int?)(cursor + limit)
                        : null,
                };
            }
        );
    }

    [McpServerTool(
        Name = "create_playlist",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false
    )]
    [McpMeta("securitySchemes", JsonValue = LibraryWrite)]
    [Description(
        "Create a guided playlist. Keep a segued run in one segment. An entry with no track_id is a text block. A retry creates a new playlist."
    )]
    public Task<CallToolResult> CreatePlaylist(
        string name,
        PlaylistDraftSegment[] segments,
        string? description = null,
        CancellationToken cancellationToken = default
    ) =>
        WithAccount(
            RelistenScopes.LibraryWrite,
            cancellationToken,
            async actor =>
            {
                var request = new CreatePlaylistRequest(
                    1,
                    CommandId.New(),
                    PlaylistId.New(),
                    new(name, description),
                    segments
                        .Select(segment => new PlaylistSegmentInput(
                            PlaylistSegmentId.New(),
                            segment
                                .Entries.Select(entry => new PlaylistEntryInput(
                                    PlaylistEntryId.New(),
                                    entry.TrackId,
                                    entry.Description
                                ))
                                .ToArray(),
                            segment.Description
                        ))
                        .ToArray()
                );
                return (await writes.CreateAsync(actor, request, cancellationToken)).Response;
            }
        );

    [McpServerTool(
        Name = "edit_playlist",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false
    )]
    [McpMeta("securitySchemes", JsonValue = LibraryWrite)]
    [Description(
        "Edit an owned playlist. Each change has operation_type and payload. Use update_metadata, insert_segment, update_segment, move_segment, delete_segment, insert_entries, update_entry, move_entry or delete_entry. Supply existing IDs as anchors. Relisten assigns IDs to new segments and entries."
    )]
    public Task<CallToolResult> EditPlaylist(
        string id,
        PlaylistEdit[] changes,
        CancellationToken cancellationToken
    ) =>
        WithAccount(
            RelistenScopes.LibraryWrite,
            cancellationToken,
            async actor =>
            {
                var playlist = PlaylistId.Parse(id);
                // Maintain the owner's boundary even if the batch service later supports collaborators.
                var revision =
                    await db
                        .Playlists.AsNoTracking()
                        .Where(row => row.Id == playlist && row.OwnerId == actor)
                        .Select(row => (long?)row.Revision)
                        .SingleOrDefaultAsync(cancellationToken)
                    ?? throw new PlaylistException(
                        404,
                        "playlist_not_found",
                        "Playlist not found."
                    );
                var operations = changes
                    .Select(change => BuildOperation(change, revision))
                    .ToArray();
                return await writes.ApplyAsync(
                    actor,
                    playlist,
                    new(1, operations),
                    cancellationToken
                );
            }
        );

    private static PlaylistOperation BuildOperation(PlaylistEdit change, long revision)
    {
        if (change.Payload.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException();
        }
        var payload = JsonNode.Parse(change.Payload.GetRawText())!.AsObject();
        if (change.OperationType == "insert_segment")
        {
            payload["segment_id"] = PlaylistSegmentId.New().Value;
        }
        if (change.OperationType is "insert_segment" or "insert_entries")
        {
            if (payload["entries"] is not JsonArray entries)
            {
                throw new JsonException();
            }
            foreach (var entry in entries)
            {
                if (entry is not JsonObject value)
                {
                    throw new JsonException();
                }
                value["entry_id"] = PlaylistEntryId.New().Value;
            }
        }
        if (
            change.OperationType == "move_entry"
            && payload["destination"] is JsonObject destination
            && destination["segment_id"] is null
        )
        {
            destination["new_segment_id"] = PlaylistSegmentId.New().Value;
        }

        var envelope = new JsonObject
        {
            ["operation_id"] = PlaylistOperationId.New().Value,
            ["base_revision"] = revision,
            ["operation_type"] = change.OperationType,
            ["payload"] = payload,
        };
        return envelope.Deserialize<PlaylistOperation>(PlaylistJson.Options)!;
    }

    private async Task<CallToolResult> WithAccount<T>(
        string scope,
        CancellationToken cancellationToken,
        Func<UserId, Task<T>> action
    )
    {
        var (session, error) = await authorization.AuthorizeAsync(scope, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        try
        {
            return McpResults.Data(await action(session!.UserId));
        }
        catch (PlaylistException exception)
        {
            return McpResults.Error(exception.Message);
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return McpResults.Error(
                "Check the playlist IDs and the format of the requested changes."
            );
        }
    }

    public sealed record PlaylistDraftSegment(
        PlaylistDraftEntry[] Entries,
        string? Description = null
    );

    public sealed record PlaylistDraftEntry(
        [property: JsonPropertyName("track_id")] string? TrackId = null,
        string? Description = null
    );

    public sealed record PlaylistEdit(
        [property: JsonPropertyName("operation_type")] string OperationType,
        JsonElement Payload
    );
}
