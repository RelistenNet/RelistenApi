using System;
using System.IO;
using SimpleMigrations;

namespace Migrations;

// Runs files embedded from Migrations/Sql. The norm, media and reality schemas are copied
// verbatim from the ratified DDL in RelistenNet/string-theory (ddl/), so they stay diffable.
public abstract class SqlFileMigration : Migration
{
    protected void ExecuteSqlFile(string fileName)
    {
        Execute(ReadEmbedded(fileName));
    }

    protected static string ReadEmbedded(string fileName)
    {
        var name = $"{typeof(SqlFileMigration).Assembly.GetName().Name}.Migrations.Sql.{fileName}";
        using var stream =
            typeof(SqlFileMigration).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing embedded migration file {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    protected override void Down()
    {
        throw new NotSupportedException(
            "The norm, media and reality schemas are append-only; roll forward."
        );
    }
}
