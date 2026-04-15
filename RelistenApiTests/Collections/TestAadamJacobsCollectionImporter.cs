using FluentAssertions;
using Hangfire.Server;
using Relisten.Api.Models;
using Relisten.Services.Collections;
using Relisten.Services.Indexing;
using Relisten.Vendor.ArchiveOrg;

namespace RelistenApiTests.Collections;

[TestFixture]
public class TestAadamJacobsCollectionImporter
{
    [Test]
    public async Task ImportLinksExistingSourcesWithoutCallingMetadataImporter()
    {
        var repository = new FakeAadamJacobsRepository();
        var artist = FakeArtist(1, "Existing Artist");
        repository.Artists.Add(artist);
        repository.ExistingSources[("ajcitem1", artist.id)] = Guid.NewGuid();

        var archiveImporter = new FakeArchiveImporter();
        var importer = CreateImporter(repository, archiveImporter, [
            new ArchiveOrgCollectionIndexItem
            {
                identifier = "ajcitem1",
                title = "Existing item",
                creator = artist.name,
                date = "2020-01-01",
                year = 2020
            }
        ]);

        var result = await importer.ImportAadamJacobsCollection();

        result.linked_existing_source.Should().Be(1);
        archiveImporter.ImportedIdentifiers.Should().BeEmpty();
        repository.LinkedItems.Should().ContainSingle(i => i.Status == ArchiveCollectionItemImportStatus.LinkedExistingSource);
        repository.RebuiltArtistIds.Should().Contain(artist.id);
        repository.RebuiltArtistIds.Should().HaveCount(1);
        repository.CollectionYearsRebuilt.Should().Be(1);
    }

    [Test]
    public async Task ImportCallsItemImporterForMissingResolvedSource()
    {
        var repository = new FakeAadamJacobsRepository();
        var artist = FakeArtist(2, "New Import Artist");
        repository.Artists.Add(artist);

        var archiveImporter = new FakeArchiveImporter();
        archiveImporter.Results["ajcitem2"] = new ArchiveItemImportResult
        {
            status = ArchiveCollectionItemImportStatus.ImportedSource,
            source_uuid = Guid.NewGuid()
        };

        var importer = CreateImporter(repository, archiveImporter, [
            new ArchiveOrgCollectionIndexItem
            {
                identifier = "ajcitem2",
                title = "Missing item",
                creator = artist.name,
                date = "2021-02-03",
                year = 2021
            }
        ]);

        var result = await importer.ImportAadamJacobsCollection();

        result.imported_source.Should().Be(1);
        archiveImporter.ImportedIdentifiers.Should().ContainSingle("ajcitem2");
        repository.LinkedItems.Should().ContainSingle(i => i.Status == ArchiveCollectionItemImportStatus.ImportedSource);
        repository.RebuiltArtistIds.Should().Contain(artist.id);
        repository.RebuiltArtistIds.Should().HaveCount(1);
    }

    private static AadamJacobsCollectionImporter CreateImporter(FakeAadamJacobsRepository repository,
        FakeArchiveImporter archiveImporter, IReadOnlyList<ArchiveOrgCollectionIndexItem> items)
    {
        return new AadamJacobsCollectionImporter(
            new FakeCollectionIndexClient(items),
            new ArchiveCollectionResolver(repository),
            repository,
            archiveImporter,
            TimeSpan.Zero);
    }

    private static Artist FakeArtist(int id, string name)
    {
        return new Artist
        {
            id = id,
            name = name,
            slug = name.ToLowerInvariant().Replace(" ", "-"),
            sort_name = name,
            musicbrainz_id = "",
            featured = 0,
            uuid = Guid.NewGuid(),
            features = ArchiveOrgArtistDefaults.ArchiveOrgDefaultFeatures(),
            upstream_sources = Array.Empty<ArtistUpstreamSource>()
        };
    }

    private sealed class FakeCollectionIndexClient : IArchiveOrgCollectionIndexClient
    {
        private readonly IReadOnlyList<ArchiveOrgCollectionIndexItem> items;

        public FakeCollectionIndexClient(IReadOnlyList<ArchiveOrgCollectionIndexItem> items)
        {
            this.items = items;
        }

        public Task<IReadOnlyList<ArchiveOrgCollectionIndexItem>> FetchCollectionsAsync(int count,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<ArchiveOrgCollectionIndexItem>>([]);
        }

        public Task<IReadOnlyList<ArchiveOrgCollectionIndexItem>> FetchCollectionItemsAsync(string collectionIdentifier,
            int count, CancellationToken cancellationToken)
        {
            collectionIdentifier.Should().Be("aadamjacobs");
            return Task.FromResult(items);
        }
    }

    private sealed class FakeArchiveImporter : IAadamJacobsArchiveImporter
    {
        public Dictionary<string, ArchiveItemImportResult> Results { get; } = new();
        public List<string> ImportedIdentifiers { get; } = new();

        public Task<ArchiveItemImportResult> ImportSingleArchiveIdentifierForArtist(Artist artist, string identifier,
            ArchiveOrgImportContext archiveContext, PerformContext? ctx)
        {
            ImportedIdentifiers.Add(identifier);
            return Task.FromResult(Results[identifier]);
        }

