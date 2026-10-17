using System;
using Relisten.Ingestion.Providers.TapersSection;

namespace Relisten.Api.Models;

/// <summary>
/// An explicit item-level review targets a performing-project candidate, not a global creator alias.
/// Approval permits evaluation against current evidence; artist approval and all normal publication checks still apply.
/// Alternatives name unresolved performers and hold new publication or canonical binding until resolved.
/// </summary>
public sealed record ArchivePerformerReview(
    string ItemIdentifier,
    Guid CandidateId,
    Guid SupportingObservationId,
    TapersSectionEvidence[] Evidence,
    string ReviewBasis,
    bool Approved = false,
    string[]? Alternatives = null
);

/// <summary>
/// Retained performer review and its immutable supporting projection. Publication must recheck
/// approval under row locks and verify that each supported field still matches the current observation.
/// The projection hash identifies evidence; it is not the applicability test for a later observation.
/// </summary>
public sealed record ArchivePerformerDecision(
    string ItemIdentifier,
    Guid CandidateId,
    Guid SupportingObservationId,
    bool Approved,
    TapersSectionEvidence[] Evidence,
    string ReviewBasis,
    string[] Alternatives,
    long Revision,
    Guid? SelectedArtistUuid,
    string SupportingPayloadJson,
    string SupportingContentHash
)
{
    /// <summary>Current reviewed candidate vocabulary used to recheck the retained project evidence.</summary>
    public string[] ProjectLabels { get; init; } = [];
}
