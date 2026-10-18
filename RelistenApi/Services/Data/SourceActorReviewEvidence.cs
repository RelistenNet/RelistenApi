using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

/// <summary>
/// Authenticates immutable extraction evidence separately from the actual heads consumed by
/// a one-off review. Historical withdrawals keep their original field and observe today's
/// resource state; a positive decision also requires the current published extraction.
/// </summary>
internal static class SourceActorReviewEvidence
{
    internal static async Task<SourceActorReviewPreparation> PrepareAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid preparationUuid,
        SourceActorReviewRequest request,
        CancellationToken ct
    )
    {
        var extraction = await SourceActorResolutionStore.ReadPublishedExtractionAsync(
            c,
            t,
            request.ExtractionAttemptUuid,
            request.Claim.SourceUuid,
            ct
        );
        var inputs = await SourceActorResolutionStore.ReadOriginalInputsAsync(c, t, extraction, ct);
        var original = SelectField(extraction, inputs, request.Claim);
        var reviews = await SourceActorResolutionStore.ReadSourceReviewsAsync(
            c,
            t,
            extraction,
            inputs,
            ct
        );
        var source =
            reviews.OwnerObservation
            ?? throw new ArgumentException(
                "Register the source resolution owner before preparing trusted review."
            );
        if (source.EditorialRevision != request.ExpectedEditorialRevision)
        {
            throw new NormalizationConflictException(
                "The source owner changed before review preparation."
            );
        }
        var prior = reviews.Reviews.SingleOrDefault(review => review.Coordinates == request.Claim);
        if ((prior?.ReviewRevision ?? 0) != request.ExpectedSourceReviewRevision)
        {
            throw new NormalizationConflictException(
                "The exact source claim review changed before preparation."
            );
        }
        var assigned = await ReadAssignmentAsync(c, t, request.Claim, ct);
        RequireAction(request.Action, prior, assigned);

        var current = await SourceActorReviewCurrentFacts.ReadAsync(c, t, extraction, ct);
        var extractionOwner = await ReadOwnerAsync(c, t, extraction.Ticket.SubjectUuid, ct);
        SourceActorReviewTarget? target = null;
        if (request.Action is SourceActorReviewAction.Accept or SourceActorReviewAction.Reassign)
        {
            var assertion = original.Scope.Extraction.Assertions.Single(value =>
                value.AssertionUuid == request.Claim.AssertionUuid
            );
            if (
                prior != null
                && (
                    !Same(prior.OriginalScope, original.Scope)
                    || !Same(prior.OriginalAssertion, assertion)
                    || prior.AssertionSha256 != AssertionHash(assertion)
                )
            )
            {
                throw new NormalizationConflictException(
                    "The existing source review's immutable proof does not match the authenticated original field."
                );
            }
            if (
                extractionOwner.PublishedWorkUuid != extraction.AttemptUuid
                || !SourceActorReviewCurrentFacts.StillSupports(extraction.Ticket, current.ReadSet)
                || current.ArtistUuid != extraction.ArtistUuid
            )
            {
                throw new NormalizationConflictException(
                    "Positive source review requires the actual current published field; select its current extraction claim."
                );
            }
            target = await SourceActorReviewTargets.ReadAsync(
                c,
                t,
                request.SelectedPersonUuid!.Value,
                ct
            );
            if (
                target
                    .Members.Single(member => member.PersonUuid == target.OriginalPersonUuid)
                    .Revision != request.ExpectedSelectedPersonRevision
            )
            {
                throw new NormalizationConflictException(
                    "The selected identity changed before review preparation."
                );
            }
        }
        var owners = new[] { source, extractionOwner }
            .Concat(target?.OwnerObservations ?? [])
            .Distinct()
            .OrderBy(owner => owner.SubjectUuid)
            .ToArray();
        var readSet = current.ReadSet with { OwnerObservations = owners };
        return new(
            preparationUuid,
            SourceActorReviewService.ProcessingVersion,
            request,
            prior?.Uuid ?? Guid.NewGuid(),
            source,
            original,
            prior,
            assigned,
            target,
            readSet
        );
    }

    internal static SourceActorReviewField SelectField(
        SourceActorExtractionConsumption extraction,
        IReadOnlyList<SourceActorResolutionInput> inputs,
        SourceActorAliasCoordinates selector
    ) =>
        IndexFields(extraction, inputs).TryGetValue(selector, out var field)
            ? field
            : throw new ArgumentException(
                "The selected claim must identify one complete original published source assertion."
            );

    // A source can contain a thousand claims in one field. Build coordinates/field lookup
    // once for correspondence; selecting every assertion must not rescan the whole source.
    internal static IReadOnlyDictionary<
        SourceActorAliasCoordinates,
        SourceActorReviewField
    > IndexFields(
        SourceActorExtractionConsumption extraction,
        IReadOnlyList<SourceActorResolutionInput> inputs
    )
    {
        var result = new Dictionary<SourceActorAliasCoordinates, SourceActorReviewField>();
        var resources = extraction.Ticket.Inputs.ToDictionary(reference => reference.RevisionUuid);
        foreach (var input in inputs)
        {
            foreach (var scope in input.Scopes)
            {
                var resource = resources[scope.OriginalScope.InputRevisionUuid];
                var fields = scope.Fields.ToDictionary(field =>
                    (
                        field.Evidence.InputRevisionUuid,
                        field.Evidence.JsonPointer,
                        field.FieldSha256
                    )
                );
                foreach (var assertion in scope.OriginalScope.Extraction.Assertions)
                {
                    var coordinates = new SourceActorAliasCoordinates(
                        extraction.SourceUuid,
                        input.OriginalInput.Contributor,
                        resource.ResourceUuid,
                        scope.OriginalScope.InputRevisionUuid,
                        assertion.NameEvidence.JsonPointer,
                        scope.OriginalScope.ProducerVersion,
                        assertion.AssertionUuid,
                        assertion.ActorReferenceUuid,
                        assertion.FieldSha256
                    );
                    var field = fields[
                        (
                            assertion.NameEvidence.InputRevisionUuid,
                            assertion.NameEvidence.JsonPointer,
                            assertion.FieldSha256
                        )
                    ];
                    var whole = field.Evidence with
                    {
                        StartCodePoint = field.Evidence.StartCodePoint ?? 0,
                        EndCodePoint =
                            field.Evidence.EndCodePoint
                            ?? (field.Evidence.StartCodePoint ?? 0)
                                + field.OriginalText.EnumerateRunes().Count(),
                        Quote = field.OriginalText,
                    };
                    if (
                        !result.TryAdd(
                            coordinates,
                            new(
                                extraction.AttemptUuid,
                                coordinates,
                                scope.OriginalScope,
                                field.OriginalText,
                                whole
                            )
                        )
                    )
                    {
                        throw new ArgumentException(
                            "A published source assertion has duplicated exact claim coordinates."
                        );
                    }
                }
            }
        }
        return result;
    }

    internal static async Task RequireUnchangedAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SourceActorReviewPreparation preparation,
        CancellationToken ct
    )
    {
        // All original mutable facts were locked and compared by shared Work first. Re-read
        // domain selectors and target topology; no fresh observations replace the saved proof.
        var extraction = await SourceActorResolutionStore.ReadPublishedExtractionAsync(
            c,
            t,
            preparation.Request.ExtractionAttemptUuid,
            preparation.Request.Claim.SourceUuid,
            ct
        );
        var inputs = await SourceActorResolutionStore.ReadOriginalInputsAsync(c, t, extraction, ct);
        if (
            !Same(
                SelectField(extraction, inputs, preparation.Request.Claim),
                preparation.OriginalField
            )
        )
        {
            throw new NormalizationConflictException(
                "The saved source field does not authenticate against its original extraction."
            );
        }
        var reviews = await SourceActorResolutionStore.ReadSourceReviewsAsync(
            c,
            t,
            extraction,
            inputs,
            ct
        );
        var review = reviews.Reviews.SingleOrDefault(value =>
            value.Coordinates == preparation.Request.Claim
        );
        var assignment = await ReadAssignmentAsync(c, t, preparation.Request.Claim, ct);
        if (
            !Same(review, preparation.PriorReview)
            || assignment != preparation.PriorAssignedPersonUuid
        )
        {
            throw new NormalizationConflictException(
                "The source claim binding changed after preparation."
            );
        }
        RequireAction(preparation.Request.Action, review, assignment);
        if (preparation.Target is { } expected)
        {
            await c.ExecuteAsync(
                new CommandDefinition(
                    "SELECT pg_advisory_xact_lock_shared(hashtextextended('normalization:people:identity',0))",
                    transaction: t,
                    cancellationToken: ct
                )
            );
            var current = await SourceActorReviewTargets.ReadAsync(
                c,
                t,
                expected.OriginalPersonUuid,
                ct
            );
            if (!Same(current, expected))
            {
                throw new NormalizationConflictException(
                    "The selected identity or redirect component changed after preparation."
                );
            }
        }
    }

    private static void RequireAction(
        SourceActorReviewAction action,
        SourceActorAliasReviewState? review,
        Guid? assigned
    )
    {
        // A reviewed rejection/retraction is the current authority even while phase 4 has
        // not physically retired older machine support. An explicit restoration uses accept.
        var bound = review == null ? assigned.HasValue : review.State == "accepted";
        if (action == SourceActorReviewAction.Accept && bound)
        {
            throw new ArgumentException(
                "Use reassign to replace an existing accepted source binding."
            );
        }
        if (action is SourceActorReviewAction.Reassign or SourceActorReviewAction.Retract && !bound)
        {
            throw new ArgumentException(
                "Reassignment or retraction requires an existing accepted source binding."
            );
        }
    }

    internal static string AssertionHash(
        SourceActorAliasAssertion<NormalizedEvidenceReference> assertion
    ) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(assertion)));

    internal static bool Same<T>(T left, T right) =>
        JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(left),
            JsonSerializer.SerializeToElement(right)
        );

    private static Task<Guid?> ReadAssignmentAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SourceActorAliasCoordinates claim,
        CancellationToken ct
    ) =>
        c.QuerySingleOrDefaultAsync<Guid?>(
            new CommandDefinition(
                """
                SELECT person_uuid FROM normalization.source_actor_alias_assignments
                WHERE source_uuid=@SourceUuid AND contributor_provider=@provider AND contributor_type=@type
                    AND contributor_key=@key AND resource_uuid=@ResourceUuid AND input_revision_uuid=@InputRevisionUuid
                    AND field_json_pointer=@FieldJsonPointer AND producer_version=@ProducerVersion AND assertion_uuid=@AssertionUuid
                    AND retired_work_uuid IS NULL
                """,
                new
                {
                    claim.SourceUuid,
                    provider = claim.Contributor.Provider,
                    type = claim.Contributor.ResourceType,
                    key = claim.Contributor.ProviderKey,
                    claim.ResourceUuid,
                    claim.InputRevisionUuid,
                    claim.FieldJsonPointer,
                    claim.ProducerVersion,
                    claim.AssertionUuid,
                },
                t,
                cancellationToken: ct
            )
        );

    private static Task<NormalizationSubjectObservation> ReadOwnerAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid owner,
        CancellationToken ct
    ) =>
        c.QuerySingleAsync<NormalizationSubjectObservation>(
            new CommandDefinition(
                """
                SELECT uuid AS "SubjectUuid",desired_generation AS "DesiredGeneration",editorial_revision AS "EditorialRevision",
                    published_work_uuid AS "PublishedWorkUuid" FROM normalization.work_subjects WHERE uuid=@owner
                """,
                new { owner },
                t,
                cancellationToken: ct
            )
        );
}
