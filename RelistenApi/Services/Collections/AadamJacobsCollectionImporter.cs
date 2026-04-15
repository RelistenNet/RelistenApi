using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hangfire.Console;
using Hangfire.Server;
using Relisten.Api.Models;
using Relisten.Vendor.ArchiveOrg;

namespace Relisten.Services.Collections;

public interface IAadamJacobsCollectionRepository
{
    Task<ArchiveCollection> EnsureAadamJacobsCollection();
    Task<DateTime> GetServerTimestamp();
    Task UpsertIndexedItem(ArchiveCollection collection, ArchiveOrgCollectionIndexItem item,
        DateTime indexRunTimestamp);
    Task<int> MarkMissingItemsRemoved(ArchiveCollection collection, DateTime indexRunTimestamp);
    Task CompleteIndexRun(ArchiveCollection collection, DateTime indexRunTimestamp, int activeItemCount);
    Task<IReadOnlyList<CollectionItem>> LoadActivePendingItems(ArchiveCollection collection);
    Task<Guid?> FindExistingSourceUuid(int artistId, string upstreamIdentifier);
    Task MarkItemLinkedToSource(ArchiveCollection collection, string upstreamIdentifier, Artist artist,
        Guid sourceUuid, ArchiveCollectionItemImportStatus status);
    Task MarkItemSkipped(ArchiveCollection collection, string upstreamIdentifier, string reason);
    Task MarkItemImportError(ArchiveCollection collection, string upstreamIdentifier, string error);
    Task WithArtistImportLock(int artistId, Func<Task> action);
    Task LinkImportedItemsForArtist(ArchiveCollection collection, int artistId);
    Task RecomputeCollectionYears(ArchiveCollection collection);
    Task CompleteImportRun(ArchiveCollection collection, DateTime importRunTimestamp);
}

public interface IAadamJacobsArchiveImporter
{
    Task<ArchiveItemImportResult> ImportSingleArchiveIdentifierForArtist(Artist artist, string identifier,
        ArchiveOrgImportContext archiveContext, PerformContext? ctx);
    Task RebuildShowsAndYears(Artist artist, PerformContext? ctx);
}

public sealed class AadamJacobsArchiveImporter : IAadamJacobsArchiveImporter
{
    private readonly Relisten.Import.ArchiveOrgImporter archiveOrgImporter;

    public AadamJacobsArchiveImporter(Relisten.Import.ArchiveOrgImporter archiveOrgImporter)
    {
        this.archiveOrgImporter = archiveOrgImporter;
    }

    public Task<ArchiveItemImportResult> ImportSingleArchiveIdentifierForArtist(Artist artist, string identifier,
        ArchiveOrgImportContext archiveContext, PerformContext? ctx)
    {
        return archiveOrgImporter.ImportSingleArchiveIdentifierForArtist(artist, identifier, archiveContext, ctx);
    }

    public async Task RebuildShowsAndYears(Artist artist, PerformContext? ctx)
    {
        ctx?.WriteLine($"Rebuilding shows for {artist.name}");
        await archiveOrgImporter.RebuildShows(artist);
        ctx?.WriteLine($"Rebuilding years for {artist.name}");
        await archiveOrgImporter.RebuildYears(artist);
    }
}

public sealed class AadamJacobsCollectionImporter
{
    private const string CollectionIdentifier = "aadamjacobs";
    private const int ScrapePageSize = 1000;
    private readonly IArchiveOrgCollectionIndexClient indexClient;
    private readonly ArchiveCollectionResolver resolver;
    private readonly IAadamJacobsCollectionRepository repository;
    private readonly IAadamJacobsArchiveImporter archiveImporter;
    private readonly TimeSpan metadataDelay;

    public AadamJacobsCollectionImporter(
        IArchiveOrgCollectionIndexClient indexClient,
        ArchiveCollectionResolver resolver,
        IAadamJacobsCollectionRepository repository,
        IAadamJacobsArchiveImporter archiveImporter)
        : this(indexClient, resolver, repository, archiveImporter, TimeSpan.FromMilliseconds(250))
    {
    }

    public AadamJacobsCollectionImporter(
        IArchiveOrgCollectionIndexClient indexClient,
        ArchiveCollectionResolver resolver,
        IAadamJacobsCollectionRepository repository,
        IAadamJacobsArchiveImporter archiveImporter,
        TimeSpan metadataDelay)
    {
        this.indexClient = indexClient;
        this.resolver = resolver;
        this.repository = repository;
        this.archiveImporter = archiveImporter;
        this.metadataDelay = metadataDelay;
    }

