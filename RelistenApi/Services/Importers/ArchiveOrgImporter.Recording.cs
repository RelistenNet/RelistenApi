using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Hangfire.Server;
using Relisten.Api.Models;
using Relisten.Data;
using Relisten.Vendor;
using Relisten.Vendor.ArchiveOrg;
using Relisten.Vendor.ArchiveOrg.Metadata;

namespace Relisten.Import;

// Shared catalog writes for ordinary Archive collection imports and resolved intake
// assignments. Callers own performer/date policy and the surrounding transaction.
public partial class ArchiveOrgImporter
{
    /// <summary>
    /// Writes validated metadata for an explicitly resolved artist and performance date.
    /// This operation neither loads nor requires an artist collection mapping. Call it
    /// inside CatalogImportTransaction so catalog rows and intake ownership commit together.
    /// Source, set, and track identities retain the ordinary Archive import formulas.
    /// </summary>
    /// <exception cref="ArchivePerformanceHeldException">
    /// Performance billing needs review. Existing playback rows remain unchanged, while
    /// assignment evidence is retained in the caller's transaction. Handle the exception
    /// before completing that transaction if the evidence should commit.
    /// </exception>
    public async Task<Source> WriteResolvedRecordingAsync(
        Artist artist,
        int archiveProviderId,
        SearchDoc document,
        RootObject metadata,
        string displayDate
    )
    {
        await WriteRecordingAsync(artist, document, metadata, archiveProviderId, displayDate, null);
        return await _sourceService.ForUpstreamIdentifierFromPrimary(artist, document.identifier)
            ?? throw new InvalidOperationException("Archive import did not produce a source.");
    }

    private async Task<ImportStats> WriteRecordingAsync(
        Artist artist,
        SearchDoc searchDoc,
        RootObject detailsRoot,
        int archiveProviderId,
        string properDisplayDate,
        PerformContext? ctx
    )
    {
        var stats = new ImportStats();

        var meta = detailsRoot.metadata!;
        var dbSource = await performanceAssignments.LockAndReadSourceAsync(
            artist,
            searchDoc.identifier
        );
        var isUpdate = dbSource != null;
        var performance = await performanceAssignments.AssessAsync(
            artist,
            dbSource,
            meta,
            properDisplayDate
        );
        if (performance.HoldReason != null)
        {
            if (dbSource != null)
            {
                await performanceAssignments.ApplyAsync(artist, dbSource, performance);
            }
            throw new ArchivePerformanceHeldException(performance.HoldReason);
        }

        var mp3Files =
            detailsRoot.files?.Where(file => file?.format == "VBR MP3") ?? Enumerable.Empty<File>();
        var flacFiles =
            detailsRoot.files?.Where(file => file?.format == "Flac" || file?.format == "24bit Flac")
            ?? Enumerable.Empty<File>();

        if (HasNoVbrMp3Files(detailsRoot))
        {
            ImportProgress.WriteLine(
                log,
                ctx,
                "\tNo VBR MP3 files found for {0}",
                searchDoc.identifier
            );

            throw new NoVBRMp3FilesException();
        }

        var dbReviews =
            detailsRoot.reviews == null
                ? new List<SourceReview>()
                : detailsRoot
                    .reviews.Select(rev =>
                    {
                        return new SourceReview
                        {
                            rating = rev.stars * 2, // scale to out of 10
                            title = rev.reviewtitle,
                            review = rev.reviewbody?.Replace("Â", "") ?? "",
                            author = rev.reviewer,
                            updated_at = rev.reviewdate,
                        };
                    })
                    .ToList();

        Venue? dbVenue = null;
        if (artist.features.per_source_venues)
        {
            var venueName = string.IsNullOrEmpty(meta.venue) ? meta.coverage : meta.venue;

            if (string.IsNullOrEmpty(venueName))
            {
                venueName = "Unknown Venue";
            }

            var venueUpstreamId =
                venueName
                + (string.IsNullOrEmpty(meta.coverage) ? "blank coverage" : meta.coverage);
            dbVenue = await _venueService.ForUpstreamIdentifier(artist, venueUpstreamId);

            if (dbVenue == null)
            {
                dbVenue = await _venueService.Save(
                    new Venue
                    {
                        artist_id = artist.id,
                        name = venueName,
                        location = string.IsNullOrEmpty(meta.coverage)
                            ? "Unknown Location"
                            : meta.coverage,
                        upstream_identifier = venueUpstreamId,
                        slug = Slugify(venueName),
                        updated_at = searchDoc.max_updated_at,
                    }
                );
            }
        }

        if (isUpdate)
        {
            var src = CreateSourceForMetadata(artist, detailsRoot, searchDoc, properDisplayDate);
            src.id = dbSource!.id;
            src.venue_id = dbVenue?.id;

            // Durable assignments own reassociation. Preserve the current primary until
            // the assignment/rebuild applies a supported correction, including date edits.
            src.show_id = dbSource.show_id;

            dbSource = await _sourceService.Save(src);
            if (dbVenue != null)
            {
                dbSource.venue = dbVenue;
            }

            stats.Updated++;
        }
        else
        {
            dbSource = await _sourceService.Save(
                CreateSourceForMetadata(artist, detailsRoot, searchDoc, properDisplayDate, dbVenue)
            );
            stats.Created++;

            existingSources[dbSource.upstream_identifier] = dbSource;
        }

        await performanceAssignments.ApplyAsync(artist, dbSource, performance);

        stats.Created += (await ReplaceSourceReviews(dbSource, dbReviews)).Count();

        stats.Created += (
            await linkService.AddLinksForSource(
                dbSource,
                LinksForSource(artist, dbSource, archiveProviderId)
            )
        ).Count();

        var dbSet = (
            await _sourceSetService.UpdateAll(
                dbSource,
                new[] { CreateSetForSource(dbSource) },
                SourceSetUuidVersion.V1
            )
        ).First();
        stats.Created++;

        var flacTracksByName = flacFiles
            .GroupBy(f => f.name)
            .ToDictionary(g => g.Key, g => (File?)g.First());

        var allFilesByName =
            detailsRoot.files?.GroupBy(f => f.name).ToDictionary(g => g.Key, g => (File?)g.First())
            ?? new Dictionary<string, File?>();

        var dbTracks = CreateSourceTracksForFiles(
            artist,
            dbSource,
            meta,
            mp3Files,
            flacTracksByName,
            dbSet,
            allFilesByName
        );

        stats.Created += (await _sourceTrackService.InsertAll(dbSource, dbTracks)).Count();

        ResetTrackSlugCounts();

        return stats;
    }

