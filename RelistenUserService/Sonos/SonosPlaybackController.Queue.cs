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
                occurrences = items.Select(
                    (item, index) =>
                        QueueOccurrence(item, index) with
                        {
                            PlaylistId = item.PlaylistId,
                            PlaylistEntryId = item.PlaylistEntryId,
                        }
                ),
            }
        );
    }

    [HttpPut("{handle:guid}/queue")]
    [RequestSizeLimit(2 * 1024 * 1024)]
    public async Task<IActionResult> ReplaceQueue(
        Guid handle,
        SonosQueueEditRequest request,
        [FromServices] SonosHandoff handoff,
        [FromServices] SonosWorkflowStarter workflows,
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

        try
        {
            var (edit, response) = await PrepareQueueEditAsync(
                original,
                request,
                config,
                handoff,
                ct
            );
            return edit is null
                ? response!
                : await ApplyQueueEditAsync(edit, request, workflows, ct);
        }
        catch (Exception error) when (Unavailable(error, ct))
        {
            return Problem(statusCode: 502, title: "Could not validate the Sonos queue edit.");
        }
    }

    private sealed record PreparedQueueEdit(
        SonosQueue Original,
        string Hash,
        SonosAccess Access,
        QueueTrack[] Current,
        QueueTrack[] Replacement,
        RemovedQueueTrack[] Removed
    );

    private async Task<(PreparedQueueEdit? Edit, IActionResult? Response)> PrepareQueueEditAsync(
        SonosQueue original,
        SonosQueueEditRequest request,
        SonosConfiguration config,
        SonosHandoff handoff,
        CancellationToken ct
    )
    {
        var hash = SonosProtocol.Hash(JsonSerializer.Serialize(request));
        if (await FindCommandAsync(original.Id, request.CommandId, ct) is { } replay)
        {
            return (null, ReadQueueEditReplay(replay, hash));
        }
        if (request.ExpectedVersion != original.QueueVersion)
        {
            return (null, QueueChanged(original));
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
            return (
                null,
                Problem(
                    statusCode: 422,
                    title: "A Sonos occurrence changed recording, reused a removed ID, or is unavailable."
                )
            );
        }

        var access = await link.AccessAsync(config, original.UserId, ct);
        return access is null
            ? (null, ReconnectRequired())
            : (new(original, hash, access, current, replacement, removed), null);
    }

    private async Task<IActionResult> ApplyQueueEditAsync(
        PreparedQueueEdit edit,
        SonosQueueEditRequest request,
        SonosWorkflowStarter workflows,
        CancellationToken ct
    )
    {
        var handle = edit.Original.Id;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockRoomAsync(edit.Original, ct);
        if (await FindCommandAsync(handle, request.CommandId, ct) is { } prior)
        {
            return ReadQueueEditReplay(prior, edit.Hash);
        }
        if (await ReadCommandQueueAsync(handle, edit.Access, ct) is not { } queue)
        {
            return PlaybackInactive();
        }
        if (request.ExpectedVersion != queue.QueueVersion)
        {
            return QueueChanged(queue);
        }

        var check = await CheckQueueEditPlayheadAsync(
            queue,
            edit.Access.Token,
            request.ExpectedItemId,
            edit.Current,
            edit.Replacement,
            ct
        );
        if (check.Playing is not { } playing)
        {
            // Keep any eviction found by reconciliation even though the edit was rejected.
            if (check.PersistInactiveState)
            {
                await transaction.CommitAsync(ct);
            }
            return check.Rejection!;
        }

        var (receipt, failure) = await SaveQueueEditAsync(
            queue,
            edit,
            request.CommandId,
            playing.ItemId,
            ct
        );
        if (receipt is null)
        {
            return failure == QueueSaveFailure.HistoryFull
                ? Conflict(new { code = "sonos_queue_history_full" })
                : PlaybackInactive();
        }

        // The worker takes the same locks, so it reads committed edits only.
        if (
            receipt.QueueVersion != queue.QueueVersion
            && !await workflows.TrySignalQueueAsync(handle, ct)
        )
        {
            // Rollback discards the edit and its receipt. Retrying the same command ID reruns the edit.
            return Problem(
                statusCode: 503,
                title: "Could not schedule the Sonos queue refresh. Retry the edit."
            );
        }

        await transaction.CommitAsync(ct);
        return Ok(QueueEditReceipt(receipt));
    }

    private sealed record QueueEditCheck(
        PlaybackOccurrence? Playing,
        IActionResult? Rejection = null,
        bool PersistInactiveState = false
    );

    private async Task<QueueEditCheck> CheckQueueEditPlayheadAsync(
        SonosQueue queue,
        string token,
        string expectedItemId,
        QueueTrack[] current,
        QueueTrack[] replacement,
        CancellationToken ct
    )
    {
        // Check the room after catalog validation. The playhead can advance without changing
        // the queue revision. Sonos can still advance after this read, before fetching the edit.
        var room = await ReadRoomAsync(token, queue.GroupId, ct);
        var playback = await ReconcileAsync(queue, room, ct);
        if (playback.Status != "active" || playback.CurrentOccurrence is not { } playing)
        {
            return new(null, PlaybackInactive(), PersistInactiveState: true);
        }

        if (playing.ItemId != expectedItemId)
        {
            return new(null, Conflict(new { code = "sonos_playback_changed" }));
        }

        var protectedLength = playing.QueueIndex + 1;
        if (!current.Take(protectedLength).SequenceEqual(replacement.Take(protectedLength)))
        {
            return new(null, Conflict(new { code = "sonos_queue_current_protected" }));
        }

        return new(playing);
    }

    private enum QueueSaveFailure
    {
        Inactive,
        HistoryFull,
    }

    private async Task<(SonosCommand? Receipt, QueueSaveFailure? Failure)> SaveQueueEditAsync(
        SonosQueue queue,
        PreparedQueueEdit edit,
        Guid commandId,
        string itemId,
        CancellationToken ct
    )
    {
        var (current, replacement) = (edit.Current, edit.Replacement);
        var version = queue.QueueVersion;
        if (!current.SequenceEqual(replacement))
        {
            var tombstones = RetainRemovedItems(current, replacement, edit.Removed);
            if (tombstones.Count > 15_000)
            {
                return (null, QueueSaveFailure.HistoryFull);
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
                return (null, QueueSaveFailure.Inactive);
            }
        }

        var receipt = new SonosCommand
        {
            QueueId = queue.Id,
            Id = commandId,
            RequestHash = edit.Hash,
            Action = "queue",
            Status = "accepted",
            ExpectedItemId = itemId,
            TargetValue = replacement.Length,
            QueueVersion = version,
        };
        db.Add(receipt);
        await db.SaveChangesAsync(ct);
        return (receipt, null);
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
                existing.Add(entry.OccurrenceId, entry.Bind(item));
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
