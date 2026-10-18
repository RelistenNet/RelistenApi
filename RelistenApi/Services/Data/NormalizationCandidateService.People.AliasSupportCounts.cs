using System;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationCandidateService
{
    private static async Task<PersonAliasSupportCounts> ReadPersonAliasSupportCounts(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid[] members,
        CancellationToken ct
    )
    {
        // People facts also run on historical schema-53 fixtures. PostgreSQL resolves table
        // names before execution, so the compatibility branch must precede any SQL naming 57.
        var hasPublicationHistory = await c.QuerySingleAsync<bool>(
            new CommandDefinition(
                "SELECT to_regclass('normalization.source_actor_alias_person_stages') IS NOT NULL",
                transaction: t,
                cancellationToken: ct
            )
        );
        var sql = hasPublicationHistory
            ? CertifiedAliasSupportCountsSql
            : LegacyAliasSupportCountsSql;

        return await c.QuerySingleAsync<PersonAliasSupportCounts>(
            new CommandDefinition(
                sql,
                new
                {
                    members,
                    certificateVersion = SourceActorAliasApplicationStore.PublishedStageCertificateVersion,
                    applicationVersion = SourceActorAliasApplicationService.Version,
                    historicalApplicationVersions = SourceActorAliasApplicationService.HistoricalAcceptedApplicationReadVersions(),
                    format = SourceActorAliasApplicationCheckpointCodec.FormatVersion,
                    accepted = ((int)SourceActorAliasNameDisposition.Accepted).ToString(
                        System.Globalization.CultureInfo.InvariantCulture
                    ),
                    duplicate = ((int)SourceActorAliasNameDisposition.ExactDuplicate).ToString(
                        System.Globalization.CultureInfo.InvariantCulture
                    ),
                },
                t,
                cancellationToken: ct
            )
        );
    }

    // This is the existing structural check, used only when the original publication has
    // no certificate. Its containing scope can be large; never put it in the certified path.
    private const string LegacyAliasSupportProofPredicate = """
        length(assignment.original_assertion->>'Version')>0
        AND assignment.original_assertion->>'AssertionUuid'=assignment.assertion_uuid::text
        AND assignment.original_assertion->>'ActorReferenceUuid'=assignment.actor_reference_uuid::text
        AND assignment.original_assertion->>'FieldSha256'=assignment.field_sha256
        AND jsonb_typeof(assignment.original_assertion->'Name')='string'
        AND length(assignment.original_assertion->>'Name')>0
        AND jsonb_typeof(assignment.original_assertion->'NameEvidence')='object'
        AND assignment.original_assertion#>>'{NameEvidence,InputRevisionUuid}'=assignment.input_revision_uuid::text
        AND assignment.original_assertion#>>'{NameEvidence,JsonPointer}'=assignment.field_json_pointer
        AND jsonb_typeof(assignment.original_assertion->'Aliases')='array'
        AND assignment.original_assertion->'Aliases'<>'[]'::jsonb
        AND jsonb_typeof(assignment.original_assertion->'Relationships')='array'
        AND assignment.original_assertion->'Relationships'<>'[]'::jsonb
        AND jsonb_typeof(assignment.original_assertion->'Context')='object'
        AND jsonb_typeof(assignment.original_assertion#>'{Context,Heading}')='object'
        AND jsonb_typeof(assignment.original_assertion#>'{Context,Qualifiers}')='array'
        AND jsonb_typeof(assignment.original_assertion#>'{Context,Statement}')='object'
        AND jsonb_typeof(assignment.original_assertion->'Unresolved')='array'
        AND assignment.original_scope->>'ProducerVersion'=assignment.producer_version
        AND assignment.original_scope->>'InputRevisionUuid'=assignment.input_revision_uuid::text
        AND assignment.original_scope#>>'{Resource,Provider}'=resource.provider
        AND assignment.original_scope#>>'{Resource,ResourceType}'=resource.resource_type
        AND assignment.original_scope#>>'{Resource,ProviderKey}'=resource.provider_key
        AND jsonb_typeof(assignment.original_scope->'RecordingOrigin')='object'
        AND jsonb_typeof(assignment.original_scope->'Extraction')='object'
        AND jsonb_typeof(assignment.original_scope#>'{Extraction,Assertions}')='array'
        AND (assignment.original_scope#>'{Extraction,Assertions}') @> jsonb_build_array(assignment.original_assertion)
        AND jsonb_typeof(assignment.original_scope#>'{Extraction,Unresolved}')='array'
        AND jsonb_typeof(assignment.original_scope#>'{Extraction,Scopes}')='array'
        """;

    private const string LegacyAliasSupportCountsSql =
        """
            SELECT count(*) AS "Supports",count(*) FILTER(WHERE (
            """
        + LegacyAliasSupportProofPredicate
        + """
            ) IS NOT TRUE) AS "InvalidProofs"
            FROM normalization.current_person_aliases alias
            JOIN normalization.person_alias_contributor_support support ON support.alias_uuid=alias.uuid
            JOIN normalization.source_actor_alias_assignments assignment ON assignment.uuid=support.assignment_uuid
            JOIN normalization.input_resources resource ON resource.uuid=assignment.resource_uuid
            WHERE alias.person_uuid=ANY(@members) AND support.retired_work_uuid IS NULL
                AND assignment.retired_work_uuid IS NULL
            """;

    // Materialize identities before touching historical proof. A support can be added by a
    // later publication to an older assignment, so its own introducing Work is the receipt
    // that must certify it. Each distinct Work/header is read and expanded once per query.
    private const string CertifiedAliasSupportCountsSql =
        """
            WITH active AS MATERIALIZED (
                SELECT support.uuid AS support_uuid,support.alias_uuid,support.assignment_uuid,
                    support.introduced_work_uuid,assignment.person_uuid,assignment.source_uuid
                FROM normalization.current_person_aliases alias
                JOIN normalization.person_alias_contributor_support support ON support.alias_uuid=alias.uuid
                JOIN normalization.source_actor_alias_assignments assignment ON assignment.uuid=support.assignment_uuid
                JOIN normalization.input_resources resource ON resource.uuid=assignment.resource_uuid
                WHERE alias.person_uuid=ANY(@members) AND support.retired_work_uuid IS NULL
                    AND assignment.retired_work_uuid IS NULL
            ), works AS MATERIALIZED (
                SELECT DISTINCT introduced_work_uuid FROM active
            ), publications AS MATERIALIZED (
                SELECT works.introduced_work_uuid,stage.person_uuid,app.source_uuid,stage.receipt_header,
                    (stage.work_uuid IS NULL OR (
                        jsonb_typeof(stage.receipt_header)='object'
                        AND NOT stage.receipt_header ? 'CertificateVersion'
                        AND NOT stage.receipt_header ? 'AuthenticatedRequestHash'
                    )) AS legacy,
                    (stage.receipt IS NOT NULL AND stage.terminal_reason IS NULL
                        AND jsonb_typeof(stage.receipt_header)='object'
                        AND jsonb_typeof(stage.stage_header)='object'
                        AND jsonb_typeof(stage.receipt_header->'CertificateVersion')='string'
                        AND jsonb_typeof(stage.receipt_header->'AuthenticatedRequestHash')='string'
                        AND stage.receipt_header->>'CertificateVersion'=@certificateVersion
                        AND stage.receipt_header->>'AuthenticatedRequestHash'=run.request_hash
                        AND length(stage.receipt_header->>'AuthenticatedRequestHash')=64
                        AND (app.version=@applicationVersion
                            OR app.version=ANY(@historicalApplicationVersions))
                        AND run.state='published'
                        AND stage.admitted_work_uuid=stage.work_uuid AND run.uuid=stage.work_uuid
                        AND run.subject_uuid=stage.owner_subject_uuid AND owner.domain='people'
                        AND owner.subject_kind='person' AND owner.subject_uuid=stage.person_uuid
                        AND stage.ticket->>'Format'=@format AND stage.ticket->>'Kind'='ticket'
                        AND stage.ticket->'Value'=run.ticket
                        AND run.ticket->>'WorkUuid'=stage.work_uuid::text
                        AND run.ticket->>'SubjectUuid'=stage.owner_subject_uuid::text
                        AND run.ticket->>'PolicyVersion'=app.version
                        AND jsonb_typeof(stage.receipt_header->'Names')='array'
                    ) IS TRUE AS certified
                FROM works
                LEFT JOIN normalization.source_actor_alias_person_stages stage
                    ON stage.work_uuid=works.introduced_work_uuid
                LEFT JOIN normalization.source_actor_alias_applications app ON app.attempt_uuid=stage.application_uuid
                LEFT JOIN normalization.work_runs run ON run.uuid=stage.admitted_work_uuid
                LEFT JOIN normalization.work_subjects owner ON owner.uuid=run.subject_uuid
            ), names AS MATERIALIZED (
                SELECT DISTINCT publications.introduced_work_uuid,
                    name->>'SupportUuid' AS support_uuid,name->>'AssignmentUuid' AS assignment_uuid,
                    name->>'ActualAliasUuid' AS alias_uuid
                FROM publications
                CROSS JOIN LATERAL jsonb_array_elements(
                    CASE WHEN certified THEN receipt_header->'Names' ELSE '[]'::jsonb END
                ) name
                WHERE name->>'Disposition' IN (@accepted,@duplicate)
            ), classified AS MATERIALIZED (
                SELECT active.*,publications.legacy,
                    (publications.certified AND publications.person_uuid=active.person_uuid
                        AND publications.source_uuid=active.source_uuid
                        AND names.support_uuid IS NOT NULL) IS TRUE AS valid
                FROM active JOIN publications USING(introduced_work_uuid)
                LEFT JOIN names ON names.introduced_work_uuid=active.introduced_work_uuid
                    AND names.support_uuid=active.support_uuid::text
                    AND names.assignment_uuid=active.assignment_uuid::text
                    AND names.alias_uuid=active.alias_uuid::text
            ), legacy AS MATERIALIZED (
                SELECT support_uuid FROM classified WHERE legacy IS TRUE
            ), legacy_counts AS (
                SELECT count(*) FILTER(WHERE (
            """
        + LegacyAliasSupportProofPredicate
        + """
                ) IS NOT TRUE) AS invalid
                FROM legacy
                JOIN normalization.person_alias_contributor_support support ON support.uuid=legacy.support_uuid
                JOIN normalization.source_actor_alias_assignments assignment ON assignment.uuid=support.assignment_uuid
                JOIN normalization.input_resources resource ON resource.uuid=assignment.resource_uuid
            )
            SELECT (SELECT count(*) FROM classified) AS "Supports",
                (SELECT count(*) FROM classified WHERE legacy IS NOT TRUE AND NOT valid)
                    +(SELECT invalid FROM legacy_counts) AS "InvalidProofs"
            """;
}
