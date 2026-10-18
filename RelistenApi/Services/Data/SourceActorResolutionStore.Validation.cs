using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

public sealed partial class SourceActorResolutionStore
{
    // Internal writes accept only service-owned preparations. Original source proof is still
    // replayed here; deterministic projections/comparisons are reproduced from the frozen
    // actual facts. Candidate databases are not queried again under attempt/publication locks.
    private static async Task ValidatePreparation(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SourceActorResolutionAttempt attempt,
        SourceActorResolutionPreparation preparation,
        CancellationToken ct
    )
    {
        var extraction = await ReadPublishedExtractionAsync(
            c,
            t,
            attempt.ExtractionAttemptUuid,
            attempt.SourceUuid,
            ct
        );
        var original = await ReadOriginalInputsAsync(c, t, extraction, ct);
        if (
            preparation.ExtractionObservation != ExtractionObservation(extraction)
            || !SourceActorResolutionCheckpoint.Same(
                preparation.ExtractionTicket,
                extraction.Ticket
            )
            || preparation.ExpectedConfigurationGeneration is < 0
            || preparation.ExpectedContributions.Any(value => value.Revision < 0)
            || preparation
                .ExpectedContributions.Select(value => value.Contributor)
                .Distinct()
                .Count() != preparation.ExpectedContributions.Count
            || preparation.Inputs.Count != original.Length
            || preparation
                .Inputs.Select(value => value.OriginalInput.Contributor)
                .Distinct()
                .Count() != original.Length
            || preparation.PeopleUniverse.Universe != NormalizationCandidateUniverseKey.People
            || preparation.PeopleUniverse.Generation < 0
            || preparation
                .Inputs.SelectMany(input => input.Scopes)
                .Sum(scope => (long)scope.Claims.Count) > MaximumClaims
        )
        {
            throw new ArgumentException(
                "Resolution must retain the complete original extraction, membership and source inventory."
            );
        }
        await ValidateCorrespondencesAsync(
            c,
            t,
            extraction,
            original,
            preparation.SourceReviews,
            ct
        );
        new FrozenClaimValidation(attempt.SourceUuid, extraction.Ticket, preparation).Validate(
            original,
            ct
        );
        var contributions = SourceActorResolutionReadSet.Contributions(preparation);
        SourceActorResolutionReadSet.RequireCapacity(preparation, contributions);
    }

    private static Task ValidateCorrespondencesAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SourceActorExtractionConsumption current,
        SourceActorResolutionInput[] currentInputs,
        SourceActorResolutionReviewSnapshot reviews,
        CancellationToken ct
    ) => new CorrespondenceValidation(c, t, current, currentInputs, reviews).ValidateAsync(ct);

    private static void ValidateCandidatePool(
        NormalizationCandidateFactsSnapshot<PersonCandidateFacts>? pool,
        PeopleActorProjectionResult projection
    )
    {
        if (projection.Query == null)
        {
            if (pool != null)
            {
                throw new ArgumentException(
                    "An unsupported source query cannot acquire a fabricated pool."
                );
            }
            return;
        }
        if (
            pool == null
            || pool.Version != NormalizationCandidateService.PeopleFactsVersion
            || string.IsNullOrWhiteSpace(pool.QueryHash)
            || !SourceActorResolutionCheckpoint.Same(pool.Query, projection.Query)
            || pool.Selection.Domain != IdentityMatchingDomain.People
            || pool.ReadSet.CandidateUniverses.Count != 1
            || pool.ReadSet.CandidateUniverses[0].Universe
                != NormalizationCandidateUniverseKey.People
            || pool.ReadSet.CandidateUniverses[0].Generation < 0
            || pool.Facts.Select(fact => fact.Hit.Uuid).Distinct().Count() != pool.Facts.Count
            || pool.Selection.Items.Select(hit => hit.Uuid).Distinct().Count()
                != pool.Selection.Items.Count
            || !pool
                .Facts.Select(fact => fact.Hit.Uuid)
                .ToHashSet()
                .SetEquals(pool.Selection.Items.Select(hit => hit.Uuid))
            || pool.Facts.Any(fact =>
                fact.Header.Uuid != fact.Hit.Uuid
                || fact.ActorMatchingFacts == null
                || fact.AcceptedAliasMatchingNames == null
                || fact.AliasMatchingNamesComplete == null
            )
            || pool.Complete && pool.Facts.Any(fact => fact.AliasMatchingNamesComplete != true)
            || pool.Facts.Any(fact =>
                !SourceActorResolutionCheckpoint.Same(
                    fact.Hit,
                    pool.Selection.Items.Single(hit => hit.Uuid == fact.Hit.Uuid)
                )
            )
        )
        {
            throw new ArgumentException(
                "Source proposals require the complete original typed people query and facts projection."
            );
        }
    }

    private static async Task RequireCurrentProofAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SourceActorResolutionBatch batch,
        CancellationToken ct
    )
    {
        var preparation = batch.Preparation;
        // Immutable producer/ticket identity is checked without transferring its potentially
        // large extraction JSON again. Work already fenced every inherited original clock.
        var producer = await c.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                """
                SELECT EXISTS(SELECT 1 FROM normalization.recording_processing_attempts a
                    JOIN normalization.work_runs r ON r.uuid=a.attempt_uuid
                    JOIN normalization.work_subjects s ON s.uuid=r.subject_uuid
                    WHERE a.attempt_uuid=@ExtractionAttemptUuid AND a.source_uuid=@SourceUuid
                        AND a.disposition='published' AND a.batch->'Ticket'=@ticket::jsonb
                        AND r.state='published' AND r.ticket=@ticket::jsonb
                        AND s.domain='extractions' AND s.subject_kind='recording' AND s.subject_uuid=@SourceUuid)
                """,
                new
                {
                    batch.ExtractionAttemptUuid,
                    batch.SourceUuid,
                    ticket = JsonSerializer.Serialize(preparation.ExtractionTicket),
                },
                t,
                cancellationToken: ct
            )
        );
        if (!producer)
        {
            throw new NormalizationConflictException("Original source extraction proof changed.");
        }
        await PeopleResolutionPolicyService.RequireStateAsync(c, t, preparation.PolicyState, ct);
        // Admission can legitimately create/configure this owner. Re-read the actual field
        // authority under its lock, including first-review absence; never substitute a zero
        // owner stamp for the preparation's nullable observation.
        var current = await ReadSourceReviewsAsync(
            c,
            t,
            batch.SourceUuid,
            preparation.ExtractionTicket,
            preparation.Inputs,
            ct
        );
        if (
            !SourceActorResolutionCheckpoint.EquivalentReviewRows(
                current.Reviews,
                preparation.SourceReviews.Reviews
            )
            || batch.Ticket.EditorialRevision
                != (preparation.SourceReviews.OwnerObservation?.EditorialRevision ?? 0)
        )
        {
            throw new NormalizationConflictException(
                "Source review authority changed after preparation."
            );
        }
    }
}
