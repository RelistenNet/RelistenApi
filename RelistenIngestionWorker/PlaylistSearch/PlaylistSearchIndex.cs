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
            var previous = await typesense.ReadDocument(target, playlistId, cancellationToken);
            await Import(
                target,
                documents
                    .Select(document =>
                        document with
                        {
                            PopularityPercentile =
                                document.PlaysYear > 0 ? previous?.PopularityPercentile ?? 0 : 0,
                        }
                    )
                    .ToArray(),
                true,
                cancellationToken
            );
        }
    }

    public async Task<DateTime> Rebuild(CancellationToken cancellationToken)
    {
        var startedAt = await rows.RebuildStartedAt(cancellationToken);
        await Sweep(cancellationToken);
        var collection = $"{Alias}_{Guid.NewGuid():N}";
        await typesense.CreateCollection(Schema(collection), cancellationToken);

        var percentiles = new SearchPercentiles();
        var after = "";
        while (true)
        {
            var documents = await rows.Read(null, after, cancellationToken);
            if (documents.Length == 0)
            {
                break;
            }
            foreach (var document in documents)
            {
                percentiles.Observe("playlist", document.PlaysYear);
            }
            await Import(collection, documents, false, cancellationToken);
            after = documents[^1].Id;
        }

        if (percentiles.Calculate().TryGetValue("playlist", out var ranks))
        {
            var updates = typesense
                .ExportDocuments(collection, "", cancellationToken)
                .Where(document => document.PlaysYear > 0);
            await foreach (var batch in updates.Chunk(1000).WithCancellation(cancellationToken))
            {
                var json = string.Join(
                    '\n',
                    batch.Select(document =>
                        JsonSerializer.Serialize(
                            new
                            {
                                id = document.Id,
                                popularity_percentile = ranks[document.PlaysYear],
                            }
                        )
                    )
                );
                var written = await typesense.Update(collection, json, cancellationToken);
                if (written.Accepted != batch.Length || written.Rejected != 0)
                {
                    throw new InvalidOperationException(
                        "Could not write playlist popularity percentiles."
                    );
                }
            }
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
                new { name = "plays_year", type = "int64" },
                new
                {
                    name = "popularity_percentile",
                    type = "float",
                    index = false,
                },
                new { name = "updated_at", type = "int64" },
            },
        };
}
