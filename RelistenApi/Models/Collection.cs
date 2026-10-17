using System;
using System.Collections.Generic;

namespace Relisten.Api.Models;

/// <summary>Operator-owned collection identity. Keep the UUID and slug when changing its description or publication state.</summary>
public sealed record CollectionDefinition(
    Guid Uuid,
    string Slug,
    string Name,
    string Description,
    bool Published = false
);

/// <summary>An upstream discovery boundary, independent of any artist-to-provider mapping.</summary>
public sealed record CollectionSelectorDefinition(
    Guid Uuid,
    Guid CollectionUuid,
    int UpstreamSourceId,
    string UpstreamIdentifier,
    bool Enabled = true
);

/// <summary>Live catalog page. A cursor continues this resource's ordering; it is not a snapshot.</summary>
public sealed class CollectionPage<T>
{
    public IReadOnlyList<T> items { get; init; } = [];
    public string? next_cursor { get; init; }
}

public sealed class RelistenCollection
{
    public Guid uuid { get; set; }
    public string slug { get; set; } = "";
    public string name { get; set; } = "";
    public string description { get; set; } = "";
    public bool published { get; set; }
    public long source_count { get; set; }
    public long show_count { get; set; }
    public long artist_count { get; set; }
}

/// <summary>Artist facet derived from playable collection contents, with explicit artist-wide inclusion identified separately.</summary>
public sealed class CollectionArtist
{
    public Guid uuid { get; set; }
    public string name { get; set; } = "";
    public string slug { get; set; } = "";
    public string sort_name { get; set; } = "";
    public bool includes_all_recordings { get; set; }
    public long source_count { get; set; }
    public long show_count { get; set; }
}

/// <summary>Canonical playback references. Duration is the sum of playable track durations in seconds.</summary>
public sealed class CollectionRecording
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
}

/// <summary>Retained inclusion basis for operator inspection, including retired or missing catalog references.</summary>
public sealed class CollectionMembershipStatus
{
    public Guid collection_uuid { get; set; }
    public string basis { get; set; } = "";
    public Guid? artist_uuid { get; set; }
    public Guid? source_uuid { get; set; }
    public Guid? selector_uuid { get; set; }
    public string? item_identifier { get; set; }
    public bool active { get; set; }
    public string reason { get; set; } = "";
    public bool reference_available { get; set; }
}

/// <summary>Operator selector state; a scan's page writes and durable checkpoint must commit together.</summary>
public sealed class CollectionSelector
{
    public Guid uuid { get; set; }
    public Guid collection_uuid { get; set; }
    public int upstream_source_id { get; set; }
    public string upstream_identifier { get; set; } = "";
    public bool enabled { get; set; }
    public Guid? scan_id { get; set; }
    public string scan_state { get; set; } = "idle";
    public string? cursor { get; set; }
    public DateTimeOffset? completed_at { get; set; }
    public string? last_error { get; set; }
    public bool search_complete { get; set; }
    public string[] seen_cursors { get; set; } = [];
    public long discovered_count { get; set; }
    public string[] pending_items { get; set; } = [];
}

/// <summary>
/// Resolves a shared Archive item without acquiring ingestion ownership. Supply an expected artist
/// only from retained performer evidence. A current metadata contradiction must be supplied as a
/// conflict reason; otherwise an exact existing canonical source can establish the performer.
/// </summary>
public sealed record ArchiveItemResolutionRequest(string item_identifier)
{
    public Guid? observation_id { get; init; }
    public Guid? expected_artist_uuid { get; init; }
    public string decision_basis { get; init; } = "exact_archive_catalog_item";
    public string? conflict_reason { get; init; }
}

/// <summary>Shared item-level performer and canonical-source decision, independent of collection membership and write ownership.</summary>
public sealed class ArchiveItemResolution
{
    public string item_identifier { get; set; } = "";
    public Guid? observation_id { get; set; }
    public Guid? accepted_observation_id { get; set; }
    public Guid? selected_artist_uuid { get; set; }
    public Guid? canonical_source_uuid { get; set; }
    public string decision_basis { get; set; } = "";
    public string status { get; set; } = "pending";
    public string? reason { get; set; }
    public string? last_error { get; set; }
    public DateTimeOffset? last_attempt_at { get; set; }
}