    private IEnumerable<Link> LinksForSource(Artist artist, Source dbSource, int archiveProviderId)
    {
        var links = new List<Link>
        {
            new()
            {
                source_id = dbSource.id,
                for_ratings = true,
                for_source = true,
                for_reviews = true,
                upstream_source_id = archiveProviderId,
                url = "https://archive.org/details/" + dbSource.upstream_identifier,
                label = "View on archive.org",
            },
        };

        if (
            artist.upstream_sources.Any(s =>
                s.upstream_source_id == 6 /* setlist.fm */
            )
        )
        {
            links.Add(
                new Link
                {
                    source_id = dbSource.id,
                    for_ratings = false,
                    for_source = false,
                    for_reviews = false,
                    upstream_source_id =
                        6 /* setlist.fm */
                    ,
                    url = "https://www.setlist.fm/",
                    label = "Setlist Information from setlist.fm",
                }
            );
        }

        return links;
    }

    private async Task<IEnumerable<SourceReview>> ReplaceSourceReviews(
        Source source,
        IEnumerable<SourceReview> reviews
    )
    {
        foreach (var review in reviews)
        {
            review.source_id = source.id;
        }

        return await _sourceReviewService.UpdateAll(source, reviews);
    }

    private SourceSet CreateSetForSource(Source source)
    {
        return new SourceSet
        {
            source_id = source.id,
            index = 0,
            is_encore = false,
            name = "Set",
            updated_at = source.updated_at,
        };
    }

