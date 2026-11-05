using System;
using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;
using Relisten.Api.Models;
using Relisten.Identifiers;

namespace Relisten.Api.Models.Api.V4;

/// <summary>Anonymous playback telemetry. Reporting does not require an account.</summary>
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed class PlayRequest
{
    [Required]
    public string? TrackId { get; init; }

    [Required]
    public PlayAppType? AppType { get; init; }

    /// <summary>Optional caller UUID for telemetry; unrelated to authenticated user_ IDs.</summary>
    public Guid? ClientUuid { get; init; }
}

// Keep legacy telemetry enum serialization unchanged. Values map to its stored
// smallints; the v4 vocabulary excludes Unknown and has explicit wire spellings.
[JsonConverter(typeof(StringEnumConverter), typeof(DefaultNamingStrategy), new object[0], false)]
public enum PlayAppType
{
    [EnumMember(Value = "ios")]
    Ios = (int)SourceTrackPlayAppType.iOS,

    [EnumMember(Value = "web")]
    Web = (int)SourceTrackPlayAppType.Web,

    [EnumMember(Value = "sonos")]
    Sonos = (int)SourceTrackPlayAppType.Sonos,

    [EnumMember(Value = "android")]
    Android = (int)SourceTrackPlayAppType.Android,
}

/// <summary>An accepted event. Repeating a report records another play; it is not idempotent.</summary>
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed record PlayReceiptResource(
    int Sequence,
    DateTime PlayedAt,
    TrackId TrackId,
    PlayAppType AppType
);

/// <summary>Public recent activity, without caller telemetry or account identifiers.</summary>
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed record LiveHistoryResource(
    int Sequence,
    DateTime PlayedAt,
    PlayAppType AppType,
    TrackResource Track,
    ShowSummaryResource Show
);
