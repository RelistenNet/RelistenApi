namespace Relisten.UserApi.Migrations;

public static class UserDataSchemaSql
{
    public const string SchemaName = "user_data";
    public const string MigrationTableName = "user_service_migrations";

    // Schema-qualify every bootstrap object so user-data migrations never depend on a default
    // search_path that might point at the catalog schema.
    public const string Bootstrap = """
        CREATE SCHEMA IF NOT EXISTS user_data;

        CREATE TABLE IF NOT EXISTS user_data.user_service_migrations (
            version BIGINT PRIMARY KEY,
            description TEXT NOT NULL,
            applied_at TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        """;

    public const string AuthTables = """
        CREATE TABLE IF NOT EXISTS user_data.users (
            id UUID PRIMARY KEY,
            username TEXT NOT NULL,
            username_lower TEXT NOT NULL UNIQUE,
            display_name TEXT NOT NULL,
            created_at TIMESTAMPTZ NOT NULL,
            updated_at TIMESTAMPTZ NOT NULL
        );

        CREATE TABLE IF NOT EXISTS user_data.user_auth_methods (
            id UUID PRIMARY KEY,
            user_id UUID NOT NULL REFERENCES user_data.users(id) ON DELETE CASCADE,
            provider TEXT NOT NULL,
            provider_subject TEXT NOT NULL,
            provider_claims JSONB,
            created_at TIMESTAMPTZ NOT NULL,
            UNIQUE (provider, provider_subject)
        );

        CREATE TABLE IF NOT EXISTS user_data.user_sessions (
            id UUID PRIMARY KEY,
            user_id UUID NOT NULL REFERENCES user_data.users(id) ON DELETE CASCADE,
            device_id TEXT NOT NULL,
            device_name TEXT,
            platform TEXT NOT NULL,
            last_used_at TIMESTAMPTZ NOT NULL,
            created_at TIMESTAMPTZ NOT NULL,
            revoked_at TIMESTAMPTZ
        );

        CREATE TABLE IF NOT EXISTS user_data.refresh_tokens (
            id UUID PRIMARY KEY,
            session_id UUID NOT NULL REFERENCES user_data.user_sessions(id) ON DELETE CASCADE,
            token_selector TEXT NOT NULL UNIQUE,
            token_secret_hash TEXT NOT NULL,
            status TEXT NOT NULL CHECK (status IN ('active', 'rotated', 'revoked', 'reuse_detected')),
            issued_at TIMESTAMPTZ NOT NULL,
            expires_at TIMESTAMPTZ NOT NULL,
            rotated_at TIMESTAMPTZ,
            replaced_by_token_id UUID REFERENCES user_data.refresh_tokens(id),
            reuse_detected_at TIMESTAMPTZ
        );

        CREATE INDEX IF NOT EXISTS idx_user_sessions_user_id
            ON user_data.user_sessions(user_id);
        CREATE INDEX IF NOT EXISTS idx_refresh_tokens_session_id
            ON user_data.refresh_tokens(session_id);
        """;

    public const string PlaylistTables = """
        CREATE TABLE IF NOT EXISTS user_data.playlists (
            id UUID PRIMARY KEY,
            short_id TEXT NOT NULL UNIQUE,
            owner_id UUID NOT NULL REFERENCES user_data.users(id) ON DELETE CASCADE,
            name TEXT NOT NULL,
            description TEXT,
            visibility TEXT NOT NULL DEFAULT 'private'
                CHECK (visibility IN ('private', 'unlisted', 'public')),
            current_revision BIGINT NOT NULL DEFAULT 0,
            moderation_status TEXT NOT NULL DEFAULT 'approved'
                CHECK (moderation_status IN ('approved', 'pending_review', 'hidden')),
            archived_at TIMESTAMPTZ,
            created_at TIMESTAMPTZ NOT NULL,
            updated_at TIMESTAMPTZ NOT NULL
        );

        CREATE TABLE IF NOT EXISTS user_data.playlist_blocks (
            id UUID PRIMARY KEY,
            playlist_id UUID NOT NULL REFERENCES user_data.playlists(id) ON DELETE CASCADE,
            created_by UUID NOT NULL REFERENCES user_data.users(id),
            created_at TIMESTAMPTZ NOT NULL
        );

        CREATE TABLE IF NOT EXISTS user_data.playlist_entries (
            id UUID PRIMARY KEY,
            playlist_id UUID NOT NULL REFERENCES user_data.playlists(id) ON DELETE CASCADE,
            source_track_uuid UUID NOT NULL,
            block_uuid UUID,
            position TEXT NOT NULL,
            block_position INT,
            added_by UUID NOT NULL REFERENCES user_data.users(id),
            created_at TIMESTAMPTZ NOT NULL,
            updated_at TIMESTAMPTZ NOT NULL,
            CONSTRAINT chk_playlist_entries_block_position
                CHECK (
                    (block_uuid IS NULL AND block_position IS NULL)
                    OR
                    (block_uuid IS NOT NULL AND block_position IS NOT NULL AND block_position >= 0)
                )
        );

        CREATE TABLE IF NOT EXISTS user_data.playlist_edit_log (
            id UUID PRIMARY KEY,
            playlist_id UUID NOT NULL REFERENCES user_data.playlists(id) ON DELETE CASCADE,
            user_id UUID NOT NULL REFERENCES user_data.users(id),
            operation JSONB NOT NULL,
            idempotency_key UUID NOT NULL UNIQUE,
            base_revision BIGINT,
            result_revision BIGINT NOT NULL,
            result_status TEXT NOT NULL,
            created_at TIMESTAMPTZ NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_playlists_owner_id
            ON user_data.playlists(owner_id)
            WHERE archived_at IS NULL;
        CREATE INDEX IF NOT EXISTS idx_playlist_entries_playlist_id
            ON user_data.playlist_entries(playlist_id);
        CREATE INDEX IF NOT EXISTS idx_playlist_entries_block_uuid
            ON user_data.playlist_entries(block_uuid)
            WHERE block_uuid IS NOT NULL;
        CREATE INDEX IF NOT EXISTS idx_playlist_blocks_playlist_id
            ON user_data.playlist_blocks(playlist_id);
        CREATE UNIQUE INDEX IF NOT EXISTS idx_playlist_entries_playlist_position
            ON user_data.playlist_entries(playlist_id, position);
        CREATE UNIQUE INDEX IF NOT EXISTS idx_playlist_entries_block_position
            ON user_data.playlist_entries(playlist_id, block_uuid, block_position)
            WHERE block_uuid IS NOT NULL;
        CREATE INDEX IF NOT EXISTS idx_playlist_edit_log_playlist_id
            ON user_data.playlist_edit_log(playlist_id);
        """;

