using System.Linq;
using Relisten.Catalog;

namespace Relisten.Api.Models.Api.V4;

public static partial class V4CatalogProjection
{
    public static ArtistCatalogResource ArtistCatalog(CatalogArtistSnapshot snapshot) =>
        new()
        {
            Artist = CatalogBrowseProjection.Artist(snapshot.Artist),
            Years = snapshot.Years.Select(Year).ToArray(),
            Shows = snapshot.Shows.Select(ShowSummary).ToArray(),
            Tours = snapshot.Tours.Select(Tour).ToArray(),
            Eras = snapshot.Eras.Select(Era).ToArray(),
            ImportedVenues = snapshot.ImportedVenues.Select(ImportedVenue).ToArray(),
            ImportedSongs = snapshot.ImportedSongs.Select(ImportedSong).ToArray(),
            VenueEntries = snapshot.VenueEntries.Select(VenueEntry).ToArray(),
            SongEntries = snapshot.SongEntries.Select(SongEntry).ToArray(),
            ArtistVenues = snapshot.ArtistVenues.Select(ArtistVenue).ToArray(),
            Venues = snapshot.Venues.Select(Venue).ToArray(),
            Songs = snapshot.Songs.Select(SongDetail).ToArray(),
        };
}
