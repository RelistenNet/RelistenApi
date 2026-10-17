using System;
using SimpleMigrations;

namespace Migrations;

/// <summary>
/// Replacement publication owns only the claims it introduced. Append publication remains
/// independent support; retiring a provider contribution cannot erase that baseline.
/// </summary>
[Migration(37, "Retain contributor support for reconciled recording credits")]
public sealed class AddRecordingCreditContributorSupport : Migration
{
    public const string SchemaSql = """
        CREATE INDEX recording_credit_history_source ON normalization.recording_credit_history(source_uuid,uuid,revision);
        CREATE TABLE normalization.recording_credit_managed (
            credit_uuid uuid PRIMARY KEY REFERENCES normalization.recording_credits(uuid),
            introduced_work_uuid uuid NOT NULL REFERENCES normalization.work_runs(uuid)
        );
        CREATE TABLE normalization.recording_credit_unmanaged_support (
            credit_uuid uuid PRIMARY KEY REFERENCES normalization.recording_credits(uuid)
        );
        CREATE TABLE normalization.recording_credit_contributor_support (
            uuid uuid PRIMARY KEY DEFAULT gen_random_uuid(),
            credit_uuid uuid NOT NULL REFERENCES normalization.recording_credits(uuid),
            contributor_provider text NOT NULL,contributor_type text NOT NULL,contributor_key text NOT NULL,
            input_revision_uuid uuid NOT NULL REFERENCES normalization.input_revisions(uuid),
            json_pointer text NOT NULL,start_code_point integer,end_code_point integer,quote text,
            introduced_work_uuid uuid NOT NULL REFERENCES normalization.work_runs(uuid),
            retired_work_uuid uuid REFERENCES normalization.work_runs(uuid),
            CHECK((start_code_point IS NULL)=(end_code_point IS NULL)),
            CHECK(start_code_point IS NULL OR (start_code_point>=0 AND end_code_point>=start_code_point))
        );
        CREATE UNIQUE INDEX recording_credit_current_contributor_citation ON normalization.recording_credit_contributor_support
            (credit_uuid,contributor_provider,contributor_type,contributor_key,input_revision_uuid,json_pointer,start_code_point,end_code_point,quote)
            NULLS NOT DISTINCT WHERE retired_work_uuid IS NULL;
        CREATE INDEX recording_credit_contributor_lookup ON normalization.recording_credit_contributor_support
            (contributor_provider,contributor_type,contributor_key,credit_uuid) WHERE retired_work_uuid IS NULL;

        CREATE FUNCTION normalization.retain_credit_support() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP='DELETE' OR TG_TABLE_NAME<>'recording_credit_contributor_support' THEN
                RAISE EXCEPTION 'Recording credit support is retained';
            END IF;
            IF OLD.retired_work_uuid IS NOT NULL OR NEW.retired_work_uuid IS NULL
                OR (to_jsonb(OLD)-'retired_work_uuid') IS DISTINCT FROM (to_jsonb(NEW)-'retired_work_uuid') THEN
                RAISE EXCEPTION 'Only current contributor support can be retired';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER recording_credit_managed_retained BEFORE UPDATE OR DELETE ON normalization.recording_credit_managed
            FOR EACH ROW EXECUTE FUNCTION normalization.retain_credit_support();
        CREATE TRIGGER recording_credit_unmanaged_retained BEFORE UPDATE OR DELETE ON normalization.recording_credit_unmanaged_support
            FOR EACH ROW EXECUTE FUNCTION normalization.retain_credit_support();
        CREATE TRIGGER recording_credit_support_retained BEFORE UPDATE OR DELETE ON normalization.recording_credit_contributor_support
            FOR EACH ROW EXECUTE FUNCTION normalization.retain_credit_support();
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException("Contributor credit support is retained evidence.");
}
