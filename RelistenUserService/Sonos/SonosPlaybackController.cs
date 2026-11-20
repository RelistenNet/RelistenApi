using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RelistenUserService.Authentication;
using RelistenUserService.Authentication.Authorization;
using RelistenUserService.Persistence;

namespace RelistenUserService.Sonos;

public sealed record SonosTransportRequest(string Action);

[ApiController]
[Authorize(Policy = AuthenticationConstants.AccountManagePolicy)]
[Route("v1/integrations/sonos/playback")]
public sealed class SonosPlaybackController(
    CurrentAccountContext account,
    SonosDevelopment development,
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
        if (development.Configuration is not { } config)
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
            foreach (var group in queues.GroupBy(row => row.GroupId))
            {
                var observed = await ReadRoomAsync(token.Token, group.Key, ct);
                foreach (var queue in group)
                {
                    if (await link.SessionActiveAsync(queue.UserId, queue.SessionId, ct))
                    {
                        var state = await ReconcileAsync(queue, observed, ct);
                        if (state.Status == "active")
                        {
                            active.Add(state);
                        }
                    }
                }
            }

            return Ok(active);
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
            development.Configuration is not { } config
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

            return Ok(
                await ReconcileAsync(queue, await ReadRoomAsync(token.Token, queue.GroupId, ct), ct)
            );
        }
        catch (Exception error) when (Unavailable(error, ct))
        {
            return Problem(statusCode: 502, title: "Could not read Sonos playback.");
        }
    }

    [HttpPost("{handle:guid}/commands")]
    [RequestSizeLimit(4096)]
    public async Task<IActionResult> Command(
        Guid handle,
        SonosTransportRequest request,
        CancellationToken ct
    )
    {
        if (
            development.Configuration is not { } config
            || await ReadOwnedQueueAsync(handle, ct) is not { } queue
        )
        {
            return NotFound();
        }

        if (request.Action is not ("play" or "pause"))
        {
            return Problem(statusCode: 422, title: "Choose play or pause.");
        }

        try
        {
            var token = await link.AccessAsync(config, account.User.Id, ct);
            if (token is null)
            {
                return ReconnectRequired();
            }

            // Use the same locks as handoff preparation. A pending replacement must settle
            // before a command can use the previous room state.
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await locks.LockAllAsync(
                [$"sonos-group:{queue.GroupId}", $"sonos-grant:{queue.UserId}"],
                ct
            );
            queue = await ReadOwnedQueueAsync(handle, ct);
            var now = clock.GetUtcNow();
            if (
                queue is null
                || queue.Status != "loaded"
                || await db.Set<SonosQueue>()
                    .AnyAsync(
                        row =>
                            row.GroupId == queue.GroupId
                            && row.Status == "dispatching"
                            && row.ExpiresAt > now,
                        ct
                    )
                || !await db.Set<SonosGrant>()
                    .AnyAsync(
                        row =>
                            row.UserId == account.User.Id
                            && row.ProtectedToken == token.GrantVersion,
                        ct
                    )
            )
            {
                return PlaybackInactive();
            }

            var state = await ReconcileAsync(
                queue,
                await ReadRoomAsync(token.Token, queue.GroupId, ct),
                ct
            );
            if (state.Status != "active" || state.PositionMillis is null)
            {
                await transaction.CommitAsync(ct);
                return PlaybackInactive();
            }

            var wantsPlay = request.Action == "play";
            if (
                state.PlaybackState
                == (wantsPlay ? "PLAYBACK_STATE_PLAYING" : "PLAYBACK_STATE_PAUSED")
            )
            {
                return Accepted(PlaybackUrl(handle));
            }

            try
            {
                // Reload the current occurrence at the observed offset. Sonos checks the
                // session target, including a takeover after the room status read.
                await protocol.ControlAsync(
                    token.Token,
                    $"{SonosProtocol.SessionPath(queue.SonosSessionId!)}/playbackSession/skipToItem",
                    new
                    {
                        itemId = state.CurrentOccurrence!.ItemId,
                        positionMillis = state.PositionMillis,
                        playOnCompletion = wantsPlay,
                    },
                    ct
                );
            }
            catch (SonosRequestException error) when (error.SessionEnded)
            {
                await MarkEvictedAsync(queue.Id, ct);
                await transaction.CommitAsync(ct);
                return PlaybackInactive();
            }

            await transaction.CommitAsync(ct);
            return Accepted(PlaybackUrl(handle));
        }
        catch (Exception error) when (Unavailable(error, ct))
        {
            return Problem(
                statusCode: 502,
                title: "Could not confirm the Sonos command. Refresh playback before trying again."
            );
        }
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
            return new RoomPlayback(state.GetString()!, itemId, position, clock.GetUtcNow());
        }
        catch (SonosRequestException error) when (error.Status == HttpStatusCode.Gone)
        {
            return null;
        }
    }

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

        var items = JsonSerializer.Deserialize<QueueTrack[]>(
            queue.ItemsJson,
            SonosHandoff.QueueJson
        )!;
        var index = room is null ? -1 : Array.FindIndex(items, item => item.Id == room.ItemId);
        if (room is null || index < 0)
        {
            if (queue.Status == "dispatching")
            {
                return Inactive(queue, "unconfirmed");
            }

            await MarkEvictedAsync(queue.Id, ct);
            return Inactive(queue, "evicted");
        }

        if (queue.Status == "dispatching")
        {
            var updated = await db.Set<SonosQueue>()
                .Where(row => row.Id == queue.Id && row.Status == "dispatching")
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.Status, "loaded"), ct);
            if (updated != 1)
            {
                return Inactive(queue, "unconfirmed");
            }
        }

        var item = items[index];
        return new(
            queue.Id,
            queue.GroupId,
            queue.GroupName ?? queue.GroupId,
            "active",
            room.State,
            new PlaybackOccurrence(
                item.Id,
                index,
                item.Track.Id.ObjectId["TrackU:".Length..],
                item.Track.Name,
                item.Track.DurationMillis,
                item.Track.Artist?.Name,
                item.Track.Album?.Name,
                item.Track.ImageUrl
            ),
            room.PositionMillis,
            room.ObservedAt
        );
    }

    private Task<int> MarkEvictedAsync(Guid id, CancellationToken ct) =>
        db.Set<SonosQueue>()
            .Where(row => row.Id == id && row.Status == "loaded")
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
            null
        );

    private static string PlaybackUrl(Guid handle) => $"/v1/integrations/sonos/playback/{handle}";

    private IActionResult ReconnectRequired() =>
        Conflict(new { code = "sonos_reconnect_required" });

    private IActionResult PlaybackInactive() => Conflict(new { code = "sonos_playback_inactive" });

    private static bool Unavailable(Exception error, CancellationToken ct) =>
        error is SonosRequestException or HttpRequestException or JsonException
        || (error is OperationCanceledException && !ct.IsCancellationRequested);

    private sealed record RoomPlayback(
        string State,
        string? ItemId,
        int? PositionMillis,
        DateTimeOffset ObservedAt
    );

    private sealed record PlaybackView(
        Guid PlaybackHandle,
        string GroupId,
        string GroupName,
        string Status,
        string? PlaybackState,
        PlaybackOccurrence? CurrentOccurrence,
        int? PositionMillis,
        DateTimeOffset? ObservedAt
    );

    private sealed record PlaybackOccurrence(
        string ItemId,
        int QueueIndex,
        string TrackId,
        string Name,
        int? DurationMillis,
        string? Artist,
        string? Album,
        string? ImageUrl
    );
}
