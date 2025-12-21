using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Relisten.Api.Models;
using Relisten.Data;
using Relisten.Import;
using Relisten.Services.Indexing;
using Relisten.Vendor.ArchiveOrg;

namespace RelistenApiTests.ArchiveOrg;

[TestFixture]
public class TestArchiveOrgArtistIndexer
{
    [Test]
    public async Task IndexArtists_ShouldCreateAndLinkArtists()
    {
        var upstreamSource = new UpstreamSource
        {
            id = 1,
            name = "archive.org"
        };

        var items = new List<ArchiveOrgCollectionIndexItem>
        {
            new()
            {
                identifier = "Guster",
                title = "Guster",
                item_count = 12
            },
            new()
            {
                identifier = "NewBand",
                title = "New Band",
                item_count = 8
            },
            new()
            {
                identifier = "TinyBand",
                title = "Tiny Band",
                item_count = 2
            }
        };

        var repository = new FakeArchiveOrgArtistIndexRepository();
        repository.SeedArtist(new Artist
        {
            id = 10,
            name = "Guster",
            slug = "guster",
            sort_name = "Guster",
            featured = (int)ArtistFeaturedFlags.None,
            musicbrainz_id = string.Empty,
            uuid = Guid.NewGuid(),
            created_at = DateTime.UtcNow,
            updated_at = DateTime.UtcNow,
            features = ArchiveOrgArtistDefaults.ArchiveOrgDefaultFeatures(),
            upstream_sources = Array.Empty<ArtistUpstreamSource>()
        });
        repository.UpstreamMappings[(1, "Guster")] = 10;

        var indexer = new ArchiveOrgArtistIndexer(
            new FakeArchiveOrgCollectionIndexClient(items),
            repository,
            new FakeUpstreamSourceLookup(upstreamSource)
        );

        var result = await indexer.IndexArtists();

        result.Created.Should().Be(1);
        result.Linked.Should().Be(0);
        result.Skipped.Should().Be(2);

        var created = repository.ArtistsBySlug["new-band"];
        created.featured.Should().Be((int)ArtistFeaturedFlags.AutoCreated);
        repository.UpstreamMappings.Should().ContainKey((1, "NewBand"));
    }

    [Test]
    public async Task IndexArtists_ShouldDisambiguateSlugCollisions()
    {
        var upstreamSource = new UpstreamSource
        {
            id = 1,
            name = "archive.org"
        };

        var items = new List<ArchiveOrgCollectionIndexItem>
        {
            new()
            {
                identifier = "NewBand",
                title = "New Band",
                item_count = 8
            }
        };

        var repository = new FakeArchiveOrgArtistIndexRepository();
        repository.SeedArtist(new Artist
        {
            id = 10,
            name = "New Band",
            slug = "new-band",
            sort_name = "New Band",
            featured = (int)ArtistFeaturedFlags.None,
            musicbrainz_id = string.Empty,
            uuid = Guid.NewGuid(),
            created_at = DateTime.UtcNow,
            updated_at = DateTime.UtcNow,
            features = ArchiveOrgArtistDefaults.ArchiveOrgDefaultFeatures(),
            upstream_sources = Array.Empty<ArtistUpstreamSource>()
        });

        var indexer = new ArchiveOrgArtistIndexer(
            new FakeArchiveOrgCollectionIndexClient(items),
            repository,
            new FakeUpstreamSourceLookup(upstreamSource)
        );

        var result = await indexer.IndexArtists();

        result.Created.Should().Be(1);
        repository.ArtistsBySlug.Should().ContainKey("new-band-2");
    }

    private sealed class FakeArchiveOrgCollectionIndexClient : IArchiveOrgCollectionIndexClient
    {
        private readonly IReadOnlyList<ArchiveOrgCollectionIndexItem> items;

        public FakeArchiveOrgCollectionIndexClient(IReadOnlyList<ArchiveOrgCollectionIndexItem> items)
        {
            this.items = items;
        }

        public Task<IReadOnlyList<ArchiveOrgCollectionIndexItem>> FetchCollectionsAsync(int count,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(items);
        }
    }

    private sealed class FakeUpstreamSourceLookup : IUpstreamSourceLookup
    {
        private readonly UpstreamSource upstreamSource;

        public FakeUpstreamSourceLookup(UpstreamSource upstreamSource)
        {
            this.upstreamSource = upstreamSource;
        }

        public Task<UpstreamSource?> FindUpstreamSourceByName(string name)
        {
            return Task.FromResult(name == upstreamSource.name ? upstreamSource : null);
        }
    }

    private sealed class FakeArchiveOrgArtistIndexRepository : IArchiveOrgArtistIndexRepository
    {
        private int nextId = 1000;
        public Dictionary<string, Artist> ArtistsBySlug { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<(int upstreamSourceId, string upstreamIdentifier), int> UpstreamMappings { get; } = new();

        public Task<Artist?> FindArtistByUpstreamIdentifier(int upstreamSourceId, string upstreamIdentifier)
        {
            return Task.FromResult(
                UpstreamMappings.TryGetValue((upstreamSourceId, upstreamIdentifier), out var artistId)
                    ? ArtistsBySlug.Values.FirstOrDefault(artist => artist.id == artistId)
                    : null
            );
        }

        public Task<SlimArtistWithFeatures> SaveArtist(SlimArtistWithFeatures artist)
        {
            var created = new Artist
            {
                id = nextId++,
                name = artist.name,
                slug = artist.slug,
                sort_name = artist.sort_name,
                featured = artist.featured,
                musicbrainz_id = artist.musicbrainz_id,
                uuid = Guid.NewGuid(),
                created_at = DateTime.UtcNow,
                updated_at = DateTime.UtcNow,
                features = artist.features,
                upstream_sources = Array.Empty<ArtistUpstreamSource>()
            };

            ArtistsBySlug[created.slug] = created;
            return Task.FromResult<SlimArtistWithFeatures>(created);
        }

        public Task<IReadOnlyList<Artist>> LoadExistingArtists()
        {
            return Task.FromResult<IReadOnlyList<Artist>>(ArtistsBySlug.Values.ToList());
        }

        public Task EnsureUpstreamSourceForArtist(int artistId, int upstreamSourceId, string upstreamIdentifier)
        {
            if (!UpstreamMappings.ContainsKey((upstreamSourceId, upstreamIdentifier)))
            {
                UpstreamMappings[(upstreamSourceId, upstreamIdentifier)] = artistId;
            }

            return Task.CompletedTask;
        }

        public void SeedArtist(Artist artist)
        {
            ArtistsBySlug[artist.slug] = artist;
        }
    }
}
