using SimpleMigrations;

namespace Migrations;

[Migration(14, "Give norm venues and songs short public ids, in place")]
public sealed class NormIds : SqlFileMigration
{
    protected override void Up()
    {
        // The DDL ships with Relisten.Normalization (string-theory ddl/004_norm_ids.sql,
        // verbatim). It rewrites every venue and song reference, history included, in one
        // transaction; writers wait on its table locks.
        Execute(Relisten.Normalization.NormalizationSql.Read("014_norm_ids.sql"));
    }
}
