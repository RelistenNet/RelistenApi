using System;
using SimpleMigrations;

namespace Migrations;

/// <summary>Retains music delivery after the recording extraction handoff has been acknowledged.</summary>
[Migration(61, "Retain resumable recording music delivery")]
public sealed class AddRecordingMusicDelivery : Migration
{
    public const string SchemaSql = """
        ALTER TABLE normalization.work_subjects DROP CONSTRAINT work_extraction_kind;
        ALTER TABLE normalization.work_subjects ADD CONSTRAINT work_extraction_kind
            CHECK(domain<>'extractions' OR subject_kind IN (
                'recording','setlist','recording_pipeline','recording_music_delivery'));

        CREATE TABLE normalization.recording_music_deliveries (
            handoff_uuid uuid PRIMARY KEY REFERENCES normalization.recording_lookup_handoffs(uuid),
            work_uuid uuid NOT NULL UNIQUE,
            source_uuid uuid NOT NULL,
            artist_uuid uuid NOT NULL,
            processing_version text NOT NULL,
            dictionary_attempt_uuid uuid NOT NULL UNIQUE,
            repertoire_attempt_uuid uuid NOT NULL UNIQUE,
            content_attempt_uuid uuid NOT NULL UNIQUE,
            phase integer NOT NULL DEFAULT 0 CHECK(phase BETWEEN 0 AND 7),
            dictionary_fetch_uuid uuid REFERENCES normalization.input_fetches(fetch_uuid),
            result jsonb CHECK(result IS NULL OR jsonb_typeof(result)='object'),
            created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            CHECK((phase=7)=(result IS NOT NULL))
        );
        CREATE INDEX recording_music_delivery_pending ON normalization.recording_music_deliveries(handoff_uuid)
            WHERE result IS NULL;
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException(
            "Stop music delivery on rollback; retain its original children and unfinished recovery state."
        );
}
