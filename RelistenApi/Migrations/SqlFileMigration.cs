using System;
using System.IO;
using SimpleMigrations;

namespace Migrations;

// Runs embedded schema files. The original norm, media and reality DDL retains its
// string-theory source; later migrations can also own derived catalog storage here.
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
