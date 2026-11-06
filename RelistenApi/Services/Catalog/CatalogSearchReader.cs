using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Relisten.Identifiers;

namespace Relisten.Catalog;

public sealed record CatalogSearchOptions(
    bool Artists = true,
    bool Shows = true,
    bool Songs = true,
    bool Sources = true,
    bool Tours = true,
    bool Venues = true
)
{
    public bool AnyEnabled => Artists || Shows || Songs || Sources || Tours || Venues;
}

public sealed class CatalogSearchResults
{
    public IReadOnlyList<CatalogArtist> Artists { get; init; } = [];
    public IReadOnlyList<CatalogShow> Shows { get; init; } = [];
    public IReadOnlyList<CatalogArtistSongEntry> Songs { get; init; } = [];
    public IReadOnlyList<CatalogSource> Sources { get; init; } = [];
    public IReadOnlyList<CatalogTour> Tours { get; init; } = [];
    public IReadOnlyList<CatalogArtistVenueEntry> Venues { get; init; } = [];
}

/// <summary>REST's six bounded categories use the same SQL membership pages and
/// entity batches as the graph. All selected categories share this read snapshot.</summary>
public sealed class CatalogSearchReader(
    DbService db,
    CatalogCollectionReader collections,
    CatalogEntityReader entities
)
{
    public async Task<CatalogSearchResults> Read(
        string? query,
        Guid? artistUuid,
        CatalogSearchOptions options,
        CancellationToken token
    )
    {
        var term = query?.Trim();
        if (string.IsNullOrEmpty(term) || term.Length < 3 || !options.AnyEnabled)
        {
            return new();
        }
        await using var session = await CatalogReadSession.Open(db, token);
        var filters = new CatalogFilters(Artist: artistUuid, Search: term);
        return new()
        {
            Artists = options.Artists
                ? await ReadCategory(
                    session,
                    CatalogReferenceKind.Artist,
                    filters,
                    b => b.Artists,
                    a => UuidTypeId.Format("artist", a.uuid),
                    token
                )
                : [],
            Shows = options.Shows
                ? await ReadCategory(
                    session,
                    CatalogReferenceKind.Show,
                    filters,
                    b => b.Shows,
                    s => UuidTypeId.Format("show", s.uuid),
                    token
                )
                : [],
            Sources = options.Sources
                ? await ReadCategory(
                    session,
                    CatalogReferenceKind.Source,
                    filters,
                    b => b.Sources,
                    s => UuidTypeId.Format("source", s.uuid),
                    token
                )
                : [],
            Tours = options.Tours
                ? await ReadCategory(
                    session,
                    CatalogReferenceKind.Tour,
                    filters,
                    b => b.Tours,
                    t => UuidTypeId.Format("tour", t.uuid),
                    token
                )
                : [],
            Songs = options.Songs ? await ReadSongs(session, filters, token) : [],
            Venues = options.Venues ? await ReadVenues(session, filters, token) : [],
        };
    }

    private async Task<IReadOnlyList<T>> ReadCategory<T>(
        CatalogReadSession session,
        CatalogReferenceKind kind,
        CatalogFilters filters,
        Func<CatalogResolvedEntities, IReadOnlyList<T>> values,
        Func<T, string> id,
        CancellationToken token
    )
    {
        var page = await Page(session, kind, filters, false, token);
        var rows = values(await entities.Read(session, page.Targets, token)).ToDictionary(id);
        return page.Targets.Select(t => rows[t.Id]).ToArray();
    }

    private async Task<IReadOnlyList<CatalogArtistSongEntry>> ReadSongs(
        CatalogReadSession session,
        CatalogFilters filters,
        CancellationToken token
    )
    {
        var page = await Page(session, CatalogReferenceKind.Song, filters, true, token);
        var rows = new Dictionary<string, CatalogArtistSongEntry>();
        foreach (var group in page.Targets.GroupBy(t => t.Kind))
        {
            var batch = await entities.Read(session, group.ToArray(), token);
            foreach (var song in batch.Songs)
            {
                rows[song.id] = new(song, null);
            }
            foreach (var raw in batch.ImportedSongs)
            {
                rows[UuidTypeId.Format("imported_song", raw.uuid)] = new(null, raw);
            }
        }
        return page.Targets.Select(t => rows[t.Id]).ToArray();
    }

    private async Task<IReadOnlyList<CatalogArtistVenueEntry>> ReadVenues(
        CatalogReadSession session,
        CatalogFilters filters,
        CancellationToken token
    )
    {
        var page = await Page(session, CatalogReferenceKind.ArtistVenue, filters, true, token);
        var rows = new Dictionary<string, CatalogArtistVenueEntry>();
        foreach (var group in page.Targets.GroupBy(t => t.Kind))
        {
            var batch = await entities.Read(session, group.ToArray(), token);
            foreach (var venue in batch.ArtistVenues)
            {
                rows[venue.id] = new(venue, null);
            }
            foreach (var raw in batch.ImportedVenues)
            {
                rows[UuidTypeId.Format("imported_venue", raw.uuid)] = new(null, raw);
            }
        }
        return page.Targets.Select(t => rows[t.Id]).ToArray();
    }

    private async Task<CatalogPageSelection> Page(
        CatalogReadSession session,
        CatalogReferenceKind kind,
        CatalogFilters filters,
        bool fallback,
        CancellationToken token
    ) =>
        (
            await collections.ReadPages(
                session,
                [
                    (
                        new CatalogCollection(kind, Filters: filters, IncludeFallback: fallback),
                        new CatalogPageRequest(20)
                    ),
                ],
                token
            )
        )[0];
}
