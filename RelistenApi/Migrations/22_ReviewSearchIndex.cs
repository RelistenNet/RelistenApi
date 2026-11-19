using SimpleMigrations;

namespace Migrations;

[Migration(22, "Index catalog review text for trigram search")]
public sealed class ReviewSearchIndex : SqlFileMigration
{
    public ReviewSearchIndex()
    {
        UseTransaction = false;
    }

    protected override void Up()
    {
        // Concurrent index creation must be sent as a statement of its own.
        Execute("CREATE EXTENSION IF NOT EXISTS pg_trgm WITH SCHEMA public");
        ExecuteSqlFile("022_review_search_index.sql");

        // A canceled concurrent build can leave an invalid index with the expected name.
        // IF NOT EXISTS skips it, so verify the build before recording the migration.
        Execute(
            """
            DO $$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1
                    FROM pg_index i
                    JOIN pg_opclass op ON op.oid = i.indclass[0]
                    WHERE i.indexrelid = to_regclass('public.source_reviews_search_trgm_idx')
                      AND i.indrelid = 'public.source_reviews'::regclass
                      AND i.indisready AND i.indisvalid
                      AND i.indnatts = 1 AND i.indpred IS NULL
                      AND op.opcname = 'gin_trgm_ops'
                      AND pg_get_expr(i.indexprs, i.indrelid) =
                          $expr$(COALESCE((title || ' — '::text), ''::text) || review)$expr$
                ) THEN
                    RAISE EXCEPTION 'source_reviews_search_trgm_idx is missing, invalid, or has an unexpected definition. Check the review-search index before retrying migration 22.';
                END IF;
            END
            $$;
            """
        );
    }

    protected override void Down() =>
        throw new System.NotSupportedException(
            "Remove the review search index through the concurrent maintenance procedure."
        );
}
