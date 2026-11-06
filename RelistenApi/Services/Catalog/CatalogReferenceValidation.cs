using System;
using System.Globalization;
using Relisten.Identifiers;
using Relisten.Normalization;

namespace Relisten.Catalog;

/// <summary>Shared identity syntax and legacy ownership requirements, checked before SQL casts.</summary>
public static class CatalogReferenceValidation
{
    public static bool RequiresArtist(CatalogReferenceKind kind, CatalogReferenceScheme scheme) =>
        scheme is CatalogReferenceScheme.LegacyInteger or CatalogReferenceScheme.LegacySlug
        && kind is not (CatalogReferenceKind.Artist or CatalogReferenceKind.Venue);

    public static bool ValidIdentity(
        CatalogReferenceKind kind,
        CatalogReferenceScheme scheme,
        string? id
    )
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        switch (scheme)
        {
            case CatalogReferenceScheme.LegacyUuid:
                return Guid.TryParseExact(id, "D", out _);
            case CatalogReferenceScheme.LegacyInteger:
                return long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                    && n > 0;
            case CatalogReferenceScheme.LegacySlug:
                return true;
            default:
                if (
                    kind
                    is not (
                        CatalogReferenceKind.Song
                        or CatalogReferenceKind.Venue
                        or CatalogReferenceKind.ArtistVenue
                    )
                )
                {
                    return UuidTypeId.TryParse(id, CatalogReferenceLookup.Prefix(kind), out _);
                }

                try
                {
                    switch (kind)
                    {
                        case CatalogReferenceKind.Song:
                            SongId.Parse(id);
                            break;
                        case CatalogReferenceKind.Venue:
                            VenueId.Parse(id);
                            break;
                        case CatalogReferenceKind.ArtistVenue:
                            ArtistVenueId.Parse(id);
                            break;
                    }
                    return true;
                }
                catch (FormatException)
                {
                    return false;
                }
        }
    }
}
