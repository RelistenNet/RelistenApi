using System;
using System.Collections.Generic;
using System.Linq;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

/// <summary>
/// One frozen, fully checked old/current field pair. Preparation owns parsing and bijection;
/// each accepted review uses its exact original UUIDs to select one mapped current assertion.
/// This object holds no mutable database authority and creates no new human review.
/// </summary>
public sealed class SourceActorReviewFieldCorrespondence
{
    private readonly SourceActorReviewField original;
    private readonly SourceActorReviewField current;
    private readonly IReadOnlyList<SourceActorReviewLocalIdMap> localIds;
    private readonly IReadOnlyList<SourceActorReviewEvidenceMap> evidence;
    private readonly Dictionary<
        Guid,
        SourceActorAliasAssertion<NormalizedEvidenceReference>
    > originalAssertions;
    private readonly Dictionary<
        Guid,
        SourceActorAliasAssertion<NormalizedEvidenceReference>
    > currentAssertions;
    private readonly Dictionary<Guid, Guid> currentAssertionIds;
    private readonly Dictionary<Guid, Guid> currentActorIds;

    internal SourceActorReviewFieldCorrespondence(
        SourceActorReviewField original,
        SourceActorReviewField current,
        SourceActorAliasExtraction<NormalizedEvidenceReference> originalProjection,
        SourceActorAliasExtraction<NormalizedEvidenceReference> currentProjection,
        IReadOnlyList<SourceActorReviewLocalIdMap> localIds,
        IReadOnlyList<SourceActorReviewEvidenceMap> evidence
    )
    {
        this.original = original;
        this.current = current;
        this.localIds = Frozen(localIds);
        this.evidence = Frozen(evidence);
        originalAssertions = originalProjection.Assertions.ToDictionary(assertion =>
            assertion.AssertionUuid
        );
        currentAssertions = currentProjection.Assertions.ToDictionary(assertion =>
            assertion.AssertionUuid
        );
        currentAssertionIds = localIds
            .Where(id => id.Kind == SourceActorReviewLocalIdKind.Assertion)
            .ToDictionary(id => id.OriginalUuid, id => id.CurrentUuid);
        currentActorIds = localIds
            .Where(id => id.Kind == SourceActorReviewLocalIdKind.ActorReference)
            .ToDictionary(id => id.OriginalUuid, id => id.CurrentUuid);
    }

    /// <summary>
    /// Selects the exact destination of one original claim. This is a field lookup, not an
    /// approval. The caller must authenticate a human review before using TryCreate.
    /// </summary>
    public bool TryGetCurrentFor(
        Guid originalAssertionUuid,
        Guid originalActorReferenceUuid,
        out SourceActorReviewField? destination,
        out string reason
    )
    {
        destination = null;
        if (
            !originalAssertions.TryGetValue(originalAssertionUuid, out var oldAssertion)
            || oldAssertion.ActorReferenceUuid != originalActorReferenceUuid
        )
        {
            reason = "original_claim_missing";
            return false;
        }
        var assertion = currentAssertions[currentAssertionIds[originalAssertionUuid]];
        if (assertion.ActorReferenceUuid != currentActorIds[originalActorReferenceUuid])
        {
            reason = "selected_actor_changed";
            return false;
        }
        destination = Select(current, assertion);
        reason = "source_claim_corresponds";
        return true;
    }

    /// <summary>
    /// Preserves the exact original accepted review and current authenticated target. The
    /// reader still owns receipt authentication and current owner/input freshness fences.
    /// </summary>
    public bool TryCreate(
        SourceActorAliasReviewState originalAcceptedReview,
        SourceActorReviewTarget target,
        out ReviewedSourceCorrespondence? proof,
        out string reason
    )
    {
        proof = null;
        var coordinates = originalAcceptedReview.Coordinates;
        if (
            !TryGetCurrentFor(
                coordinates.AssertionUuid,
                coordinates.ActorReferenceUuid,
                out var destination,
                out reason
            )
        )
        {
            return false;
        }
        var assertion = originalAssertions[coordinates.AssertionUuid];
        var selectedOriginal = Select(original, assertion);
        try
        {
            SourceActorReviewCorrespondence.RequireOriginalReview(
                originalAcceptedReview,
                selectedOriginal,
                target,
                assertion
            );
            SourceActorReviewCorrespondence.RequireTarget(target);

            // The proof shares only frozen field data. The supplied human snapshot and target
            // lists are copied so later caller mutation cannot change an already returned proof.
            var review = originalAcceptedReview with
            {
                Coordinates = selectedOriginal.Coordinates,
                OriginalAssertion = assertion,
                OriginalScope = selectedOriginal.Scope,
            };
            proof = new(
                SourceActorReviewCorrespondence.Version,
                review,
                selectedOriginal,
                destination!,
                localIds,
                evidence,
                target with
                {
                    Members = Frozen(target.Members),
                    Redirects = Frozen(target.Redirects),
                    OwnerObservations = Frozen(target.OwnerObservations),
                }
            );
            reason = "reviewed_source_acceptance";
            return true;
        }
        catch (SourceActorReviewCorrespondence.CorrespondenceRefused refusal)
        {
            reason = refusal.Message;
            return false;
        }
    }

    private static SourceActorReviewField Select(
        SourceActorReviewField field,
        SourceActorAliasAssertion<NormalizedEvidenceReference> assertion
    ) =>
        field with
        {
            Coordinates = field.Coordinates with
            {
                AssertionUuid = assertion.AssertionUuid,
                ActorReferenceUuid = assertion.ActorReferenceUuid,
            },
        };

    internal static SourceActorReviewField Freeze(SourceActorReviewField field) =>
        field with
        {
            Scope = field.Scope with
            {
                Extraction = new(
                    Frozen(field.Scope.Extraction.Assertions.Select(Freeze)),
                    Frozen(field.Scope.Extraction.Unresolved.Select(Freeze)),
                    Frozen(
                        field.Scope.Extraction.Scopes.Select(scope =>
                            scope with
                            {
                                Evidence = Frozen(scope.Evidence),
                            }
                        )
                    )
                ),
            },
        };

    private static SourceActorAliasAssertion<NormalizedEvidenceReference> Freeze(
        SourceActorAliasAssertion<NormalizedEvidenceReference> assertion
    ) =>
        assertion with
        {
            Aliases = Frozen(
                assertion.Aliases.Select(alias => alias with { Evidence = Frozen(alias.Evidence) })
            ),
            Relationships = Frozen(
                assertion.Relationships.Select(relation =>
                    relation with
                    {
                        ReferenceUuids = Frozen(relation.ReferenceUuids),
                        Roles = Frozen(relation.Roles),
                        Evidence = Frozen(relation.Evidence),
                    }
                )
            ),
            Context = assertion.Context with { Qualifiers = Frozen(assertion.Context.Qualifiers) },
            Unresolved = Frozen(assertion.Unresolved.Select(Freeze)),
        };

    private static ActorEvidenceUnresolved<NormalizedEvidenceReference> Freeze(
        ActorEvidenceUnresolved<NormalizedEvidenceReference> issue
    ) =>
        issue with
        {
            CandidateReferenceUuids = Frozen(issue.CandidateReferenceUuids),
            Evidence = Frozen(issue.Evidence),
        };

    private static IReadOnlyList<T> Frozen<T>(IEnumerable<T> values) =>
        Array.AsReadOnly(values.ToArray());
}
