using System;
using System.Collections.Generic;

namespace Relisten.Api.Models.Normalization;

/// <summary>
/// One stored assignment revision. Historical reviewed rejections are audit facts, not current
/// exclusions after a later correction. Preserve equivalent rows separately, even when their
/// targets now resolve to one survivor. Evidence belongs to this exact assignment revision.
/// </summary>
public sealed record ImportedVenueAssignmentFact(
    Guid AssignmentUuid,
    long Revision,
    Guid StoredVenueUuid,
    Guid? ResolvedVenueUuid,
    string State,
    string Basis,
    string ReasonCode,
    Guid DecisionUuid,
    bool IsHistorical,
    Guid? SupersedingDecisionUuid,
    IReadOnlyList<NormalizedEvidenceReference> Evidence
);

/// <summary>
/// Assignment authority and global venue candidates from one primary read-only snapshot.
/// Assignments includes all current rows and historical reviewed rejections. AssignmentTargets
/// retains each resolved target's facts even when ordinary name retrieval did not select it.
/// Candidates.ReadSet includes those extra owners and the exact imported-assignment owner stamp;
/// use that complete read set, not a later GetSubject result, for matching admission.
/// ContextHash identifies the request, bounds and reader version, not unchanged returned facts.
/// ReadSet supplies the concurrency authority. A missing owner is explicit and makes Complete
/// false; this read never registers one.
/// </summary>
public sealed record NormalizationImportedVenueResolutionContext(
    string Version,
    string ContextHash,
    Guid ImportedVenueUuid,
    NormalizationSubjectState? Owner,
    IReadOnlyList<ImportedVenueAssignmentFact> Assignments,
    IReadOnlyList<VenueCandidateFacts> AssignmentTargets,
    NormalizationCandidateFactsSnapshot<VenueCandidateFacts> Candidates
)
{
    public CandidateFactsReadSet ReadSet => Candidates.ReadSet;
    public bool Complete => Candidates.Complete;
}
