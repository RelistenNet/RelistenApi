using SimpleMigrations;

namespace Migrations;

[Migration(9, "Add max_created_at to show_source_information")]
public class AddMaxCreatedAtToShowSourceInfo : Migration
{
    protected override void Up()
    {
        Execute(@"
            ALTER TABLE show_source_information ADD COLUMN max_created_at timestamp with time zone;

            UPDATE show_source_information ssi
            SET max_created_at = (
                SELECT MAX(created_at) FROM sources WHERE show_id = ssi.show_id
            );

            -- Delete orphaned rows that have no matching sources (stale data)
            DELETE FROM show_source_information
            WHERE max_created_at IS NULL;

            ALTER TABLE show_source_information ALTER COLUMN max_created_at SET NOT NULL;
        ");
    }

    protected override void Down()
    {
        Execute(@"
            ALTER TABLE show_source_information DROP COLUMN max_created_at;
        ");
    }
}
