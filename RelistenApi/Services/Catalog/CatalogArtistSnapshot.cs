using System.Collections.Generic;

namespace Relisten.Catalog;

/// <summary>
/// Complete artist-owned catalog membership plus shared entity patches. Recordings
/// remain lazy show/source reads; shared patches never imply global deletion.
/// </summary>
public sealed record CatalogArtistSnapshot(
    CatalogArtist Artist,
    IReadOnlyList<CatalogYear> Years,
    IReadOnlyList<CatalogShow> Shows,
    IReadOnlyList<CatalogTour> Tours,
    IReadOnlyList<CatalogEra> Eras,
    IReadOnlyList<CatalogImportedVenue> ImportedVenues,
    IReadOnlyList<CatalogImportedSong> ImportedSongs,
    IReadOnlyList<CatalogArtistVenueEntry> VenueEntries,
    IReadOnlyList<CatalogArtistSongEntry> SongEntries,
    IReadOnlyList<CatalogArtistVenue> ArtistVenues,
    IReadOnlyList<CatalogVenue> Venues,
    IReadOnlyList<CatalogSongDetail> Songs
);
