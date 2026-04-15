using FluentAssertions;
using Relisten.Api.Models;
using Relisten.Services.Collections;
using Relisten.Services.Indexing;

namespace RelistenApiTests.Collections;

[TestFixture]
public class TestArchiveCollectionResolver
{
    private static readonly ArchiveCollection AadamJacobs = new()
    {
        uuid = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        slug = "aadam-jacobs",
        upstream_identifier = "aadamjacobs",
        collection_type = "taper_archive",
        name = "Aadam Jacobs Collection"
    };

    [Test]
    public async Task BlockedMappingSkipsCreatorWithoutCreatingArtist()
    {
        var repository = new FakeArchiveCollectionResolverRepository();
        repository.Mappings[("Bad Actor", AadamJacobs.uuid)] = new CollectionArtistMapping
        {
            collection_uuid = AadamJacobs.uuid,
            creator_name = "Bad Actor",
            canonical_name = "Bad Actor",
            blocked = true,
            block_reason = "not music",
            decision_source = "manual"
        };

        var resolver = new ArchiveCollectionResolver(repository);

        var result = await resolver.ResolveCreatorForItem(AadamJacobs, "Bad Actor", "badactor2020-01-01");

        result.status.Should().Be(ArchiveCollectionArtistResolutionStatus.Skipped);
        result.skip_reason.Should().Be("not music");
        repository.CreatedArtists.Should().BeEmpty();
    }

    [Test]
    public async Task ExistingExactArtistNameIsMappedAndReused()
    {
        var repository = new FakeArchiveCollectionResolverRepository();
        var artist = FakeArtist(10, "Mekons", "mekons");
        repository.Artists.Add(artist);

        var resolver = new ArchiveCollectionResolver(repository);

        var result = await resolver.ResolveCreatorForItem(AadamJacobs, "Mekons", "mekons2020-01-01");

        result.status.Should().Be(ArchiveCollectionArtistResolutionStatus.Resolved);
        result.artist!.id.Should().Be(artist.id);
        result.decision_source.Should().Be("exact_name");
        repository.SavedMappings.Should().ContainSingle(m => m.artist_uuid == artist.uuid);
        repository.CreatedArtists.Should().BeEmpty();
    }

    [Test]
    public async Task UnknownTrustedCreatorCreatesCollectionDerivedArtist()
    {
        var repository = new FakeArchiveCollectionResolverRepository();
        var resolver = new ArchiveCollectionResolver(repository);

        var result = await resolver.ResolveCreatorForItem(AadamJacobs, "New AJC Artist", "newajcartist2020-01-01");

        result.status.Should().Be(ArchiveCollectionArtistResolutionStatus.Resolved);
        result.artist!.featured.Should().Be((int)(ArtistFeaturedFlags.AutoCreated |
                                                  ArtistFeaturedFlags.CollectionDerived));
        result.artist.slug.Should().Be("new-ajc-artist");
        result.decision_source.Should().Be("auto_created");
        repository.UpstreamOwnershipRowsCreated.Should().Be(0);
        repository.TouchedArtistIds.Should().Contain(result.artist.id);
        repository.TouchedArtistIds.Should().HaveCount(1);
    }

    [Test]
    public async Task ExistingMappingIsReusedWithoutDuplicateArtistCreation()
    {
        var repository = new FakeArchiveCollectionResolverRepository();
        var artist = FakeArtist(11, "Jon Langford", "jon-langford");
        repository.Artists.Add(artist);
        repository.Mappings[("Jon Langford", AadamJacobs.uuid)] = new CollectionArtistMapping
        {
            collection_uuid = AadamJacobs.uuid,
            creator_name = "Jon Langford",
            artist_uuid = artist.uuid,
            canonical_name = artist.name,
            decision_source = "manual"
        };

        var resolver = new ArchiveCollectionResolver(repository);

        var result = await resolver.ResolveCreatorForItem(AadamJacobs, "Jon Langford", "jonlangford2020-01-01");

        result.status.Should().Be(ArchiveCollectionArtistResolutionStatus.Resolved);
        result.artist!.id.Should().Be(artist.id);
        result.decision_source.Should().Be("manual");
        repository.CreatedArtists.Should().BeEmpty();
        repository.SavedMappings.Should().BeEmpty();
    }

    private static Artist FakeArtist(int id, string name, string slug)
    {
        return new Artist
        {
            id = id,
            name = name,
            slug = slug,
            sort_name = name,
            musicbrainz_id = "",
            featured = 0,
            uuid = Guid.NewGuid(),
            features = ArchiveOrgArtistDefaults.ArchiveOrgDefaultFeatures(),
            upstream_sources = Array.Empty<ArtistUpstreamSource>()
        };
    }

    private sealed class FakeArchiveCollectionResolverRepository : IArchiveCollectionResolverRepository
    {
        public List<Artist> Artists { get; } = new();
        public List<SlimArtistWithFeatures> CreatedArtists { get; } = new();
        public List<CollectionArtistMapping> SavedMappings { get; } = new();
        public List<int> TouchedArtistIds { get; } = new();
        public int UpstreamOwnershipRowsCreated { get; set; }
        public Dictionary<(string Creator, Guid CollectionUuid), CollectionArtistMapping> Mappings { get; } = new();

        public Task<CollectionArtistMapping?> FindMapping(Guid collectionUuid, string creatorName)
        {
            Mappings.TryGetValue((creatorName, collectionUuid), out var mapping);
            return Task.FromResult(mapping);
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
            artist.id = Artists.Count + 100;
            artist.uuid = Guid.NewGuid();
            CreatedArtists.Add(artist);
            Artists.Add(new Artist
            {
                id = artist.id,
                name = artist.name,
                slug = artist.slug,
                sort_name = artist.sort_name,
                musicbrainz_id = artist.musicbrainz_id,
                featured = artist.featured,
                uuid = artist.uuid,
                features = artist.features,
                upstream_sources = Array.Empty<ArtistUpstreamSource>()
            });

            return Task.FromResult(artist);
        }

        public Task SaveMapping(CollectionArtistMapping mapping)
        {
            SavedMappings.Add(mapping);
            Mappings[(mapping.creator_name, mapping.collection_uuid)] = mapping;
            return Task.CompletedTask;
        }

        public Task TouchApiUpdatedAt(int artistId)
        {
            TouchedArtistIds.Add(artistId);
            return Task.CompletedTask;
        }
    }
}
