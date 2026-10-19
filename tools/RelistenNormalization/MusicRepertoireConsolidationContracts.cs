using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>A byte-pinned derived file, never a new catalog capture or accepted identity.</summary>
public sealed record MusicConsolidationArtifact(string Path, long Bytes, string Sha256, long Rows);

/// <summary>
/// The exporter authenticates these exact title values against current immutable result
/// witnesses. DictionaryNames contains every retained literal dictionary spelling, not
/// just the catalogue's preferred display title. Source scopes remain independent.
/// </summary>
public sealed record MusicConsolidationInput(
    string Version,
    string State,
    MusicConsolidationCatalogue Catalogue,
    MusicConsolidationArtifact Descriptors,
    MusicConsolidationArtifact DictionaryNames,
    IReadOnlyList<MusicConsolidationSource> Sources,
    IReadOnlyList<MusicConsolidationWitnessChunk> WitnessChunks
);

public sealed record MusicConsolidationCatalogue(
    string Path,
    long Bytes,
    string Sha256,
    double BuiltAt
);

public sealed record MusicConsolidationSource(int Id, string Scope, string Run);

public sealed record MusicConsolidationWitnessChunk(
    string Run,
    string Chunk,
    long Bytes,
    string Sha256
);

public sealed record MusicTitleWitness(
    string Run,
    string Chunk,
    int Line,
    long RecordId,
    string? ValuePointer,
    string ChunkSha256,
    string DocumentKind = "retained_result"
);

public sealed record MusicRepertoireDescriptor(
    string Scope,
    Guid ArtistUuid,
    string OriginalKey,
    string? Title,
    string Basis,
    string Status,
    Guid? LegacySongUuid,
    IReadOnlyList<string> WorkKeys,
    MusicTitleWitness Witness
);

public sealed record MusicDictionaryName(
    string Scope,
    Guid ArtistUuid,
    string? SongKey,
    Guid LegacySongUuid,
    string Title,
    string InputKey,
    IReadOnlyList<string> WorkKeys,
    MusicTitleWitness Witness
);

/// <summary>
/// Each target is a shadow interpretation of the cited original title. CompoundMember
/// records inferred textual membership; variant relations do not assert title identity.
/// Neither implies a segue, audio boundaries, a new performance or accepted authority.
/// </summary>
public enum MusicRepertoireRelation
{
    IdentityAlias,
    CompoundMember,
    Jam,
    Tease,
    Reprise,
    Practice,
}

public sealed record MusicRepertoireTarget(
    ShadowRepertoireSong Song,
    MusicRepertoireRelation Relation,
    string Disposition,
    string Reason,
    NormalizationTextEvidence Evidence,
    IReadOnlyList<TrackTitleQualifier> Qualifiers,
    IReadOnlyList<ShadowRepertoireCandidate> ComparisonDiagnostics
);

/// <summary>
/// One logical result per original scoped key. The raw key and witness always survive.
/// A compound may have several targets. Review/unresolved outcomes remain in the image.
/// </summary>
public sealed record MusicRepertoireCorrespondence(
    string Scope,
    Guid ArtistUuid,
    string OriginalKey,
    string? OriginalTitle,
    string Disposition,
    string Reason,
    string SequenceBasis,
    IReadOnlyList<MusicRepertoireTarget> Targets,
    MusicTitleWitness SourceWitness,
    TrackTitleExtraction? Extraction
);

public sealed record MusicConsolidationOutput(
    string Version,
    bool Complete,
    string InputManifestSha256,
    string ExtractorVersion,
    string ResolverVersion,
    long LogicalRows,
    IReadOnlyList<MusicConsolidationArtifact> Chunks,
    MusicConsolidationArtifact Progress
);
