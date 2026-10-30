using System.Collections.Generic;

namespace Relisten.Catalog;

// Artist browse entries describe show membership, not a mapping from an imported
// venue row to one place. Different shows from that row may resolve differently.
public sealed record CatalogArtistVenueEntry(
    int? FallbackShowCount,
    CatalogArtistVenue? ArtistVenue,
    CatalogImportedVenue? ImportedVenue
);

public sealed record CatalogImportedVenueDetail(
    CatalogImportedVenue Venue,
    IReadOnlyList<CatalogShow> Shows
);
