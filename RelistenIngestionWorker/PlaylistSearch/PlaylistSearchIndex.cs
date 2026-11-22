using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Relisten.Accounts.Contracts.Identifiers;
using Relisten.Accounts.Contracts.Search;
using Relisten.Search;

namespace RelistenIngestionWorker.PlaylistSearch;

internal sealed class PlaylistSearchIndex(
    PlaylistSearchRows rows,
    TypesenseClient typesense,
    IConfiguration configuration
)
{
    private string Alias => ReadAliasName(configuration);

    internal static string ReadAliasName(IConfiguration config) =>
        config["Search:PlaylistAlias"] ?? PlaylistSearchJob.DefaultAlias;

    public async Task Sync(string playlistId, CancellationToken cancellationToken)
    {
        // Record before resolving the alias so a sync to the old collection is replayed.
        await rows.RecordSyncRequest(playlistId, cancellationToken);
        await SyncCurrent(playlistId, cancellationToken);
    }

    private Task SyncCurrent(string playlistId, CancellationToken cancellationToken) =>
        rows.WithPlaylistWriteLock(
            playlistId,
            () => WriteCurrentPlaylist(playlistId, cancellationToken),
            cancellationToken
        );

    private async Task WriteCurrentPlaylist(string playlistId, CancellationToken cancellationToken)
    {
        var target = await typesense.ReadAlias(Alias, cancellationToken);
        if (target is null)
        {
            // The initial full build will publish the first alias.
            return;
        }

        var documents = await rows.Read(playlistId, "", cancellationToken);
        if (documents.Length == 0)
        {
            await typesense.DeleteDocument(target, playlistId, cancellationToken);
        }
        else
        {
            await Import(target, documents, true, cancellationToken);
        }
    }

    public async Task<DateTime> Rebuild(CancellationToken cancellationToken)
    {
        var startedAt = await rows.RebuildStartedAt(cancellationToken);
        await Sweep(cancellationToken);
        var collection = $"{Alias}_{Guid.NewGuid():N}";
        await typesense.CreateCollection(Schema(collection), cancellationToken);

        var after = "";
        while (true)
        {
            var documents = await rows.Read(null, after, cancellationToken);
            if (documents.Length == 0)
            {
                break;
            }
            await Import(collection, documents, false, cancellationToken);
            after = documents[^1].Id;
        }

        await typesense.SetAlias(Alias, collection, cancellationToken);
        return startedAt;
    }

    public async Task CatchUp(DateTime startedAt, CancellationToken cancellationToken)
    {
        foreach (
            var playlistId in await rows.PruneAndReadSyncRequests(startedAt, cancellationToken)
        )
        {
            await SyncCurrent(playlistId, cancellationToken);
        }

        // Retire the old collection after the new alias has caught up.
        await Sweep(cancellationToken);
    }

    public static async Task Purge(
        TypesenseClient typesense,
        string alias,
        string ownerId,
        CancellationToken cancellationToken
    )
    {
        if (!UserId.TryParse(ownerId, out _))
        {
            return;
        }

        foreach (var collection in await typesense.ReadCollectionNames(cancellationToken))
        {
            if (IsGeneration(alias, collection))
            {
                await typesense.DeleteDocumentsMatching(
                    collection,
                    $"owner_id:={ownerId}",
                    cancellationToken
                );
            }
        }
    }

    private async Task Sweep(CancellationToken cancellationToken)
    {
        var active = await typesense.ReadAlias(Alias, cancellationToken);
        foreach (var collection in await typesense.ReadCollectionNames(cancellationToken))
        {
            if (collection != active && IsGeneration(Alias, collection))
            {
                await typesense.DeleteCollection(collection, cancellationToken);
            }
        }
    }

    private static bool IsGeneration(string alias, string name) =>
        name.StartsWith(alias + "_", StringComparison.Ordinal)
        && Guid.TryParseExact(name[(alias.Length + 1)..], "N", out _);

    private async Task Import(
        string collection,
        PlaylistSearchDocument[] documents,
        bool upsert,
        CancellationToken cancellationToken
    )
    {
        if (documents.Length == 0)
        {
            return;
        }

        var jsonl = string.Join(
            '\n',
            documents.Select(document => JsonSerializer.Serialize(document, TypesenseClient.Json))
        );
        var result = await typesense.Import(collection, jsonl, upsert, cancellationToken);
        if (result.Accepted != documents.Length || result.Rejected != 0)
        {
            // Import errors can contain the original document, including private notes.
            throw new InvalidOperationException(
                $"Typesense accepted {result.Accepted} of {documents.Length} playlist documents."
            );
        }

        // Purge can commit after we read a playlist but before Typesense accepted it.
        // Check after the write: either we see the missing owner or a later purge removes it.
        var owners = await rows.ExistingOwners(
            documents.Select(document => document.OwnerId).Distinct().ToArray(),
            cancellationToken
        );
        await typesense.DeleteDocuments(
            collection,
            documents
                .Where(document => !owners.Contains(document.OwnerId))
                .Select(document => document.Id),
            cancellationToken
        );
    }

    private static object Schema(string name) =>
        new
        {
            name,
            fields = new object[]
            {
                new { name = "name", type = "string" },
                new { name = "description", type = "string" },
                new { name = "entry_notes", type = "string[]" },
                new { name = "guide_text", type = "string[]" },
                new { name = "artists", type = "string[]" },
                new { name = "artist_ids", type = "string[]" },
                new { name = "songs", type = "string[]" },
                new { name = "curator", type = "string" },
                new { name = "owner_id", type = "string" },
                new { name = "published", type = "bool" },
                new { name = "archived", type = "bool" },
                new { name = "taken_down", type = "bool" },
                new { name = "follow_count", type = "int64" },
                new { name = "updated_at", type = "int64" },
            },
        };
}
