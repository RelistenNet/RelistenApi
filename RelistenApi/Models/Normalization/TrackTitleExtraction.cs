using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Relisten.Api.Models.Normalization;

/// <summary>
/// Artist-scoped interpretation of a retained file title. Positions describe title
/// mentions, not performance occurrences or audio boundaries. Matching and acceptance
/// remain separate decisions.
/// </summary>
public sealed record TrackTitleExtraction(
    Guid ArtistUuid,
    string OriginalTitle,
    IReadOnlyList<TrackTitleSegment> Segments,
    TrackTitleSequence Sequence,
    IReadOnlyList<TrackTitleQualifier> Qualifiers,
    bool IsResolved,
    string? Reason
);

/// <summary>
/// A provisional content mention. Evidence offsets count Unicode code points in
/// OriginalTitle. A segue marker records what the title says; it does not assert
/// an accepted performance transition. Reprise remains part of a song's title.
/// </summary>
public sealed record TrackTitleSegment(
    int Position,
    TrackContentKind Kind,
    string? Title,
    NormalizationTextEvidence Evidence,
    TitleCoverage Coverage,
    TitleTransition TransitionAfter
)
{
    /// <summary>
    /// A syntax-only alternative for dictionary review. The retained Title takes
    /// priority: a named jam, tease, reprise or practice can have its own identity.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TrackTitleCoreCandidate? CoreTitle { get; init; }

    public string Basis => "title_derived";
    public Guid? OccurrenceUuid => null;
    public long? StartMs => null;
    public long? EndMs => null;
}

/// <summary>Syntax removed from a content mention, retained with its exact evidence.</summary>
public sealed record TrackTitleQualifier(string Kind, NormalizationTextEvidence Evidence)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TrackTitleStructuralKind? StructuralKind { get; init; }
}

public sealed record TrackTitleCoreCandidate(
    string Title,
    NormalizationTextEvidence Evidence,
    IReadOnlyList<TrackTitleVariant> Variants
);

public sealed record TrackTitleVariant(
    TrackTitleVariantKind Kind,
    NormalizationTextEvidence Evidence
);

public enum TrackTitleVariantKind
{
    Jam,
    Tease,
    Reprise,
    Practice,
}

public enum TrackTitleStructuralKind
{
    FilenameMetadata,
    AudioExtension,
    TrackNumber,
    SetLabel,
    Duration,
    PartialCoverage,
    Aborted,
    Filler,
    Separator,
    PartialMarker,
}

public enum TrackContentKind
{
    Song,
    Jam,
    Speech,
    Nonmusic,
    Unknown,
}

public enum TrackTitleSequence
{
    Single,
    TitleOrder,
    Unresolved,
}

public enum TitleCoverage
{
    Unknown,
    Partial,
    PartialMarker,
}

public enum TitleTransition
{
    Unknown,
    SegueMarker,
}
