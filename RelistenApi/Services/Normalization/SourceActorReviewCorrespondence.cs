using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

/// <summary>
/// Checks strict carry-forward between two authenticated published fields. Only retained-input
/// revision namespaces may change. This class reads no mutable state and writes no authority.
/// The caller authenticates both input scopes, the original human receipt and current target.
/// </summary>
public sealed class SourceActorReviewCorrespondence
{
    public const string Version = "source-actor-review-correspondence-v1";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly SourceActorAliasExtractor extractor = new();

    /// <summary>
    /// Returns an explicit proof or a stable review reason. Authenticated arguments are required:
    /// a structurally valid caller-built record is not evidence of a published extraction or
    /// trusted human review. No fallback to names, equal hashes or sampled evidence is allowed.
    /// </summary>
    public bool TryCreate(
        SourceActorAliasReviewState originalAcceptedReview,
        SourceActorReviewField original,
        SourceActorReviewField current,
        SourceActorReviewTarget target,
        out ReviewedSourceCorrespondence? proof,
        out string reason
    )
    {
        proof = null;
        if (originalAcceptedReview.Coordinates != original.Coordinates)
        {
            reason = "original_review_scope_mismatch";
            return false;
        }
        if (
            !TryPrepareFields(original, current, out var fields, out reason)
            || !fields!.TryCreate(originalAcceptedReview, target, out var selected, out reason)
        )
        {
            return false;
        }
        if (selected!.Current.Coordinates.AssertionUuid != current.Coordinates.AssertionUuid)
        {
            reason = "selected_claim_changed";
            return false;
        }
        if (
            selected.Current.Coordinates.ActorReferenceUuid
            != current.Coordinates.ActorReferenceUuid
        )
        {
            reason = "selected_actor_changed";
            return false;
        }
        proof = selected;
        return true;
    }

    /// <summary>
    /// Authenticates correspondence for an entire field once, then indexes every original and
    /// current assertion. Cache this frozen pair by the exact extraction attempts and stable
    /// field coordinates; individual accepted reviews select their own mapped claim directly.
    /// The containing scopes and retained inputs must already have been authenticated by the reader.
    /// </summary>
    public bool TryPrepareFields(
        SourceActorReviewField original,
        SourceActorReviewField current,
        out SourceActorReviewFieldCorrespondence? fields,
        out string reason
    )
    {
        fields = null;
        try
        {
            // Freeze once before validation. Neither a caller's original lists nor a returned
            // proof can mutate a pair that subsequent reviews reuse.
            original = SourceActorReviewFieldCorrespondence.Freeze(original);
            current = SourceActorReviewFieldCorrespondence.Freeze(current);
            RequireSameField(original, current);
            var fieldName = FieldName(original);
            Require(fieldName == FieldName(current), "field_contract_changed");
            var local = extractor.Extract(
                new Dictionary<string, string?> { [fieldName] = original.FieldText }
            );
            var map = new FieldMap(original, current);
            var expectedOriginal = map.Map(local, original);
            var expectedCurrent = map.Map(local, current);
            var originalProjection = ProjectField(original);
            var currentProjection = ProjectField(current);

            // Comparing every typed member preserves array order, context, unresolved evidence
            // and coverage. Extra or missing facts cannot disappear through an ID-only match.
            Require(Equal(expectedOriginal, originalProjection), "original_projection_mismatch");
            Require(Equal(expectedCurrent, currentProjection), "current_projection_mismatch");
            RequireUniqueDeclarations(originalProjection);
            RequireUniqueDeclarations(currentProjection);
            fields = new(
                original,
                current,
                originalProjection,
                currentProjection,
                map.LocalIds,
                map.Evidence
            );
            reason = "source_fields_correspond";
            return true;
        }
        catch (CorrespondenceRefused refusal)
        {
            reason = refusal.Message;
            return false;
        }
        catch (EncoderFallbackException)
        {
            reason = "invalid_field_unicode";
            return false;
        }
    }

