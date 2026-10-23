using System;
using System.IO;

namespace Relisten.Normalization;

/// The kb DDL, embedded from Sql/: copied verbatim from RelistenNet/string-theory ddl/, so it
/// stays diffable. RelistenApi's migrations run it in the shared migration sequence.
public static class NormalizationSql
{
    public static string Read(string fileName)
    {
        var assembly = typeof(NormalizationSql).Assembly;
        var name = $"{assembly.GetName().Name}.Sql.{fileName}";
        using var stream =
            assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing embedded normalization SQL {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