    public const string PlaylistSharingTables = """
        CREATE TABLE IF NOT EXISTS user_data.playlist_share_tokens (
            id UUID PRIMARY KEY,
            playlist_id UUID NOT NULL REFERENCES user_data.playlists(id) ON DELETE CASCADE,
            created_by UUID NOT NULL REFERENCES user_data.users(id),
            role TEXT NOT NULL CHECK (role IN ('viewer', 'editor')),
            token_hash TEXT NOT NULL UNIQUE,
            expires_at TIMESTAMPTZ,
            revoked_at TIMESTAMPTZ,
            created_at TIMESTAMPTZ NOT NULL
        );

        CREATE TABLE IF NOT EXISTS user_data.playlist_mobile_access_grants (
            id UUID PRIMARY KEY,
            playlist_id UUID NOT NULL REFERENCES user_data.playlists(id) ON DELETE CASCADE,
            source_share_token_id UUID REFERENCES user_data.playlist_share_tokens(id) ON DELETE SET NULL,
            device_id TEXT NOT NULL,
            platform TEXT NOT NULL,
            role TEXT NOT NULL CHECK (role IN ('viewer', 'editor')),
            token_selector TEXT NOT NULL UNIQUE,
            token_secret_hash TEXT NOT NULL,
            issued_at TIMESTAMPTZ NOT NULL,
            expires_at TIMESTAMPTZ NOT NULL,
            revoked_at TIMESTAMPTZ
        );

        CREATE TABLE IF NOT EXISTS user_data.playlist_collaborators (
            id UUID PRIMARY KEY,
            playlist_id UUID NOT NULL REFERENCES user_data.playlists(id) ON DELETE CASCADE,
            user_id UUID NOT NULL REFERENCES user_data.users(id) ON DELETE CASCADE,
            role TEXT NOT NULL CHECK (role IN ('editor')),
            invited_by UUID REFERENCES user_data.users(id),
            invited_at TIMESTAMPTZ NOT NULL,
            accepted_at TIMESTAMPTZ,
            revoked_at TIMESTAMPTZ,
            UNIQUE (playlist_id, user_id)
        );

        CREATE TABLE IF NOT EXISTS user_data.playlist_followers (
            playlist_id UUID NOT NULL REFERENCES user_data.playlists(id) ON DELETE CASCADE,
            user_id UUID NOT NULL REFERENCES user_data.users(id) ON DELETE CASCADE,
            followed_at TIMESTAMPTZ NOT NULL,
            unfollowed_at TIMESTAMPTZ,
            PRIMARY KEY (playlist_id, user_id)
        );

        CREATE INDEX IF NOT EXISTS idx_playlist_share_tokens_playlist_id
            ON user_data.playlist_share_tokens(playlist_id);
        CREATE INDEX IF NOT EXISTS idx_playlist_mobile_access_grants_playlist_id
            ON user_data.playlist_mobile_access_grants(playlist_id);
        CREATE INDEX IF NOT EXISTS idx_playlist_collaborators_user_id
            ON user_data.playlist_collaborators(user_id)
            WHERE revoked_at IS NULL;
        CREATE INDEX IF NOT EXISTS idx_playlist_followers_user_id
            ON user_data.playlist_followers(user_id)
            WHERE unfollowed_at IS NULL;
        """;
}
