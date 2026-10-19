using System.Text.Json.Serialization;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>
/// All observed assertions for one qualified external work. Artist relationships
/// stay external; their UUIDs never become canonical person or catalog artist IDs.
/// Repeated, conflicting and incomplete observations remain independently cited.
/// </summary>
public sealed record RetainedCompositionWork(
    string EntityKey,
    ObservedProviderIdentity Identity,
    IReadOnlyList<ObservedValue<string>> Titles,
    IReadOnlyList<MusicBrainzAliasObservation> Aliases,
    IReadOnlyList<MusicBrainzIswcObservation> Iswcs,
    IReadOnlyList<RetainedCompositionWriterAssertion> WriterRoles,
    IReadOnlyList<MusicBrainzWorkObservation> LookupAssertions,
    IReadOnlyList<RetainedCompositionSearchAssertion> SearchAssertions,
    IReadOnlyList<ShadowProviderCompositionRelationship> RecordingWorkRelationships,
    IReadOnlyList<MusicBrainzRecordingObservation> RecordingAssertions
)
{
    public bool? CompleteAuthorship => null;
    public bool ExternalUniverseComplete => false;
}

public sealed record RetainedCompositionSearchAssertion(
    ProviderObservationOrigin Page,
    MusicBrainzSearchHitObservation Hit
);

/// <summary>Role/direction/kind are exact provider facts, not a resolved authorship set.</summary>
public sealed record RetainedCompositionWriterAssertion(
    ObservedValue<string>? Role,
    ObservedValue<string>? Direction,
    ObservedValue<string>? TargetType,
    MusicBrainzEntityReference? Artist,
    NormalizedEvidenceReference Evidence,
    string SourceForm
);

/// <summary>
/// One unique proposed target in the completed music correspondence image. The
/// immutable image retains all original memberships; this seed is not a new file,
/// performance, accepted song or assertion that its artist authored the work.
/// </summary>
public sealed record CompositionHypothesisSong(
    string Scope,
    Guid ArtistUuid,
    ShadowRepertoireSong Song,
    IReadOnlyList<ShadowRepertoireDictionaryEvidence> DictionaryNames,
    IReadOnlyList<MusicRepertoireRelation> Relations,
    MusicTitleWitness SourceWitness,
    long OriginalMemberships,
    string? ArtistName = null,
    CompositionArtistContext? ArtistContext = null,
    string? ArtistContextManifestSha256 = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        CompositionSelectionTargetContext? TargetContext = null
);

public sealed record CompositionWorkHypothesis(
    string WorkEntityKey,
    ObservedProviderIdentity WorkIdentity,
    IReadOnlyList<ObservedValue<string>> MatchingTitles,
    string RetrievalBasis,
    IdentityCandidateComparison Comparison
);

/// <summary>
/// Candidate retrieval is deliberately distinct from existing supported Links.
/// Even one retained candidate is a hypothesis; absent rivals do not prove unique
/// identity in the live external universe. Missing candidates remain explicit.
/// </summary>
public sealed record CompositionHypothesisResult(
    CompositionHypothesisSong Song,
    string Outcome,
    string Reason,
    IReadOnlyList<CompositionWorkHypothesis> Candidates,
    string Method
)
{
    public bool ExternalUniverseComplete => false;
    public bool PublicationPermitted => false;
}

/// <summary>
/// A future inference adapter receives only these supplied candidates and their
/// retained facts. Its selection is a labelled hypothesis, never publication or
/// authority to invent a work identifier. No inference adapter is enabled here.
/// </summary>
public interface ICompositionHypothesisSelector
{
    Task<CompositionHypothesisSelection> SelectAsync(
        CompositionHypothesisSong song,
        IReadOnlyList<RetainedCompositionWork> suppliedCandidates,
        CancellationToken ct = default
    );
}

public sealed record CompositionHypothesisSelection(
    Guid? SelectedWorkMbid,
    IReadOnlyList<Guid> AlternativeWorkMbids,
    string Outcome,
    string Method,
    string Rationale,
    CompositionHypothesisSelectionResult? Inference = null
);

public sealed record CompositionHypothesisBatchRequest(
    string Version,
    MusicConsolidationArtifact MusicInputManifest,
    MusicConsolidationArtifact MusicOutputManifest,
    IReadOnlyList<RetainedCompositionInputArtifact> RetainedProviderInputs,
    MusicConsolidationArtifact? ArtistContextManifest = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        CompositionHypothesisResourceLimits? Limits = null
);

/// <summary>Explicit local resource bounds; omitted limits retain the historical batch defaults.</summary>
public sealed record CompositionHypothesisResourceLimits(
    long MinimumFreeBytes,
    long MaximumOutputBytes
);

/// <summary>An actual display name from the frozen catalogue; it implies no authorship.</summary>
public sealed record CompositionArtistContext(
    string Scope,
    Guid ArtistUuid,
    string? Name,
    Guid? MusicBrainzId,
    CompositionArtistContextOrigin Origin
);

public sealed record CompositionArtistContextOrigin(
    string Kind,
    string Table,
    CompositionArtistContextKey Key,
    string Field,
    string CatalogueSha256
);

public sealed record CompositionArtistContextKey(string Scope, Guid Uuid);

public sealed record CompositionArtistContextManifest(
    string Version,
    string State,
    MusicConsolidationArtifact MusicInputManifest,
    MusicConsolidationArtifact MusicOutputManifest,
    MusicConsolidationCatalogue Catalogue,
    MusicConsolidationArtifact Artists,
    IReadOnlyList<MusicConsolidationSource> Sources,
    bool NamesAreAuthorshipEvidence
);

/// <summary>The format is explicit; newly acquired revision JSON needs no wrapper or recapture.</summary>
public sealed record RetainedCompositionInputArtifact(
    string Path,
    long Bytes,
    string Sha256,
    long Rows,
    string Format = "provider_wrappers_jsonl"
);

public sealed record CompositionHypothesisOutput(
    string Version,
    bool Complete,
    string RequestSha256,
    string DecoderVersion,
    string IndexVersion,
    string ProjectorVersion,
    long Songs,
    long Works,
    IReadOnlyDictionary<string, long> Outcomes,
    IReadOnlyList<MusicConsolidationArtifact> Chunks,
    MusicConsolidationArtifact Progress,
    CompositionHypothesisBatchRequest Inputs
);
