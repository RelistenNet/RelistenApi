using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Relisten.Ingestion;
using Relisten.Ingestion.Providers.PhishIn;
using Relisten.Vendor;
using Relisten.Vendor.Phishin;

namespace Relisten.Import;

// Fetch provider catalogs before opening a transaction. Item attempts reload current catalog rows
// and restore only the stable upstream era/song mappings from preparation.
public partial class PhishinImporter
{
    /// <summary>Fetches and commits prerequisite catalogs, then returns the mappings used by shows.</summary>
    public async Task<PhishInCatalogs> PrepareCatalogsAsync(ProviderContext context)
    {
        var eras = (await PhishinApiRequest<IDictionary<string, IList<string>>>("eras", null)).data;
        var tours = (await PhishinApiRequest<PhishinSmallTour[]>("tours", null)).data;
        var songs = (await PhishinApiRequest<PhishinSmallSong[]>("songs", null)).data;
        var venues = (await PhishinApiRequest<PhishinSmallVenue[]>("venues", null)).data;

        return await context.CommitAsync(async () =>
        {
            await PreloadData(context.Artist);
            await ProcessEras(context.Artist, null, eras);
            await ProcessTours(context.Artist, null, tours);
            await ProcessSongs(context.Artist, null, songs);
            await ProcessVenues(context.Artist, null, venues);

            return new PhishInCatalogs(
                eras,
                songs
                    .Where(song => !song.alias_for.HasValue)
                    .Select(song => new PhishInSongMapping(song.id, song.slug))
                    .ToArray()
            );
        });
    }

    /// <summary>Preserves forward full refresh and backward lower-year-bound refresh pagination.</summary>
    public async Task<
        Page<DiscoveredItem<PhishInShowId, PhishInShowInput>, PhishInPage>
    > ReadShowPageAsync(ProviderContext context, int? fromYear, PhishInPage? cursor)
    {
        var page = cursor?.Number ?? 1;
        var listing = await PhishinApiRequest<PhishinShow[]>("shows", null, "date", 20, page);
        if (listing.total_pages < 0)
        {
            throw new InvalidDataException("Phish.in returned an invalid show page.");
        }

        // The filtered refresh starts with a probe, then walks backward from the
        // last page until it reaches the lower date bound.
        if (cursor == null && fromYear.HasValue && listing.total_pages > 1)
        {
            return new([], new PhishInPage(listing.total_pages));
        }

        var shows = listing
            .data.Where(show =>
                !fromYear.HasValue
                || string.Compare(show.date, $"{fromYear.Value:D4}-01-01", StringComparison.Ordinal)
                    >= 0
            )
            .ToArray();
        var next = NextShowPage(page, listing.total_pages, fromYear, shows.Length);

        return new(shows.Select(ToSelectedShow).ToArray(), next);
    }

    /// <summary>Imports a selected show and returns current catalog identities after any retry.</summary>
    public Task<PhishInItemResult> ImportShowAsync(
        ProviderContext context,
        PhishInCatalogs catalogs,
        DiscoveredItem<PhishInShowId, PhishInShowInput> item
    ) =>
        context.CommitAsync(async () =>
        {
            await performances.LockArtistAsync(context.Artist);
            await PreloadData(context.Artist);
            RestoreCatalogMappings(catalogs);

            var input = item.Input;
            var existing = existingSources.GetValue(item.Id.Value.ToString());
            var unchanged =
                existing != null
                && existing.updated_at >= input.UpdatedAt
                && existingSetlistShows.ContainsKey(item.Id.Value.ToString());
            var show = ToProviderShow(item);

            await ProcessOneShow(new ImportStats(), context.Artist, context.Source, show, null);

            return new PhishInItemResult(
                item.Id,
                unchanged ? ItemDisposition.Unchanged : ItemDisposition.Applied,
                new(existingSources[item.Id.Value.ToString()]!.id),
                new(existingSetlistShows[item.Id.Value.ToString()]!.id)
            );
        });

    private static PhishInPage? NextShowPage(
        int page,
        int totalPages,
        int? fromYear,
        int matchingShows
    )
    {
        if (fromYear.HasValue)
        {
            return page > 1 && matchingShows > 0 ? new PhishInPage(page - 1) : null;
        }

        return page < totalPages ? new PhishInPage(page + 1) : null;
    }

    private static DiscoveredItem<PhishInShowId, PhishInShowInput> ToSelectedShow(
        PhishinShow show
    ) =>
        new(
            new(show.id),
            new(
                show.date,
                show.sbd,
                show.remastered,
                show.tour_id,
                show.venue.id,
                show.taper_notes,
                show.updated_at,
                show.tags.Select(tag => tag.name).ToArray(),
                show.tracks.ToArray()
            )
        );

    private static PhishinShow ToProviderShow(DiscoveredItem<PhishInShowId, PhishInShowInput> item)
    {
        var input = item.Input;
        return new PhishinShow
        {
            id = item.Id.Value,
            date = input.Date,
            sbd = input.Soundboard,
            remastered = input.Remastered,
            tour_id = input.TourId,
            venue = new PhishinSmallVenue { id = input.VenueId },
            taper_notes = input.TaperNotes!,
            updated_at = input.UpdatedAt,
            tags = input.Tags.Select(name => new PhishinTag { name = name }).ToList(),
            tracks = input.Tracks.ToList(),
        };
    }

    private void RestoreCatalogMappings(PhishInCatalogs catalogs)
    {
        yearToEraMapping.Clear();
        foreach (var era in catalogs.Eras)
        {
            foreach (var year in era.Value)
            {
                yearToEraMapping[year] = existingEras[era.Key];
            }
        }

        var songsBySlug = existingSetlistSongs
            .Values.Where(song => song != null)
            .GroupBy(song => song!.slug)
            .ToDictionary(group => group.Key, group => group.First());

        foreach (var song in catalogs.Songs)
        {
            if (songsBySlug.TryGetValue(song.Slug, out var known))
            {
                existingSetlistSongs[song.Id.ToString()] = known;
            }
        }
    }
}
