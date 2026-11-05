using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RelistenUserService.Persistence.Migrations;

/// <summary>
/// Coordinated private-alpha cutover: preserve identity and memberships, retire credentials.
/// Requires account writes stopped. Restore the whole pre-cutover backup to return to old binaries.
/// </summary>
public partial class StoreTypedAccountIds : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_users_id_uuid_v7",
            schema: "identity",
            table: "users"
        );

        migrationBuilder.DropCheckConstraint(
            name: "ck_username_command_receipts_id_uuid_v7",
            schema: "identity",
            table: "username_command_receipts"
        );

        migrationBuilder.DropCheckConstraint(
            name: "ck_sessions_id_uuid_v7",
            schema: "identity",
            table: "sessions"
        );

        migrationBuilder.DropCheckConstraint(
            name: "ck_native_sessions_id_uuid_v7",
            schema: "identity",
            table: "native_sessions"
        );

        migrationBuilder.DropCheckConstraint(
            name: "ck_library_changes_catalog_type",
            schema: "user_data",
            table: "library_changes"
        );

        migrationBuilder.DropCheckConstraint(
            name: "ck_library_changes_favorite_id_uuid_v7",
            schema: "user_data",
            table: "library_changes"
        );

        migrationBuilder.DropCheckConstraint(
            name: "ck_library_changes_id_uuid_v7",
            schema: "user_data",
            table: "library_changes"
        );

        migrationBuilder.DropCheckConstraint(
            name: "ck_favorites_catalog_type",
            schema: "user_data",
            table: "favorites"
        );

        migrationBuilder.DropCheckConstraint(
            name: "ck_favorites_id_uuid_v7",
            schema: "user_data",
            table: "favorites"
        );

        migrationBuilder.DropCheckConstraint(
            name: "ck_favorite_mutation_receipts_canonical_favorite_id",
            schema: "user_data",
            table: "favorite_mutation_receipts"
        );

        migrationBuilder.DropCheckConstraint(
            name: "ck_favorite_mutation_receipts_catalog_type",
            schema: "user_data",
            table: "favorite_mutation_receipts"
        );

        migrationBuilder.DropCheckConstraint(
            name: "ck_favorite_mutation_receipts_id_uuid_v7",
            schema: "user_data",
            table: "favorite_mutation_receipts"
        );

        migrationBuilder.DropCheckConstraint(
            name: "ck_favorite_mutation_receipts_submitted_favorite_id",
            schema: "user_data",
            table: "favorite_mutation_receipts"
        );

        migrationBuilder.Sql(
            """
-- DDL and data conversion run in the EF migration transaction, with old writers stopped.
-- Save exact FK definitions, including the composite same-user SSO relationship.
CREATE TEMP TABLE account_id_foreign_keys ON COMMIT DROP AS
SELECT conrelid::regclass AS relation, conname AS name, pg_get_constraintdef(oid) AS definition
FROM pg_constraint
WHERE contype = 'f' AND connamespace IN ('identity'::regnamespace, 'user_data'::regnamespace);
DO $$ DECLARE fk record; BEGIN
    FOR fk IN SELECT * FROM account_id_foreign_keys LOOP
        EXECUTE format('ALTER TABLE %s DROP CONSTRAINT %I', fk.relation, fk.name);
    END LOOP;
END $$;

-- Migration-local lossless UUID encoder. No persistent SQL codec or UUID storage fallback.
CREATE FUNCTION pg_temp.account_type_id(prefix text, value uuid) RETURNS text
LANGUAGE sql IMMUTABLE STRICT AS $$
    SELECT prefix || '_' || string_agg(
        substr('0123456789abcdefghjkmnpqrstvwxyz',
            substring('00' || ('x' || replace(value::text, '-', ''))::bit(128)::text
                FROM n * 5 + 1 FOR 5)::bit(5)::integer + 1, 1), '' ORDER BY n)
    FROM generate_series(0, 25) AS n
$$;

-- All pre-cutover first-party sessions require re-sign-in. Signed/encrypted payloads
-- remain opaque; revoked OpenIddict entries retain their original keys and audit data.
UPDATE identity.native_sessions SET revoked_at = now(), updated_at = now() WHERE revoked_at IS NULL;
UPDATE identity.sessions SET revoked_at = now(), updated_at = now() WHERE revoked_at IS NULL;
UPDATE identity.openiddict_tokens SET status = 'revoked'
WHERE subject IN (SELECT id::text FROM identity.users)
   OR authorization_id IN (
       SELECT id FROM identity.openiddict_authorizations
       WHERE subject IN (SELECT id::text FROM identity.users));
UPDATE identity.openiddict_authorizations SET status = 'revoked'
WHERE subject IN (SELECT id::text FROM identity.users);
UPDATE identity.openiddict_authorizations AS a
SET subject = pg_temp.account_type_id('user', u.id)
FROM identity.users AS u WHERE a.subject = u.id::text;
UPDATE identity.openiddict_tokens AS t
SET subject = pg_temp.account_type_id('user', u.id)
FROM identity.users AS u WHERE t.subject = u.id::text;

ALTER TABLE identity.users
    ALTER COLUMN id TYPE text USING pg_temp.account_type_id('user', id);

ALTER TABLE identity.external_identities
    ALTER COLUMN user_id TYPE text USING pg_temp.account_type_id('user', user_id);

ALTER TABLE identity.native_sessions
    ALTER COLUMN id TYPE text USING pg_temp.account_type_id('session', id),
    ALTER COLUMN user_id TYPE text USING pg_temp.account_type_id('user', user_id);

ALTER TABLE identity.sessions
    ALTER COLUMN id TYPE text USING pg_temp.account_type_id('session', id),
    ALTER COLUMN user_id TYPE text USING pg_temp.account_type_id('user', user_id),
    ALTER COLUMN auth_sso_session_id TYPE text USING pg_temp.account_type_id('session', auth_sso_session_id);

ALTER TABLE identity.username_holds
    ALTER COLUMN user_id TYPE text USING pg_temp.account_type_id('user', user_id);

ALTER TABLE identity.username_command_receipts
    ALTER COLUMN id TYPE text USING pg_temp.account_type_id('command', id),
    ALTER COLUMN user_id TYPE text USING pg_temp.account_type_id('user', user_id);

ALTER TABLE user_data.favorites
    ALTER COLUMN id TYPE text USING pg_temp.account_type_id('favorite', id),
    ALTER COLUMN user_id TYPE text USING pg_temp.account_type_id('user', user_id);

ALTER TABLE user_data.favorite_mutation_receipts
    ALTER COLUMN id TYPE text USING pg_temp.account_type_id('mutation', id),
    ALTER COLUMN user_id TYPE text USING pg_temp.account_type_id('user', user_id),
    ALTER COLUMN submitted_favorite_id TYPE text USING pg_temp.account_type_id('favorite', submitted_favorite_id),
    ALTER COLUMN canonical_favorite_id TYPE text USING pg_temp.account_type_id('favorite', canonical_favorite_id);

ALTER TABLE user_data.library_changes
    ALTER COLUMN id TYPE text USING pg_temp.account_type_id('library_change', id),
    ALTER COLUMN user_id TYPE text USING pg_temp.account_type_id('user', user_id),
    ALTER COLUMN favorite_id TYPE text USING pg_temp.account_type_id('favorite', favorite_id);

ALTER TABLE user_data.library_states
    ALTER COLUMN user_id TYPE text USING pg_temp.account_type_id('user', user_id);

ALTER TABLE user_data.favorites RENAME COLUMN catalog_uuid TO catalog_id;
ALTER TABLE user_data.favorites ALTER COLUMN catalog_id TYPE text
    USING pg_temp.account_type_id(CASE catalog_type
        WHEN 'source_track' THEN 'track' WHEN 'song' THEN 'imported_song'
        WHEN 'venue' THEN 'imported_venue' ELSE catalog_type END, catalog_id);
UPDATE user_data.favorites SET catalog_type = CASE catalog_type
    WHEN 'source_track' THEN 'track' WHEN 'song' THEN 'imported_song'
    WHEN 'venue' THEN 'imported_venue' ELSE catalog_type END;

ALTER TABLE user_data.favorite_mutation_receipts RENAME COLUMN catalog_uuid TO catalog_id;
ALTER TABLE user_data.favorite_mutation_receipts ALTER COLUMN catalog_id TYPE text
    USING pg_temp.account_type_id(CASE catalog_type
        WHEN 'source_track' THEN 'track' WHEN 'song' THEN 'imported_song'
        WHEN 'venue' THEN 'imported_venue' ELSE catalog_type END, catalog_id);
UPDATE user_data.favorite_mutation_receipts SET catalog_type = CASE catalog_type
    WHEN 'source_track' THEN 'track' WHEN 'song' THEN 'imported_song'
    WHEN 'venue' THEN 'imported_venue' ELSE catalog_type END;

ALTER TABLE user_data.library_changes RENAME COLUMN catalog_uuid TO catalog_id;
ALTER TABLE user_data.library_changes ALTER COLUMN catalog_id TYPE text
    USING pg_temp.account_type_id(CASE catalog_type
        WHEN 'source_track' THEN 'track' WHEN 'song' THEN 'imported_song'
        WHEN 'venue' THEN 'imported_venue' ELSE catalog_type END, catalog_id);
UPDATE user_data.library_changes SET catalog_type = CASE catalog_type
    WHEN 'source_track' THEN 'track' WHEN 'song' THEN 'imported_song'
    WHEN 'venue' THEN 'imported_venue' ELSE catalog_type END;

ALTER INDEX user_data."IX_favorites_user_id_catalog_type_catalog_uuid"
    RENAME TO "IX_favorites_user_id_catalog_type_catalog_id";

DO $$ DECLARE fk record; BEGIN
    FOR fk IN SELECT * FROM account_id_foreign_keys LOOP
        EXECUTE format('ALTER TABLE %s ADD CONSTRAINT %I %s', fk.relation, fk.name, fk.definition);
    END LOOP;
END $$;
DROP FUNCTION pg_temp.account_type_id(text, uuid);

"""
        );

        migrationBuilder.AddCheckConstraint(
            name: "ck_users_id_typed_id",
            schema: "identity",
            table: "users",
            sql: "id ~ '^user_[0-7][0-9a-hjkmnp-tv-z]{9}[ef][0-9a-hjkmnp-tv-z]{15}$'"
        );

        migrationBuilder.AddCheckConstraint(
            name: "ck_username_command_receipts_id_typed_id",
            schema: "identity",
            table: "username_command_receipts",
            sql: "id ~ '^command_[0-7][0-9a-hjkmnp-tv-z]{9}[ef][0-9a-hjkmnp-tv-z]{15}$'"
        );

        migrationBuilder.AddCheckConstraint(
            name: "ck_sessions_id_typed_id",
            schema: "identity",
            table: "sessions",
            sql: "id ~ '^session_[0-7][0-9a-hjkmnp-tv-z]{9}[ef][0-9a-hjkmnp-tv-z]{15}$'"
        );

        migrationBuilder.AddCheckConstraint(
            name: "ck_native_sessions_id_typed_id",
            schema: "identity",
            table: "native_sessions",
            sql: "id ~ '^session_[0-7][0-9a-hjkmnp-tv-z]{9}[ef][0-9a-hjkmnp-tv-z]{15}$'"
        );

        migrationBuilder.AddCheckConstraint(
            name: "ck_library_changes_catalog_type",
            schema: "user_data",
            table: "library_changes",
            sql: "catalog_type IN ('artist', 'show', 'source', 'track', 'imported_song', 'imported_venue', 'song', 'tour', 'venue')"
        );

        migrationBuilder.AddCheckConstraint(
            name: "ck_library_changes_favorite_id_typed_id",
            schema: "user_data",
            table: "library_changes",
            sql: "favorite_id ~ '^favorite_[0-7][0-9a-hjkmnp-tv-z]{9}[ef][0-9a-hjkmnp-tv-z]{15}$'"
        );

        migrationBuilder.AddCheckConstraint(
            name: "ck_library_changes_id_typed_id",
            schema: "user_data",
            table: "library_changes",
            sql: "id ~ '^library_change_[0-7][0-9a-hjkmnp-tv-z]{9}[ef][0-9a-hjkmnp-tv-z]{15}$'"
        );

        migrationBuilder.AddCheckConstraint(
            name: "ck_favorites_catalog_type",
            schema: "user_data",
            table: "favorites",
            sql: "catalog_type IN ('artist', 'show', 'source', 'track', 'imported_song', 'imported_venue', 'song', 'tour', 'venue')"
        );

        migrationBuilder.AddCheckConstraint(
            name: "ck_favorites_id_typed_id",
            schema: "user_data",
            table: "favorites",
            sql: "id ~ '^favorite_[0-7][0-9a-hjkmnp-tv-z]{9}[ef][0-9a-hjkmnp-tv-z]{15}$'"
        );

        migrationBuilder.AddCheckConstraint(
            name: "ck_favorite_mutation_receipts_canonical_favorite_id",
            schema: "user_data",
            table: "favorite_mutation_receipts",
            sql: "canonical_favorite_id IS NULL OR canonical_favorite_id ~ '^favorite_[0-7][0-9a-hjkmnp-tv-z]{9}[ef][0-9a-hjkmnp-tv-z]{15}$'"
        );

        migrationBuilder.AddCheckConstraint(
            name: "ck_favorite_mutation_receipts_catalog_type",
            schema: "user_data",
            table: "favorite_mutation_receipts",
            sql: "catalog_type IN ('artist', 'show', 'source', 'track', 'imported_song', 'imported_venue', 'song', 'tour', 'venue')"
        );

        migrationBuilder.AddCheckConstraint(
            name: "ck_favorite_mutation_receipts_id_typed_id",
            schema: "user_data",
            table: "favorite_mutation_receipts",
            sql: "id ~ '^mutation_[0-7][0-9a-hjkmnp-tv-z]{9}[ef][0-9a-hjkmnp-tv-z]{15}$'"
        );

        migrationBuilder.AddCheckConstraint(
            name: "ck_favorite_mutation_receipts_submitted_favorite_id",
            schema: "user_data",
            table: "favorite_mutation_receipts",
            sql: "submitted_favorite_id IS NULL OR submitted_favorite_id ~ '^favorite_[0-7][0-9a-hjkmnp-tv-z]{9}[ef][0-9a-hjkmnp-tv-z]{15}$'"
        );
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException(
            "Restore the pre-cutover account backup; credential revocation cannot be undone by a schema downgrade."
        );
}
