using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

/// <summary>Original published extraction and source review snapshots; no current clock replaces a producer observation.</summary>
public sealed partial class SourceActorResolutionStore
{
    // Only the ID-owned resolver supplies this internal read callback. The store owns the
    // snapshot; matching owns the consumed projections. Neither may commit or refresh clocks.
    internal Task<SourceActorResolutionPreparation?> ReadPreparationSnapshotAsync(
        SourceActorResolutionAttempt attempt,
        Func<
            NpgsqlConnection,
            NpgsqlTransaction,
            SourceActorResolutionSource,
            CancellationToken,
            Task<SourceActorResolutionPreparation?>
        > prepare,
        CancellationToken ct
    ) =>
        db.WithWriteConnection(async connection =>
        {
            var c = (NpgsqlConnection)connection;
            await using var t = await c.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            await c.ExecuteAsync(
                new CommandDefinition(
                    "SET TRANSACTION READ ONLY",
                    transaction: t,
                    cancellationToken: ct
                )
            );
            var extraction = await ReadPublishedExtractionAsync(
                c,
                t,
                attempt.ExtractionAttemptUuid,
                attempt.SourceUuid,
                ct
            );
            var inputs = await ReadOriginalInputsAsync(c, t, extraction, ct);
            var reviews = await ReadSourceReviewsAsync(c, t, extraction, inputs, ct);
            var correspondences = await SourceActorReviewedEvidence.ReadCorrespondencesAsync(
                c,
                t,
                extraction,
                inputs,
                reviews,
                ct
            );
            reviews = reviews with
            {
                Correspondences = correspondences.Count == 0 ? null : correspondences,
            };
            var generation = await c.QuerySingleAsync<long>(
                new CommandDefinition(
                    "SELECT generation FROM normalization.candidate_universe_revisions WHERE domain='people' AND scope_uuid='00000000-0000-0000-0000-000000000000'",
                    transaction: t,
                    cancellationToken: ct
                )
            );
            var source = new SourceActorResolutionSource(
                extraction,
                inputs,
                reviews,
                new(NormalizationCandidateUniverseKey.People, generation)
            );
            var result = await prepare(c, t, source, ct);
            await t.CommitAsync(ct);
            return result;
        });

