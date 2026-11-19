using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace RelistenUserService.Persistence.Migrations;

[DbContext(typeof(AccountsDbContext))]
[Migration("20261119050000_AddCloudPlayHistory")]
public sealed class AddCloudPlayHistory : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            CREATE EXTENSION IF NOT EXISTS timescaledb;

            CREATE TABLE user_data.plays (
                user_id text NOT NULL REFERENCES identity.users(id) ON DELETE CASCADE,
                qualified_at timestamptz NOT NULL,
                play_id text NOT NULL,
                started_at timestamptz NOT NULL,
                received_at timestamptz NOT NULL,
                track_id text NOT NULL,
                playlist_id text,
                playlist_entry_id text,
                catalog_duration_ms integer,
                app_type smallint NOT NULL,
                offline boolean NOT NULL,
                origin smallint NOT NULL,
                PRIMARY KEY (user_id, qualified_at, play_id),
                CONSTRAINT ck_plays_origin CHECK (origin IN (1, 2))
            );

            SELECT public.create_hypertable(
                'user_data.plays',
                public.by_range('qualified_at', INTERVAL '90 days'),
                create_default_indexes => false
            );

            ALTER TABLE user_data.plays SET (
                timescaledb.enable_columnstore,
                timescaledb.segmentby = 'user_id',
                timescaledb.orderby = 'qualified_at DESC'
            );
            """
        );

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Preserve account history. Disable uploads to roll back.");
}
