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
}
