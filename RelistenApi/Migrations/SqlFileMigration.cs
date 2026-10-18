using System;
using System.IO;
using SimpleMigrations;

namespace Migrations;

// Runs a .sql file embedded from Migrations/Sql. The reality pipeline's schemas are copied
// verbatim from the ratified DDL in RelistenNet/string-theory (ddl/), so they stay diffable.
public abstract class SqlFileMigration : Migration
{
    protected void ExecuteSqlFile(string fileName)
    {
        var name = $"{typeof(SqlFileMigration).Assembly.GetName().Name}.Migrations.Sql.{fileName}";
        using var stream =
            typeof(SqlFileMigration).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing embedded migration SQL {name}");
        using var reader = new StreamReader(stream);
        Execute(reader.ReadToEnd());
    }

    protected override void Down()
    {
        throw new NotSupportedException("Reality pipeline schemas are append-only; roll forward.");
    }
}
