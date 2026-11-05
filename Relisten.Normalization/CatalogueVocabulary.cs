using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace Relisten.Normalization;

// These are persisted normalization vocabularies. EnumMember owns their exact wire
// spelling; changing a token changes catalog rows and durable workflow payloads.
// Host-specific serializers must use this spelling rather than Enum.ToString().
[JsonConverter(typeof(NormalizationEnums.JsonConverter<SongKind>))]
public enum SongKind
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

[JsonConverter(typeof(NormalizationEnums.JsonConverter<VenueKind>))]
public enum VenueKind
{
    [EnumMember(Value = "site")]
    Site,

    [EnumMember(Value = "room")]
    Room,
}

[JsonConverter(typeof(NormalizationEnums.JsonConverter<VenueNameKind>))]
public enum VenueNameKind
{
    [EnumMember(Value = "official")]
    Official,

    [EnumMember(Value = "short")]
    Short,

    [EnumMember(Value = "spelling")]
    Spelling,
}

[JsonConverter(typeof(NormalizationEnums.JsonConverter<VenueCoordinatePrecision>))]
public enum VenueCoordinatePrecision
{
    [EnumMember(Value = "venue")]
    Venue,

    [EnumMember(Value = "city")]
    City,
}

[JsonConverter(typeof(NormalizationEnums.JsonConverter<TrackQualifier>))]
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
