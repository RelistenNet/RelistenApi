using System;
using SimpleMigrations;

namespace Migrations;

/// <summary>
/// Retained provider bytes have no catalog snapshot fetch receipt. Preserve the original
/// attempt format while binding new delivery consumers to their immutable initiating handoff.
/// </summary>
[Migration(35, "Retain complete multi-input recording preparations")]
public sealed class AddNormalizationRecordingInputGroups : Migration
{
    public const string SchemaSql = """
        ALTER TABLE normalization.recording_processing_attempts
            ADD COLUMN initiating_handoff_uuid uuid REFERENCES normalization.recording_lookup_handoffs(uuid);
        -- These are migration 26's two generated check names, verified against PostgreSQL.
        -- The remaining checkpoint/publication/terminal constraints continue unchanged.
        ALTER TABLE normalization.recording_processing_attempts
            DROP CONSTRAINT recording_processing_attempts_check,
            DROP CONSTRAINT recording_processing_attempts_check1;
        ALTER TABLE normalization.recording_processing_attempts
            ADD CONSTRAINT recording_preparation_fetch_presence
                CHECK(preparation IS NOT NULL OR preparation_fetch_uuid IS NULL),
            ADD CONSTRAINT recording_preparation_fetch_reference
                CHECK(preparation IS NULL OR (jsonb_typeof(preparation)='object'
                    AND (preparation->>'FetchUuid')::uuid IS NOT DISTINCT FROM preparation_fetch_uuid));

        CREATE OR REPLACE FUNCTION normalization.retain_recording_processing_attempt() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP='DELETE' OR OLD.disposition IS NOT NULL THEN
                RAISE EXCEPTION 'Recording processing attempts and terminal outcomes are retained';
            END IF;
            IF (OLD.attempt_uuid,OLD.source_uuid,OLD.processing_version,OLD.initiating_handoff_uuid,OLD.created_at)
                    IS DISTINCT FROM (NEW.attempt_uuid,NEW.source_uuid,NEW.processing_version,NEW.initiating_handoff_uuid,NEW.created_at) THEN
                RAISE EXCEPTION 'Recording processing attempt requests are immutable';
            END IF;
            IF OLD.preparation IS NOT NULL AND (OLD.preparation,OLD.preparation_fetch_uuid)
                    IS DISTINCT FROM (NEW.preparation,NEW.preparation_fetch_uuid) THEN
                RAISE EXCEPTION 'Recording processing preparation is immutable';
            END IF;
            IF OLD.batch IS NOT NULL AND (OLD.batch,OLD.extraction_work_uuid)
                    IS DISTINCT FROM (NEW.batch,NEW.extraction_work_uuid) THEN
                RAISE EXCEPTION 'Completed recording extraction is immutable';
            END IF;
            RETURN NEW;
        END $$;
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException(
            "Stop recording processors and delivery workers on rollback; retain multi-input attempts and initiating handoffs."
        );
}
