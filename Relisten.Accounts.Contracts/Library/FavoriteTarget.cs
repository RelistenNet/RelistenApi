using Relisten.Identifiers;

namespace Relisten.Accounts.Contracts.Library;

/// <summary>An exact catalog identity. Membership writes do not require catalog availability.</summary>
public sealed record FavoriteTarget(string Kind, string Id)
{
    public bool IsValid() =>
        Kind switch
        {
            FavoriteCatalogTypes.Song or FavoriteCatalogTypes.Venue => ShortTypeId.IsValid(
                Id,
                Kind
            ),
            FavoriteCatalogTypes.Artist
            or FavoriteCatalogTypes.Show
            or FavoriteCatalogTypes.Source
            or FavoriteCatalogTypes.Track
            or FavoriteCatalogTypes.Tour
            or FavoriteCatalogTypes.ImportedSong
            or FavoriteCatalogTypes.ImportedVenue => UuidTypeId.TryParse(Id, Kind, out var uuid)
                && uuid != Guid.Empty,
            _ => false,
        };
}
