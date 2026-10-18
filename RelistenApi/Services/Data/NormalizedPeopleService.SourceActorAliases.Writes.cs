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

public sealed partial class NormalizedPeopleService
{
    private static async Task<SourceAliasAddition> AddSourceActorAliases(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid applicationUuid,
        SourceActorAliasPersonStage stage,
        SourceActorAliasStageDelta delta,
        CancellationToken ct
    )
    {
        var authority =
            delta.Acceptance
            ?? throw new ArgumentException(
                "Alias addition requires its authenticated original authority reference."
            );
        var coordinates =
            delta.Coordinates
            ?? throw new ArgumentException(
                "Alias addition requires exact retained source coordinates."
            );
        var scope = stage
            .Facts
            .Inputs[authority.Claim.InputIndex]
            .Scopes[authority.Claim.ScopeIndex]
            .OriginalScope;
        var assertion = scope.Extraction.Assertions[authority.Claim.AssertionIndex];
        var names = new List<SourceActorAliasNameOutcome>();
        var bindings = new List<SourceActorAliasMapBinding>();
        SourceAliasAssignment? assignment = null;
        foreach (var name in delta.Names)
        {
            // Human source selection authenticates an identity, not canonical metadata review.
            // No append publication is used here: that path grants unmanaged alias authority.
            var written = await WriteAutomaticAliasAsync(
                c,
                t,
                stage.WorkUuid,
                new(
                    name.RequestedAliasUuid,
                    stage.OriginalPersonUuid,
                    name.Wording,
                    name.Evidence,
                    "validated_match"
                ),
                new HashSet<Guid>(),
                ct
            );
            Guid? support = null;
            if (written.Accepted)
            {
                if (written.AliasUuid == null)
                {
                    throw new InvalidOperationException(
                        "An accepted canonical alias result must name its actual stored assertion."
                    );
                }
                assignment ??= await GetOrCreateSourceAliasAssignment(
                    c,
                    t,
                    applicationUuid,
                    stage,
                    delta,
                    coordinates,
                    scope,
                    assertion,
                    ct
                );
                support = await GetOrCreateSourceAliasSupport(
                    c,
                    t,
                    stage,
                    assignment.Uuid,
                    written.AliasUuid.Value,
                    ct
                );
                bindings.Add(
                    new(
                        assignment.Uuid,
                        written.AliasUuid.Value,
                        support.Value,
                        assignment.Acceptance
                    )
                );
            }
            names.Add(
                new(
                    authority.Claim,
                    name.RequestedAliasUuid,
                    name.Wording,
                    SourceAliasDisposition(written.Disposition),
                    SourceAliasReason(written.Disposition),
                    written.AliasUuid,
                    written.Accepted ? assignment!.Uuid : null,
                    support
                )
            );
        }
        return new(names, bindings);
    }

