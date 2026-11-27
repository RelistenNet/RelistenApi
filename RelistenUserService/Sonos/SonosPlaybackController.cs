using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RelistenUserService.Authentication;
using RelistenUserService.Authentication.Authorization;
using RelistenUserService.Persistence;

namespace RelistenUserService.Sonos;

[ApiController]
[Authorize(Policy = AuthenticationConstants.AccountManagePolicy)]
[Route("v1/integrations/sonos/playback")]
public sealed partial class SonosPlaybackController(
    CurrentAccountContext account,
    SonosIntegration integration,
    SonosAccountLink link,
    SonosProtocol protocol,
    AccountsDbContext db,
    AdvisoryLockService locks,
    TimeProvider clock
) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (integration.Configuration is not { } config)
        {
            return NotFound();
        }

        var now = clock.GetUtcNow();
        var queues = await db.Set<SonosQueue>()
            .AsNoTracking()
            .Where(row =>
                row.UserId == account.User.Id
                && row.ExpiresAt > now
                && (row.Status == "loaded" || row.Status == "dispatching")
                && row.SonosSessionId != null
            )
            .ToListAsync(ct);
        if (queues.Count == 0)
        {
            return Ok(Array.Empty<PlaybackView>());
        }

        try
        {
            var token = await link.AccessAsync(config, account.User.Id, ct);
            if (token is null)
            {
                return ReconnectRequired();
            }

            var active = new List<PlaybackView>();
            var roomUnavailable = false;
            foreach (var group in queues.GroupBy(row => row.GroupId))
            {
                var eligible = new List<SonosQueue>();
                foreach (var queue in group)
                {
                    if (await link.SessionActiveAsync(queue.UserId, queue.SessionId, ct))
                    {
                        eligible.Add(queue);
                    }
                }

                if (eligible.Count == 0)
                {
                    continue;
                }

                try
                {
                    var views = await ReadGroupPlaybackAsync(token.Token, eligible, ct);
                    active.AddRange(views.Where(view => view.Status == "active"));
                }
                catch (Exception error) when (Unavailable(error, ct))
                {
                    roomUnavailable = true;
                }
            }

            return active.Count == 0 && roomUnavailable
                ? Problem(statusCode: 502, title: "Could not read Sonos playback.")
                : Ok(active);
        }
        catch (Exception error) when (Unavailable(error, ct))
        {
            return Problem(statusCode: 502, title: "Could not read Sonos playback.");
        }
    }

    [HttpGet("{handle:guid}")]
    public async Task<IActionResult> GetPlayback(Guid handle, CancellationToken ct)
    {
        if (
            integration.Configuration is not { } config
            || await ReadOwnedQueueAsync(handle, ct) is not { } queue
        )
        {
            return NotFound();
        }

        if (queue.Status is not ("loaded" or "dispatching"))
        {
            return Ok(Inactive(queue, queue.Status));
        }

        try
        {
            var token = await link.AccessAsync(config, account.User.Id, ct);
            if (token is null)
            {
                return ReconnectRequired();
            }

            var playback = await ReadGroupPlaybackAsync(token.Token, [queue], ct);
            return Ok(playback[0]);
        }
        catch (Exception error) when (Unavailable(error, ct))
        {
            return Problem(statusCode: 502, title: "Could not read Sonos playback.");
        }
    }

    private async Task<List<PlaybackView>> ReadGroupPlaybackAsync(
        string token,
        IReadOnlyList<SonosQueue> queues,
        CancellationToken ct
    )
    {
        // Recovering a lost load response can change which queue owns the room.
        // Serialize that observation with handoffs, while normal polls just read.
        await using var transaction = queues.Any(queue => queue.Status == "dispatching")
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        if (transaction is not null)
        {
            await LockRoomAsync(queues[0], ct);
        }

        var room = await ReadRoomAsync(token, queues[0].GroupId, ct);
        var views = new List<PlaybackView>(queues.Count);
        foreach (var queue in queues)
        {
            views.Add(await ReconcileAsync(queue, room, ct));
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }

        return views;
    }

    private async Task<SonosQueue?> ReadOwnedQueueAsync(Guid handle, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var queue = await db.Set<SonosQueue>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row =>
                    row.Id == handle
                    && row.UserId == account.User.Id
                    && row.SonosSessionId != null
                    && row.ExpiresAt > now,
                ct
            );
        // Other devices on this account can control the queue while the creating login is valid.
        return queue is not null && await link.SessionActiveAsync(queue.UserId, queue.SessionId, ct)
            ? queue
            : null;
    }

    private async Task<RoomPlayback?> ReadRoomAsync(
        string token,
        string group,
        CancellationToken ct
    )
    {
        try
        {
            var status = await protocol.ControlAsync(
                token,
                $"{SonosHandoff.GroupPath(group)}/playback",
                null,
                ct
            );
            if (
                status.ValueKind != JsonValueKind.Object
                || !status.TryGetProperty("playbackState", out var state)
                || state.ValueKind != JsonValueKind.String
            )
            {
                throw new JsonException("Sonos returned incomplete playback status.");
            }

            var itemId =
                status.TryGetProperty("itemId", out var item)
                && item.ValueKind == JsonValueKind.String
                    ? item.GetString()
                    : null;
            int? position =
                status.TryGetProperty("positionMillis", out var offset)
                && offset.ValueKind == JsonValueKind.Number
                && offset.TryGetInt32(out var millis)
                && millis >= 0
                    ? millis
                    : null;
            var queueVersion =
                status.TryGetProperty("queueVersion", out var version)
                && version.ValueKind == JsonValueKind.String
                && long.TryParse(version.GetString(), out var parsed)
                    ? parsed
                    : (long?)null;
            status.TryGetProperty("playModes", out var modes);
            status.TryGetProperty("availablePlaybackActions", out var actions);
            var repeatMode = (ReadBoolean(modes, "repeat"), ReadBoolean(modes, "repeatOne")) switch
            {
                (_, true) => SonosRepeatMode.One,
                (true, false) => SonosRepeatMode.All,
                (false, false) => SonosRepeatMode.Off,
                _ => (SonosRepeatMode?)null,
            };

            return new RoomPlayback(
                state.GetString()!,
                itemId,
                position,
                clock.GetUtcNow(),
                queueVersion,
                repeatMode,
                ReadBoolean(actions, "canRepeat"),
                ReadBoolean(actions, "canRepeatOne")
            );
        }
        catch (SonosRequestException error)
            when (error.Status is HttpStatusCode.Gone or HttpStatusCode.NotFound
                || error.Code == "ERROR_INVALID_OBJECT_ID"
            )
        {
            return null;
        }
    }

    private static bool? ReadBoolean(JsonElement obj, string property) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(property, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private async Task<PlaybackView> ReconcileAsync(
        SonosQueue queue,
        RoomPlayback? room,
        CancellationToken ct
    )
    {
        if (room is { ItemId: null or "" })
        {
            // During buffering there can be no current item. Wait for evidence of a replacement.
            return Inactive(queue, "unconfirmed");
        }

        var items = queue.ReadItems();
        var index = room is null ? -1 : Array.FindIndex(items, item => item.Id == room.ItemId);
        if (room is null || index < 0)
        {
            if (
                queue.Status == "dispatching"
                || (
                    room is not null
                    && queue.ReadRemovedItems().Any(entry => entry.Item.Id == room.ItemId)
                )
            )
            {
                return Inactive(queue, "unconfirmed");
            }

            // An edit may have committed after discovery read its snapshot and before
            // the speaker reported an item first introduced in that edit.
            var evicted = await MarkEvictedAsync(queue.Id, ct, queue.QueueVersion);
            return Inactive(queue, evicted == 1 ? "evicted" : "unconfirmed");
        }

        if (queue.Status == "dispatching" && !await SonosHandoff.ConfirmLoadedAsync(db, queue, ct))
        {
            return Inactive(queue, "unconfirmed");
        }

        var item = items[index];
        return new(
            queue.Id,
            queue.GroupId,
            queue.GroupName ?? queue.GroupId,
            "active",
            room.State,
            QueueOccurrence(item, index) with
            {
                TrackId = item.Track.Id.ObjectId["TrackU:".Length..],
            },
            room.PositionMillis,
            room.ObservedAt,
            items.Length,
            queue.QueueVersion,
            room.QueueVersion,
            room.RepeatMode,
            room.CanRepeat,
            room.CanRepeatOne
        );
    }

    private Task<int> MarkEvictedAsync(Guid id, CancellationToken ct, long? version = null) =>
        db.Set<SonosQueue>()
            .Where(row =>
                row.Id == id
                && row.Status == "loaded"
                && (version == null || row.QueueVersion == version)
            )
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.Status, "evicted"), ct);

    private static PlaybackView Inactive(SonosQueue queue, string status) =>
        new(
            queue.Id,
            queue.GroupId,
            queue.GroupName ?? queue.GroupId,
            status,
            null,
            null,
            null,
            null,
            QueueVersion: queue.QueueVersion
        );

    private static string PlaybackUrl(Guid handle) => $"/v1/integrations/sonos/playback/{handle}";

    private IActionResult ReconnectRequired() =>
        Conflict(new { code = "sonos_reconnect_required" });

    private IActionResult PlaybackInactive() => Conflict(new { code = "sonos_playback_inactive" });

    private static bool Unavailable(Exception error, CancellationToken ct) =>
        error is SonosRequestException or HttpRequestException or JsonException
        || (error is OperationCanceledException && !ct.IsCancellationRequested);

    // Command receipts persist these values as the target of a repeat command.
    [JsonConverter(typeof(JsonStringEnumConverter<SonosRepeatMode>))]
    private enum SonosRepeatMode
    {
        [JsonStringEnumMemberName("off")]
        Off = 0,

        [JsonStringEnumMemberName("all")]
        All = 1,

        [JsonStringEnumMemberName("one")]
        One = 2,
    }

    private sealed record RoomPlayback(
        string State,
        string? ItemId,
        int? PositionMillis,
        DateTimeOffset ObservedAt,
        long? QueueVersion,
        SonosRepeatMode? RepeatMode,
        bool? CanRepeat,
        bool? CanRepeatOne
    );

    private sealed record PlaybackView(
        Guid PlaybackHandle,
        string GroupId,
        string GroupName,
        string Status,
        string? PlaybackState,
        PlaybackOccurrence? CurrentOccurrence,
        int? PositionMillis,
        DateTimeOffset? ObservedAt,
        int? QueueLength = null,
        long? QueueVersion = null,
        long? SonosQueueVersion = null,
        SonosRepeatMode? RepeatMode = null,
        bool? CanRepeat = null,
        bool? CanRepeatOne = null
    );

    private sealed record PlaybackOccurrence(
        string ItemId,
        int QueueIndex,
        string TrackId,
        string Name,
        int? DurationMillis,
        string? Artist,
        string? Album,
        string? ImageUrl,
        string OccurrenceId
    )
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? PlaylistId { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? PlaylistEntryId { get; init; }
    }
}
