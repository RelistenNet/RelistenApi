using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

/// <summary>
/// Authenticates existing human source decisions and derives field correspondence for a
/// proposal snapshot. A correspondence retains the original receipt and current target proof;
/// this reader creates no review, assignment or metadata authority. Use the caller's primary
/// snapshot, then fence every returned target owner together with the source/extraction proof.
/// </summary>
internal static class SourceActorReviewedEvidence
{
    internal static async Task<
        IReadOnlyList<ReviewedSourceCorrespondence>
    > ReadCorrespondencesAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SourceActorExtractionConsumption currentExtraction,
        IReadOnlyList<SourceActorResolutionInput> currentInputs,
        SourceActorResolutionReviewSnapshot reviews,
        CancellationToken ct
    )
    {
        var result = new List<ReviewedSourceCorrespondence>();
        var checker = new SourceActorReviewCorrespondence();
        var originals =
            new Dictionary<
                Guid,
                IReadOnlyDictionary<SourceActorAliasCoordinates, SourceActorReviewField>
            >();
        var currentFields = SourceActorReviewEvidence
            .IndexFields(currentExtraction, currentInputs)
            .Values.GroupBy(FieldKey)
            .ToDictionary(group => group.Key, group => group.First());
        var pairs =
            new Dictionary<
                (Guid OriginalExtraction, FieldIdentity CurrentField),
                SourceActorReviewFieldCorrespondence?
            >();
        var targets = new Dictionary<Guid, SourceActorReviewTarget>();
        var unsupportedExtractions = new HashSet<Guid>();
        foreach (
            var review in reviews
                .Reviews.Where(value => value.State == "accepted")
                .OrderBy(value => value.Uuid)
        )
        {
            ct.ThrowIfCancellationRequested();
            if (review.Coordinates.ProducerVersion != RetainedSourceActorAliasExtractor.Version)
            {
                // Preserve earlier producer review rows as holds. A current parser cannot
                // authenticate a correspondence for an unsupported historical contract.
                continue;
            }
            var preparation = await ReadReviewPreparationAsync(c, t, review, ct);
            if (preparation == null)
            {
                // Historical authority without this supported immutable human preparation
                // remains an explicit review hold. Object shape alone proves no approval.
                continue;
            }
            if (unsupportedExtractions.Contains(preparation.Request.ExtractionAttemptUuid))
            {
                continue;
            }
            if (!originals.TryGetValue(preparation.Request.ExtractionAttemptUuid, out var original))
            {
                var extraction = await SourceActorResolutionStore.ReadPublishedExtractionAsync(
                    c,
                    t,
                    preparation.Request.ExtractionAttemptUuid,
                    currentExtraction.SourceUuid,
                    ct
                );
                if (extraction.ProcessingVersion != NormalizationRecordingProcessor.Version)
                {
                    unsupportedExtractions.Add(extraction.AttemptUuid);
                    continue;
                }
                var inputs = await SourceActorResolutionStore.ReadOriginalInputsAsync(
                    c,
                    t,
                    extraction,
                    ct
                );
                original = SourceActorReviewEvidence.IndexFields(extraction, inputs);
                originals.Add(extraction.AttemptUuid, original);
            }
            if (
                !original.TryGetValue(review.Coordinates, out var originalField)
                || !SourceActorReviewEvidence.Same(originalField, preparation.OriginalField)
            )
            {
                throw new ArgumentException(
                    "Original human review preparation does not authenticate its published field."
                );
            }
            var fieldIdentity = FieldKey(originalField);
            if (!currentFields.TryGetValue(fieldIdentity, out var currentField))
            {
                continue;
            }
            var pairKey = (originalField.ExtractionAttemptUuid, fieldIdentity);
            if (!pairs.TryGetValue(pairKey, out var pair))
            {
                checker.TryPrepareFields(originalField, currentField, out pair, out _);
                pairs.Add(pairKey, pair);
            }
            if (pair == null)
            {
                continue;
            }
            if (!targets.TryGetValue(review.SelectedPersonUuid!.Value, out var target))
            {
                target = await SourceActorReviewTargets.ReadAsync(
                    c,
                    t,
                    review.SelectedPersonUuid.Value,
                    ct
                );
                targets.Add(target.OriginalPersonUuid, target);
            }
            if (pair.TryCreate(review, target, out var proof, out _))
            {
                result.Add(proof!);
            }
        }
        // Exact current review controls its own claim. Otherwise a negative related field
        // decision blocks automatic correspondence, and competing accepted targets remain a
        // review hold. Never choose human authority by timestamp or history row ordering.
        return result
            .Where(proof => CanCarry(proof, reviews.Reviews))
            .GroupBy(proof => proof.Current.Coordinates)
            .Where(group =>
                group.Select(proof => proof.OriginalReview.SelectedPersonUuid).Distinct().Count()
                == 1
            )
            .SelectMany(group => group)
            .ToArray();
    }

    private static bool CanCarry(
        ReviewedSourceCorrespondence proof,
        IReadOnlyList<SourceActorAliasReviewState> reviews
    )
    {
        var current = proof.Current.Coordinates;
        var exact = reviews.SingleOrDefault(review => review.Coordinates == current);
        if (exact != null)
        {
            return exact.State == "accepted" && exact.Uuid == proof.OriginalReview.Uuid;
        }
        return !reviews.Any(review =>
            SameField(review.Coordinates, current) && review.State is "rejected" or "retracted"
        );
    }

    private static bool SameField(
        SourceActorAliasCoordinates left,
        SourceActorAliasCoordinates right
    ) =>
        left.SourceUuid == right.SourceUuid
        && left.Contributor == right.Contributor
        && left.ResourceUuid == right.ResourceUuid
        && left.FieldJsonPointer == right.FieldJsonPointer;

    /// <summary>
    /// Authenticates the exact immutable human operation and saved preparation for an accepted
    /// or negative review snapshot. Returns null for unsupported historical protocols. Callers
    /// still authenticate the published field and fence the current review/target observations.
    /// </summary>
    internal static async Task<SourceActorReviewPreparation?> ReadReviewPreparationAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SourceActorAliasReviewState review,
        CancellationToken ct
    )
    {
        var receipt = await c.QuerySingleOrDefaultAsync<HumanReviewPreparation>(
            new CommandDefinition(
                """
                SELECT preparation.preparation::text AS "PreparationJson",operation.action AS "Action"
                FROM normalization.review_operations operation
                JOIN normalization.source_actor_review_preparations preparation
                    ON preparation.preparation_uuid::text=operation.payload->>'PreparationUuid'
                WHERE operation.uuid=@LastOperationUuid AND operation.subject_uuid=@SourceResolutionOwnerUuid
                    AND operation.action IN ('accept','replace','reject','retract')
                    AND operation.payload->'Preparation'=preparation.preparation
                    AND operation.read_set=preparation.preparation->'ReadSet'
                    AND operation.editorial_revision=(preparation.preparation#>>'{Request,ExpectedEditorialRevision}')::bigint+1
                """,
                new { review.LastOperationUuid, review.SourceResolutionOwnerUuid },
                t,
                cancellationToken: ct
            )
        );
        if (receipt == null)
        {
            return null;
        }
        var preparation = JsonSerializer.Deserialize<SourceActorReviewPreparation>(
            receipt.PreparationJson
        )!;
        var (expectedAction, expectedState) = preparation.Request.Action switch
        {
            SourceActorReviewAction.Accept => ("accept", "accepted"),
            SourceActorReviewAction.Reassign => ("replace", "accepted"),
            SourceActorReviewAction.Reject => ("reject", "rejected"),
            SourceActorReviewAction.Retract => ("retract", "retracted"),
            _ => ("", ""),
        };
        if (
            receipt.Action != expectedAction
            || review.State != expectedState
            || preparation.ProcessingVersion != SourceActorReviewService.ProcessingVersion
            || preparation.ReviewStateUuid != review.Uuid
            || preparation.Request.Claim != review.Coordinates
            || preparation.Request.SelectedPersonUuid != review.SelectedPersonUuid
            || preparation.Request.ExpectedSourceReviewRevision + 1 != review.ReviewRevision
            || preparation.SourceOwner.SubjectUuid != review.SourceResolutionOwnerUuid
        )
        {
            return null;
        }
        return preparation;
    }

    private sealed class HumanReviewPreparation
    {
        public string PreparationJson { get; init; } = null!;
        public string Action { get; init; } = null!;
    }

    private static FieldIdentity FieldKey(SourceActorReviewField field) =>
        new(
            field.Coordinates.SourceUuid,
            field.Coordinates.Contributor,
            field.Coordinates.ResourceUuid,
            field.Coordinates.FieldJsonPointer,
            field.Coordinates.ProducerVersion
        );

    private sealed record FieldIdentity(
        Guid SourceUuid,
        NormalizationResourceKey Contributor,
        Guid ResourceUuid,
        string FieldJsonPointer,
        string ProducerVersion
    );
}