    public async Task<AadamJacobsCollectionImportResult> ImportAadamJacobsCollection(PerformContext? ctx = null,
        CancellationToken cancellationToken = default)
    {
        var result = new AadamJacobsCollectionImportResult();
        var collection = await repository.EnsureAadamJacobsCollection();
        var indexRunTimestamp = await repository.GetServerTimestamp();

        var scrapeItems = (await indexClient.FetchCollectionItemsAsync(CollectionIdentifier, ScrapePageSize,
                cancellationToken))
            .Where(item => !string.IsNullOrWhiteSpace(item.identifier))
            .GroupBy(item => item.identifier, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        foreach (var item in scrapeItems)
        {
            await repository.UpsertIndexedItem(collection, item, indexRunTimestamp);
        }

        result.indexed_items = scrapeItems.Count;
        result.removed_items = await repository.MarkMissingItemsRemoved(collection, indexRunTimestamp);
        await repository.CompleteIndexRun(collection, indexRunTimestamp, scrapeItems.Count);

        var activeItems = await repository.LoadActivePendingItems(collection);
        var resolvedItems = new List<ResolvedCollectionItem>();

        foreach (var item in activeItems)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var resolution = await resolver.ResolveCreatorForItem(collection, item.creator_raw ?? "",
                item.upstream_identifier);
            if (resolution.status == ArchiveCollectionArtistResolutionStatus.Skipped)
            {
                await repository.MarkItemSkipped(collection, item.upstream_identifier,
                    resolution.skip_reason ?? "skipped");
                result.skipped++;
                continue;
            }

            if (resolution.status == ArchiveCollectionArtistResolutionStatus.ImportError ||
                resolution.artist == null)
            {
                await repository.MarkItemImportError(collection, item.upstream_identifier,
                    resolution.error_message ?? "artist resolution failed");
                result.import_error++;
                continue;
            }

            resolvedItems.Add(new ResolvedCollectionItem(item, resolution.artist));
        }

        var affectedArtists = new Dictionary<int, Artist>();
        foreach (var group in resolvedItems.GroupBy(item => item.Artist.id))
        {
            var artist = group.First().Artist;
            await repository.WithArtistImportLock(artist.id, async () =>
            {
                foreach (var resolvedItem in group)
                {
                    var item = resolvedItem.Item;
                    var existingSourceUuid = await repository.FindExistingSourceUuid(artist.id, item.upstream_identifier);
                    if (existingSourceUuid.HasValue)
                    {
                        await repository.MarkItemLinkedToSource(collection, item.upstream_identifier, artist,
                            existingSourceUuid.Value, ArchiveCollectionItemImportStatus.LinkedExistingSource);
                        affectedArtists[artist.id] = artist;
                        result.linked_existing_source++;
                        continue;
                    }

                    var importResult = await archiveImporter.ImportSingleArchiveIdentifierForArtist(artist,
                        item.upstream_identifier, new ArchiveOrgImportContext(), ctx);

                    if (metadataDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(metadataDelay, cancellationToken);
                    }

                    switch (importResult.status)
                    {
                        case ArchiveCollectionItemImportStatus.LinkedExistingSource:
                        case ArchiveCollectionItemImportStatus.ImportedSource:
                            if (!importResult.source_uuid.HasValue)
                            {
                                await repository.MarkItemImportError(collection, item.upstream_identifier,
                                    "item importer did not return source_uuid");
                                result.import_error++;
                                break;
                            }

                            await repository.MarkItemLinkedToSource(collection, item.upstream_identifier, artist,
                                importResult.source_uuid.Value, importResult.status);
                            affectedArtists[artist.id] = artist;
                            if (importResult.status == ArchiveCollectionItemImportStatus.LinkedExistingSource)
                            {
                                result.linked_existing_source++;
                            }
                            else
                            {
                                result.imported_source++;
                            }

                            break;
                        case ArchiveCollectionItemImportStatus.Skipped:
                            await repository.MarkItemSkipped(collection, item.upstream_identifier,
                                importResult.skip_reason ?? "skipped");
                            result.skipped++;
                            break;
                        default:
                            await repository.MarkItemImportError(collection, item.upstream_identifier,
                                importResult.error_message ?? "import failed");
                            result.import_error++;
                            break;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (affectedArtists.ContainsKey(artist.id))
                {
                    await archiveImporter.RebuildShowsAndYears(artist, ctx);
                    await repository.LinkImportedItemsForArtist(collection, artist.id);
                }
            });
        }

        result.affected_artists = affectedArtists.Count;
        await repository.RecomputeCollectionYears(collection);
        await repository.CompleteImportRun(collection, await repository.GetServerTimestamp());

        ctx?.WriteLine(
            $"AJC import indexed={result.indexed_items}, linked={result.linked_existing_source}, imported={result.imported_source}, skipped={result.skipped}, errors={result.import_error}");

        return result;
    }

    private sealed record ResolvedCollectionItem(CollectionItem Item, Artist Artist);
}
