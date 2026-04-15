using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Relisten.Api.Models;
using Relisten.Import;
using Relisten.Services.Indexing;

namespace Relisten.Services.Collections;

public interface IArchiveCollectionResolverRepository
{
    Task<CollectionArtistMapping?> FindMapping(Guid collectionUuid, string creatorName);
    Task<Artist?> FindArtistByUuid(Guid artistUuid);
    Task<IReadOnlyList<Artist>> FindArtistsBySourceIdentifier(string upstreamIdentifier);
    Task<Artist?> FindArtistByExactName(string name);
    Task<Artist?> FindArtistByNormalizedName(string normalizedName);
    Task<IReadOnlyList<string>> LoadArtistSlugs();
    Task<SlimArtistWithFeatures> SaveArtist(SlimArtistWithFeatures artist);
    Task SaveMapping(CollectionArtistMapping mapping);
    Task TouchApiUpdatedAt(int artistId);
}

public class ArchiveCollectionResolver
{
    private readonly IArchiveCollectionResolverRepository repository;

    public ArchiveCollectionResolver(IArchiveCollectionResolverRepository repository)
    {
        this.repository = repository;
    }

    public async Task<ArchiveCollectionArtistResolution> ResolveCreatorForItem(ArchiveCollection collection,
        string creatorName, string upstreamIdentifier)
    {
        var canonicalCreator = creatorName.Trim();
        if (string.IsNullOrWhiteSpace(canonicalCreator))
        {
            return Skipped("missing_creator");
        }

        var existingMapping = await repository.FindMapping(collection.uuid, canonicalCreator);
        if (existingMapping != null)
        {
            if (existingMapping.blocked)
            {
                return Skipped(existingMapping.block_reason ?? "blocked");
            }

            if (existingMapping.artist_uuid.HasValue)
            {
                var mappedArtist = await repository.FindArtistByUuid(existingMapping.artist_uuid.Value);
                if (mappedArtist == null)
                {
                    return ImportError($"mapped artist not found: {existingMapping.artist_uuid.Value}");
                }

                return Resolved(mappedArtist, existingMapping.decision_source);
            }
        }

        var sourceArtists = await repository.FindArtistsBySourceIdentifier(upstreamIdentifier);
        if (sourceArtists.Count == 1)
        {
            return await PersistResolution(collection, canonicalCreator, sourceArtists[0], "source_match");
        }

        var exactArtist = await repository.FindArtistByExactName(canonicalCreator);
        if (exactArtist != null)
        {
            return await PersistResolution(collection, canonicalCreator, exactArtist, "exact_name");
        }

        var normalizedArtist = await repository.FindArtistByNormalizedName(NormalizeCreatorName(canonicalCreator));
        if (normalizedArtist != null)
        {
            return await PersistResolution(collection, canonicalCreator, normalizedArtist, "normalized_name");
        }

        var created = await CreateCollectionDerivedArtist(canonicalCreator);
        return await PersistResolution(collection, canonicalCreator, created, "auto_created", touchArtist: true);
    }

    public static string NormalizeCreatorName(string value)
    {
        return Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]+", "");
    }

    private async Task<Artist> CreateCollectionDerivedArtist(string creatorName)
    {
        var existingSlugs = new HashSet<string>(await repository.LoadArtistSlugs(), StringComparer.OrdinalIgnoreCase);
        var slug = BuildUniqueSlug(creatorName, existingSlugs);
        var features = ArchiveOrgArtistDefaults.ArchiveOrgDefaultFeatures();

        var created = await repository.SaveArtist(new SlimArtistWithFeatures
        {
            id = 0,
            name = creatorName,
            slug = slug,
            sort_name = creatorName.Replace("The ", ""),
            musicbrainz_id = string.Empty,
            featured = (int)(ArtistFeaturedFlags.AutoCreated | ArtistFeaturedFlags.CollectionDerived),
            features = features
        });

        return new Artist
        {
            id = created.id,
            name = created.name,
            slug = created.slug,
            sort_name = created.sort_name,
            musicbrainz_id = created.musicbrainz_id,
            featured = created.featured,
            uuid = created.uuid,
            features = created.features,
            upstream_sources = Array.Empty<ArtistUpstreamSource>()
        };
    }

    private static string BuildUniqueSlug(string creatorName, HashSet<string> existingSlugs)
    {
        var baseSlug = SlugUtils.Slugify(creatorName);
        if (string.IsNullOrWhiteSpace(baseSlug))
        {
            baseSlug = "collection-artist";
        }

        if (existingSlugs.Add(baseSlug))
        {
            return baseSlug;
        }

        var suffix = 2;
        while (true)
        {
            var candidate = $"{baseSlug}-{suffix}";
            if (existingSlugs.Add(candidate))
            {
                return candidate;
            }

            suffix++;
        }
    }

    private async Task<ArchiveCollectionArtistResolution> PersistResolution(ArchiveCollection collection,
        string creatorName, Artist artist, string decisionSource, bool touchArtist = false)
    {
        await repository.SaveMapping(new CollectionArtistMapping
        {
            collection_uuid = collection.uuid,
            creator_name = creatorName,
            artist_uuid = artist.uuid,
            canonical_name = artist.name,
            blocked = false,
            decision_source = decisionSource
        });

        if (touchArtist)
        {
            await repository.TouchApiUpdatedAt(artist.id);
        }

        return Resolved(artist, decisionSource);
    }

    private static ArchiveCollectionArtistResolution Resolved(Artist artist, string decisionSource)
    {
        return new ArchiveCollectionArtistResolution
        {
            status = ArchiveCollectionArtistResolutionStatus.Resolved,
            artist = artist,
            decision_source = decisionSource
        };
    }

    private static ArchiveCollectionArtistResolution Skipped(string reason)
    {
        return new ArchiveCollectionArtistResolution
        {
            status = ArchiveCollectionArtistResolutionStatus.Skipped,
            skip_reason = reason
        };
    }

    private static ArchiveCollectionArtistResolution ImportError(string message)
    {
        return new ArchiveCollectionArtistResolution
        {
            status = ArchiveCollectionArtistResolutionStatus.ImportError,
            error_message = message
        };
    }
}
