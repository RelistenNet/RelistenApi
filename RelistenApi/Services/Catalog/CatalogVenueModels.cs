using System.Collections.Generic;

namespace Relisten.Catalog;

// Imported entries expose their complete collections. With partial normalization,
// those shows can also appear under a canonical artist/place relationship.
public sealed record CatalogArtistVenueEntry(
    CatalogArtistVenue? ArtistVenue,
    CatalogImportedVenue? ImportedVenue
);

public sealed record CatalogVenueDetail(
    CatalogVenue Venue,
    IReadOnlyList<CatalogShow> Shows,
    IReadOnlyList<CatalogArtist> Artists
);

public sealed record CatalogArtistVenueDetail(
    CatalogArtistVenue ArtistVenue,
    IReadOnlyList<CatalogShow> Shows,
    IReadOnlyList<CatalogArtist> Artists
);

public sealed record CatalogImportedVenueDetail(
    CatalogImportedVenue Venue,
    IReadOnlyList<CatalogShow> Shows,
    IReadOnlyList<CatalogArtist> Artists
);
