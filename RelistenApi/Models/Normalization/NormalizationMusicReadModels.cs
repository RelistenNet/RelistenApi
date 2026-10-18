using System;
using System.Collections.Generic;

namespace Relisten.Api.Models.Normalization;

/// <summary>Accepted music identity. A title-derived repertoire entry does not establish a global work.</summary>
public abstract class NormalizationMusicEntity
{
    public Guid uuid { get; set; }
    public long revision { get; set; }
    public DateTimeOffset updated_at { get; set; }
    public Guid? requested_uuid { get; set; }
    public Guid? redirect_uuid { get; set; }
    public string display_title { get; set; } = "";
    public string[] aliases { get; set; } = [];
    internal string page_sort { get; set; } = "";
}

public sealed class NormalizedRepertoireSong : NormalizationMusicEntity
{
    public Guid artist_uuid { get; set; }
    public string basis { get; set; } = "";
    public Guid? composition_uuid { get; set; }
    public Guid[] imported_song_uuids { get; set; } = [];
    public NormalizedCompositionLink? composition { get; set; }
    public IReadOnlyList<NormalizationExternalIdentity> external_identities { get; set; } = [];
}

public sealed class NormalizedComposition : NormalizationMusicEntity
{
    public IReadOnlyList<NormalizedCompositionLink> repertoire_links { get; set; } = [];
    public IReadOnlyList<NormalizedWorkCredit> work_credits { get; set; } = [];
    public IReadOnlyList<NormalizationExternalIdentity> external_identities { get; set; } = [];
}

public abstract class NormalizedMusicRelationship
{
    public Guid uuid { get; set; }
    public long revision { get; set; }
    public DateTimeOffset updated_at { get; set; }
    public Guid decision_uuid { get; set; }
    public string basis { get; set; } = "";
    public string evidence_status { get; set; } = "missing";
    public IReadOnlyList<NormalizationPublicEvidence> evidence { get; set; } = [];
    internal Guid revision_uuid { get; set; }
}

public sealed class NormalizedCompositionLink : NormalizedMusicRelationship
{
    public Guid repertoire_song_uuid { get; set; }
    public Guid composition_uuid { get; set; }
    public string match_reason { get; set; } = "";
}

public sealed class NormalizedWorkCredit : NormalizedMusicRelationship
{
    public string role { get; set; } = "";
    public string original_text { get; set; } = "";
    public Guid? person_uuid { get; set; }
    public Guid? artist_uuid { get; set; }
}

/// <summary>An account of a performance, including accounts without a catalog show or available audio.</summary>
public sealed class NormalizedSetlistEdition : NormalizedMusicRelationship
{
    public Guid artist_uuid { get; set; }
    public Guid? show_uuid { get; set; }
    public string literal_date { get; set; } = "";
    public bool ordered { get; set; }
    public bool selected { get; set; }
    public string provider { get; set; } = "";
    public string resource_type { get; set; } = "";
    public string provider_key { get; set; } = "";
}

public sealed class NormalizedSongOccurrence : NormalizedMusicRelationship
{
    public Guid edition_uuid { get; set; }
    public NormalizedSetlistEdition edition { get; set; } = null!;
    public int? position { get; set; }
    public string? set_label { get; set; }
    public Guid? set_uuid { get; set; }
    public string kind { get; set; } = "";
    public Guid? repertoire_song_uuid { get; set; }
    public string original_text { get; set; } = "";
    public string transition_after { get; set; } = "unknown";
    public Guid? parent_occurrence_uuid { get; set; }
    public int? component_position { get; set; }
    public Guid? supersedes_occurrence_uuid { get; set; }
    public string? provider_item_key { get; set; }
    internal string page_sort { get; set; } = "";
}

/// <summary>
/// Accepted file segments retain unknown times and can span songs or sets within one performance.
/// An aligned occurrence must agree with the recording's current show assignment.
/// </summary>
public sealed class NormalizedTrackContent : NormalizedMusicRelationship
{
    public Guid source_track_uuid { get; set; }
    public int position { get; set; }
    public string kind { get; set; } = "";
    public string original_text { get; set; } = "";
    public Guid? repertoire_song_uuid { get; set; }
    public Guid? occurrence_uuid { get; set; }
    public long? start_ms { get; set; }
    public long? end_ms { get; set; }
    public string match_evidence { get; set; } = "";
    public bool reference_available { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    )]
    [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public NormalizedTrackTitleFacts? title_facts { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    )]
    [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public string? title_facts_status { get; set; }
}

/// <summary>What the retained title says; these values establish no audio boundary or verified performance transition.</summary>
public sealed record NormalizedTrackTitleFacts(
    string coverage,
    string transition_after,
    IReadOnlyList<NormalizedTrackTitleQualifier> qualifiers,
    [property: System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    )]
    [property: Newtonsoft.Json.JsonProperty(
        NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore
    )]
        NormalizationPublicEvidence? source_evidence
);

public sealed record NormalizedTrackTitleQualifier(
    string kind,
    NormalizationPublicEvidence evidence
);
