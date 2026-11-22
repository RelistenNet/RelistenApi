using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Relisten.Accounts.Contracts.Playlists;
using RelistenUserService.Playlists.Entities;

namespace RelistenUserService.Playlists;

public sealed partial class PlaylistWriteService
{
    /// <summary>
    /// Sets the owner's desired publication state. The command is idempotent by value, so a
    /// retry needs no receipt: repeating it reports <c>changed: false</c>. The first publish
    /// mints the public code, which later publishes reuse.
    /// </summary>
    public async Task<PlaylistPublication> SetPublicationAsync(
        UserId actor,
        PlaylistId id,
        SetPlaylistPublicationRequest request,
        CancellationToken cancellationToken
    )
    {
        PlaylistValidation.Validate(request);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            cancellationToken
        );
        var state = await LockActorLibraryAsync(actor, [], [], cancellationToken);
        var playlist = await LockOwnedPlaylistAsync(dbContext, actor, id, cancellationToken);
        var generation = await GenerationAsync(cancellationToken);

        if (request.Published)
        {
            RequirePublishable(playlist);
        }

        var changed = playlist.Published != request.Published;
        if (changed)
        {
            var now = timeProvider.GetUtcNow();
            playlist.Published = request.Published;
            playlist.PublicCode ??= RandomNumberGenerator.GetHexString(32, lowercase: true);
            // Publication is an owner edit, so it moves the playlist up the owner's list.
            playlist.UpdatedAt = now;
            stateStore.Append(state, SummaryChange(playlist, now));
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        if (search is not null && changed)
        {
            await search.Changed(id);
        }
        return new(
            1,
            generation,
            id,
            playlist.Published,
            playlist.Published ? playlist.PublicCode : null,
            changed
        );
    }

    private static void RequirePublishable(Playlist playlist)
    {
        if (playlist.TakenDown)
        {
            throw new PlaylistException(409, "playlist_taken_down", "This playlist is taken down.");
        }

        if (playlist.Archived)
        {
            throw new PlaylistException(
                409,
                "playlist_archived",
                "Unarchive the playlist before publishing it."
            );
        }
    }

    public async Task<(PlaylistCreated Response, bool Replayed)> CopyAsync(
        UserId actor,
        string publicCode,
        CopyPublicPlaylistRequest request,
        CancellationToken cancellationToken
    )
    {
        PlaylistValidation.ValidateCommand(request.ContractVersion, request.CommandId);
        if (!PlaylistVisibility.IsCode(publicCode))
        {
            throw PlaylistVisibility.NotFound();
        }

        var hash = PlaylistHasher.Hash(publicCode, request);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            cancellationToken
        );
        var state = await LockActorLibraryAsync(
            actor,
            [request.CommandId.Value],
            [],
            cancellationToken
        );
        var generation = await GenerationAsync(cancellationToken);

        // Store the receipt against the private copy. A takedown or purge of the
        // source cannot invalidate a successful copy or keep a retry from finding it.
        var receipt = await dbContext.PlaylistReceipts.FindAsync(
            [request.CommandId.Value],
            cancellationToken
        );
        if (receipt is not null)
        {
            CheckReceipt(receipt, actor, receipt.PlaylistId, hash);
            await transaction.CommitAsync(cancellationToken);
            return (new(1, generation, receipt.PlaylistId, receipt.Revision!.Value), true);
        }

        var source = await PlaylistVisibility.LockPublicSourceAsync(
            dbContext,
            publicCode,
            cancellationToken
        );
        var structure = await PlaylistReadService.ReadStructureAsync(
            dbContext,
            source.Id,
            cancellationToken
        );
        var copy = PlaylistCopy.CreateRequest(request, source, structure);
        var count = await CheckNewPlaylistQuotaAsync(actor, copy, cancellationToken);
        var created = AddPlaylist(actor, state, copy, "copy", hash, count);
        await dbContext.SaveChangesAsync(cancellationToken);
        await PlaylistCatalogIndex.RefreshAsync(dbContext, created.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (search is not null)
        {
            await search.Changed(created.Id);
        }
        return (new(1, generation, created.Id, created.Revision), false);
    }
}
