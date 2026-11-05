using System;
using System.Collections.Generic;

namespace Relisten.Catalog;

// Materialized catalog facts retain numeric keys for database joins.
// These types have no serializer or API-version behavior. Public projections select fields explicitly.
public abstract class CatalogTimestamps
{
    public DateTime created_at { get; set; }
    public DateTime updated_at { get; set; }
}

public sealed class CatalogShow : CatalogTimestamps
{
    public long? venue_id { get; set; }
    public long id { get; set; }
    public Guid uuid { get; set; }
    public Guid artist_uuid { get; set; }
    public Guid? year_uuid { get; set; }
    public Guid? tour_uuid { get; set; }
    public DateTime date { get; set; }
    public string display_date { get; set; } = null!;
    public float avg_rating { get; set; }
    public float? avg_duration { get; set; }
    public DateTime most_recent_source_updated_at { get; set; }
    public bool has_soundboard_source { get; set; }
    public bool has_streamable_flac_source { get; set; }
    public int source_count { get; set; }
    public bool normalization_enabled { get; set; }
    public CatalogImportedVenue? ImportedVenue { get; set; }
    public CatalogArtistVenue? ArtistVenue { get; set; }
    public CatalogYear? Year { get; set; }
    public CatalogTour? Tour { get; set; }
    public CatalogEra? Era { get; set; }
    public Relisten.Api.Models.PopularityMetrics? Popularity { get; set; }
    public List<CatalogSource> Sources { get; set; } = [];
}

public sealed class CatalogSource : CatalogTimestamps
{
    public long id { get; set; }
    public Guid uuid { get; set; }
    public Guid artist_uuid { get; set; }
    public Guid show_uuid { get; set; }
    public long? venue_id { get; set; }
    public string display_date { get; set; } = null!;
    public bool is_soundboard { get; set; }
    public bool is_remaster { get; set; }
    public bool has_jamcharts { get; set; }
    public double avg_rating { get; set; }
    public int num_reviews { get; set; }
    public int? num_ratings { get; set; }
    public double avg_rating_weighted { get; set; }
    public double duration { get; set; }
    public string upstream_identifier { get; set; } = null!;
    public string? description { get; set; }
    public string? taper_notes { get; set; }
    public string? source { get; set; }
    public string? taper { get; set; }
    public string? transferrer { get; set; }
    public string? lineage { get; set; }
    public int flac_type { get; set; }
    public int review_count { get; set; }
    public CatalogImportedVenue? ImportedVenue { get; set; }
    public List<CatalogSet> Sets { get; set; } = [];
    public List<CatalogLink> Links { get; set; } = [];
}

public sealed class CatalogSet : CatalogTimestamps
{
    public long id { get; set; }
    public Guid uuid { get; set; }
    public long source_id { get; set; }
    public Guid source_uuid { get; set; }
    public Guid artist_uuid { get; set; }
    public int index { get; set; }
    public bool is_encore { get; set; }
    public string name { get; set; } = null!;
    public List<CatalogTrack> Tracks { get; set; } = [];
}

public sealed class CatalogTrack : CatalogTimestamps
{
    public long id { get; set; }
    public Guid uuid { get; set; }
    public long source_set_id { get; set; }
    public Guid source_uuid { get; set; }
    public Guid source_set_uuid { get; set; }
    public Guid show_uuid { get; set; }
    public Guid artist_uuid { get; set; }
    public int track_position { get; set; }
    public int? duration { get; set; }
    public string title { get; set; } = null!;
    public string slug { get; set; } = null!;
    public string? mp3_url { get; set; }
    public string? mp3_md5 { get; set; }
    public string? flac_url { get; set; }
    public string? flac_md5 { get; set; }
    public CatalogNormalization Normalization { get; set; } =
        new("unavailable", "not_interpreted", []);
}