    private static void RequireUniqueDeclarations(
        SourceActorAliasExtraction<NormalizedEvidenceReference> extraction
    )
    {
        var declarations = extraction
            .Assertions.SelectMany(assertion =>
                new[] { assertion.AssertionUuid, assertion.ActorReferenceUuid }
                    .Concat(
                        assertion.Aliases.SelectMany(alias =>
                            new[] { alias.Uuid, alias.ReferenceUuid }
                        )
                    )
                    .Concat(assertion.Relationships.Select(relation => relation.Uuid))
            )
            .ToArray();
        Require(
            declarations.Distinct().Count() == declarations.Length,
            "ambiguous_local_id_anchor"
        );
    }

    internal static void RequireOriginalReview(
        SourceActorAliasReviewState review,
        SourceActorReviewField original,
        SourceActorReviewTarget target,
        SourceActorAliasAssertion<NormalizedEvidenceReference> assertion
    )
    {
        Require(
            review.State == "accepted"
                && review.ReviewRevision > 0
                && review.Uuid != Guid.Empty
                && review.LastOperationUuid != Guid.Empty
                && review.SelectedPersonUuid == target.OriginalPersonUuid,
            "original_review_not_accepted"
        );
        Require(
            review.Coordinates == original.Coordinates
                && Equal(review.OriginalScope, original.Scope),
            "original_review_scope_mismatch"
        );
        Require(
            Equal(review.OriginalAssertion, assertion)
                && review.AssertionSha256 == Hash(JsonSerializer.SerializeToUtf8Bytes(assertion)),
            "original_review_assertion_mismatch"
        );
    }

    private static void RequireSameField(
        SourceActorReviewField original,
        SourceActorReviewField current
    )
    {
        Require(
            original.Scope.ProducerVersion == RetainedSourceActorAliasExtractor.Version
                && current.Scope.ProducerVersion == RetainedSourceActorAliasExtractor.Version
                && original.Coordinates.ProducerVersion == original.Scope.ProducerVersion
                && current.Coordinates.ProducerVersion == current.Scope.ProducerVersion,
            "unsupported_source_alias_producer"
        );
        Require(
            original.Coordinates.SourceUuid == current.Coordinates.SourceUuid
                && original.Coordinates.Contributor == current.Coordinates.Contributor
                && original.Coordinates.ResourceUuid == current.Coordinates.ResourceUuid
                && original.Scope.Resource == current.Scope.Resource
                && original.Coordinates.FieldJsonPointer == current.Coordinates.FieldJsonPointer,
            "source_field_identity_changed"
        );
        RequireFieldCoordinates(original);
        RequireFieldCoordinates(current);
        Require(
            SameOrigin(
                original.Scope.RecordingOrigin,
                current.Scope.RecordingOrigin,
                original,
                current
            ) && SameOrigin(original.Scope.FileOrigin, current.Scope.FileOrigin, original, current),
            "source_field_origin_changed"
        );
        Require(
            string.Equals(original.FieldText, current.FieldText, StringComparison.Ordinal)
                && StrictUtf8
                    .GetBytes(original.FieldText)
                    .AsSpan()
                    .SequenceEqual(StrictUtf8.GetBytes(current.FieldText)),
            "source_field_text_changed"
        );
        Require(
            original.FieldEvidence with
            {
                InputRevisionUuid = current.Scope.InputRevisionUuid,
            } == current.FieldEvidence,
            "source_field_locator_changed"
        );
    }

    private static void RequireFieldCoordinates(SourceActorReviewField field)
    {
        var evidence = field.FieldEvidence;
        Require(
            field.Scope.InputRevisionUuid == field.Coordinates.InputRevisionUuid
                && evidence.InputRevisionUuid == field.Scope.InputRevisionUuid
                && evidence.JsonPointer == field.Coordinates.FieldJsonPointer
                && evidence.StartCodePoint is >= 0
                && evidence.EndCodePoint
                    == evidence.StartCodePoint + field.FieldText.EnumerateRunes().Count()
                && evidence.Quote == field.FieldText,
            "invalid_whole_field_evidence"
        );
        Require(
            field.Coordinates.FieldSha256 == Hash(StrictUtf8.GetBytes(field.FieldText)),
            "source_field_hash_mismatch"
        );
    }

