using System;
using System.Data;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

/// <summary>
/// Trusted one-off source decisions. Prepares authenticated original evidence and actual
/// read-only facts, then applies by IDs through Work. Only source review state changes;
/// person assignment, canonical aliases, support, recording roles and kinds remain untouched.
/// </summary>
public sealed class SourceActorReviewService(
    DbService db,
    NormalizationWorkService work,
    SourceActorReviewStore store
)
{
    public const string ProcessingVersion = "source-actor-trusted-review-v1";

    /// <summary>
    /// First immutable preparation wins. A repeated ID with another request conflicts; an
    /// exact retry returns the saved observations, including after they have become stale.
    /// </summary>
    public async Task<SourceActorReviewPreparation> PrepareAsync(
        Guid preparationUuid,
        SourceActorReviewRequest request,
        CancellationToken ct = default
    )
    {
        Validate(preparationUuid, request);
        var prior = await store.GetPreparationAsync(preparationUuid, ct);
        if (prior != null)
        {
            SourceActorReviewStore.RequireRequest(prior, request);
            return prior;
        }
        var preparation = await db.WithWriteConnection(async connection =>
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
            var result = await SourceActorReviewEvidence.PrepareAsync(
                c,
                t,
                preparationUuid,
                request,
                ct
            );
            await t.CommitAsync(ct);
            return result;
        });
        return await store.SaveAsync(preparation, ct);
    }

    /// <summary>
    /// Applies only the saved preparation, with a trusted operator and reason. Work checks
    /// committed exact replay before current facts; stale or failed reviews can change no
    /// domain state or receipt. New evidence/selection needs another preparation and operation.
    /// </summary>
    public async Task<NormalizationReviewResult> ApplyAsync(
        Guid operationUuid,
        Guid preparationUuid,
        string reason,
        string trustedOperator,
        CancellationToken ct = default
    )
    {
        if (operationUuid == Guid.Empty || preparationUuid == Guid.Empty)
        {
            throw new ArgumentException("Stable operation and preparation UUIDs are required.");
        }
        var preparation =
            await store.GetPreparationAsync(preparationUuid, ct)
            ?? throw new ArgumentException("Unknown source review preparation.");
        var action = preparation.Request.Action switch
        {
            SourceActorReviewAction.Accept => "accept",
            SourceActorReviewAction.Reject => "reject",
            SourceActorReviewAction.Retract => "retract",
            SourceActorReviewAction.Reassign => "replace",
            _ => throw new ArgumentException("Unsupported source review action."),
        };
        return await work.ApplyReviewAsync(
            new NormalizationReviewCommand(
                operationUuid,
                preparation.SourceOwner.SubjectUuid,
                preparation.Request.ExpectedEditorialRevision,
                action,
                JsonSerializer.SerializeToElement(
                    new SourceReviewPayload(preparationUuid, preparation)
                ),
                reason,
                trustedOperator,
                ReadSet: preparation.ReadSet
            ),
            async (c, t, token) =>
            {
                // Keep validation inside the callback: committed replay must still succeed
                // after inputs, targets or processing versions change.
                if (preparation.ProcessingVersion != ProcessingVersion)
                {
                    throw new NormalizationConflictException(
                        "Prepare source review with a supported processing version."
                    );
                }
                await SourceActorReviewEvidence.RequireUnchangedAsync(c, t, preparation, token);
                await WriteReviewAsync(c, t, operationUuid, preparation, token);
            },
            ct
        );
    }

    private static async Task WriteReviewAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid operationUuid,
        SourceActorReviewPreparation preparation,
        CancellationToken ct
    )
    {
        var claim = preparation.Request.Claim;
        var assertion = preparation.OriginalField.Scope.Extraction.Assertions.Single(value =>
            value.AssertionUuid == claim.AssertionUuid
        );
        var state = preparation.Request.Action switch
        {
            SourceActorReviewAction.Accept or SourceActorReviewAction.Reassign => "accepted",
            SourceActorReviewAction.Reject => "rejected",
            _ => "retracted",
        };
        var parameters = new
        {
            preparation.ReviewStateUuid,
            claim.SourceUuid,
            provider = claim.Contributor.Provider,
            type = claim.Contributor.ResourceType,
            key = claim.Contributor.ProviderKey,
            claim.ResourceUuid,
            claim.InputRevisionUuid,
            claim.FieldJsonPointer,
            claim.ProducerVersion,
            claim.AssertionUuid,
            claim.ActorReferenceUuid,
            claim.FieldSha256,
            assertionHash = SourceActorReviewEvidence.AssertionHash(assertion),
            assertion = JsonSerializer.Serialize(assertion),
            scope = JsonSerializer.Serialize(preparation.OriginalField.Scope),
            owner = preparation.SourceOwner.SubjectUuid,
            state,
            selected = preparation.Target?.OriginalPersonUuid,
            operationUuid,
            expected = preparation.Request.ExpectedSourceReviewRevision,
        };
        var written = await c.ExecuteAsync(
            new CommandDefinition(
                preparation.PriorReview == null
                    ? """
                    INSERT INTO normalization.source_actor_alias_review_state(
                        uuid,source_uuid,contributor_provider,contributor_type,contributor_key,resource_uuid,input_revision_uuid,
                        field_json_pointer,producer_version,assertion_uuid,actor_reference_uuid,field_sha256,assertion_sha256,
                        original_assertion,original_scope,source_resolution_owner_uuid,review_revision,state,selected_person_uuid,last_operation_uuid)
                    VALUES(@ReviewStateUuid,@SourceUuid,@provider,@type,@key,@ResourceUuid,@InputRevisionUuid,
                        @FieldJsonPointer,@ProducerVersion,@AssertionUuid,@ActorReferenceUuid,@FieldSha256,@assertionHash,
                        @assertion::jsonb,@scope::jsonb,@owner,1,@state,@selected,@operationUuid)
                    """
                    : """
                    UPDATE normalization.source_actor_alias_review_state
                    SET review_revision=review_revision+1,state=@state,selected_person_uuid=@selected,last_operation_uuid=@operationUuid
                    WHERE uuid=@ReviewStateUuid AND review_revision=@expected
                    """,
                parameters,
                t,
                cancellationToken: ct
            )
        );
        if (written != 1)
        {
            throw new NormalizationConflictException(
                "Source review CAS changed while applying its decision."
            );
        }
        // The receipt FK/action guard is deferred: Work writes the immutable operation after
        // this callback, then advances only the source owner's editorial revision/generation.
    }

    private static void Validate(Guid preparationUuid, SourceActorReviewRequest request)
    {
        if (
            preparationUuid == Guid.Empty
            || request.ExtractionAttemptUuid == Guid.Empty
            || request.Claim.SourceUuid == Guid.Empty
            || request.Claim.AssertionUuid == Guid.Empty
            || request.ExpectedEditorialRevision < 0
            || request.ExpectedSourceReviewRevision < 0
            || !Enum.IsDefined(request.Action)
        )
        {
            throw new ArgumentException(
                "Source review requires stable selectors, a supported action and nonnegative expected revisions."
            );
        }
        var positive =
            request.Action is SourceActorReviewAction.Accept or SourceActorReviewAction.Reassign;
        if (
            positive != request.SelectedPersonUuid.HasValue
            || positive != request.ExpectedSelectedPersonRevision.HasValue
            || request.SelectedPersonUuid == Guid.Empty
            || request.ExpectedSelectedPersonRevision is < 1
        )
        {
            throw new ArgumentException(
                "Only positive source review selects an existing identity and its expected revision."
            );
        }
    }

    private sealed record SourceReviewPayload(
        Guid PreparationUuid,
        SourceActorReviewPreparation Preparation
    );
}
