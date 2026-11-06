using System.Runtime.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Relisten.Api.Models.Api.V4;

[JsonConverter(typeof(StringEnumConverter))]
public enum SongEntryKind
{
    [EnumMember(Value = "song")]
    Song,

    [EnumMember(Value = "imported_song")]
    ImportedSong,
}

[JsonConverter(typeof(StringEnumConverter))]
public enum VenueEntryKind
{
    [EnumMember(Value = "artist_venue")]
    ArtistVenue,

    [EnumMember(Value = "imported_venue")]
    ImportedVenue,
}

// The public subset of normalization venue name kinds. Spelling aliases help imports
// and search recognize misspellings but are never returned as venue names.
[JsonConverter(typeof(StringEnumConverter))]
public enum VenueNameResourceKind
{
    [EnumMember(Value = "official")]
    Official,

    [EnumMember(Value = "short")]
    Short,
}

// API-owned response states and recording vocabulary. Normalization kinds,
// precision and qualifiers come directly from Relisten.Normalization.
// EnumMember preserves wire spelling for JSON and OpenAPI.

[JsonConverter(typeof(StringEnumConverter))]
public enum TrackNormalizationState
{
    [EnumMember(Value = "unavailable")]
    Unavailable,

    [EnumMember(Value = "partial")]
    Partial,

    [EnumMember(Value = "interpreted")]
    Interpreted,
}

[JsonConverter(typeof(StringEnumConverter))]
public enum TrackNormalizationUnavailableReason
{
    [EnumMember(Value = "disabled")]
    Disabled,

    [EnumMember(Value = "not_interpreted")]
    NotInterpreted,

    [EnumMember(Value = "inconsistent")]
    Inconsistent,
}

[JsonConverter(typeof(StringEnumConverter))]
public enum TrackPartState
{
    [EnumMember(Value = "matched")]
    Matched,

    [EnumMember(Value = "non_song")]
    NonSong,

    [EnumMember(Value = "unresolved")]
    Unresolved,
}

[JsonConverter(typeof(StringEnumConverter))]
public enum SourceFlacType
{
    [EnumMember(Value = "NoFlac")]
    NoFlac,

    [EnumMember(Value = "Flac16Bit")]
    Flac16Bit,

    [EnumMember(Value = "Flac24Bit")]
    Flac24Bit,

    [EnumMember(Value = "NoPlayableFlac")]
    NoPlayableFlac,

    [EnumMember(Value = "Unknown")]
    Unknown,
}
