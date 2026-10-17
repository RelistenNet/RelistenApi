using System;
using System.Collections.Generic;

namespace Relisten.Api.Models.Normalization;

/// <summary>A live keyset page. Concurrent corrections can move entries; clients deduplicate by UUID.</summary>
public sealed class NormalizationPage<T>
{
    public IReadOnlyList<T> items { get; init; } = [];
    public string? next_cursor { get; init; }
}

public abstract class NormalizationNamedEntity
{
    public Guid uuid { get; set; }
    public long revision { get; set; }
    public DateTimeOffset updated_at { get; set; }
    public Guid? requested_uuid { get; set; }
    public Guid? redirect_uuid { get; set; }
    public string display_name { get; set; } = "";
    public string kind { get; set; } = "";
    public string[] aliases { get; set; } = [];
}

public sealed class NormalizedPerson : NormalizationNamedEntity
{
    public IReadOnlyList<NormalizationExternalIdentity> external_identities { get; set; } = [];
}

public sealed class NormalizedVenue : NormalizationNamedEntity
{
    public Guid? parent_venue_uuid { get; set; }
    public IReadOnlyList<NormalizedVenueName> names { get; set; } = [];
    public IReadOnlyList<NormalizedVenueLocation> locations { get; set; } = [];
    public IReadOnlyList<NormalizationExternalIdentity> external_identities { get; set; } = [];
}

/// <summary>Selected retained support. Status describes input freshness, independent of catalog availability.</summary>
public class NormalizationPublicEvidence
{
    public Guid input_revision_uuid { get; set; }
    public string json_pointer { get; set; } = "";
    public int? start_code_point { get; set; }
    public int? end_code_point { get; set; }
    public string? quote { get; set; }
    public string evidence_status { get; set; } = "missing";
}

public sealed class NormalizationExternalIdentity : NormalizationPublicEvidence
{
    public long revision { get; set; }
    public string basis { get; set; } = "";
    public string provider { get; set; } = "";
    public string resource_type { get; set; } = "";
    public string provider_key { get; set; } = "";
}

public sealed class NormalizedVenueName : NormalizationPublicEvidence
{
    public long revision { get; set; }
    public string basis { get; set; } = "";
    public Guid uuid { get; set; }
    public string wording { get; set; } = "";
    public string? language { get; set; }
    public DateTime? valid_from { get; set; }
    public DateTime? valid_to { get; set; }
}

public sealed class NormalizedVenueLocation : NormalizationPublicEvidence
{
    public long revision { get; set; }
    public string basis { get; set; } = "";
    public Guid uuid { get; set; }
    public string original_locality { get; set; } = "";
    public string? country { get; set; }
    public string? region { get; set; }
    public string? city { get; set; }
    public string? address { get; set; }
    public double? latitude { get; set; }
    public double? longitude { get; set; }
    public string? coordinate_precision { get; set; }
    public DateTime? valid_from { get; set; }
    public DateTime? valid_to { get; set; }
}

public sealed class NormalizedRecordingReference
{
    public Guid source_uuid { get; set; }
    public Guid artist_uuid { get; set; }
    public string artist_name { get; set; } = "";
    public string artist_slug { get; set; } = "";
    public Guid show_uuid { get; set; }
    public string date { get; set; } = "";
    public string display_date { get; set; } = "";
    public long track_count { get; set; }
    public double duration { get; set; }
    public Guid[] credit_uuids { get; set; } = [];
    public string[] roles { get; set; } = [];
}

public sealed class NormalizedShowReference
{
    public Guid show_uuid { get; set; }
    public Guid artist_uuid { get; set; }
    public string display_date { get; set; } = "";
    public string performance_type { get; set; } = "unknown";
    public string? performance_label { get; set; }
    public string[] match_evidence { get; set; } = [];
    public string association_kind { get; set; } = "venue_assignment";
    public string[] decision_basis { get; set; } = [];
}

/// <summary>Historical accepted credits remain readable after a catalog source or track disappears.</summary>
public sealed class NormalizedRecordingCredit
{
    public Guid uuid { get; set; }
    public Guid source_uuid { get; set; }
    public Guid? person_uuid { get; set; }
    public Guid? scope_track_uuid { get; set; }
    public string role { get; set; } = "";
    public string original_text { get; set; } = "";
    public string basis { get; set; } = "";
    public long revision { get; set; }
    public Guid decision_uuid { get; set; }
    public bool reference_available { get; set; }
    public string evidence_status { get; set; } = "missing";
    public IReadOnlyList<NormalizationPublicEvidence> evidence { get; set; } = [];
}
