using System;
using System.Collections.Generic;

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
    public string Basis => "title_derived";
    public Guid? OccurrenceUuid => null;
    public long? StartMs => null;
    public long? EndMs => null;
}

/// <summary>Syntax removed from a content mention, retained with its exact evidence.</summary>
public sealed record TrackTitleQualifier(string Kind, NormalizationTextEvidence Evidence);

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
