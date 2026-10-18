using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

public sealed partial class SourceActorResolutionStore
{
    // Original receipt/target acquisition belongs to the trusted source snapshot reader.
    // This phase authenticates frozen source fields again and reproduces correspondence.
    // A whole-field bijection is checked once, then each original human review selects its
    // own exact destination. No identity-name fallback or new factual target query is used.
    private sealed class CorrespondenceValidation(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SourceActorExtractionConsumption current,
        SourceActorResolutionInput[] currentInputs,
        SourceActorResolutionReviewSnapshot reviews
    )
    {
        private readonly SourceActorReviewCorrespondence checker = new();
        private readonly Dictionary<
            Guid,
            IReadOnlyDictionary<SourceActorAliasCoordinates, SourceActorReviewField>
        > originals = [];
        private readonly Dictionary<FieldPairKey, CheckedFieldPair> pairs = [];
        private readonly Dictionary<
            NormalizationRecordingSourceActorAliasScope,
            HashSet<NormalizationRecordingSourceActorAliasScope>
        > checkedScopes = new(ReferenceEqualityComparer.Instance);

        public async Task ValidateAsync(CancellationToken ct)
        {
            var currentFields = SourceActorReviewEvidence.IndexFields(current, currentInputs);
            foreach (var proof in reviews.Correspondences ?? [])
            {
                ct.ThrowIfCancellationRequested();
                var review = reviews.Reviews.SingleOrDefault(value =>
                    value.Uuid == proof.OriginalReview.Uuid
                );
                if (
                    review == null
                    || !SameReview(review, proof.OriginalReview)
                    || proof.Current.ExtractionAttemptUuid != current.AttemptUuid
                )
                {
                    throw new ArgumentException(
                        "Correspondence requires its actual relevant source review and current extraction."
                    );
                }
                var oldFields = await ReadOriginalFieldsAsync(
                    proof.Original.ExtractionAttemptUuid,
                    ct
                );
                if (
                    !oldFields.TryGetValue(proof.Original.Coordinates, out var oldField)
                    || !currentFields.TryGetValue(proof.Current.Coordinates, out var currentField)
                    || !SameField(oldField, proof.Original)
                    || !SameField(currentField, proof.Current)
                )
                {
                    throw new ArgumentException(
                        "Correspondence must retain both exact original published fields."
                    );
                }
                var pair = PreparePair(oldField, currentField);
                if (
                    !pair.Fields.TryCreate(review, proof.Target, out var expected, out _)
                    || proof.Version != expected!.Version
                    || proof.Current.Coordinates != expected.Current.Coordinates
                    || !pair.SameMaps(proof, expected)
                )
                {
                    throw new ArgumentException(
                        "Correspondence must reproduce its complete field and bijective local evidence mapping."
                    );
                }
            }
        }

        private async Task<
            IReadOnlyDictionary<SourceActorAliasCoordinates, SourceActorReviewField>
        > ReadOriginalFieldsAsync(Guid extractionAttemptUuid, CancellationToken ct)
        {
            if (originals.TryGetValue(extractionAttemptUuid, out var existing))
            {
                return existing;
            }
            var extraction = await ReadPublishedExtractionAsync(
                c,
                t,
                extractionAttemptUuid,
                current.SourceUuid,
                ct
            );
            var inputs = await ReadOriginalInputsAsync(c, t, extraction, ct);
            var fields = SourceActorReviewEvidence.IndexFields(extraction, inputs);
            originals.Add(extractionAttemptUuid, fields);
            return fields;
        }

        private CheckedFieldPair PreparePair(
            SourceActorReviewField original,
            SourceActorReviewField destination
        )
        {
            var coordinates = destination.Coordinates;
            var key = new FieldPairKey(
                original.ExtractionAttemptUuid,
                destination.ExtractionAttemptUuid,
                coordinates.Contributor,
                coordinates.ResourceUuid,
                coordinates.FieldJsonPointer,
                coordinates.ProducerVersion
            );
            if (pairs.TryGetValue(key, out var existing))
            {
                return existing;
            }
            if (!checker.TryPrepareFields(original, destination, out var fields, out _))
            {
                throw new ArgumentException(
                    "Frozen source fields do not support strict correspondence."
                );
            }
            var pair = new CheckedFieldPair(fields!);
            pairs.Add(key, pair);
            return pair;
        }

        private bool SameField(SourceActorReviewField expected, SourceActorReviewField actual) =>
            expected.ExtractionAttemptUuid == actual.ExtractionAttemptUuid
            && expected.Coordinates == actual.Coordinates
            && expected.FieldText == actual.FieldText
            && expected.FieldEvidence == actual.FieldEvidence
            && SameScope(expected.Scope, actual.Scope);

        private bool SameReview(
            SourceActorAliasReviewState expected,
            SourceActorAliasReviewState actual
        ) =>
            expected.Uuid == actual.Uuid
            && expected.Coordinates == actual.Coordinates
            && expected.AssertionSha256 == actual.AssertionSha256
            && expected.SourceResolutionOwnerUuid == actual.SourceResolutionOwnerUuid
            && expected.ReviewRevision == actual.ReviewRevision
            && expected.State == actual.State
            && expected.SelectedPersonUuid == actual.SelectedPersonUuid
            && expected.LastOperationUuid == actual.LastOperationUuid
            && SourceActorResolutionCheckpoint.Same(
                expected.OriginalAssertion,
                actual.OriginalAssertion
            )
            && SameScope(expected.OriginalScope, actual.OriginalScope);

        private bool SameScope(
            NormalizationRecordingSourceActorAliasScope expected,
            NormalizationRecordingSourceActorAliasScope actual
        )
        {
            if (ReferenceEquals(expected, actual))
            {
                return true;
            }
            if (!checkedScopes.TryGetValue(expected, out var checkedValues))
            {
                checkedValues = new(ReferenceEqualityComparer.Instance);
                checkedScopes.Add(expected, checkedValues);
            }
            if (checkedValues.Contains(actual))
            {
                return true;
            }
            if (!SourceActorResolutionCheckpoint.Same(expected, actual))
            {
                return false;
            }
            checkedValues.Add(actual);
            return true;
        }
    }

    private sealed record FieldPairKey(
        Guid OriginalExtraction,
        Guid CurrentExtraction,
        NormalizationResourceKey Contributor,
        Guid ResourceUuid,
        string FieldJsonPointer,
        string ProducerVersion
    );

    private sealed class CheckedFieldPair(SourceActorReviewFieldCorrespondence fields)
    {
        public SourceActorReviewFieldCorrespondence Fields { get; } = fields;
        private readonly HashSet<IReadOnlyList<SourceActorReviewLocalIdMap>> checkedLocalMaps = new(
            ReferenceEqualityComparer.Instance
        );
        private readonly HashSet<IReadOnlyList<SourceActorReviewEvidenceMap>> checkedEvidenceMaps =
            new(ReferenceEqualityComparer.Instance);

        public bool SameMaps(
            ReviewedSourceCorrespondence actual,
            ReviewedSourceCorrespondence expected
        )
        {
            if (!checkedLocalMaps.Contains(actual.LocalIds))
            {
                if (!SourceActorResolutionCheckpoint.Same(actual.LocalIds, expected.LocalIds))
                {
                    return false;
                }
                checkedLocalMaps.Add(actual.LocalIds);
            }
            if (!checkedEvidenceMaps.Contains(actual.Evidence))
            {
                if (!SourceActorResolutionCheckpoint.Same(actual.Evidence, expected.Evidence))
                {
                    return false;
                }
                checkedEvidenceMaps.Add(actual.Evidence);
            }
            return true;
        }
    }
}
