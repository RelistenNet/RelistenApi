using SimpleMigrations;

namespace Migrations;

/// <summary>Stores restartable ingestion inputs and receipts outside Temporal history.</summary>
[Migration(13, "Add ingestion artifacts and transactional receipts")]
public sealed class AddIngestionRecords : Migration
{
    protected override void Up() => Execute(@"
        CREATE TABLE ingestion_records (
            request_id text NOT NULL,
            record_key text NOT NULL,
            payload jsonb,
            created_at timestamptz NOT NULL DEFAULT now(),
            updated_at timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (request_id, record_key)
        );
        CREATE INDEX ingestion_records_updated_at ON ingestion_records(updated_at);");
    protected override void Down() => Execute("DROP TABLE ingestion_records;");
}
