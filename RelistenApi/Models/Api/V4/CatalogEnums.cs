using System.Runtime.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Relisten.Api.Models.Api.V4;

// Closed public vocabularies. EnumMember preserves the wire spelling independently
// of C# names; Newtonsoft uses these same types when generating OpenAPI schemas.
// Notes and provider labels remain open text. Database constraints define the
// normalized kind, precision and qualifier vocabularies.

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
public enum NormalizedSongKind
{
    [EnumMember(Value = "song")]
    Song,

    [EnumMember(Value = "jam")]
    Jam,

    [EnumMember(Value = "segment")]
    Segment,

    [EnumMember(Value = "tuning")]
    Tuning,

    [EnumMember(Value = "banter")]
    Banter,

    [EnumMember(Value = "other")]
    Other,
}

[JsonConverter(typeof(StringEnumConverter))]
public enum NormalizedVenueKind
{
    [EnumMember(Value = "site")]
    Site,

    [EnumMember(Value = "room")]
    Room,
}

[JsonConverter(typeof(StringEnumConverter))]
public enum VenueCoordinatePrecision
{
    [EnumMember(Value = "venue")]
    Venue,

    [EnumMember(Value = "city")]
    City,
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

[JsonConverter(typeof(StringEnumConverter))]
public enum TrackQualifier
{
    [EnumMember(Value = "reprise")]
    Reprise,

    [EnumMember(Value = "partial")]
    Partial,

    [EnumMember(Value = "unfinished")]
    Unfinished,

    [EnumMember(Value = "instrumental")]
    Instrumental,

    [EnumMember(Value = "acoustic")]
    Acoustic,

    [EnumMember(Value = "with_guest")]
    WithGuest,

    [EnumMember(Value = "continued")]
    Continued,

    [EnumMember(Value = "tape_flip")]
    TapeFlip,

    [EnumMember(Value = "soundcheck")]
    Soundcheck,

    [EnumMember(Value = "tease")]
    Tease,

    [EnumMember(Value = "intro")]
    Intro,

    [EnumMember(Value = "outro")]
    Outro,

    [EnumMember(Value = "jam")]
    Jam,
}
