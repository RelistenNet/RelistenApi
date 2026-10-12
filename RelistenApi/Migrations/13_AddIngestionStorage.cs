using SimpleMigrations;

namespace Migrations;

/// <summary>Bounded discovery snapshots and atomic retry receipts for the ingestion worker.</summary>
[Migration(13, "Add expiring ingestion snapshots and receipts")]
public sealed class AddIngestionStorage : Migration
{
    protected override void Up() =>
        Execute(
            """
            CREATE TABLE ingestion_snapshots (
                request_id text NOT NULL,
                name text NOT NULL,
                payload jsonb NOT NULL,
                expires_at timestamptz NOT NULL,
                PRIMARY KEY (request_id, name)
            );
            CREATE INDEX ingestion_snapshots_expiry ON ingestion_snapshots(expires_at);
            CREATE TABLE ingestion_receipts (
                request_id text NOT NULL,
                operation text NOT NULL,
                result jsonb,
                expires_at timestamptz NOT NULL,
                PRIMARY KEY (request_id, operation)
            );
            CREATE INDEX ingestion_receipts_expiry ON ingestion_receipts(expires_at);
            """
        );

    protected override void Down() =>
        Execute("DROP TABLE ingestion_receipts; DROP TABLE ingestion_snapshots;");
}
