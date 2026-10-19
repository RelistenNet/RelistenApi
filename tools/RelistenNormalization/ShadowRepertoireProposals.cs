using System.Text.Json.Serialization;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>A catalog song reference or a title-derived, artist-scoped shadow identity.</summary>
public sealed record ShadowRepertoireSong(
    string EntityKey,
    Guid ArtistUuid,
    Guid? LegacySongUuid,
    string Name,
    string Basis
);

/// <summary>Locates a dictionary title in the retained raw input, without copying its whole row.</summary>
public sealed record ShadowRepertoireDictionaryEvidence(
    string InputKey,
    string JsonPointer,
    string Quote
)
{
    /// <summary>When present, JsonPointer addresses this pinned document (a result or its verified catalog context).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MusicTitleWitness? RetainedWitness { get; init; }
}

/// <summary>
/// A candidate with one actual witness per literal title spelling, ignoring canonical
/// Unicode, surrounding whitespace and case differences. Direct dictionary rows supply
/// the preferred display name; punctuation-distinct membership spellings retain their own
/// proof. Repeated occurrences remain in setlist accounts rather than every matching file.
/// </summary>
public sealed record ShadowRepertoireCandidate(
    ShadowRepertoireSong Song,
    IReadOnlyList<ShadowRepertoireDictionaryEvidence> Evidence,
    IdentityCandidateComparison Comparison
);

/// <summary>
/// A title mention and its proposed local correspondence. Candidates remain separate
/// when more than one catalog song has the same normalized title. Neither a mention
/// nor a correspondence establishes an audio boundary or a composition identity.
/// </summary>
public sealed record ShadowRepertoireSegment(
    int Position,
    TrackContentKind Kind,
    string? Title,
    NormalizationTextEvidence Evidence,
    string Outcome,
    string? Reason,
    ShadowRepertoireSong? Song,
    IReadOnlyList<ShadowRepertoireSong> CandidateSongs,
    IReadOnlyList<ShadowRepertoireCandidate> ComparisonDiagnostics
);

/// <summary>
/// Preserves the actual extractor response alongside shadow links. IsResolvedByExtractor
/// describes title syntax only; supported partial links do not resolve the whole file.
/// Playback stays with FileUuid and SourceUuid under their current catalog ownership.
/// </summary>
public sealed record ShadowRepertoireTrack(
    Guid FileUuid,
    Guid? SourceUuid,
    string ExtractorVersion,
    string OriginalTitle,
    bool IsResolvedByExtractor,
    IReadOnlyList<ShadowRepertoireSegment> Segments,
    IReadOnlyList<ShadowRepertoireCandidate> WholeFileDictionaryCandidates,
    TrackTitleExtraction? Extraction
);
