using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>
/// A prediction-only package. Source preparation and independent labels remain separate artifacts.
/// File names are fixed so a manifest cannot cause arbitrary filesystem reads.
/// </summary>
public sealed record CompositionEvaluationPackageManifest(
    string Version,
    IReadOnlyDictionary<string, CompositionEvaluationFile> Files
);

public sealed record CompositionEvaluationFile(long Bytes, string Sha256, int Rows);

/// <summary>
/// Original bytes and their resource profile. Origin explains whether Input.Uuid is an existing
/// retained revision or a package-local offline coordinate; neither grants database authority.
/// Decode requests the actual provider decoder. Unsupported official evidence can remain undecoded.
/// </summary>
public sealed record CompositionEvaluationDocument(
    NormalizationInputRevision Input,
    bool Decode,
    string Origin
);

public sealed record CompositionEvaluationIdentity(
    MatchingExternalIdentity Identity,
    NormalizedEvidenceReference Evidence
);

/// <summary>
/// Cited input facts, never label-derived features. AuthorUuids must be empty unless accepted
/// canonical-person authority is independently available in ContextEvidence. External author MBIDs
/// and adjudication IDs are not canonical-person UUIDs. CompleteAuthorship retains source semantics.
/// </summary>
public sealed record CompositionEvaluationRecord(
    string Id,
    IdentityMatchingDomain Domain,
    Guid? CatalogUuid,
    Guid? ArtistUuid,
    IReadOnlyList<ObservedValue<string>> Titles,
    IReadOnlyList<CompositionEvaluationIdentity> ExternalIdentities,
    IReadOnlyList<Guid> AuthorUuids,
    bool CompleteAuthorship,
    Guid? CompositionUuid,
    IReadOnlyList<NormalizedEvidenceReference> ContextEvidence
);

/// <summary>
/// Complete means that every record in this frozen catalog/index scope was enumerated. It never
/// describes all external works. Search return membership must not define evaluation eligibility.
/// </summary>
public sealed record CompositionEvaluationUniverse(
    string Id,
    IdentityMatchingDomain Domain,
    IReadOnlyList<string> RecordIds,
    bool Complete,
    IReadOnlyList<string> Issues
);

public enum CompositionEvaluationRetrievalState
{
    Available,
    Unavailable,
    NotRun,
}

/// <summary>
/// CandidateIds retain retrieved members of the independently frozen universe. Unrepresented
/// identities retain returned hits lacking a record projection. Partial successes survive a later
/// unavailable page; Issues and exact evidence retain that limitation.
/// </summary>
public sealed record CompositionEvaluationRetrieval(
    CompositionEvaluationRetrievalState State,
    IReadOnlyList<string> CandidateIds,
    IReadOnlyList<ObservedProviderIdentity> UnrepresentedIdentities,
    IReadOnlyList<string> Issues,
    IReadOnlyList<NormalizedEvidenceReference> Evidence
);

public sealed record CompositionEvaluationTask(
    string Id,
    IdentityMatchingDomain Domain,
    string ClaimId,
    string UniverseId,
    CompositionEvaluationRetrieval Retrieval,
    MusicBrainzSearchSeed? SearchSeed
);

public sealed record CompositionEvaluationCandidateOutcome(
    string RecordId,
    bool Retrieved,
    IdentityCandidateComparison Comparison
);

/// <summary>
/// Pair diagnostics over every frozen candidate, with retrieval coverage kept separate. No
/// accepted work, new-work truth, label, identity merge or publication decision is produced.
/// </summary>
public sealed record CompositionEvaluationOutcome(
    string TaskId,
    IdentityMatchingDomain Domain,
    string ClaimId,
    string UniverseId,
    bool UniverseComplete,
    IReadOnlyList<string> UniverseIssues,
    CompositionEvaluationRetrieval Retrieval,
    MusicBrainzSearchVariants? QueryPlan,
    IReadOnlyList<CompositionEvaluationCandidateOutcome> Candidates
);

public sealed record CompositionEvaluationDecodedInput(
    Guid InputUuid,
    string Origin,
    ProviderDecodeResult Observations
);

public sealed record CompositionEvaluationReport(
    string State,
    string Version,
    string PackageManifestSha256,
    IReadOnlyDictionary<string, CompositionEvaluationFile> InputFiles,
    IReadOnlyDictionary<string, CompositionEvaluationFile> OutputFiles,
    int Tasks,
    int CandidateComparisons,
    int DecodedInputs,
    string DecoderVersion,
    string QueryBuilderVersion,
    string MatcherAssemblySha256,
    string ExporterAssemblySha256,
    bool PolicyActivationPermitted
);