    private Source CreateSourceForMetadata(
        Artist artist,
        RootObject detailsRoot,
        SearchDoc searchDoc,
        string properDisplayDate,
        Venue? dbVenue = null
    )
    {
        var meta = detailsRoot.metadata;

        var sbd =
            meta.identifier.EmptyIfNull().ContainsInsensitive("sbd")
            || meta.title.EmptyIfNull().ContainsInsensitive("sbd")
            || meta.source.EmptyIfNull().ContainsInsensitive("sbd")
            || meta.lineage.EmptyIfNull().ContainsInsensitive("sbd");

        var remaster =
            meta.identifier.EmptyIfNull().ContainsInsensitive("remast")
            || meta.title.EmptyIfNull().ContainsInsensitive("remast")
            || meta.source.EmptyIfNull().ContainsInsensitive("remast")
            || meta.lineage.EmptyIfNull().ContainsInsensitive("remast");

        var flac_type = FlacType.NoFlac;

        if (detailsRoot.files?.Any(f => f.format == "24bit Flac") ?? false)
        {
            flac_type = FlacType.Flac24Bit;
        }
        else if (detailsRoot.files?.Any(f => f.format == "Flac") ?? false)
        {
            flac_type = FlacType.Flac16Bit;
        }

        return new Source
        {
            artist_id = artist.id,
            is_soundboard = sbd,
            is_remaster = remaster,
            has_jamcharts = false,
            avg_rating = 0, // dbReviews.Average(rev => 1.0 * rev.rating),
            num_reviews = 0, // dbReviews.Count,
            upstream_identifier = meta.identifier,
            description = meta.description.EmptyIfNull(),
            taper_notes = meta.notes.EmptyIfNull(),
            source = meta.source.EmptyIfNull(),
            taper = meta.taper.EmptyIfNull(),
            transferrer = meta.transferer.EmptyIfNull(),
            lineage = meta.lineage.EmptyIfNull(),
            updated_at = searchDoc.max_updated_at,
            venue_id = dbVenue?.id,
            display_date = properDisplayDate,
            flac_type = flac_type,
        };
    }

    private IEnumerable<SourceTrack> CreateSourceTracksForFiles(
        Artist artist,
        Source dbSource,
        Metadata meta,
        IEnumerable<File> mp3Files,
        IDictionary<string, File?> flacFiles,
        SourceSet set,
        IDictionary<string, File?> allFilesByName
    )
    {
        var trackNum = 0;

        return mp3Files
            .Where(file =>
            {
                return !(
                    (file.title == null && file.original == null && file.name == null)
                    || file.length == null
                    || file.name == null
                );
            })
            .OrderBy(file => file.name)
            .Select(file =>
            {
                var r = CreateSourceTrackForFile(
                    artist,
                    dbSource,
                    meta,
                    file,
                    trackNum,
                    flacFiles,
                    set,
                    allFilesByName
                );
                trackNum = r.track_position;

                return r;
            });
    }

    private SourceTrack CreateSourceTrackForFile(
        Artist artist,
        Source dbSource,
        Metadata meta,
        File file,
        int previousTrackNumber,
        IDictionary<string, File?> flacFiles,
        SourceSet set,
        IDictionary<string, File?> allFilesByName
    )
    {
        var trackNum = previousTrackNumber + 1;

        var title = file.name;

        if (!string.IsNullOrEmpty(file.title))
        {
            title = file.title;
        }
        else if (
            !string.IsNullOrEmpty(file.original)
            && allFilesByName.TryGetValue(file.original, out var original)
            && original != null
            && !string.IsNullOrWhiteSpace(original.title)
        )
        {
            // sometimes if file.title is null the original file.title will be null as well
            title = original.title;
        }

        var flac = file.original == null ? null : flacFiles.GetValue(file.original);

        return new SourceTrack
        {
            title = title,
            track_position = trackNum,
            source_set_id = set?.id ?? -1,
            source_id = dbSource.id,
            duration = file.length!.Split(':')
                .Reverse()
                .Select(
                    (v, k) =>
                        (int)Math.Round(Math.Max(1, 60 * k) * double.Parse(v, NumberStyles.Any))
                )
                .Sum(),
            slug = SlugifyTrack(title),
            mp3_url = $"https://archive.org/download/{meta.identifier}/{file.name}",
            mp3_md5 = file.md5,
            flac_url =
                flac == null ? null : $"https://archive.org/download/{meta.identifier}/{flac.name}",
            flac_md5 = flac?.md5,
            updated_at = dbSource.updated_at,
            artist_id = artist.id,
        };
    }
}