    internal static async Task<SourceActorExtractionConsumption> ReadPublishedExtractionAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid extractionAttemptUuid,
        Guid? expectedSourceUuid,
        CancellationToken ct
    )
    {
        const string selection =
            "FROM normalization.recording_processing_attempts WHERE attempt_uuid=@extractionAttemptUuid AND (@expectedSourceUuid::uuid IS NULL OR source_uuid=@expectedSourceUuid) AND disposition='published'";
        var parameters = new { extractionAttemptUuid, expectedSourceUuid };
        var bytes =
            await c.QuerySingleOrDefaultAsync<long?>(
                new CommandDefinition(
                    $"SELECT octet_length(({SourceActorConsumptionProjectionSql})::text) {selection}",
                    parameters,
                    t,
                    cancellationToken: ct
                )
            )
            ?? throw new ArgumentException(
                "Source actor resolution requires its exact published extraction."
            );
        if (bytes > MaximumSourceConsumptionBytes)
        {
            throw new SourceActorResolutionCapacityException(
                "source_consumption_capacity_exceeded"
            );
        }
        var json = await c.QuerySingleAsync<string>(
            new CommandDefinition(
                $"SELECT ({SourceActorConsumptionProjectionSql})::text {selection}",
                parameters,
                t,
                cancellationToken: ct
            )
        );
        var extraction = JsonSerializer.Deserialize<SourceActorExtractionConsumption>(json)!;
        var valid = await c.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                """
                SELECT EXISTS(SELECT 1 FROM normalization.work_runs r JOIN normalization.work_subjects s ON s.uuid=r.subject_uuid
                    WHERE r.uuid=@AttemptUuid AND r.state='published' AND r.ticket=@ticket::jsonb
                        AND s.domain='extractions' AND s.subject_kind='recording' AND s.subject_uuid=@SourceUuid)
                """,
                new
                {
                    extraction.AttemptUuid,
                    extraction.SourceUuid,
                    ticket = JsonSerializer.Serialize(extraction.Ticket),
                },
                t,
                cancellationToken: ct
            )
        );
        if (
            !valid
            || extraction.AttemptUuid != extractionAttemptUuid
            || expectedSourceUuid.HasValue && extraction.SourceUuid != expectedSourceUuid
            || extraction.Ticket.WorkUuid != extraction.AttemptUuid
            || extraction.Ticket.PolicyVersion != extraction.ProcessingVersion
            || !NormalizationRecordingReadSet.IsComplete(extraction.Ticket, extraction.Preparation)
        )
        {
            throw new ArgumentException(
                "Source actor resolution requires the original complete published producer and ticket."
            );
        }
        return extraction;
    }

    internal static Task<SourceActorResolutionInput[]> ReadOriginalInputsAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SourceActorExtractionConsumption extraction,
        CancellationToken ct
    ) => AuthenticateOriginalInputsAsync(c, t, extraction, false, ct);

    // A negative review may correct the immediately preceding recording producer, whose
    // recording decoders and alias grammar are unchanged. This is not current interpretation
    // authority: every original group, file and selected Local root is still replayed exactly.
    internal static Task<SourceActorResolutionInput[]> ReadOriginalInputsForNegativeReviewAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SourceActorExtractionConsumption extraction,
        CancellationToken ct
    ) => AuthenticateOriginalInputsAsync(c, t, extraction, true, ct);

    private static async Task<SourceActorResolutionInput[]> AuthenticateOriginalInputsAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SourceActorExtractionConsumption extraction,
        bool allowV6NegativeReview,
        CancellationToken ct
    )
    {
        var original = NormalizationRecordingReadSet.Inputs(extraction.Preparation);
        var groups = new[]
        {
            new SourceActorExtractionInput(
                extraction.Preparation.Binding.InputRevisionUuid,
                extraction.Extraction
            ),
        }
            .Concat(extraction.AdditionalExtractions ?? [])
            .ToArray();
        if (
            groups.Select(group => group.InputRevisionUuid).Distinct().Count() != groups.Length
            || !groups
                .Select(group => group.InputRevisionUuid)
                .ToHashSet()
                .SetEquals(original.Select(input => input.Binding.InputRevisionUuid))
        )
        {
            throw new ArgumentException(
                "Source actor input groups must exactly cover the published preparation."
            );
        }
        if (
            groups.Any(group => group.Extraction.SourceActorAliases != null)
            && groups.Any(group => group.Extraction.SourceActorAliases == null)
        )
        {
            throw new ArgumentException(
                "Alias-aware extractions must retain every input group, including empty scopes."
            );
        }
        var inputs = (
            await NormalizationEvidenceService.GetInputsAsync(
                c,
                t,
                original.SelectMany(input => input.Binding.Inputs).Distinct().ToArray(),
                cancellationToken: ct
            )
        ).ToDictionary(input => input.Uuid);
        var authenticator = new OriginalInputAuthenticator(
            extraction.ProcessingVersion,
            inputs,
            allowV6NegativeReview
        );
        var result = original
            .Select(prepared =>
                authenticator.Authenticate(
                    prepared,
                    groups
                        .Single(group =>
                            group.InputRevisionUuid == prepared.Binding.InputRevisionUuid
                        )
                        .Extraction,
                    ct
                )
            )
            .ToArray();
        if (
            result
                .SelectMany(input => input.Scopes)
                .Sum(scope => (long)scope.OriginalScope.Extraction.Assertions.Count) > MaximumClaims
        )
        {
            throw new SourceActorResolutionCapacityException("source_actor_claim_limit");
        }
        return result;
    }

    internal static NormalizationSubjectObservation ExtractionObservation(
        SourceActorExtractionConsumption extraction
    ) =>
        new(
            extraction.Ticket.SubjectUuid,
            extraction.Ticket.DesiredGeneration,
            extraction.Ticket.EditorialRevision,
            extraction.AttemptUuid
        );
}

internal sealed record SourceActorResolutionSource(
    SourceActorExtractionConsumption Extraction,
    IReadOnlyList<SourceActorResolutionInput> Inputs,
    SourceActorResolutionReviewSnapshot SourceReviews,
    NormalizationCandidateUniverseDependency PeopleUniverse
);
