using System.Collections.Generic;

namespace Relisten.Catalog;

// Imported entries expose their complete collections. With partial normalization,
// those shows can also appear under a canonical artist/place relationship.
public sealed record CatalogArtistVenueEntry(
    CatalogArtistVenue? ArtistVenue,
    CatalogImportedVenue? ImportedVenue
);

public sealed record CatalogImportedVenueDetail(
    CatalogImportedVenue Venue,
    IReadOnlyList<CatalogShow> Shows
);
