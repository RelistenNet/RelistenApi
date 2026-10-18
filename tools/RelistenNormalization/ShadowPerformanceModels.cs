using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>
/// A captured catalog home. Missing show context stays unknown; dates and source sets
/// never allocate another performance or change the home of playable audio.
/// </summary>
public sealed record ShadowPerformanceHome(
    Guid? ShowUuid,
    long? ShowId,
    Guid? SourceUuid,
    Guid? FileUuid,
    Guid? SourceSetUuid,
    string? LiteralDate,
    string PerformanceType,
    string? PerformanceLabel,
    IReadOnlyList<string> Unresolved
);

/// <summary>Direct catalog playback; no song occurrence or inferred audio boundary is required.</summary>
public sealed record ShadowFilePlayback(
    Guid FileUuid,
    Guid? SourceUuid,
    Guid? ShowUuid,
    string? Mp3Url
);

/// <summary>Title mentions link to repertoire proposals while retaining the complete original file.</summary>
public sealed record ShadowFileContents(
    ShadowPerformanceHome Home,
    ShadowFilePlayback Playback,
    ShadowRepertoireTrack? TitleInterpretation
);

/// <summary>
/// An unordered stored membership has neither an occurrence identity nor a position.
/// Repeated supplied memberships survive; collection iteration order supplies no musical order.
/// </summary>
public sealed record ShadowSetlistMembership(
    long? LegacySongId,
    Guid? LegacySongUuid,
    string? SongEntityKey,
    string? Title
);

/// <summary>
/// One explicitly ordered provider passage. Its key identifies a shadow occurrence in
/// this retained account, not an accepted canonical performance or an audio segment.
/// </summary>
public sealed record ShadowSetlistOccurrence(
    string EntityKey,
    int? Ordinal,
    int? SetOrdinal,
    string Title,
    string? SongEntityKey,
    ProviderPassageObservation Observation,
    ShadowRepertoireSegment SongInterpretation
);

/// <summary>An account remains distinct from the catalog performance it may eventually describe.</summary>
public sealed record ShadowSetlistAccount(
    string EntityKey,
    Guid? LegacySetlistUuid,
    Guid? ShowUuid,
    string? LiteralDate,
    string? PerformanceEntityKey,
    bool Ordered,
    IReadOnlyList<ShadowSetlistMembership> Memberships,
    IReadOnlyList<ShadowSetlistOccurrence> Occurrences,
    ProviderSetlistObservation? ProviderAccount
);

/// <summary>Provider recording/account identity without a fabricated catalog show UUID.</summary>
public sealed record ShadowProviderPerformance(
    string EntityKey,
    NormalizationResourceKey Identity,
    string? LiteralDate,
    ProviderObservationOrigin Origin
);