        public Task RebuildShowsAndYears(Artist artist, PerformContext? ctx)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAadamJacobsRepository : IAadamJacobsCollectionRepository,
        IArchiveCollectionResolverRepository
    {
        public ArchiveCollection Collection { get; } = new()
        {
            uuid = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            slug = "aadam-jacobs",
            upstream_identifier = "aadamjacobs",
            name = "Aadam Jacobs Collection"
        };

        public List<Artist> Artists { get; } = new();
        public List<CollectionItem> IndexedItems { get; } = new();
        public Dictionary<(string Identifier, int ArtistId), Guid> ExistingSources { get; } = new();
        public List<(string Identifier, ArchiveCollectionItemImportStatus Status)> LinkedItems { get; } = new();
        public List<int> RebuiltArtistIds { get; } = new();
        public int CollectionYearsRebuilt { get; private set; }

        public Task<ArchiveCollection> EnsureAadamJacobsCollection()
        {
            return Task.FromResult(Collection);
        }

        public Task<DateTime> GetServerTimestamp()
        {
            return Task.FromResult(new DateTime(2026, 4, 15, 12, 0, 0, DateTimeKind.Utc));
        }

        public Task UpsertIndexedItem(ArchiveCollection collection, ArchiveOrgCollectionIndexItem item,
            DateTime indexRunTimestamp)
        {
            IndexedItems.Add(new CollectionItem
            {
                collection_uuid = collection.uuid,
                upstream_identifier = item.identifier,
                title = item.title,
                creator_raw = item.creator,
                date_raw = item.date,
                display_date = item.date,
                year = item.year,
                import_status = ArchiveCollectionItemImportStatus.Pending,
                last_seen_at = indexRunTimestamp
            });

            return Task.CompletedTask;
        }

        public Task<int> MarkMissingItemsRemoved(ArchiveCollection collection, DateTime indexRunTimestamp)
        {
            return Task.FromResult(0);
        }

        public Task CompleteIndexRun(ArchiveCollection collection, DateTime indexRunTimestamp, int activeItemCount)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CollectionItem>> LoadActivePendingItems(ArchiveCollection collection)
        {
            return Task.FromResult<IReadOnlyList<CollectionItem>>(IndexedItems);
        }

        public Task<Guid?> FindExistingSourceUuid(int artistId, string upstreamIdentifier)
        {
            ExistingSources.TryGetValue((upstreamIdentifier, artistId), out var sourceUuid);
            return Task.FromResult(sourceUuid == Guid.Empty ? (Guid?)null : sourceUuid);
        }

        public Task MarkItemLinkedToSource(ArchiveCollection collection, string upstreamIdentifier, Artist artist,
            Guid sourceUuid, ArchiveCollectionItemImportStatus status)
        {
            LinkedItems.Add((upstreamIdentifier, status));
            return Task.CompletedTask;
        }

        public Task MarkItemSkipped(ArchiveCollection collection, string upstreamIdentifier, string reason)
        {
            LinkedItems.Add((upstreamIdentifier, ArchiveCollectionItemImportStatus.Skipped));
            return Task.CompletedTask;
        }

        public Task MarkItemImportError(ArchiveCollection collection, string upstreamIdentifier, string error)
        {
            LinkedItems.Add((upstreamIdentifier, ArchiveCollectionItemImportStatus.ImportError));
            return Task.CompletedTask;
        }

        public Task WithArtistImportLock(int artistId, Func<Task> action)
        {
            return action();
        }

        public Task LinkImportedItemsForArtist(ArchiveCollection collection, int artistId)
        {
            RebuiltArtistIds.Add(artistId);
            return Task.CompletedTask;
        }

        public Task RecomputeCollectionYears(ArchiveCollection collection)
        {
            CollectionYearsRebuilt++;
            return Task.CompletedTask;
        }

        public Task CompleteImportRun(ArchiveCollection collection, DateTime importRunTimestamp)
        {
            return Task.CompletedTask;
        }

        public Task<CollectionArtistMapping?> FindMapping(Guid collectionUuid, string creatorName)
        {
            return Task.FromResult<CollectionArtistMapping?>(null);
        }

        public Task<Artist?> FindArtistByUuid(Guid artistUuid)
        {
            return Task.FromResult(Artists.FirstOrDefault(a => a.uuid == artistUuid));
        }

        public Task<IReadOnlyList<Artist>> FindArtistsBySourceIdentifier(string upstreamIdentifier)
        {
            return Task.FromResult<IReadOnlyList<Artist>>([]);
        }

        public Task<Artist?> FindArtistByExactName(string name)
        {
            return Task.FromResult(Artists.FirstOrDefault(a => a.name == name));
        }

        public Task<Artist?> FindArtistByNormalizedName(string normalizedName)
        {
            return Task.FromResult(Artists.FirstOrDefault(a =>
                ArchiveCollectionResolver.NormalizeCreatorName(a.name) == normalizedName));
        }

        public Task<IReadOnlyList<string>> LoadArtistSlugs()
        {
            return Task.FromResult<IReadOnlyList<string>>(Artists.Select(a => a.slug).ToList());
        }

        public Task<SlimArtistWithFeatures> SaveArtist(SlimArtistWithFeatures artist)
        {
            throw new NotImplementedException();
        }

        public Task SaveMapping(CollectionArtistMapping mapping)
        {
            return Task.CompletedTask;
        }

        public Task TouchApiUpdatedAt(int artistId)
        {
            return Task.CompletedTask;
        }
    }
}
