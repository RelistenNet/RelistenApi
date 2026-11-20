using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace RelistenUserService.Sonos;

public sealed record SonosQueueEditRequest(
    Guid CommandId,
    long ExpectedVersion,
    string ExpectedItemId,
    SonosOccurrenceInput[] Occurrences
);

public sealed partial class SonosPlaybackController
{
    [HttpGet("{handle:guid}/queue")]
    public async Task<IActionResult> GetQueue(Guid handle, CancellationToken ct)
    {
        if (
            integration.Configuration is null
            || await ReadOwnedQueueAsync(handle, ct) is not { } queue
        )
        {
            return NotFound();
        }

        if (queue.Status != "loaded")
        {
            return PlaybackInactive();
        }

        var items = queue.ReadItems();
        return Ok(
            new
            {
                queue.QueueVersion,
                queueLength = items.Length,
                refreshPending = queue.RefreshedQueueVersion < queue.QueueVersion,
                occurrences = items.Select(QueueOccurrence),
            }
        );
    }

    [HttpPut("{handle:guid}/queue")]
    [RequestSizeLimit(2 * 1024 * 1024)]
    public async Task<IActionResult> ReplaceQueue(
        Guid handle,
        SonosQueueEditRequest request,
        [FromServices] SonosHandoff handoff,
        CancellationToken ct
    )
    {
        if (
            integration.Configuration is not { } config
            || await ReadOwnedQueueAsync(handle, ct) is not { } original
        )
        {
            return NotFound();
        }

        if (
            request.CommandId == Guid.Empty
            || request.ExpectedVersion < 1
            || !Guid.TryParseExact(request.ExpectedItemId, "D", out _)
            || !SonosHandoff.ValidOccurrences(request.Occurrences)
        )
        {
            return Problem(statusCode: 422, title: "Invalid Sonos queue edit.");
        }

        var hash = SonosProtocol.Hash(JsonSerializer.Serialize(request));
        try
        {
            if (await FindCommandAsync(handle, request.CommandId, ct) is { } replay)
            {
                return ReadQueueEditReplay(replay, hash);
            }

            if (request.ExpectedVersion != original.QueueVersion)
            {
                return QueueChanged(original);
            }

            var current = original.ReadItems();
            var removed = original.ReadRemovedItems();
            var replacement = await ResolveReplacementAsync(
                config,
                handoff,
                request.Occurrences,
                current,
                removed,
                ct
            );
            if (replacement is null)
            {
                return Problem(
                    statusCode: 422,
                    title: "A Sonos occurrence changed recording, reused a removed ID, or is unavailable."
                );
            }

            var access = await link.AccessAsync(config, original.UserId, ct);
            if (access is null)
            {
                return ReconnectRequired();
            }

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await LockRoomAsync(original, ct);
            var prior = await FindCommandAsync(handle, request.CommandId, ct);
            if (prior is not null)
            {
                return ReadQueueEditReplay(prior, hash);
            }

            if (await ReadCommandQueueAsync(handle, access, ct) is not { } queue)
            {
                return PlaybackInactive();
            }

            if (request.ExpectedVersion != queue.QueueVersion)
            {
                return QueueChanged(queue);
            }

            // Check the room after catalog validation. The playhead can advance without changing
            // the queue revision. Sonos can still advance after this read, before fetching the edit.
            var room = await ReadRoomAsync(access.Token, queue.GroupId, ct);
            var playback = await ReconcileAsync(queue, room, ct);
            if (playback.Status != "active" || playback.CurrentOccurrence is not { } playing)
            {
                await transaction.CommitAsync(ct);
                return PlaybackInactive();
            }

            if (playing.ItemId != request.ExpectedItemId)
            {
                return Conflict(new { code = "sonos_playback_changed" });
            }

            var protectedLength = playing.QueueIndex + 1;
            if (!current.Take(protectedLength).SequenceEqual(replacement.Take(protectedLength)))
            {
                return Conflict(new { code = "sonos_queue_current_protected" });
            }

            var result = await SaveQueueEditAsync(
                queue,
                current,
                replacement,
                removed,
                request.CommandId,
                hash,
                playing.ItemId,
                ct
            );
            await transaction.CommitAsync(ct);
            return result;
        }
        catch (Exception error) when (Unavailable(error, ct))
        {
            return Problem(statusCode: 502, title: "Could not validate the Sonos queue edit.");
        }
    }

