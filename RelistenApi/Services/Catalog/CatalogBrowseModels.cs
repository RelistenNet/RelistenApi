using System;
using System.Collections.Generic;

namespace Relisten.Catalog;

// Artist browse facts are independent of API versions and legacy model attributes.
// Numeric keys remain private join inputs; projections publish UUID-backed identities.
public sealed class CatalogArtist : CatalogTimestamps
{
    public long id { get; set; }
    public Guid uuid { get; set; }
    public string name { get; set; } = null!;
    public string slug { get; set; } = null!;
    public string? sort_name { get; set; }
    public string? musicbrainz_id { get; set; }
    public int featured { get; set; }
    public int show_count { get; set; }
    public int source_count { get; set; }
    public CatalogFeatures Features { get; set; } = new();
    public List<CatalogProviderAssignment> UpstreamSources { get; set; } = [];
}

public sealed class CatalogFeatures
{
    public long artist_id { get; set; }
    public bool descriptions { get; set; }
    public bool eras { get; set; }
    public bool multiple_sources { get; set; }
    public bool reviews { get; set; }
    public bool ratings { get; set; }
    public bool tours { get; set; }
    public bool taper_notes { get; set; }
    public bool source_information { get; set; }
    public bool sets { get; set; }
    public bool per_show_venues { get; set; }
    public bool per_source_venues { get; set; }
    public bool venue_coords { get; set; }
    public bool songs { get; set; }
    public bool years { get; set; }
    public bool track_md5s { get; set; }
    public bool review_titles { get; set; }
    public bool jam_charts { get; set; }
    public bool setlist_data_incomplete { get; set; }
    public bool track_names { get; set; }
    public bool venue_past_names { get; set; }
    public bool reviews_have_ratings { get; set; }
    public bool track_durations { get; set; }
    public bool can_have_flac { get; set; }
    public bool norm_catalogue { get; set; }
}

public sealed class CatalogProviderAssignment
{
    public long artist_id { get; set; }
    public string provider { get; set; } = null!;
    public string? upstream_identifier { get; set; }
    public string? url { get; set; }
    public string? description { get; set; }
    public string? credit_line { get; set; }
}

public sealed record CatalogYearDetail(CatalogYear Year, IReadOnlyList<CatalogShow> Shows);

public sealed record CatalogTourDetail(CatalogTour Tour, IReadOnlyList<CatalogShow> Shows);
