using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hangfire.Console;
using Hangfire.Server;
using Relisten.Api.Models;
using Relisten.Data;
using Relisten.Import;
using Relisten.Vendor.ArchiveOrg;

namespace Relisten.Services.Indexing;

public class ArchiveOrgArtistIndexResult
{
    public int Created { get; set; }
    public int Linked { get; set; }
    public int Skipped { get; set; }
    public int SkippedBelowThreshold { get; set; }
    public int SkippedExisting { get; set; }
    public int SkippedMissingIdentifier { get; set; }
    public int SkippedInvalidSlug { get; set; }
}

public interface IArchiveOrgArtistIndexRepository
{
    Task<Artist?> FindArtistByUpstreamIdentifier(int upstreamSourceId, string upstreamIdentifier);
    Task<IReadOnlyList<Artist>> LoadExistingArtists();
    Task<SlimArtistWithFeatures> SaveArtist(SlimArtistWithFeatures artist);
    Task EnsureUpstreamSourceForArtist(int artistId, int upstreamSourceId, string upstreamIdentifier);
}

public class ArchiveOrgArtistIndexRepository : IArchiveOrgArtistIndexRepository
{
    private readonly ArtistService artistService;
    private readonly UpstreamSourceService upstreamSourceService;

    public ArchiveOrgArtistIndexRepository(ArtistService artistService, UpstreamSourceService upstreamSourceService)
    {
        this.artistService = artistService;
        this.upstreamSourceService = upstreamSourceService;
    }

    public Task<Artist?> FindArtistByUpstreamIdentifier(int upstreamSourceId, string upstreamIdentifier)
    {
        return artistService.FindArtistByUpstreamIdentifier(upstreamSourceId, upstreamIdentifier);
    }

    public async Task<IReadOnlyList<Artist>> LoadExistingArtists()
    {
        var artists = await artistService.All();
        return artists.ToList();
    }

    public Task<SlimArtistWithFeatures> SaveArtist(SlimArtistWithFeatures artist)
    {
        return artistService.Save(artist);
    }

    public Task EnsureUpstreamSourceForArtist(int artistId, int upstreamSourceId, string upstreamIdentifier)
    {
        return upstreamSourceService.EnsureUpstreamSourceForArtist(artistId, upstreamSourceId, upstreamIdentifier);
    }
}

public static class ArchiveOrgArtistDefaults
{
    public static Features ArchiveOrgDefaultFeatures()
    {
        return new Features
        {
            descriptions = true,
            eras = false,
            multiple_sources = true,
            reviews = true,
            ratings = true,
            tours = false,
            taper_notes = true,
            source_information = true,
            sets = false,
            per_show_venues = false,
            per_source_venues = true,
            venue_coords = false,
            songs = false,
            years = true,
            track_md5s = true,
            review_titles = true,
            jam_charts = false,
            setlist_data_incomplete = false,
            track_names = true,
            venue_past_names = false,
            reviews_have_ratings = true,
            track_durations = true,
            can_have_flac = true
        };
    }
}

public class ArchiveOrgArtistIndexer
{
    private const int DefaultItemCountThreshold = 5;
    private const int DefaultScrapeCount = 10000;
    private readonly IArchiveOrgCollectionIndexClient indexClient;
    private readonly IArchiveOrgArtistIndexRepository repository;
    private readonly IUpstreamSourceLookup upstreamSourceLookup;

    public ArchiveOrgArtistIndexer(
        IArchiveOrgCollectionIndexClient indexClient,
        IArchiveOrgArtistIndexRepository repository,
        IUpstreamSourceLookup upstreamSourceLookup
    )
    {
        this.indexClient = indexClient;
        this.repository = repository;
        this.upstreamSourceLookup = upstreamSourceLookup;
    }

    public async Task<ArchiveOrgArtistIndexResult> IndexArtists(PerformContext? context = null,
        int minItemCount = DefaultItemCountThreshold, CancellationToken cancellationToken = default)
    {
        var upstreamSource = await upstreamSourceLookup.FindUpstreamSourceByName(ArchiveOrgImporter.DataSourceName);
        if (upstreamSource == null)
        {
            throw new InvalidOperationException("archive.org upstream source was not found.");
        }

        var items = (await indexClient.FetchCollectionsAsync(DefaultScrapeCount, cancellationToken)).ToList();
        context?.WriteLine($"archive.org collections fetched: {items.Count}");
        var existingArtists = await repository.LoadExistingArtists();
        var existingSlugs = new HashSet<string>(
            existingArtists.Select(artist => artist.slug),
            StringComparer.OrdinalIgnoreCase);

        var result = new ArchiveOrgArtistIndexResult();

        foreach (var item in items)
        {
            if (item.item_count < minItemCount)
            {
                result.Skipped++;
                result.SkippedBelowThreshold++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.identifier) || string.IsNullOrWhiteSpace(item.title))
            {
                result.Skipped++;
                result.SkippedMissingIdentifier++;
                continue;
            }

            var existingMapping =
                await repository.FindArtistByUpstreamIdentifier(upstreamSource.id, item.identifier);
            if (existingMapping != null)
            {
                result.Skipped++;
                result.SkippedExisting++;
                continue;
            }

            var slug = BuildUniqueSlug(item, existingSlugs, context);
            if (string.IsNullOrWhiteSpace(slug))
            {
                result.Skipped++;
                result.SkippedInvalidSlug++;
                continue;
            }

            var created = await repository.SaveArtist(new SlimArtistWithFeatures
            {
                id = 0,
                name = item.title,
                slug = slug,
                sort_name = item.title.Replace("The ", ""),
                musicbrainz_id = string.Empty,
                featured = (int)ArtistFeaturedFlags.AutoCreated,
                features = ArchiveOrgArtistDefaults.ArchiveOrgDefaultFeatures()
            });

            await repository.EnsureUpstreamSourceForArtist(created.id, upstreamSource.id, item.identifier);
            existingSlugs.Add(created.slug);
            context?.WriteLine($"archive.org artist created: {created.name} ({item.identifier}) slug={created.slug}");
            result.Created++;
        }

        context?.WriteLine(
            $"archive.org artist index: created={result.Created}, linked={result.Linked}, skipped={result.Skipped}");
        context?.WriteLine(
            $"archive.org artist index skip reasons: below_threshold={result.SkippedBelowThreshold}, existing={result.SkippedExisting}, missing_identifier={result.SkippedMissingIdentifier}, invalid_slug={result.SkippedInvalidSlug}");

        return result;
    }

    private static string? BuildUniqueSlug(ArchiveOrgCollectionIndexItem item, HashSet<string> existingSlugs,
        PerformContext? context)
    {
        var baseSlug = SlugUtils.Slugify(item.title);
        if (string.IsNullOrWhiteSpace(baseSlug))
        {
            return null;
        }

        if (!existingSlugs.Contains(baseSlug))
        {
            return baseSlug;
        }

        // Ensure we avoid uuid collisions for distinct artists that slugify to the same value.
        for (var suffix = 2; suffix <= 10; suffix++)
        {
            var candidate = $"{baseSlug}-{suffix}";
            if (!existingSlugs.Contains(candidate))
            {
                context?.WriteLine(
                    $"archive.org artist slug conflict: base={baseSlug} identifier={item.identifier} resolved={candidate}");
                return candidate;
            }
        }

        return null;
    }
}
