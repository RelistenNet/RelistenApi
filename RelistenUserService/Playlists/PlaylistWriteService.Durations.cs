using Microsoft.EntityFrameworkCore;
using Relisten.Accounts.Contracts.Playlists;

namespace RelistenUserService.Playlists;

public sealed partial class PlaylistWriteService
{
    // These reads only avoid unnecessary Catalog requests. Ownership, receipts and revision
    // checks remain authoritative in the write transaction after this lookup finishes.
    private async Task<IReadOnlyDictionary<PlaylistEntryId, int>> ResolveCreateDurationsAsync(
        CreatePlaylistRequest request,
        CancellationToken cancellationToken
    )
    {
        var entries = request
            .Segments.SelectMany(segment => segment.Entries)
            .Where(entry => entry.TrackId is not null)
            .ToArray();
        if (
            entries.Length == 0
            || await dbContext
                .PlaylistReceipts.AsNoTracking()
                .AnyAsync(receipt => receipt.Id == request.CommandId.Value, cancellationToken)
        )
        {
            return new Dictionary<PlaylistEntryId, int>();
        }

        var durations = await trackDurations.ResolveAsync(
            entries.Select(entry => entry.TrackId),
            cancellationToken
        );
        return entries
            .Where(entry => durations.ContainsKey(entry.TrackId!))
            .ToDictionary(entry => entry.EntryId, entry => durations[entry.TrackId!]);
    }

    private async Task<IReadOnlyDictionary<string, int>> ResolveEditDurationsAsync(
        UserId actor,
        PlaylistId id,
        PlaylistBatchRequest request,
        CancellationToken cancellationToken
    )
    {
        var inserts = request
            .Operations.Select(operation => new
            {
                Id = operation.OperationId.Value,
                Tracks = InsertedEntries(operation.Payload)
                    .Select(entry => entry.TrackId)
                    .OfType<string>()
                    .ToArray(),
            })
            .Where(insert => insert.Tracks.Length > 0)
            .ToArray();
        if (
            inserts.Length == 0
            || !await dbContext
                .Playlists.AsNoTracking()
                .AnyAsync(
                    playlist => playlist.Id == id && playlist.OwnerId == actor,
                    cancellationToken
                )
        )
        {
            return new Dictionary<string, int>();
        }

        var operationIds = inserts.Select(insert => insert.Id).ToArray();
        var receipted = await dbContext
            .PlaylistReceipts.AsNoTracking()
            .Where(receipt => operationIds.Contains(receipt.Id))
            .Select(receipt => receipt.Id)
            .ToArrayAsync(cancellationToken);
        var priorOperations = receipted.ToHashSet(StringComparer.Ordinal);
        return await trackDurations.ResolveAsync(
            inserts
                .Where(insert => !priorOperations.Contains(insert.Id))
                .SelectMany(insert => insert.Tracks),
            cancellationToken
        );
    }

    private static IReadOnlyList<PlaylistEntryInput> InsertedEntries(
        PlaylistOperationPayload payload
    ) =>
        payload switch
        {
            InsertPlaylistEntries insert => insert.Entries,
            InsertPlaylistSegment insert => insert.Entries,
            _ => [],
        };
}
