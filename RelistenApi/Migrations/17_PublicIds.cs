using SimpleMigrations;

namespace Migrations;

[Migration(17, "Reserve normalized public IDs across retirement and restoration")]
public sealed class PublicIds : SqlFileMigration
{
    protected override void Up() =>
        Execute(Relisten.Normalization.NormalizationSql.Read("017_public_ids.sql"));
}
