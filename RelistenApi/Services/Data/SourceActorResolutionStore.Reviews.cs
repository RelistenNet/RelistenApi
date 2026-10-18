using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class SourceActorResolutionStore
{
    // Revision and producer are deliberately absent from the field join. A reviewed field
    // cannot lose its protection merely because the next extraction changes either value.
    // Original scopes are transferred once each. Review rows refer to this query's exact
    // scope table; the byte guard covers both representations before either is transferred.
    private const string SourceReviewRowsSql = """
        SELECT review.uuid AS review_uuid, review.original_scope AS scope_json,
            jsonb_build_object(
                'Uuid',review.uuid,
                'Coordinates',jsonb_build_object(
                    'SourceUuid',review.source_uuid,
                    'Contributor',jsonb_build_object(
                        'Provider',review.contributor_provider,
                        'ResourceType',review.contributor_type,
                        'ProviderKey',review.contributor_key),
                    'ResourceUuid',review.resource_uuid,
                    'InputRevisionUuid',review.input_revision_uuid,
                    'FieldJsonPointer',review.field_json_pointer,
                    'ProducerVersion',review.producer_version,
                    'AssertionUuid',review.assertion_uuid,
                    'ActorReferenceUuid',review.actor_reference_uuid,
                    'FieldSha256',review.field_sha256),
                'AssertionSha256',review.assertion_sha256,
                'OriginalAssertion',review.original_assertion,
                'SourceResolutionOwnerUuid',review.source_resolution_owner_uuid,
                'ReviewRevision',review.review_revision,
                'State',review.state,
                'SelectedPersonUuid',review.selected_person_uuid,
                'LastOperationUuid',review.last_operation_uuid) AS review_json
        FROM normalization.source_actor_alias_review_state review
        JOIN jsonb_to_recordset(@fields::jsonb) AS field(
            "ContributorProvider" text,"ContributorType" text,"ContributorKey" text,
            "ResourceUuid" uuid,"FieldJsonPointer" text)
            ON review.contributor_provider=field."ContributorProvider"
                AND review.contributor_type=field."ContributorType"
                AND review.contributor_key=field."ContributorKey"
                AND review.resource_uuid=field."ResourceUuid"
                AND review.field_json_pointer=field."FieldJsonPointer"
        WHERE review.source_uuid=@sourceUuid
        """;

    /// <summary>
    /// Reads complete field review authority and the actual source owner in the caller's
    /// original snapshot. The caller supplies a repeatable-read snapshot or already holds
    /// the source Work lock during publication; this reader never registers or locks owners.
    /// Inputs must already contain the authenticated complete original scope/field inventory.
    /// </summary>
    internal static Task<SourceActorResolutionReviewSnapshot> ReadSourceReviewsAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SourceActorExtractionConsumption extraction,
        IReadOnlyList<SourceActorResolutionInput> inputs,
        CancellationToken ct
    ) => ReadSourceReviewsAsync(c, t, extraction.SourceUuid, extraction.Ticket, inputs, ct);

    private static async Task<SourceActorResolutionReviewSnapshot> ReadSourceReviewsAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid sourceUuid,
        NormalizationWorkTicket extractionTicket,
        IReadOnlyList<SourceActorResolutionInput> inputs,
        CancellationToken ct
    )
    {
        var owner = await c.QuerySingleOrDefaultAsync<NormalizationSubjectObservation>(
            new CommandDefinition(
                """
                SELECT uuid AS "SubjectUuid",desired_generation AS "DesiredGeneration",
                    editorial_revision AS "EditorialRevision",published_work_uuid AS "PublishedWorkUuid"
                FROM normalization.work_subjects
                WHERE domain='people' AND subject_kind='source_actor_resolution' AND subject_uuid=@sourceUuid
                """,
                new { sourceUuid },
                t,
                cancellationToken: ct
            )
        );
        var resources = extractionTicket.Inputs.ToDictionary(
            reference => reference.RevisionUuid,
            reference => reference.ResourceUuid
        );
        var fields = inputs
            .SelectMany(input =>
                input.Scopes.SelectMany(scope =>
                    scope.Fields.Select(field => new SourceReviewFieldKey(
                        input.OriginalInput.Contributor.Provider,
                        input.OriginalInput.Contributor.ResourceType,
                        input.OriginalInput.Contributor.ProviderKey,
                        resources[scope.OriginalScope.InputRevisionUuid],
                        field.Evidence.JsonPointer
                    ))
                )
            )
            .Distinct()
            .OrderBy(field => field.ContributorProvider, StringComparer.Ordinal)
            .ThenBy(field => field.ContributorType, StringComparer.Ordinal)
            .ThenBy(field => field.ContributorKey, StringComparer.Ordinal)
            .ThenBy(field => field.ResourceUuid)
            .ThenBy(field => field.FieldJsonPointer, StringComparer.Ordinal)
            .ToArray();
        if (fields.Length == 0)
        {
            return new(owner, []);
        }

        var parameters = new { sourceUuid, fields = JsonSerializer.Serialize(fields) };
        // The complete containing scope can be repeated on hundreds of review rows.
        // Split it before measuring or transferring rows, not after materializing that
        // repetition in process memory. Both queries use this repeatable snapshot.
        var scopesSql = $"""
            SELECT distinct_scope.scope_json,
                (row_number() OVER (ORDER BY distinct_scope.scope_json)-1)::integer AS scope_ref
            FROM (SELECT DISTINCT scope_json FROM ({SourceReviewRowsSql}) AS review) AS distinct_scope
            """;
        var rowsSql = $"""
            SELECT review.review_uuid,
                review.review_json || jsonb_build_object('OriginalScopeRef',scope.scope_ref) AS row_json
            FROM ({SourceReviewRowsSql}) AS review
            JOIN ({scopesSql}) AS scope USING(scope_json)
            """;
        var bytes = await c.QuerySingleAsync<long>(
            new CommandDefinition(
                $"""
                SELECT
                    (SELECT COALESCE(sum(octet_length(snapshot.row_json::text)),0) FROM ({rowsSql}) AS snapshot)
                    + (SELECT COALESCE(sum(octet_length(scope.scope_json::text)),0) FROM ({scopesSql}) AS scope)
                """,
                parameters,
                t,
                cancellationToken: ct
            )
        );
        if (bytes > MaximumCheckpointBytes)
        {
            throw new SourceActorResolutionCapacityException("source_review_capacity_exceeded");
        }

        var scopeRows = await c.QueryAsync<string>(
            new CommandDefinition(
                $"SELECT scope.scope_json::text FROM ({scopesSql}) AS scope ORDER BY scope.scope_ref",
                parameters,
                t,
                cancellationToken: ct
            )
        );
        var scopes = scopeRows
            .Select(json =>
                JsonSerializer.Deserialize<NormalizationRecordingSourceActorAliasScope>(json)!
            )
            .ToArray();
        var rows = await c.QueryAsync<string>(
            new CommandDefinition(
                $"SELECT snapshot.row_json::text FROM ({rowsSql}) AS snapshot ORDER BY snapshot.review_uuid",
                parameters,
                t,
                cancellationToken: ct
            )
        );
        var reviews = rows.Select(json =>
            {
                var row = JsonSerializer.Deserialize<SourceReviewTransfer>(json)!;
                return new SourceActorAliasReviewState(
                    row.Uuid,
                    row.Coordinates,
                    row.AssertionSha256,
                    row.OriginalAssertion,
                    scopes[row.OriginalScopeRef],
                    row.SourceResolutionOwnerUuid,
                    row.ReviewRevision,
                    row.State,
                    row.SelectedPersonUuid,
                    row.LastOperationUuid
                );
            })
            .ToArray();
        return new(owner, reviews);
    }

    // This read-only transfer shape is scoped to one database snapshot. Its integer is
    // never a domain identity, a persisted checkpoint reference or authority for a write.
    private sealed record SourceReviewTransfer(
        Guid Uuid,
        SourceActorAliasCoordinates Coordinates,
        string AssertionSha256,
        SourceActorAliasAssertion<NormalizedEvidenceReference> OriginalAssertion,
        int OriginalScopeRef,
        Guid SourceResolutionOwnerUuid,
        long ReviewRevision,
        string State,
        Guid? SelectedPersonUuid,
        Guid LastOperationUuid
    );

    private sealed record SourceReviewFieldKey(
        string ContributorProvider,
        string ContributorType,
        string ContributorKey,
        Guid ResourceUuid,
        string FieldJsonPointer
    );
}