    private static async Task<SourceAliasAssignment> GetOrCreateSourceAliasAssignment(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid applicationUuid,
        SourceActorAliasPersonStage stage,
        SourceActorAliasStageDelta delta,
        SourceActorAliasCoordinates coordinates,
        NormalizationRecordingSourceActorAliasScope scope,
        SourceActorAliasAssertion<NormalizedEvidenceReference> assertion,
        CancellationToken ct
    )
    {
        var args = new DynamicParameters(
            new
            {
                coordinates.SourceUuid,
                coordinates.ResourceUuid,
                coordinates.InputRevisionUuid,
                coordinates.FieldJsonPointer,
                coordinates.ProducerVersion,
                coordinates.AssertionUuid,
                coordinates.ActorReferenceUuid,
                coordinates.FieldSha256,
            }
        );
        args.Add("Provider", coordinates.Contributor.Provider);
        args.Add("ResourceType", coordinates.Contributor.ResourceType);
        args.Add("ProviderKey", coordinates.Contributor.ProviderKey);
        var existing = await c.QuerySingleOrDefaultAsync<SourceAliasAssignmentRow>(
            new CommandDefinition(
                """
                SELECT uuid AS "Uuid",person_uuid AS "PersonUuid",assertion_sha256 AS "AssertionSha256",
                    original_assertion::text AS "Assertion",original_scope::text AS "Scope",authority_receipt::text AS "Authority"
                FROM normalization.source_actor_alias_assignments
                WHERE source_uuid=@SourceUuid AND contributor_provider=@Provider AND contributor_type=@ResourceType
                    AND contributor_key=@ProviderKey AND resource_uuid=@ResourceUuid AND input_revision_uuid=@InputRevisionUuid
                    AND field_json_pointer=@FieldJsonPointer AND producer_version=@ProducerVersion AND assertion_uuid=@AssertionUuid
                    AND retired_work_uuid IS NULL
                """,
                args,
                t,
                cancellationToken: ct
            )
        );
        if (existing != null)
        {
            var prior = stage
                .PriorMap.SelectMany(entry => entry.Bindings)
                .Where(binding => binding.AssignmentUuid == existing.Uuid)
                .Select(binding => binding.Acceptance)
                .Distinct()
                .ToArray();
            if (
                existing.PersonUuid != stage.OriginalPersonUuid
                || existing.AssertionSha256 != delta.AssertionSha256
                || prior.Length != 1
                || !SourceAliasJsonSame(existing.Assertion, assertion)
                || !SourceAliasJsonSame(existing.Scope, scope)
                || !SourceAliasJsonSame(existing.Authority, prior[0])
            )
            {
                throw new NormalizationConflictException(
                    "Existing exact source assignment differs from its retained original authority or accepted person map."
                );
            }
            // Reuse preserves its original receipt. A new human decision or active policy
            // observation does not rewrite an earlier accepted machine interpretation.
            return new(existing.Uuid, prior[0]);
        }
        var authority = delta.Acceptance! with { AcceptedStageUuid = stage.StageUuid };
        var uuid = SourceActorAliasApplicationService.StableUuid(
            stage.StageUuid,
            "assignment/" + JsonSerializer.Serialize(coordinates)
        );
        args.Add("Uuid", uuid);
        args.Add("AssertionSha256", delta.AssertionSha256);
        args.Add("Assertion", JsonSerializer.Serialize(assertion));
        args.Add("Scope", JsonSerializer.Serialize(scope));
        args.Add("Authority", JsonSerializer.Serialize(authority));
        args.Add("PersonUuid", stage.OriginalPersonUuid);
        args.Add(
            "SourceResolutionOwnerUuid",
            stage.Facts.SourceReviews.OwnerObservation?.SubjectUuid
                ?? throw new NormalizationConflictException(
                    "Source alias assignment requires its observed original source owner."
                )
        );
        args.Add("ExtractionOwnerUuid", stage.Facts.OriginalExtractionTicket.SubjectUuid);
        args.Add("ExtractionAttemptUuid", stage.Facts.ExtractionAttemptUuid);
        args.Add("ApplicationAttemptUuid", applicationUuid);
        args.Add("StageUuid", stage.StageUuid);
        args.Add(
            "Basis",
            authority.Kind == SourceActorAliasAuthorityKind.Machine ? "validated_match" : "reviewed"
        );
        args.Add("WorkUuid", stage.WorkUuid);
        var inserted = await c.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO normalization.source_actor_alias_assignments(
                    uuid,source_uuid,contributor_provider,contributor_type,contributor_key,resource_uuid,input_revision_uuid,
                    field_json_pointer,producer_version,assertion_uuid,actor_reference_uuid,field_sha256,assertion_sha256,
                    original_assertion,original_scope,authority_receipt,person_uuid,source_resolution_owner_uuid,
                    extraction_owner_uuid,extraction_attempt_uuid,application_attempt_uuid,stage_uuid,basis,introduced_work_uuid)
                VALUES(@Uuid,@SourceUuid,@Provider,@ResourceType,@ProviderKey,@ResourceUuid,@InputRevisionUuid,
                    @FieldJsonPointer,@ProducerVersion,@AssertionUuid,@ActorReferenceUuid,@FieldSha256,@AssertionSha256,
                    @Assertion::jsonb,@Scope::jsonb,@Authority::jsonb,@PersonUuid,@SourceResolutionOwnerUuid,
                    @ExtractionOwnerUuid,@ExtractionAttemptUuid,@ApplicationAttemptUuid,@StageUuid,@Basis,@WorkUuid)
                ON CONFLICT DO NOTHING
                """,
                args,
                t,
                cancellationToken: ct
            )
        );
        if (inserted != 1)
        {
            throw new NormalizationConflictException(
                "New source assignment UUID or exact coordinates collided with another retained binding."
            );
        }
        return new(uuid, authority);
    }

    private static async Task<Guid> GetOrCreateSourceAliasSupport(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SourceActorAliasPersonStage stage,
        Guid assignmentUuid,
        Guid aliasUuid,
        CancellationToken ct
    )
    {
        var existing = await c.QuerySingleOrDefaultAsync<Guid?>(
            new CommandDefinition(
                """
                SELECT uuid FROM normalization.person_alias_contributor_support
                WHERE assignment_uuid=@assignmentUuid AND alias_uuid=@aliasUuid AND retired_work_uuid IS NULL
                """,
                new { assignmentUuid, aliasUuid },
                t,
                cancellationToken: ct
            )
        );
        if (existing.HasValue)
        {
            return existing.Value;
        }
        var uuid = SourceActorAliasApplicationService.StableUuid(
            stage.StageUuid,
            $"support/{assignmentUuid:D}/{aliasUuid:D}"
        );
        var inserted = await c.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO normalization.person_alias_contributor_support(uuid,assignment_uuid,alias_uuid,introduced_work_uuid)
                VALUES(@uuid,@assignmentUuid,@aliasUuid,@WorkUuid) ON CONFLICT DO NOTHING
                """,
                new
                {
                    uuid,
                    assignmentUuid,
                    aliasUuid,
                    stage.WorkUuid,
                },
                t,
                cancellationToken: ct
            )
        );
        if (inserted != 1)
        {
            throw new NormalizationConflictException(
                "New source alias support UUID collided with another retained authority row."
            );
        }
        return uuid;
    }

    private static async Task RetireSourceActorAliases(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SourceActorAliasPersonStage stage,
        SourceActorAliasStageDelta delta,
        CancellationToken ct
    )
    {
        // Retire support first. Only the exact prepared current IDs and original person may
        // change; canonical aliases/history remain independent, including reviewed authority.
        var supports = delta.RetireSupportUuids.Distinct().ToArray();
        var assignments = delta.RetireAssignmentUuids.Distinct().ToArray();
        var retiredSupport = await c.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE normalization.person_alias_contributor_support support SET retired_work_uuid=@WorkUuid
                FROM normalization.source_actor_alias_assignments assignment
                WHERE support.uuid=ANY(@supports) AND support.assignment_uuid=assignment.uuid
                    AND assignment.person_uuid=@OriginalPersonUuid AND assignment.retired_work_uuid IS NULL
                    AND support.retired_work_uuid IS NULL
                """,
                new
                {
                    stage.WorkUuid,
                    stage.OriginalPersonUuid,
                    supports,
                },
                t,
                cancellationToken: ct
            )
        );
        var retiredAssignment = await c.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE normalization.source_actor_alias_assignments SET retired_work_uuid=@WorkUuid
                WHERE uuid=ANY(@assignments) AND person_uuid=@OriginalPersonUuid AND retired_work_uuid IS NULL
                """,
                new
                {
                    stage.WorkUuid,
                    stage.OriginalPersonUuid,
                    assignments,
                },
                t,
                cancellationToken: ct
            )
        );
        if (retiredSupport != supports.Length || retiredAssignment != assignments.Length)
        {
            throw new NormalizationConflictException(
                "Source alias retirement no longer matches its exact prepared original-person authority rows."
            );
        }
    }

    private static bool SourceAliasJsonSame<T>(string json, T value)
    {
        using var document = JsonDocument.Parse(json);
        return JsonElement.DeepEquals(
            document.RootElement,
            JsonSerializer.SerializeToElement(value)
        );
    }

    private static SourceActorAliasNameDisposition SourceAliasDisposition(
        CanonicalPersonAliasWriteDisposition disposition
    ) =>
        disposition switch
        {
            CanonicalPersonAliasWriteDisposition.Accepted =>
                SourceActorAliasNameDisposition.Accepted,
            CanonicalPersonAliasWriteDisposition.ExactDuplicate =>
                SourceActorAliasNameDisposition.ExactDuplicate,
            CanonicalPersonAliasWriteDisposition.Pending => SourceActorAliasNameDisposition.Pending,
            CanonicalPersonAliasWriteDisposition.ProtectedReviewedNaturalClaim =>
                SourceActorAliasNameDisposition.ProtectedReviewedNaturalClaim,
            CanonicalPersonAliasWriteDisposition.ConflictingUuid =>
                SourceActorAliasNameDisposition.ConflictingUuid,
            _ => throw new ArgumentException("Unknown canonical alias write disposition."),
        };

    private static string SourceAliasReason(CanonicalPersonAliasWriteDisposition disposition) =>
        disposition switch
        {
            CanonicalPersonAliasWriteDisposition.Accepted => "canonical_alias_accepted",
            CanonicalPersonAliasWriteDisposition.ExactDuplicate =>
                "canonical_alias_exact_duplicate",
            CanonicalPersonAliasWriteDisposition.Pending => "canonical_alias_pending",
            CanonicalPersonAliasWriteDisposition.ProtectedReviewedNaturalClaim =>
                "canonical_alias_review_protected",
            CanonicalPersonAliasWriteDisposition.ConflictingUuid => "canonical_alias_uuid_conflict",
            _ => throw new ArgumentException("Unknown canonical alias write disposition."),
        };

    private sealed record SourceAliasAddition(
        IReadOnlyList<SourceActorAliasNameOutcome> Names,
        IReadOnlyList<SourceActorAliasMapBinding> Bindings
    );

    private sealed record SourceAliasAssignment(
        Guid Uuid,
        SourceActorAliasAcceptanceReference Acceptance
    );

    private sealed record SourceAliasAssignmentRow(
        Guid Uuid,
        Guid PersonUuid,
        string AssertionSha256,
        string Assertion,
        string Scope,
        string Authority
    );
}