    private static bool SameOrigin(
        ProviderObservationOrigin? original,
        ProviderObservationOrigin? current,
        SourceActorReviewField oldField,
        SourceActorReviewField newField
    ) =>
        original == null
            ? current == null
            : current != null
                && original.Evidence.InputRevisionUuid == oldField.Scope.InputRevisionUuid
                && current.Evidence.InputRevisionUuid == newField.Scope.InputRevisionUuid
                && original with
                {
                    Evidence = original.Evidence with
                    {
                        InputRevisionUuid = newField.Scope.InputRevisionUuid,
                    },
                } == current;

    internal static void RequireTarget(SourceActorReviewTarget target)
    {
        Require(
            target.Members.Select(member => member.PersonUuid).Distinct().Count()
                == target.Members.Count,
            "invalid_selected_identity_component"
        );
        var members = target.Members.ToDictionary(member => member.PersonUuid);
        Require(
            members.Count > 0
                && members.ContainsKey(target.OriginalPersonUuid)
                && members.ContainsKey(target.ResolvedPersonUuid)
                && members.Values.All(member =>
                    member.Revision > 0 && member.ResolvedPersonUuid == target.ResolvedPersonUuid
                ),
            "invalid_selected_identity_component"
        );
        Require(
            target.Redirects.Select(edge => edge.OriginalUuid).Distinct().Count()
                == target.Redirects.Count,
            "invalid_selected_identity_redirects"
        );
        var redirects = target.Redirects.ToDictionary(edge => edge.OriginalUuid);
        Require(
            redirects.Count == members.Count - 1
                && !redirects.ContainsKey(target.ResolvedPersonUuid)
                && redirects.Values.All(edge =>
                    members.ContainsKey(edge.OriginalUuid) && members.ContainsKey(edge.SurvivorUuid)
                ),
            "invalid_selected_identity_redirects"
        );
        foreach (var member in members.Keys)
        {
            var visited = new HashSet<Guid>();
            var cursor = member;
            while (cursor != target.ResolvedPersonUuid)
            {
                Require(
                    visited.Add(cursor) && redirects.ContainsKey(cursor),
                    "invalid_selected_identity_redirects"
                );
                cursor = redirects[cursor].SurvivorUuid;
            }
        }
        Require(
            target.OwnerObservations.Count > 0
                && target.OwnerObservations.Select(owner => owner.SubjectUuid).Distinct().Count()
                    == target.OwnerObservations.Count,
            "invalid_selected_identity_owners"
        );
    }

    private static string FieldName(SourceActorReviewField field)
    {
        var coverage = field
            .Scope.Extraction.Scopes.Where(scope =>
                scope.Evidence.Any(evidence => evidence == field.FieldEvidence)
            )
            .ToArray();
        Require(coverage.Length == 1, "ambiguous_field_coverage");
        return coverage[0].Field;
    }

    private static SourceActorAliasExtraction<NormalizedEvidenceReference> ProjectField(
        SourceActorReviewField field
    )
    {
        bool ContainsField(IEnumerable<NormalizedEvidenceReference> citations)
        {
            var evidence = citations.ToArray();
            Require(evidence.Length > 0, "uncited_field_projection");
            var belongs = evidence.Any(item =>
                item.JsonPointer == field.FieldEvidence.JsonPointer
                && item.StartCodePoint < field.FieldEvidence.EndCodePoint
                && item.EndCodePoint > field.FieldEvidence.StartCodePoint
            );
            if (belongs)
            {
                Require(
                    evidence.All(item =>
                        item.InputRevisionUuid == field.Scope.InputRevisionUuid
                        && item.JsonPointer == field.FieldEvidence.JsonPointer
                        && item.StartCodePoint >= field.FieldEvidence.StartCodePoint
                        && item.EndCodePoint <= field.FieldEvidence.EndCodePoint
                    ),
                    "cross_field_evidence"
                );
            }
            return belongs;
        }
        var projection = new SourceActorAliasExtraction<NormalizedEvidenceReference>(
            field
                .Scope.Extraction.Assertions.Where(assertion => ContainsField(Citations(assertion)))
                .ToArray(),
            field
                .Scope.Extraction.Unresolved.Where(issue => ContainsField(issue.Evidence))
                .ToArray(),
            field.Scope.Extraction.Scopes.Where(scope => ContainsField(scope.Evidence)).ToArray()
        );
        var localIds = projection
            .Assertions.SelectMany(LocalIds)
            .Concat(projection.Unresolved.SelectMany(issue => issue.CandidateReferenceUuids))
            .ToHashSet();
        var otherIds = field
            .Scope.Extraction.Assertions.Except(projection.Assertions)
            .SelectMany(LocalIds)
            .Concat(
                field
                    .Scope.Extraction.Unresolved.Except(projection.Unresolved)
                    .SelectMany(issue => issue.CandidateReferenceUuids)
            );
        Require(!otherIds.Any(localIds.Contains), "cross_field_reference");
        return projection;
    }

