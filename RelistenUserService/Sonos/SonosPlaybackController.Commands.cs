using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace RelistenUserService.Sonos;

public sealed record SonosTransportRequest(
    string Action,
    Guid? CommandId = null,
    string? ExpectedItemId = null,
    int? PositionMillis = null,
    int? Volume = null,
    bool? Muted = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        long? ExpectedQueueVersion = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? TargetOccurrenceId = null
);

public sealed partial class SonosPlaybackController
{
    [HttpGet("{handle:guid}/volume")]
    public async Task<IActionResult> GetVolume(Guid handle, CancellationToken ct)
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
            return PlaybackInactive();
        }

        try
        {
            var access = await link.AccessAsync(config, account.User.Id, ct);
            if (access is null)
            {
                return ReconnectRequired();
            }

            var playback = await ReadRoomAsync(access.Token, queue.GroupId, ct);
            if ((await ReconcileAsync(queue, playback, ct)).Status != "active")
            {
                return PlaybackInactive();
            }

            return Ok(await ReadVolumeAsync(access.Token, queue.GroupId, ct));
        }
        catch (Exception error) when (Unavailable(error, ct))
        {
            return Problem(statusCode: 502, title: "Could not read Sonos group volume.");
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
            integration.Configuration is not { } config
            || await ReadOwnedQueueAsync(handle, ct) is not { } queue
        )
        {
            return NotFound();
        }

        if (!ValidCommand(request))
        {
            return Problem(statusCode: 422, title: "Invalid Sonos playback command.");
        }

        var commandId = request.CommandId ?? Guid.CreateVersion7();
        var hash = SonosProtocol.Hash(JsonSerializer.Serialize(request));
        try
        {
            var existing = await FindCommandAsync(handle, commandId, ct);
            if (existing is not null)
            {
                return existing.RequestHash == hash
                    ? CommandResponse(
                        await ReconcileCommandAsync(config, queue, existing, ct),
                        request.CommandId is null
                    )
                    : Conflict(new { code = "sonos_command_id_conflict" });
            }

            var access = await link.AccessAsync(config, account.User.Id, ct);
            if (access is null)
            {
                return ReconnectRequired();
            }

            var prepared = await PrepareCommandAsync(queue, request, commandId, hash, access, ct);
            if (prepared.Result is { } result)
            {
                return result;
            }

            var receipt = prepared.Command!;
            if (!prepared.Replay && receipt.Status == "pending")
            {
                await DispatchCommandAsync(queue, receipt, access, ct);
            }

            return CommandResponse(receipt, request.CommandId is null);
        }
        catch (Exception error) when (Unavailable(error, ct))
        {
            return Problem(
                statusCode: 502,
                title: "Could not confirm the Sonos command. Refresh playback before trying again."
            );
        }
    }

    [HttpGet("{handle:guid}/commands/{commandId:guid}")]
    public async Task<IActionResult> GetCommand(Guid handle, Guid commandId, CancellationToken ct)
    {
        if (
            integration.Configuration is not { } config
            || await ReadOwnedQueueAsync(handle, ct) is not { } queue
            || await FindCommandAsync(handle, commandId, ct) is not { } receipt
        )
        {
            return NotFound();
        }

        return Ok(CommandView(await ReconcileCommandAsync(config, queue, receipt, ct)));
    }

    private async Task<(
        SonosCommand? Command,
        IActionResult? Result,
        bool Replay
    )> PrepareCommandAsync(
        SonosQueue original,
        SonosTransportRequest request,
        Guid commandId,
        string hash,
        SonosAccess access,
        CancellationToken ct
    )
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockRoomAsync(original, ct);
        var previous = await FindCommandAsync(original.Id, commandId, ct);
        if (previous is not null)
        {
            return previous.RequestHash == hash
                ? (previous, null, true)
                : (null, Conflict(new { code = "sonos_command_id_conflict" }), true);
        }

        if (await ReadCommandQueueAsync(original.Id, access, ct) is not { } queue)
        {
            return (null, PlaybackInactive(), false);
        }

        var room = await ReadRoomAsync(access.Token, queue.GroupId, ct);
        var playback = await ReconcileAsync(queue, room, ct);
        if (playback.Status != "active" || playback.CurrentOccurrence is not { } current)
        {
            await transaction.CommitAsync(ct);
            return (null, PlaybackInactive(), false);
        }

        if (request.ExpectedItemId is { } expectedItem && expectedItem != current.ItemId)
        {
            return (null, Conflict(new { code = "sonos_playback_changed" }), false);
        }

        if (request.ExpectedQueueVersion is { } expected && expected != queue.QueueVersion)
        {
            return (null, QueueChanged(queue), false);
        }

        var receipt = new SonosCommand
        {
            QueueId = queue.Id,
            Id = commandId,
            RequestHash = hash,
            Action = request.Action,
            ExpectedItemId = current.ItemId,
            QueueVersion = request.Action is "next" or "previous" or "skip"
                ? queue.QueueVersion
                : null,
        };
        if (!ResolveCommandTarget(receipt, request, queue, playback, current))
        {
            return (null, Conflict(new { code = "sonos_command_unavailable" }), false);
        }

        db.Add(receipt);
        await db.Set<SonosQueue>()
            .Where(row => row.Id == queue.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.LatestCommandId, commandId), ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return (receipt, null, false);
    }

    private static bool ResolveCommandTarget(
        SonosCommand receipt,
        SonosTransportRequest request,
        SonosQueue queue,
        PlaybackView state,
        PlaybackOccurrence current
    )
    {
        switch (request.Action)
        {
            case "play":
            case "pause":
                receipt.TargetItemId = current.ItemId;
                receipt.PlayOnCompletion = request.Action == "play";
                if (
                    state.PlaybackState
                    == (
                        receipt.PlayOnCompletion.Value
                            ? "PLAYBACK_STATE_PLAYING"
                            : "PLAYBACK_STATE_PAUSED"
                    )
                )
                {
                    receipt.Status = "confirmed";
                }

                return true;
            case "seek":
                receipt.TargetItemId = current.ItemId;
                receipt.TargetValue = request.PositionMillis;
                return current.DurationMillis is not > 0
                    || request.PositionMillis < current.DurationMillis;
            case "next":
            case "previous":
            case "skip":
                var items = queue.ReadItems();
                var index = request.Action switch
                {
                    "next" => current.QueueIndex + 1,
                    "previous" => current.QueueIndex - 1,
                    _ => Array.FindIndex(
                        items,
                        item => item.OccurrenceKey == request.TargetOccurrenceId
                    ),
                };
                if (index < 0 || index >= items.Length)
                {
                    return false;
                }

                receipt.TargetItemId = items[index].Id;
                receipt.TargetValue = 0;
                if (receipt.TargetItemId == current.ItemId)
                {
                    receipt.Status = "confirmed";
                }

                return true;
            case "volume":
                receipt.TargetValue = request.Volume;
                return true;
            case "mute":
                receipt.TargetMuted = request.Muted;
                return true;
            default:
                return false;
        }
    }

    private async Task DispatchCommandAsync(
        SonosQueue original,
        SonosCommand receipt,
        SonosAccess access,
        CancellationToken ct
    )
    {
        // The pending row committed before dispatch. Retries only observe it, including
        // after a process crash. A newer command can supersede one that has yet to send.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockRoomAsync(original, ct);
        var persisted = await FindCommandAsync(receipt.QueueId, receipt.Id, ct);
        if (persisted is not { Status: "pending" })
        {
            receipt.Status = persisted?.Status ?? "rejected";
            receipt.ErrorCode = persisted?.ErrorCode;
            return;
        }

        var queue = await ReadCommandQueueAsync(original.Id, access, ct);
        if (queue is null || queue.LatestCommandId != receipt.Id)
        {
            await FinishCommandAsync(
                receipt,
                "rejected",
                queue is null ? "sonos_playback_inactive" : "sonos_command_superseded",
                ct
            );
            await transaction.CommitAsync(ct);
            return;
        }

        if (receipt.QueueVersion is { } version && queue.QueueVersion != version)
        {
            await FinishCommandAsync(receipt, "rejected", "sonos_queue_changed", ct);
            await transaction.CommitAsync(ct);
            return;
        }

        try
        {
            var room = await ReadRoomAsync(access.Token, queue.GroupId, ct);
            var playback = await ReconcileAsync(queue, room, ct);
            if (playback.Status != "active")
            {
                await FinishCommandAsync(receipt, "rejected", "sonos_playback_inactive", ct);
            }
            else if (
                !GroupCommand(receipt.Action)
                && playback.CurrentOccurrence!.ItemId != receipt.ExpectedItemId
            )
            {
                await FinishCommandAsync(receipt, "rejected", "sonos_playback_changed", ct);
            }
            else
            {
                await SendCommandAsync(queue, receipt, access.Token, playback, ct);
            }

            await transaction.CommitAsync(ct);
        }
        catch (Exception error) when (Unavailable(error, ct))
        {
            // Preserve pending on a lost response. The next receipt read checks
            // the original desired state, without sending the remote command again.
        }
    }

    private async Task SendCommandAsync(
        SonosQueue queue,
        SonosCommand receipt,
        string token,
        PlaybackView playback,
        CancellationToken ct
    )
    {
        if (receipt.Action is "play" or "pause" && playback.PositionMillis is not { })
        {
            await FinishCommandAsync(receipt, "rejected", "sonos_playback_changed", ct);
            return;
        }

        string path;
        object body;
        var groupPath = SonosHandoff.GroupPath(queue.GroupId);
        var sessionPath = SonosProtocol.SessionPath(queue.SonosSessionId!);
        switch (receipt.Action)
        {
            case "volume":
                var volume = await ReadVolumeAsync(token, queue.GroupId, ct);
                if (volume.Fixed)
                {
                    await FinishCommandAsync(receipt, "rejected", "sonos_volume_fixed", ct);
                    return;
                }

                path = $"{groupPath}/groupVolume";
                body = new { volume = receipt.TargetValue, muted = volume.Muted };
                break;
            case "mute":
                path = $"{groupPath}/groupVolume/mute";
                body = new { muted = receipt.TargetMuted };
                break;
            case "seek":
                path = $"{sessionPath}/playbackSession/seek";
                body = new { itemId = receipt.TargetItemId, positionMillis = receipt.TargetValue };
                break;
            default:
                // Target the queue's session so Play/Pause cannot affect a replacement session.
                // In the same session, this can restore the sampled playhead after a seek or skip.
                path = $"{sessionPath}/playbackSession/skipToItem";
                var shouldPlay =
                    receipt.PlayOnCompletion
                    ?? playback.PlaybackState
                        is "PLAYBACK_STATE_PLAYING"
                            or "PLAYBACK_STATE_BUFFERING";
                body = new
                {
                    itemId = receipt.TargetItemId,
                    queueVersion = queue.QueueVersion.ToString(CultureInfo.InvariantCulture),
                    positionMillis = receipt.TargetValue ?? playback.PositionMillis,
                    playOnCompletion = shouldPlay,
                };
                break;
        }

        try
        {
            await protocol.ControlAsync(token, path, body, ct);
        }
        catch (SonosRequestException error)
            when (receipt.Action == "seek" && error.Code == "ERROR_INVALID_OBJECT_ID")
        {
            await FinishCommandAsync(receipt, "rejected", "sonos_playback_changed", ct);
            return;
        }
        catch (SonosRequestException error)
            when (!GroupCommand(receipt.Action) && error.SessionEnded)
        {
            await MarkEvictedAsync(queue.Id, ct);
            await FinishCommandAsync(receipt, "rejected", "sonos_playback_inactive", ct);
            return;
        }
        catch (SonosRequestException error)
            when (error.Status
                    is HttpStatusCode.BadRequest
                        or HttpStatusCode.Unauthorized
                        or HttpStatusCode.Forbidden
                        or HttpStatusCode.NotFound
                        or HttpStatusCode.Gone
                || error.Code == "ERROR_COMMAND_FAILED"
            )
        {
            await FinishCommandAsync(receipt, "rejected", "sonos_command_rejected", ct);
            return;
        }

        await FinishCommandAsync(receipt, "accepted", null, ct);
    }

    private async Task<SonosCommand> ReconcileCommandAsync(
        SonosConfiguration config,
        SonosQueue queue,
        SonosCommand receipt,
        CancellationToken ct
    )
    {
        if (receipt.Status != "pending")
        {
            return receipt;
        }

        try
        {
            var access = await link.AccessAsync(config, queue.UserId, ct);
            if (
                access is null
                || queue.Status is "evicted" or "revoked" or "failed" or "interrupted"
            )
            {
                await FinishCommandAsync(receipt, "rejected", "sonos_playback_inactive", ct);
                return receipt;
            }

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await LockRoomAsync(queue, ct);
            var currentReceipt = await FindCommandAsync(receipt.QueueId, receipt.Id, ct);
            if (currentReceipt is not { Status: "pending" })
            {
                return currentReceipt ?? receipt;
            }

            if (await ReadCommandQueueAsync(queue.Id, access, ct) is not { } currentQueue)
            {
                return receipt;
            }

            queue = currentQueue;
            if (queue.LatestCommandId != receipt.Id)
            {
                await FinishCommandAsync(receipt, "rejected", "sonos_command_superseded", ct);
                await transaction.CommitAsync(ct);
                return receipt;
            }

            var room = await ReadRoomAsync(access.Token, queue.GroupId, ct);
            var state = await ReconcileAsync(queue, room, ct);
            if (state.Status != "active")
            {
                if (state.Status == "evicted")
                {
                    await FinishCommandAsync(receipt, "rejected", "sonos_playback_inactive", ct);
                }

                await transaction.CommitAsync(ct);
                return receipt;
            }

            var observed = receipt.Action switch
            {
                "play" => state.CurrentOccurrence!.ItemId == receipt.TargetItemId
                    && state.PlaybackState == "PLAYBACK_STATE_PLAYING",
                "pause" => state.CurrentOccurrence!.ItemId == receipt.TargetItemId
                    && state.PlaybackState == "PLAYBACK_STATE_PAUSED",
                "next" or "previous" or "skip" => state.CurrentOccurrence!.ItemId
                    == receipt.TargetItemId,
                "seek" => state.CurrentOccurrence!.ItemId == receipt.TargetItemId
                    && state.PositionMillis == receipt.TargetValue,
                "volume" => (await ReadVolumeAsync(access.Token, queue.GroupId, ct)).Volume
                    == receipt.TargetValue,
                "mute" => (await ReadVolumeAsync(access.Token, queue.GroupId, ct)).Muted
                    == receipt.TargetMuted,
                _ => false,
            };
            if (observed)
            {
                await FinishCommandAsync(receipt, "confirmed", null, ct);
            }
            else if (receipt.QueueVersion is { } expected && expected != queue.QueueVersion)
            {
                await FinishCommandAsync(receipt, "rejected", "sonos_queue_changed", ct);
            }

            await transaction.CommitAsync(ct);
        }
        catch (Exception error) when (Unavailable(error, ct))
        {
            // Keep a pending receipt readable during a Sonos outage.
        }

        return receipt;
    }

    private async Task<SonosQueue?> ReadCommandQueueAsync(
        Guid handle,
        SonosAccess access,
        CancellationToken ct
    )
    {
        var queue = await ReadOwnedQueueAsync(handle, ct);
        var now = clock.GetUtcNow();
        if (
            queue is not { Status: "loaded" }
            || await db.Set<SonosQueue>()
                .AnyAsync(
                    row =>
                        row.GroupId == queue.GroupId
                        && row.Status == "dispatching"
                        && row.DispatchUntil > now,
                    ct
                )
            || !await db.Set<SonosGrant>()
                .AnyAsync(
                    row =>
                        row.UserId == account.User.Id && row.ProtectedToken == access.GrantVersion,
                    ct
                )
        )
        {
            return null;
        }

        return queue;
    }

    private Task LockRoomAsync(SonosQueue queue, CancellationToken ct) =>
        locks.LockAllAsync([$"sonos-group:{queue.GroupId}", $"sonos-grant:{queue.UserId}"], ct);

    private Task<SonosCommand?> FindCommandAsync(Guid handle, Guid id, CancellationToken ct) =>
        db.Set<SonosCommand>()
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.QueueId == handle && row.Id == id, ct);

    private async Task FinishCommandAsync(
        SonosCommand receipt,
        string status,
        string? code,
        CancellationToken ct
    )
    {
        var changed = await db.Set<SonosCommand>()
            .Where(row =>
                row.QueueId == receipt.QueueId && row.Id == receipt.Id && row.Status == "pending"
            )
            .ExecuteUpdateAsync(
                set =>
                    set.SetProperty(row => row.Status, status)
                        .SetProperty(row => row.ErrorCode, code),
                ct
            );
        if (changed == 1)
        {
            receipt.Status = status;
            receipt.ErrorCode = code;
        }
    }

    private IActionResult CommandResponse(SonosCommand receipt, bool legacy)
    {
        if (receipt.Status == "rejected")
        {
            return Conflict(new { code = receipt.ErrorCode, commandId = receipt.Id });
        }

        if (legacy)
        {
            return receipt.Status == "pending"
                ? Problem(
                    statusCode: 502,
                    title: "Could not confirm the Sonos command. Refresh playback before trying again."
                )
                : Accepted(PlaybackUrl(receipt.QueueId));
        }

        return Accepted(
            $"{PlaybackUrl(receipt.QueueId)}/commands/{receipt.Id}",
            CommandView(receipt)
        );
    }

    private static object CommandView(SonosCommand receipt) =>
        new
        {
            commandId = receipt.Id,
            receipt.Status,
            code = receipt.ErrorCode,
            receipt.TargetItemId,
            playbackUrl = PlaybackUrl(receipt.QueueId),
        };

    private async Task<GroupVolume> ReadVolumeAsync(
        string token,
        string group,
        CancellationToken ct
    )
    {
        var result = await protocol.ControlAsync(
            token,
            $"{SonosHandoff.GroupPath(group)}/groupVolume",
            null,
            ct
        );
        if (
            result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("volume", out var volume)
            || volume.ValueKind != JsonValueKind.Number
            || !volume.TryGetInt32(out var level)
            || level is < 0 or > 100
            || !result.TryGetProperty("muted", out var muted)
            || muted.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
        )
        {
            throw new JsonException("Sonos returned incomplete group volume.");
        }

        var fixedVolume =
            result.TryGetProperty("fixed", out var value) && value.ValueKind == JsonValueKind.True;
        return new(level, muted.GetBoolean(), fixedVolume);
    }

    private static bool ValidCommand(SonosTransportRequest request)
    {
        var validTarget =
            request.Action == "skip"
                ? request.ExpectedQueueVersion.HasValue
                    && SonosHandoff.ValidOccurrenceId(request.TargetOccurrenceId)
                : request.TargetOccurrenceId is null;
        if (
            !validTarget
            || request.CommandId == Guid.Empty
            || request.ExpectedQueueVersion is < 1
            || (request.ExpectedItemId is { } id && !Guid.TryParseExact(id, "D", out _))
        )
        {
            return false;
        }

        return request.Action switch
        {
            "play" or "pause" => request.PositionMillis is null
                && request.Volume is null
                && request.Muted is null,
            "seek" => request.CommandId != null
                && request.ExpectedItemId != null
                && request.PositionMillis is >= 0
                && request.Volume is null
                && request.Muted is null,
            "next" or "previous" or "skip" => request.CommandId != null
                && request.ExpectedItemId != null
                && request.PositionMillis is null
                && request.Volume is null
                && request.Muted is null,
            "volume" => request.CommandId != null
                && request.Volume is >= 0 and <= 100
                && request.PositionMillis is null
                && request.Muted is null,
            "mute" => request.CommandId != null
                && request.Muted != null
                && request.PositionMillis is null
                && request.Volume is null,
            _ => false,
        };
    }

    private static bool GroupCommand(string action) => action is "volume" or "mute";

    private sealed record GroupVolume(int Volume, bool Muted, bool Fixed);
}
