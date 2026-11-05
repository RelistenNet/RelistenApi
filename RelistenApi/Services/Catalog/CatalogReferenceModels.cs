using System;
using System.Runtime.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Relisten.Catalog;

[JsonConverter(typeof(StringEnumConverter))]
public enum CatalogReferenceKind
{
    [EnumMember(Value = "artist")]
    Artist,

    [EnumMember(Value = "show")]
    Show,

    [EnumMember(Value = "source")]
    Source,

    [EnumMember(Value = "source_set")]
    SourceSet,

    [EnumMember(Value = "track")]
    Track,

    [EnumMember(Value = "year")]
    Year,

    [EnumMember(Value = "tour")]
    Tour,

    [EnumMember(Value = "imported_venue")]
    ImportedVenue,

    [EnumMember(Value = "imported_song")]
    ImportedSong,

    [EnumMember(Value = "setlist_show")]
    SetlistShow,

    [EnumMember(Value = "source_review")]
    SourceReview,

    [EnumMember(Value = "venue")]
    Venue,

    [EnumMember(Value = "song")]
    Song,

    [EnumMember(Value = "artist_venue")]
    ArtistVenue,
}

[JsonConverter(typeof(StringEnumConverter))]
public enum CatalogReferenceScheme
{
    [EnumMember(Value = "v4")]
    V4,

    [EnumMember(Value = "legacy_uuid")]
    LegacyUuid,

    [EnumMember(Value = "legacy_integer")]
    LegacyInteger,

    [EnumMember(Value = "legacy_slug")]
    LegacySlug,
}

[JsonConverter(typeof(StringEnumConverter))]
public enum CatalogReferenceAvailability
{
    [EnumMember(Value = "available")]
    Available,

    [EnumMember(Value = "unavailable")]
    Unavailable,

    [EnumMember(Value = "redirected")]
    Redirected,

    [EnumMember(Value = "retired")]
    Retired,
}

/// <summary>A validated identity request. Artist context disambiguates owned legacy identities.</summary>
public sealed record CatalogReference(
    CatalogReferenceKind Kind,
    CatalogReferenceScheme Scheme,
    string Id,
    Guid? ArtistUuid
);

/// <summary>Current identity and its UUID when backed by an imported catalog row.</summary>
public sealed record CatalogResolvedTarget(CatalogReferenceKind Kind, string Id, Guid? Uuid);

public sealed record CatalogReferenceResolution(
    CatalogReference Reference,
    CatalogReferenceAvailability Availability,
    CatalogResolvedTarget? Target,
    string? Reason
);
