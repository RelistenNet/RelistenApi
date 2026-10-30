using System;
using System.Collections.Generic;

namespace Relisten.Catalog;

/// <summary>
/// Resolver entity patches contain requested resources and shallow recording parents.
/// Their collections never establish child membership or catalog-wide completeness.
/// </summary>
public sealed class CatalogResolvedEntities
{
    public IReadOnlyList<CatalogArtist> Artists { get; init; } = [];
    public IReadOnlyList<CatalogShow> Shows { get; init; } = [];
    public IReadOnlyList<CatalogSource> Sources { get; init; } = [];
    public IReadOnlyList<CatalogSet> SourceSets { get; init; } = [];
    public IReadOnlyList<CatalogTrack> Tracks { get; init; } = [];
    public IReadOnlyList<CatalogYear> Years { get; init; } = [];
    public IReadOnlyList<CatalogTour> Tours { get; init; } = [];
    public IReadOnlyList<CatalogImportedVenue> ImportedVenues { get; init; } = [];
    public IReadOnlyList<CatalogImportedSong> ImportedSongs { get; init; } = [];
    public IReadOnlyList<CatalogVenue> Venues { get; init; } = [];
    public IReadOnlyList<CatalogSongDetail> Songs { get; init; } = [];
    public IReadOnlyList<CatalogArtistVenue> ArtistVenues { get; init; } = [];
    public IReadOnlyList<CatalogSourceReview> SourceReviews { get; init; } = [];
    public IReadOnlyList<CatalogSetlistShow> SetlistShows { get; init; } = [];
}

/// <summary>Imported setlist evidence. Its date does not imply a corresponding catalog show.</summary>
public sealed class CatalogSetlistShow : CatalogTimestamps
{
    public Guid uuid { get; set; }
    public Guid artist_uuid { get; set; }
    public long venue_id { get; set; }
    public Guid imported_venue_uuid { get; set; }
    public Guid? tour_uuid { get; set; }
    public long? era_id { get; set; }
    public int? era_order { get; set; }
    public string? era_name { get; set; }
    public DateTime date { get; set; }
    public string upstream_identifier { get; set; } = null!;
}