    private async Task<IActionResult> SaveQueueEditAsync(
        SonosQueue queue,
        QueueTrack[] current,
        QueueTrack[] replacement,
        RemovedQueueTrack[] removed,
        Guid commandId,
        string hash,
        string itemId,
        CancellationToken ct
    )
    {
        var version = queue.QueueVersion;
        if (!current.SequenceEqual(replacement))
        {
            var tombstones = RetainRemovedItems(current, replacement, removed);
            if (tombstones.Count > 15_000)
            {
                return Conflict(new { code = "sonos_queue_history_full" });
            }

            var itemsJson = JsonSerializer.Serialize(replacement, SonosHandoff.QueueJson);
            var removedJson = JsonSerializer.Serialize(tombstones, SonosHandoff.QueueJson);
            version++;
            var nextRefresh = clock.GetUtcNow();
            var saved = await db.Set<SonosQueue>()
                .Where(row =>
                    row.Id == queue.Id
                    && row.Status == "loaded"
                    && row.QueueVersion == queue.QueueVersion
                )
                .ExecuteUpdateAsync(
                    update =>
                        update
                            .SetProperty(row => row.ItemsJson, itemsJson)
                            .SetProperty(row => row.RemovedItemsJson, removedJson)
                            .SetProperty(row => row.QueueVersion, version)
                            .SetProperty(row => row.RefreshAfter, nextRefresh),
                    ct
                );
            if (saved != 1)
            {
                return PlaybackInactive();
            }
        }

        var receipt = new SonosCommand
        {
            QueueId = queue.Id,
            Id = commandId,
            RequestHash = hash,
            Action = "queue",
            Status = "accepted",
            ExpectedItemId = itemId,
            TargetValue = replacement.Length,
            QueueVersion = version,
        };
        db.Add(receipt);
        await db.SaveChangesAsync(ct);
        return Ok(QueueEditReceipt(receipt));
    }

    private IActionResult ReadQueueEditReplay(SonosCommand prior, string hash) =>
        prior.Action == "queue" && prior.RequestHash == hash
            ? Ok(QueueEditReceipt(prior))
            : Conflict(new { code = "sonos_command_id_conflict" });

    private static async Task<QueueTrack[]?> ResolveReplacementAsync(
        SonosConfiguration config,
        SonosHandoff handoff,
        SonosOccurrenceInput[] request,
        QueueTrack[] current,
        RemovedQueueTrack[] removed,
        CancellationToken ct
    )
    {
        var existing = current.ToDictionary(item => item.OccurrenceKey, StringComparer.Ordinal);
        var retired = removed
            .Select(entry => entry.Item.OccurrenceKey)
            .ToHashSet(StringComparer.Ordinal);
        if (
            request.Any(entry =>
                retired.Contains(entry.OccurrenceId)
                || (
                    existing.TryGetValue(entry.OccurrenceId, out var item)
                    && item.CatalogTrackId != entry.TrackId
                )
            )
        )
        {
            return null;
        }

        var additions = request.Where(item => !existing.ContainsKey(item.OccurrenceId)).ToArray();
        if (additions.Length > 0)
        {
            var resolved = await handoff.ResolveTracksAsync(
                config.ServiceId,
                additions.Select(item => item.TrackId).ToArray(),
                ct
            );
            if (resolved is null)
            {
                return null;
            }

            foreach (var (entry, item) in additions.Zip(resolved))
            {
                existing.Add(
                    entry.OccurrenceId,
                    item with
                    {
                        OccurrenceId = entry.OccurrenceId,
                        SourceTrackId = entry.TrackId,
                    }
                );
            }
        }

        return request.Select(entry => existing[entry.OccurrenceId]).ToArray();
    }

    private static List<RemovedQueueTrack> RetainRemovedItems(
        QueueTrack[] current,
        QueueTrack[] replacement,
        RemovedQueueTrack[] previouslyRemoved
    )
    {
        var retained = replacement.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var removed = new List<RemovedQueueTrack>(previouslyRemoved);
        string? previousRetained = null;
        foreach (var item in current)
        {
            if (retained.Contains(item.Id))
            {
                previousRetained = item.Id;
            }
            else
            {
                removed.Add(new(item, previousRetained));
            }
        }

        return removed;
    }

    private IActionResult QueueChanged(SonosQueue queue) =>
        Conflict(new { code = "sonos_queue_changed", queue.QueueVersion });

    private static object QueueEditReceipt(SonosCommand receipt) =>
        new
        {
            commandId = receipt.Id,
            receipt.QueueVersion,
            queueLength = receipt.TargetValue,
        };

    private static PlaybackOccurrence QueueOccurrence(QueueTrack item, int index) =>
        new(
            item.Id,
            index,
            item.CatalogTrackId,
            item.Track.Name,
            item.Track.DurationMillis,
            item.Track.Artist?.Name,
            item.Track.Album?.Name,
            item.Track.ImageUrl,
            item.OccurrenceKey
        );
}
