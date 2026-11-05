using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Relisten.Identifiers;

namespace Relisten.Api.Models.Api.V4;

/// <summary>Public activity over a completed window. Plays are track starts, not distinct listeners.</summary>
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed record LiveSummaryResource(
    int WindowMinutes,
    DateTime WindowStart,
    DateTime WindowEnd,
    long? LastSequence,
    long TotalPlays,
    long ListenedSeconds,
    IReadOnlyList<LiveAppTypeResource> AppTypes,
    IReadOnlyList<LiveTimelineResource> Timeline,
    int ArtistCount,
    long OtherArtistsPlays,
    IReadOnlyList<LiveShowResource> Shows,
    IReadOnlyList<LiveArtistResource> Artists,
    IReadOnlyList<LiveShowResource> DeepCuts,
    IReadOnlyList<LiveShowResource> Fresh,
    LiveOnThisDayResource OnThisDay
);

[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed record LiveAppTypeResource(PlayAppType AppType, long Plays);

[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed record LiveTimelineResource(DateTime BucketStart, long Plays);

/// <summary>
/// Activity for one show. Source is its most-played recording in the window;
/// latest_play may come from another source. Fresh entries describe recent concerts.
/// </summary>
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed record LiveShowResource(
    ShowSummaryResource Show,
    SourceSummaryResource Source,
    LiveLatestPlayResource LatestPlay,
    long Plays,
    long ListenedSeconds,
    long Plays30d,
    double ExpectedPlays,
    double Surprise,
    bool IsAnniversary,
    int SourceCount,
    DateTime FirstPlayedAt,
    DateTime LastPlayedAt,
    IReadOnlyList<int> Timeline
);

[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed record LiveLatestPlayResource(TrackResource Track, DateTime PlayedAt);

[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed record LiveArtistResource(
    ArtistId ArtistId,
    long Plays,
    long ListenedSeconds,
    int ShowCount,
    double Share,
    double ExpectedPlays,
    double Surprise,
    LiveShowResource? TopShow
);

[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed record LiveOnThisDayResource(
    string MonthDay,
    long Plays,
    int ShowCount,
    IReadOnlyList<ShowId> ShowIds
);