public sealed class CatalogImportedVenue : CatalogTimestamps
{
    public int show_count { get; set; }
    public long id { get; set; }
    public Guid uuid { get; set; }
    public Guid artist_uuid { get; set; }
    public double? latitude { get; set; }
    public double? longitude { get; set; }
    public string? name { get; set; }
    public string? location { get; set; }
    public string? upstream_identifier { get; set; }
    public string? slug { get; set; }
    public string? past_names { get; set; }
}

public sealed class CatalogLink : CatalogTimestamps
{
    public long source_id { get; set; }
    public Guid source_uuid { get; set; }
    public string provider { get; set; } = null!;
    public bool for_reviews { get; set; }
    public bool for_ratings { get; set; }
    public bool for_source { get; set; }
    public string url { get; set; } = null!;
    public string label { get; set; } = null!;
}

public sealed class CatalogEra
{
    public int order { get; set; }
    public string name { get; set; } = null!;
}

public sealed class CatalogSong
{
    public string id { get; set; } = null!;
    public Guid artist_uuid { get; set; }
    public string name { get; set; } = null!;
    public string? kind { get; set; }
    public string? merged_into { get; set; }
}

public sealed class CatalogVenue
{
    public string id { get; set; } = null!;
    public int global_show_count { get; set; }
    public string name { get; set; } = null!;
    public string slug { get; set; } = null!;
    public string kind { get; set; } = null!;
    public string? parent_id { get; set; }
    public string? city { get; set; }
    public string? region { get; set; }
    public string? country { get; set; }
    public double? latitude { get; set; }
    public double? longitude { get; set; }
    public string? coordinate_precision { get; set; }
    public string? merged_into { get; set; }
    public IReadOnlyList<CatalogVenueName> Names { get; set; } = [];
}

/// <summary>A persisted artist/place identity and its last refreshed count.</summary>
public sealed class CatalogArtistVenue : CatalogTimestamps
{
    public string id { get; set; } = null!;
    public Guid artist_uuid { get; set; }
    public string venue_id { get; set; } = null!;
    public int artist_show_count { get; set; }

    // Relationship reads attach the already-resolved venue from the caller's snapshot.
    public CatalogVenue Venue { get; set; } = null!;
}

// Aliases retained on merged-away venues are exposed under the surviving identity.
public sealed class CatalogVenueName
{
    public string canonical_venue_id { get; set; } = null!;
    public string name { get; set; } = null!;
    public string kind { get; set; } = null!;
}

public sealed class CatalogPart
{
    public Guid track_uuid { get; set; }
    public int part { get; set; }
    public string? song_id { get; set; }
    public string? text { get; set; }
    public bool resolved { get; set; }
    public string[] qualifiers { get; set; } = [];
    public string[] notes { get; set; } = [];
    public bool segue_after { get; set; }
    public CatalogSong? Song { get; set; }
}

public sealed record CatalogNormalization(
    string State,
    string? Reason,
    IReadOnlyList<CatalogPart> Parts
);

public sealed class CatalogYear : CatalogTimestamps
{
    public Relisten.Api.Models.PopularityMetrics? Popularity { get; set; }
    public Guid uuid { get; set; }
    public Guid artist_uuid { get; set; }
    public int show_count { get; set; }
    public int source_count { get; set; }
    public bool has_soundboard_source { get; set; }
    public int? duration { get; set; }
    public float? avg_duration { get; set; }
    public float avg_rating { get; set; }
    public string year { get; set; } = null!;
}

public sealed class CatalogTour : CatalogTimestamps
{
    public long id { get; set; }
    public long artist_id { get; set; }
    public Guid uuid { get; set; }
    public Guid artist_uuid { get; set; }
    public int show_count { get; set; }
    public DateTime? start_date { get; set; }
    public DateTime? end_date { get; set; }
    public string name { get; set; } = null!;
    public string slug { get; set; } = null!;
    public string upstream_identifier { get; set; } = null!;
}
