using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Relisten.Accounts.Contracts.Playlists;

namespace RelistenUserService.Playlists;

public sealed partial class PlaylistWriteService
{
    public async Task<PlaylistPublication> SetPublicationAsync(
        UserId actor,
        PlaylistId id,
        SetPlaylistPublicationRequest request,
        CancellationToken cancellationToken
    )
    {
        PlaylistValidation.ValidateCommand(request.ContractVersion, request.CommandId);
        var hash = PlaylistHasher.Hash(id, request);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            cancellationToken
        );
        var state = await LockActorLibraryAsync(
            actor,
            [request.CommandId.Value],
            [],
            cancellationToken
        );
        var playlist = await LockOwnedPlaylistAsync(actor, id, cancellationToken);
        var generation = await GenerationAsync(cancellationToken);
        var receipt = await dbContext.PlaylistReceipts.FindAsync(
            [request.CommandId.Value],
            cancellationToken
        );
        if (receipt is not null)
        {
            CheckReceipt(receipt, actor, id, hash);
            await transaction.CommitAsync(cancellationToken);
            return new(
                1,
                generation,
                id,
                request.Published,
                request.Published ? playlist.PublicCode : null,
                receipt.Changed
            );
        }

        if (request.Published)
        {
            if (playlist.TakenDown || playlist.Archived)
            {
                throw playlist.TakenDown
                    ? new PlaylistException(
                        409,
                        "playlist_taken_down",
                        "This playlist is taken down."
                    )
                    : new PlaylistException(
                        409,
                        "playlist_archived",
                        "Unarchive the playlist before publishing it."
                    );
            }

            playlist.PublicCode ??= RandomNumberGenerator.GetHexString(32, lowercase: true);
        }

        var changed = playlist.Published != request.Published;
        var now = timeProvider.GetUtcNow();
        if (changed)
        {
            playlist.Published = request.Published;
            playlist.UpdatedAt = now;
            AppendLibrarySummary(
                state,
                playlist,
                await CountTracksAsync(id, cancellationToken),
                now
            );
        }

        dbContext.PlaylistReceipts.Add(
            new()
            {
                Id = request.CommandId.Value,
                ActorId = actor,
                PlaylistId = id,
                Kind = "publication",
                PayloadHash = hash,
                Changed = changed,
                CreatedAt = now,
            }
        );
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(
            1,
            generation,
            id,
            request.Published,
            request.Published ? playlist.PublicCode : null,
            changed
        );
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
        var created = await AddPlaylistAsync(actor, state, copy, "copy", hash, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (new(1, generation, created.Id, created.Revision), false);
    }

    internal async Task<bool> SetTakedownAsync(
        PlaylistId id,
        bool takenDown,
        CancellationToken cancellationToken
    )
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            cancellationToken
        );
        var owner = await dbContext
            .Playlists.Where(row => row.Id == id)
            .Select(row => (UserId?)row.OwnerId)
            .SingleOrDefaultAsync(cancellationToken);
        if (owner is null)
        {
            throw PlaylistVisibility.NotFound();
        }

        // Serialize with owner edits and share commands. The operator can moderate
        // any existing playlist, including one owned by a disabled account.
        if (
            !await PlaylistAccountLock.AcquireAsync(
                dbContext,
                owner.Value,
                requireActive: false,
                cancellationToken
            )
        )
        {
            throw PlaylistVisibility.NotFound();
        }

        var now = timeProvider.GetUtcNow();
        var state = await stateStore.GetOrCreateForUpdateAsync(owner.Value, now, cancellationToken);
        var playlist = await LockOwnedPlaylistAsync(owner.Value, id, cancellationToken);
        if (playlist.TakenDown == takenDown)
        {
            return false;
        }

        playlist.TakenDown = takenDown;
        playlist.UpdatedAt = now;
        AppendLibrarySummary(state, playlist, await CountTracksAsync(id, cancellationToken), now);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
