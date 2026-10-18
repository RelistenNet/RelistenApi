using System;
using SimpleMigrations;

namespace Migrations;

[Migration(43, "Retain bounded MusicBrainz acquisition and provider admission")]
public sealed class AddMusicBrainzAcquisition : Migration
{
    public const string SchemaSql = """
        CREATE TABLE normalization.musicbrainz_commands (
            command_uuid uuid PRIMARY KEY,
            request jsonb NOT NULL,
            profile jsonb NOT NULL,
            acquisition_version text NOT NULL,
            state text NOT NULL DEFAULT 'Pending' CHECK(state IN ('Pending','Completed','Failed')),
            not_before timestamptz,
            result jsonb,
            created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            CHECK((state='Pending')=(result IS NULL))
        );
        CREATE TABLE normalization.musicbrainz_attempts (
            attempt_uuid uuid PRIMARY KEY,
            command_uuid uuid NOT NULL REFERENCES normalization.musicbrainz_commands(command_uuid),
            ordinal integer NOT NULL CHECK(ordinal BETWEEN 1 AND 3),
            preparation jsonb NOT NULL,
            phase text NOT NULL CHECK(phase IN ('DispatchStarted','ResponseSaved','Uncertain','Retryable','Failed','Captured')),
            reason text,
            capture_fetch_uuid uuid REFERENCES normalization.input_fetches(fetch_uuid),
            UNIQUE(command_uuid,ordinal)
        );
        CREATE TABLE normalization.musicbrainz_responses (
            attempt_uuid uuid PRIMARY KEY REFERENCES normalization.musicbrainz_attempts(attempt_uuid),
            response jsonb NOT NULL,
            body bytea,
            sha256 text CHECK(sha256 ~ '^[0-9a-f]{64}$'),
            eligible boolean NOT NULL,
            saved_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            CHECK((body IS NULL)=(sha256 IS NULL)),
            CHECK(body IS NULL OR octet_length(body)<=8388608)
        );
        CREATE TABLE normalization.musicbrainz_gate (
            singleton boolean PRIMARY KEY DEFAULT true CHECK(singleton),
            attempt_uuid uuid,
            token uuid,
            lease_until timestamptz,
            not_before timestamptz NOT NULL DEFAULT '-infinity',
            CHECK((attempt_uuid IS NULL)=(token IS NULL)),
            CHECK((token IS NULL)=(lease_until IS NULL))
        );
        INSERT INTO normalization.musicbrainz_gate(singleton) VALUES(true);

        CREATE FUNCTION normalization.protect_musicbrainz_history() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP <> 'UPDATE' THEN
                RAISE EXCEPTION 'MusicBrainz acquisition history is retained';
            END IF;
            IF TG_TABLE_NAME='musicbrainz_commands' THEN
                IF NEW.command_uuid<>OLD.command_uuid OR NEW.request<>OLD.request OR NEW.profile<>OLD.profile
                    OR NEW.acquisition_version<>OLD.acquisition_version OR NEW.created_at<>OLD.created_at
                    OR (OLD.result IS NOT NULL AND NEW IS DISTINCT FROM OLD) THEN
                    RAISE EXCEPTION 'MusicBrainz command facts and terminal result are immutable';
                END IF;
            ELSE
                IF NEW.attempt_uuid<>OLD.attempt_uuid OR NEW.command_uuid<>OLD.command_uuid
                    OR NEW.ordinal<>OLD.ordinal OR NEW.preparation<>OLD.preparation
                    OR (OLD.capture_fetch_uuid IS NOT NULL AND NEW.capture_fetch_uuid IS DISTINCT FROM OLD.capture_fetch_uuid) THEN
                    RAISE EXCEPTION 'MusicBrainz attempt preparation and capture link are immutable';
                END IF;
            END IF;
            RETURN NEW;
        END
        $$;
        CREATE TRIGGER musicbrainz_commands_retained BEFORE UPDATE OR DELETE ON normalization.musicbrainz_commands
            FOR EACH ROW EXECUTE FUNCTION normalization.protect_musicbrainz_history();
        CREATE TRIGGER musicbrainz_attempts_retained BEFORE UPDATE OR DELETE ON normalization.musicbrainz_attempts
            FOR EACH ROW EXECUTE FUNCTION normalization.protect_musicbrainz_history();
        CREATE TRIGGER musicbrainz_commands_no_truncate BEFORE TRUNCATE ON normalization.musicbrainz_commands
            FOR EACH STATEMENT EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TRIGGER musicbrainz_attempts_no_truncate BEFORE TRUNCATE ON normalization.musicbrainz_attempts
            FOR EACH STATEMENT EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TRIGGER musicbrainz_responses_immutable BEFORE UPDATE OR DELETE ON normalization.musicbrainz_responses
            FOR EACH ROW EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TRIGGER musicbrainz_responses_no_truncate BEFORE TRUNCATE ON normalization.musicbrainz_responses
            FOR EACH STATEMENT EXECUTE FUNCTION normalization.reject_input_history_mutation();
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException(
            "MusicBrainz acquisition receipts survive rollback; stop its writers instead."
        );
}
