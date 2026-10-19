using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Relisten.Api.Models.Normalization;

/// <summary>Original coordinates copied by the retained-batch adapter, never fetched by the selector.</summary>
public sealed record CompositionSelectionWitness(
    string Run,
    string Chunk,
    int Line,
    long RecordId,
    string? ValuePointer,
    string ChunkSha256,
    string DocumentKind
);

public sealed record CompositionSelectionDictionaryName(
    string InputKey,
    string JsonPointer,
    string Quote,
    CompositionSelectionWitness? RetainedWitness
);

/// <summary>One artist-scoped song interpretation; its performer is not necessarily its writer.</summary>
public sealed record CompositionSelectionSong(
    string Scope,
    Guid ArtistUuid,
    string? ArtistName,
    string EntityKey,
    string Title,
    string Basis,
    Guid? LegacySongUuid,
    IReadOnlyList<CompositionSelectionDictionaryName> DictionaryNames,
    IReadOnlyList<string> Relations,
    CompositionSelectionWitness SourceWitness,
    long OriginalMemberships,
    CompositionSelectionArtistContext? ArtistContext = null
);

/// <summary>Pins the actual scoped artists.name cell supplied by the retained context artifact.</summary>
public sealed record CompositionSelectionArtistContext(
    string ManifestSha256,
    string CatalogueSha256,
    string Kind,
    string Table,
    string Field,
    string Scope,
    Guid ArtistUuid,
    string? Name,
    Guid? MusicBrainzId
);

/// <summary>Exact provider role, direction and target kind; no canonical authorship is allocated.</summary>
public sealed record CompositionSelectionWriterAssertion(
    ObservedValue<string>? Role,
    ObservedValue<string>? Direction,
    ObservedValue<string>? TargetType,
    MusicBrainzEntityReference? Artist,
    NormalizedEvidenceReference Evidence,
    string SourceForm
);

public sealed record CompositionSelectionSearchAssertion(
    ProviderObservationOrigin Page,
    MusicBrainzSearchHitObservation Hit
);

public sealed record CompositionSelectionRecordingRelationship(
    ObservedProviderIdentity Recording,
    ObservedProviderIdentity Work,
    MusicBrainzRelationshipObservation Relationship
);

/// <summary>
/// Supplied qualified work facts. Missing rivals or writer assertions do not make
/// the external work universe or authorship set complete.
/// </summary>
public sealed record CompositionSelectionWork(
    Guid WorkMbid,
    string EntityKey,
    ObservedProviderIdentity Identity,
    IReadOnlyList<ObservedValue<string>> Titles,
    IReadOnlyList<MusicBrainzAliasObservation> Aliases,
    IReadOnlyList<MusicBrainzIswcObservation> Iswcs,
    IReadOnlyList<CompositionSelectionWriterAssertion> WriterRoles,
    IReadOnlyList<MusicBrainzWorkObservation> LookupAssertions,
    IReadOnlyList<CompositionSelectionSearchAssertion> SearchAssertions,
    IReadOnlyList<CompositionSelectionRecordingRelationship> RecordingWorkRelationships,
    IReadOnlyList<MusicBrainzRecordingObservation> RecordingAssertions
)
{
    public bool? CompleteAuthorship => null;
    public bool ExternalUniverseComplete => false;
}

/// <summary>The complete supplied candidate universe for one cacheable inference request.</summary>
public sealed record CompositionHypothesisSelectionInput(
    CompositionSelectionSong Song,
    IReadOnlyList<CompositionSelectionWork> SuppliedCandidates,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        CompositionSelectionTargetContext? TargetContext = null
);

/// <summary>
/// All retained views of this target. These are source observations, not a claim
/// that a catalog artist, house band or named guest composed the selected work.
/// </summary>
public sealed record CompositionSelectionTargetContext(
    CompositionSelectionTargetKey Key,
    CompositionSelectionTargetArtist Artist,
    CompositionSelectionTargetCoverage Coverage,
    IReadOnlyList<CompositionSelectionTargetWitness> Witnesses
);

public sealed record CompositionSelectionTargetKey(
    string Scope,
    Guid ArtistUuid,
    string SongEntityKey
);

public sealed record CompositionSelectionTargetOrigin(
    string Run,
    string Chunk,
    long Line,
    string Sha256,
    string JsonPointer
);

public sealed record CompositionSelectionTargetArtist(
    string? Name,
    Guid? MusicBrainzId,
    IReadOnlyList<CompositionSelectionTargetObservation> Observations
);

public sealed record CompositionSelectionTargetObservation(
    string Field,
    JsonElement Value,
    CompositionSelectionTargetOrigin Origin
);

/// <summary>Complete retrieval may still report unavailable recording text, IDs or other facts.</summary>
public sealed record CompositionSelectionTargetCoverage(
    long FileWitnesses,
    long RecordingContexts,
    long SetlistWitnesses,
    long MissingRecordingContexts,
    bool MissingArtistMbid,
    long OmittedWitnesses,
    bool Complete
);

public sealed record CompositionSelectionTargetText(
    string Field,
    string TextSha256,
    string Value,
    CompositionSelectionTargetOrigin Origin,
    string? Format = null
);

/// <summary>The original typed claim retains its own role and evidence coordinates.</summary>
public sealed record CompositionSelectionTargetClaim(
    string Kind,
    string? Role,
    string? Value,
    JsonElement Observation,
    CompositionSelectionTargetOrigin Origin
);

public sealed record CompositionSelectionTargetWitness(
    string Kind,
    CompositionSelectionTargetOrigin Origin,
    Guid? FileUuid,
    Guid? SourceUuid,
    Guid? ShowUuid,
    string? NativeFileKey,
    string? RecordingKey,
    string? SetlistKey,
    bool? Ordered,
    IReadOnlyList<string> MatchingRepertoireKeys,
    IReadOnlyList<CompositionSelectionTargetText> Texts,
    IReadOnlyList<CompositionSelectionTargetClaim> Claims,
    IReadOnlyList<string> MissingFields
);

/// <summary>
/// Model inference and its request receipt, separate from observed work facts.
/// State describes execution; Outcome is selected or insufficient_evidence for a
/// valid response. Unknown billing remains accounted by the shared job ledger.
/// </summary>
public sealed record CompositionHypothesisSelectionResult(
    string State,
    Guid? SelectedWorkMbid,
    IReadOnlyList<Guid> AlternativeWorkMbids,
    string Outcome,
    string Rationale,
    string Method,
    string? CacheKey = null,
    bool FromCache = false,
    string? Reason = null,
    string? ProviderRequestId = null,
    long? InputTokens = null,
    long? OutputTokens = null,
    decimal? ActualUsd = null
)
{
    public bool PublicationPermitted => false;
    public bool ExternalUniverseComplete => false;
}

/// <summary>Pure preflight for the exact renderer; no key, cache, admission or provider request is used.</summary>
public sealed record CompositionHypothesisRequestDescription(
    string Method,
    string Model,
    string CacheKey,
    int RenderedBytes,
    int SuppliedCandidates,
    bool WithinRequestLimit,
    decimal MaximumReservedUsd
);
