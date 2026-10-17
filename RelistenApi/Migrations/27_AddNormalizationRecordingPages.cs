using System;
using SimpleMigrations;

namespace Migrations;

/// <summary>Retains bounded recording selections and safe failure receipts independently of catalog lifetime.</summary>
[Migration(27, "Retain bounded recording normalization pages and failure receipts")]
public sealed class AddNormalizationRecordingPages : Migration
{
    public const string SchemaSql = """
        CREATE INDEX sources_artist_uuid ON public.sources(artist_id,uuid);

        CREATE TABLE normalization.recording_processing_pages (
            page_uuid uuid PRIMARY KEY,
            artist_uuid uuid,
            after_source_uuid uuid,
            selection_limit integer NOT NULL CHECK(selection_limit BETWEEN 1 AND 100),
            processing_version text NOT NULL CHECK(length(trim(processing_version))>0),
            selected_at timestamptz NOT NULL,
            has_more boolean NOT NULL
        );
        CREATE TABLE normalization.recording_processing_page_items (
            page_uuid uuid NOT NULL REFERENCES normalization.recording_processing_pages(page_uuid),
            position integer NOT NULL CHECK(position>=0 AND position<100),
            source_uuid uuid NOT NULL,
            attempt_uuid uuid NOT NULL UNIQUE,
            PRIMARY KEY(page_uuid,position),
            UNIQUE(page_uuid,source_uuid),
            UNIQUE(page_uuid,attempt_uuid)
        );
        CREATE TABLE normalization.recording_processing_page_failures (
            sequence bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            invocation_uuid uuid NOT NULL,
            page_uuid uuid NOT NULL,
            attempt_uuid uuid NOT NULL,
            code text NOT NULL CHECK(code IN ('PreparationConflict','DatabaseFailed','ProcessingTimeout','ProcessingFailed')),
            retryable boolean NOT NULL,
            occurred_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            UNIQUE(invocation_uuid,attempt_uuid),
            FOREIGN KEY(page_uuid,attempt_uuid)
                REFERENCES normalization.recording_processing_page_items(page_uuid,attempt_uuid)
        );
        CREATE INDEX recording_page_failure_sequence ON normalization.recording_processing_page_failures(page_uuid,sequence);

        CREATE TRIGGER recording_page_immutable BEFORE UPDATE OR DELETE ON normalization.recording_processing_pages
            FOR EACH ROW EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TRIGGER recording_page_items_immutable BEFORE UPDATE OR DELETE ON normalization.recording_processing_page_items
            FOR EACH ROW EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TRIGGER recording_page_failure_immutable BEFORE UPDATE OR DELETE ON normalization.recording_processing_page_failures
            FOR EACH ROW EXECUTE FUNCTION normalization.reject_input_history_mutation();
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException(
            "Stop recording page runners on rollback; retain their selections and failure receipts."
        );
}