    private static IEnumerable<Guid> LocalIds(
        SourceActorAliasAssertion<NormalizedEvidenceReference> assertion
    ) =>
        new[] { assertion.AssertionUuid, assertion.ActorReferenceUuid }
            .Concat(
                assertion.Aliases.SelectMany(alias => new[] { alias.Uuid, alias.ReferenceUuid })
            )
            .Concat(
                assertion.Relationships.SelectMany(relation =>
                    relation.ReferenceUuids.Prepend(relation.Uuid)
                )
            )
            .Concat(assertion.Unresolved.SelectMany(issue => issue.CandidateReferenceUuids));

    private static IEnumerable<NormalizedEvidenceReference> Citations(
        SourceActorAliasAssertion<NormalizedEvidenceReference> assertion
    ) =>
        new[] { assertion.NameEvidence, assertion.Context.Heading, assertion.Context.Statement }
            .Concat(assertion.Context.Qualifiers)
            .Concat(assertion.Aliases.SelectMany(alias => alias.Evidence))
            .Concat(assertion.Relationships.SelectMany(relation => relation.Evidence))
            .Concat(assertion.Unresolved.SelectMany(issue => issue.Evidence));

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static bool Equal<T>(T first, T second) =>
        JsonSerializer
            .SerializeToUtf8Bytes(first)
            .AsSpan()
            .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(second));

    internal static void Require(bool condition, string reason)
    {
        if (!condition)
        {
            throw new CorrespondenceRefused(reason);
        }
    }

    internal sealed class CorrespondenceRefused(string reason) : Exception(reason);

    // Replays the supported mapper's exact namespace contract from original parser anchors.
    // A producer change refuses above and requires an explicit new correspondence version.
    private sealed class FieldMap(SourceActorReviewField original, SourceActorReviewField current)
    {
        public List<SourceActorReviewLocalIdMap> LocalIds { get; } = [];
        public List<SourceActorReviewEvidenceMap> Evidence { get; } = [];
        private readonly Dictionary<Guid, SourceActorReviewLocalIdMap> anchors = [];
        private readonly HashSet<Guid> originalIds = [];
        private readonly HashSet<Guid> currentIds = [];
        private readonly HashSet<SourceActorReviewEvidenceMap> evidenceMaps = [];

        public SourceActorAliasExtraction<NormalizedEvidenceReference> Map(
            SourceActorAliasExtraction<NormalizationTextEvidence> extraction,
            SourceActorReviewField field
        ) =>
            new(
                extraction.Assertions.Select(assertion => Assertion(assertion, field)).ToArray(),
                extraction.Unresolved.Select(issue => Unresolved(issue, field)).ToArray(),
                extraction
                    .Scopes.Select(
                        scope => new SourceActorAliasScopeCoverage<NormalizedEvidenceReference>(
                            scope.Field,
                            scope.Complete,
                            scope.Reason,
                            scope.Evidence.Select(evidence => Citation(evidence, field)).ToArray()
                        )
                    )
                    .ToArray()
            );

        private SourceActorAliasAssertion<NormalizedEvidenceReference> Assertion(
            SourceActorAliasAssertion<NormalizationTextEvidence> assertion,
            SourceActorReviewField field
        ) =>
            new(
                assertion.Version,
                Id(SourceActorReviewLocalIdKind.Assertion, assertion.AssertionUuid, field),
                Id(
                    SourceActorReviewLocalIdKind.ActorReference,
                    assertion.ActorReferenceUuid,
                    field
                ),
                assertion.FieldSha256,
                assertion.Name,
                Citation(assertion.NameEvidence, field),
                assertion.Aliases.Select(alias => Alias(alias, field)).ToArray(),
                assertion.Relationships.Select(relation => Relationship(relation, field)).ToArray(),
                new(
                    Citation(assertion.Context.Heading, field),
                    assertion
                        .Context.Qualifiers.Select(evidence => Citation(evidence, field))
                        .ToArray(),
                    Citation(assertion.Context.Statement, field)
                ),
                assertion.Unresolved.Select(issue => Unresolved(issue, field)).ToArray()
            );

        private ActorAliasFact<NormalizedEvidenceReference> Alias(
            ActorAliasFact<NormalizationTextEvidence> alias,
            SourceActorReviewField field
        ) =>
            new(
                Id(SourceActorReviewLocalIdKind.Alias, alias.Uuid, field),
                Id(SourceActorReviewLocalIdKind.ActorReference, alias.ReferenceUuid, field),
                alias.Wording,
                alias.Evidence.Select(evidence => Citation(evidence, field)).ToArray()
            );

        private ActorCoReferenceFact<NormalizedEvidenceReference> Relationship(
            ActorCoReferenceFact<NormalizationTextEvidence> relation,
            SourceActorReviewField field
        ) =>
            new(
                Id(SourceActorReviewLocalIdKind.Relationship, relation.Uuid, field),
                relation.Relationship,
                relation
                    .ReferenceUuids.Select(uuid =>
                        Id(SourceActorReviewLocalIdKind.ActorReference, uuid, field)
                    )
                    .ToArray(),
                relation.Roles,
                relation.Evidence.Select(evidence => Citation(evidence, field)).ToArray()
            );

        private ActorEvidenceUnresolved<NormalizedEvidenceReference> Unresolved(
            ActorEvidenceUnresolved<NormalizationTextEvidence> issue,
            SourceActorReviewField field
        ) =>
            new(
                issue.Reason,
                issue
                    .CandidateReferenceUuids.Select(uuid =>
                        Id(SourceActorReviewLocalIdKind.ActorReference, uuid, field)
                    )
                    .ToArray(),
                issue.Evidence.Select(evidence => Citation(evidence, field)).ToArray()
            );

        private Guid Id(
            SourceActorReviewLocalIdKind kind,
            Guid anchor,
            SourceActorReviewField field
        )
        {
            if (!anchors.TryGetValue(anchor, out var mapping))
            {
                mapping = new(kind, anchor, Scope(anchor, original), Scope(anchor, current));
                Require(
                    originalIds.Add(mapping.OriginalUuid) && currentIds.Add(mapping.CurrentUuid),
                    "ambiguous_local_id_namespace"
                );
                anchors.Add(anchor, mapping);
                LocalIds.Add(mapping);
            }
            Require(mapping.Kind == kind, "ambiguous_local_id_anchor");
            return ReferenceEquals(field, original) ? mapping.OriginalUuid : mapping.CurrentUuid;
        }

        private NormalizedEvidenceReference Citation(
            NormalizationTextEvidence evidence,
            SourceActorReviewField field
        )
        {
            NormalizedEvidenceReference Map(SourceActorReviewField value) =>
                new(
                    value.Scope.InputRevisionUuid,
                    value.FieldEvidence.JsonPointer,
                    value.FieldEvidence.StartCodePoint + evidence.StartCodePoint,
                    value.FieldEvidence.StartCodePoint + evidence.EndCodePoint,
                    evidence.Quote
                );
            var mapped = new SourceActorReviewEvidenceMap(Map(original), Map(current));
            if (evidenceMaps.Add(mapped))
            {
                Evidence.Add(mapped);
            }
            return ReferenceEquals(field, original) ? mapped.Original : mapped.Current;
        }

        private static Guid Scope(Guid anchor, SourceActorReviewField field) =>
            new(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(
                        $"{RetainedSourceActorAliasExtractor.Version}/{field.Scope.InputRevisionUuid:D}/{field.FieldEvidence.JsonPointer}/{field.FieldEvidence.StartCodePoint ?? 0}/{anchor:D}"
                    )
                )[..16]
            );
    }
}
