using System;
using SimpleMigrations;

namespace Migrations;

[Migration(47, "Distinguish immutable MusicBrainz lookup and search commands")]
public sealed class AddMusicBrainzSearchProfiles : Migration
{
    public const string SchemaSql = """
        ALTER TABLE normalization.musicbrainz_commands
            ADD COLUMN request_kind text NOT NULL DEFAULT 'lookup' CHECK(request_kind IN ('lookup','search'));
        CREATE FUNCTION normalization.protect_musicbrainz_request_kind() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF NEW.request_kind IS DISTINCT FROM OLD.request_kind THEN
                RAISE EXCEPTION 'MusicBrainz request kind is immutable';
            END IF;
            RETURN NEW;
        END
        $$;
        CREATE TRIGGER musicbrainz_request_kind_immutable BEFORE UPDATE ON normalization.musicbrainz_commands
            FOR EACH ROW EXECUTE FUNCTION normalization.protect_musicbrainz_request_kind();
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException(
            "Retain search command history and stop acquisition callers before binary rollback."
        );
}
